"""Runs in-game test suites (YAML or JSON) against LE1 through one long-lived mele-mcp server, and writes a report.

  scenario.py PATH [PATH...] [--only IDS] [--from ID] [--dry-run] [--out DIR] [--unattended]
  scenario.py --restore-saves [BACKUP_DIR] [--out DIR]
  scenario.py --selftest

PATH is a suite file (.yaml, .yml, .json) or a folder of them, run in name order (files starting with _ and README* are
skipped). The game must run, with MeleBridge 3 or later and the DebugLogger and Kismet Logger ASIs. Server settings as
in mcp_call.py: the environment, then tools/mele-mcp/build.env (MELE_DOTNET, MELE_DLL or MELE_OUT, MELE_SCENARIO_OUT).
The report goes to <out>\\<suite>\\<yyyymmdd-hhmmss>\\. --dry-run validates the suite offline and prints the plan;
--selftest checks the runner's own logic offline. Exit codes: 0 no FAIL, ERROR or ABORTED; 1 a FAIL or ERROR; 2 a usage,
suite or prerequisite error; 3 ABORTED; 4 a save restore pending or failed. The format and the guards: docs/scenarios.md.
"""
import argparse
import datetime
import fnmatch
import glob
import hashlib
import json
import math
import ntpath
import os
import re
import select
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import traceback

try:
    import yaml
except ImportError:  # .json suites still load
    yaml = None

BUILD_ENV = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "build.env")
TOOLS = {"live_status", "live_keep_ticking", "live_find", "live_get", "live_set", "live_call", "live_describe",
         "live_console", "live_watch", "game_plot", "game_screenshot", "game_kismet_log", "game_log", "game_status",
         "le_status", "game_remote_event", "game_cycle"}
REQUIRED = ["live_status", "live_keep_ticking", "live_console", "live_find", "live_get", "live_set", "live_call",
            "game_plot", "game_screenshot", "le_status"]
# The game of every run: a suite's game may only be LE1. Every tool of TOOLS but those in NO_GAME requires game, so
# Runner.call adds it to each call that lacks it.
GAME = "LE1"
NO_GAME = {"le_status"}
CONSOLE_DENY = {"biosavegame", "quicksavegame", "quicksave", "quickload", "loadgame", "bioloadgame", "autosave",
                "cleanduplicateplayersaves", "dumpsavegame", "exit", "quit", "disconnect", "exec"}
# Any function with Save or Saving in its name (QuickSave, AutoSave, TryAutoSaving, BioSaveGame, SaveProfile,
# DumpSaveGame, CleanDuplicatePlayerSaves...), and the loads.
CALL_DENY = re.compile(r"^(ConsoleCommand|QuickLoad|BioLoadGame|LoadGame\w*|NewCharacterProfile|\w*Sav(e|ing)\w*)$", re.I)
# Read-only queries the pattern above would catch. BioWorldInfo.IsAbleToSave returns whether the game allows a save now,
# and why not (sReason), without saving.
CALL_ALLOW = {"isabletosave"}
BRIDGE3 = "MeleBridge 3 or later is not installed: tools/mele-bridge/build.sh --install with the game closed"
FLUSH = "getall BioWorldInfo bHasPathNodes"
ENTRY = re.compile(r"^\[[\d:.]+ (\w+)\] ")
GETALL = re.compile(r"^appLogErrorf: (?:\d+\) \S+ \S+\.\w+ =|\t\d+: )")
SYNC_LOAD = re.compile(r"Loading package synchronously: (\S+)$")
SAVE_LOAD = re.compile(r"Loading (\S+) on device Id")
LOAD_FAIL = re.compile(r"(Unable to load .* on device Id.*|Failed to load game\..*)$")
SAVED = re.compile(r"Saved (\S+) on device")
CANT_SAVE = re.compile(r"(Can't Save: .*|Unable to save.*)$")
KISMET = re.compile(r"^\[(\d+\.\d+)\] (\S+) (\S+)(?: (.+))?$")
LOADING = re.compile(r"did not take the request in time|did not answer|not found|protocol check", re.I)
VAR = re.compile(r"\$\{([^}]+)\}")
IDENT = re.compile(r"[a-z0-9_-]+")
BUILTINS = {"map", "world", "pc", "player", "gvt", "gui", "run_dir", "stamp", "file", "check", "career"}
COMMON = {"name", "optional", "save_as"}
SOURCES = {"status", "get", "call", "count", "plot", "distance", "kismet", "log", "all", "any"}
EXPECT_OPS = {"eq", "ne", "min", "max", "lt", "gt", "in", "contains", "regex", "null", "len"}
FINDER = {"class", "tag", "name_contains", "path_contains", "where", "alive"}
TOP = {"suite", "id", "title", "game", "out", "include", "career", "saves", "cold_start", "skip", "rows", "restore",
       "vars", "macros", "setup", "checks", "teardown"}
CHECK_KEYS = {"id", "title", "row", "skip", "needs", "steps", "cleanup"}
# kind: (argument keys, the key a scalar value stands for, required keys)
KINDS = {
    "use": ({"macro", "with"}, "macro", {"macro"}),
    "console": ({"command", "expect_lines", "forbid_lines", "timeout"}, "command", {"command"}),
    "load": ({"slot", "save", "map", "timeout"}, None, {"map"}),
    "quickload": ({"map", "timeout"}, None, {"map"}),
    "at": ({"map", "start", "allow_party", "timeout"}, None, {"map", "start"}),
    "wait_loaded": ({"map", "expect_load", "allow_party", "settle", "load_grace", "timeout"}, None, set()),
    "plot": ({"bool", "int", "float"}, None, set()),
    "assert_plot": ({"bool", "int", "float"}, None, set()),
    "assert": (SOURCES | {"expect"}, None, set()),
    "wait_until": (SOURCES | {"expect", "timeout", "stable", "poll"}, None, set()),
    "sleep": ({"seconds"}, "seconds", {"seconds"}),
    "mark": ({"name"}, "name", {"name"}),
    "live_get": ({"object", "property", "depth", "expect"}, None, {"object", "property"}),
    "live_set": ({"object", "property", "value"}, None, {"object", "property", "value"}),
    "live_call": ({"object", "function", "args", "expect", "timeout"}, None, {"object", "function"}),
    "count": ({"find", "expect"}, None, {"find"}),
    "distance": ({"a", "b", "axis", "expect"}, None, {"a", "b"}),
    "teleport": ({"object", "to", "near", "offset", "ahead"}, None, set()),
    "raise_event": ({"object", "name", "class", "path_contains", "originator", "instigator", "pick"}, None, set()),
    "squad_pick": ({"members", "open", "event", "expect_spawn", "timeout"}, None, {"members"}),
    "snapshot": ({"name", "object", "props"}, None, {"name", "object", "props"}),
    "compare": ({"snapshot", "ignore", "tol", "objects", "props"}, None, set()),
    "quicksave": ({"timeout"}, None, set()),
    "screenshot": ({"name", "review", "max_width"}, "name", {"name"}),
    "assert_log": ({"since", "errors", "regex", "ignore", "expect"}, None, set()),
    "compare_log": ({"with", "since", "mode", "ignore"}, None, {"with"}),
    "assert_kismet": ({"since", "contains", "expect", "order"}, None, set()),
    "prompt": ({"text", "until", "timeout"}, "text", {"text"}),
    "watch": ({"functions", "seconds", "object_contains", "expect"}, None, {"functions"}),
    "keep_ticking": ({"on"}, "on", {"on"}),
    "game_cycle": ({"commands", "wait_for_map", "settle", "timeout"}, None, set()),
    "close_game": ({"timeout", "force"}, None, set()),
    "backup_saves": (set(), None, set()), "restore_saves": (set(), None, set()), "status": (set(), None, set()),
}
SAVE_AS = {"live_get", "live_call", "count", "distance", "snapshot", "assert_log", "watch", "status", "squad_pick"}
CAREER_KINDS = {"quickload", "quicksave", "backup_saves", "restore_saves"}


class SuiteError(Exception):
    """A usage, suite or prerequisite problem: exit 2."""


class RestorePending(Exception):
    """A save backup still waits to be restored: exit 4."""


class GuardError(Exception):
    """A guard refused a command, call, tool or path."""


class StepFail(Exception):
    """A step or an assertion failed."""


class ToolError(Exception):
    """The server answered isError, or could not answer at all."""


class ServerDead(Exception):
    """The server's stdout closed, or no answer came before the deadline."""


class Aborted(Exception):
    """The run must stop: exit 3."""


class SkipCheck(Exception):
    """The check becomes SKIP (a prompt in an --unattended run)."""


def settings():
    """The MELE_* variables of build.env, overridden by the environment. bash reads the file, as for deploy.sh, so
    quotes and comments mean the same for both."""
    found = {}
    if os.path.isfile(BUILD_ENV):
        try:
            out = subprocess.run(["bash", "-c", 'set -a; . "$1" > /dev/null || exit; exec env -0', "bash", BUILD_ENV],
                                 stdout=subprocess.PIPE, check=True).stdout
        except (OSError, subprocess.CalledProcessError):
            raise SystemExit(f"cannot read {os.path.normpath(BUILD_ENV)} with bash")
        for item in out.decode("utf-8", "replace").split("\0"):
            key, sep, value = item.partition("=")
            if sep and key.startswith("MELE_"):
                found[key] = value
    found.update((k, v) for k, v in os.environ.items() if k.startswith("MELE_"))
    return found


def default_out():
    """%LOCALAPPDATA%\\mele-mcp\\server, deploy.sh's folder when MELE_OUT is not set."""
    try:
        out = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                              "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $env:LOCALAPPDATA"],
                             stdout=subprocess.PIPE, stderr=subprocess.DEVNULL).stdout
        local = out.decode("utf-8", "replace").strip()
    except OSError:
        local = ""
    if not local:
        raise SystemExit("cannot find %LOCALAPPDATA%: set MELE_DLL or MELE_OUT (or put MELE_OUT in build.env)")
    return local + r"\mele-mcp\server"


# ---- small helpers ----

def now_iso():
    return datetime.datetime.now().isoformat(timespec="milliseconds")


def short(value, n=160):
    text = value if isinstance(value, str) else json.dumps(value, default=str)
    return text if len(text) <= n else text[:n] + " ..."


def sh(args, timeout=60):
    """A command's stdout as text; raises OSError or CalledProcessError when it fails."""
    out = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=timeout, check=True).stdout
    return out.decode("utf-8", "replace").strip()


def to_win(path):
    """A Windows path for a Windows path, /mnt/<drive>/..., or (through wslpath -w) any other WSL path."""
    if re.match(r"^[A-Za-z]:([\\/]|$)|^\\\\", path):
        return path.replace("/", "\\")
    m = re.match(r"^/mnt/([A-Za-z])(/.*)?$", path)
    if m:
        return m.group(1).upper() + ":" + (m.group(2) or "\\").replace("/", "\\")
    try:
        return sh(["wslpath", "-w", path])
    except (OSError, subprocess.SubprocessError):
        return path


def to_wsl(path):
    """A WSL path for a drive path (C:\\x is /mnt/c/x), a UNC path (through wslpath -u) or a WSL path."""
    m = re.match(r"^([A-Za-z]):[\\/]?(.*)$", path)
    if m:
        return "/mnt/" + m.group(1).lower() + "/" + m.group(2).replace("\\", "/")
    try:
        return sh(["wslpath", "-u", path]) if path.startswith("\\\\") else path
    except (OSError, subprocess.SubprocessError):
        return path


def le1_running():
    """Whether MassEffect1.exe runs; a failed tasklist counts as running, as in build.sh."""
    try:
        return "masseffect1.exe" in sh(["tasklist.exe", "/FO", "CSV", "/NH", "/FI", "IMAGENAME eq MassEffect1.exe"],
                                       30).lower()
    except (OSError, subprocess.SubprocessError):
        return True


def documents():
    """The user's Documents folder, as a WSL path."""
    return to_wsl(sh(["powershell.exe", "-NoProfile", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; "
                      "[Environment]::GetFolderPath('MyDocuments')"]))


def assert_writable(path, install):
    """Refuses a path under the game install (normalised, case-folded); every file the runner writes passes here."""
    if not install:
        raise GuardError("the game install folder is unknown, so the runner writes nothing")
    p, root = (ntpath.normpath(to_win(x)).casefold().rstrip("\\") for x in (path, install))
    if p == root or p.startswith(root + "\\"):
        raise GuardError(f"{path} is inside the game install")


def console_guard(command, career=None):
    """Refuses save, load and quit commands ('a | b' checks both parts); career allows nextframedefer loadgame <career>_*."""
    for part in str(command).split("|"):
        words = part.split()
        while words and words[0].lower() in ("nextframedefer", "defer"):
            words = words[1:]
        first = words[0].lower() if words else ""
        if career and first == "loadgame" and len(words) > 1 and words[1].lower().startswith(career.lower() + "_"):
            continue
        if "save" in first or first in CONSOLE_DENY:
            raise GuardError(f"console command refused: {part.strip()}")


def function_guard(function):
    if CALL_DENY.match(str(function)) and str(function).lower() not in CALL_ALLOW:
        raise GuardError(f"live_call {function} refused: it would bypass the console guard")


def plotval(v):
    """'True'/'False' and numeric text as a bool or a number (plot values come back as text)."""
    if isinstance(v, str):
        t = v.strip()
        if t.lower() in ("true", "false"):
            return t.lower() == "true"
        for cast in (int, float):
            try:
                return cast(t)
            except ValueError:
                pass
    return v


def equal(a, b, tol=1e-4):
    """Equality for checks: strings without case, numbers within tol, None the same as 'None', lists and dicts deep."""
    a, b = plotval(a), plotval(b)
    if a in (None, "None") or b in (None, "None"):
        return a in (None, "None") and b in (None, "None")
    if isinstance(a, bool) or isinstance(b, bool):
        return a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(a - b) <= tol
    if isinstance(a, str) and isinstance(b, str):
        return a.casefold() == b.casefold()
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(equal(x, y, tol) for x, y in zip(a, b))
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(equal(a[k], b[k], tol) for k in a)
    return a == b


def check_expect(value, expect, tol=1e-4):
    """(ok, the expectation as text): a scalar means equality, a mapping any of EXPECT_OPS (all must hold)."""
    if not isinstance(expect, dict):
        return equal(value, expect, tol), f"== {short(expect)}"
    v = plotval(value)
    number = isinstance(v, (int, float)) and not isinstance(v, bool)
    for op, want in expect.items():
        op = "null" if op is None else op  # YAML reads a 'null:' key as None
        if op == "eq":
            ok = equal(v, want, tol)
        elif op == "ne":
            ok = not equal(v, want, tol)
        elif op in ("min", "max", "lt", "gt"):
            ok = number and {"min": v >= want - tol, "max": v <= want + tol, "lt": v < want, "gt": v > want}[op]
        elif op == "in":
            ok = any(equal(v, w, tol) for w in want)
        elif op == "contains":
            ok = (str(want).casefold() in v.casefold()) if isinstance(v, str) else \
                isinstance(v, list) and any(equal(x, want, tol) for x in v)
        elif op == "regex":
            ok = v is not None and re.search(want, v if isinstance(v, str) else json.dumps(v), re.I) is not None
        elif op == "null":
            ok = (v in (None, "None")) == bool(want)
        elif op == "len":
            n = len(v) if isinstance(v, (list, str, dict)) else -1
            ok = check_expect(n, want)[0]
        else:
            raise StepFail(f"unknown expect key {op}")
        if not ok:
            return False, f"{op} {short(want)}"
    return True, " ".join(f"{k} {short(w)}" for k, w in expect.items())


def dig(value, path):
    """value[a][b][0] for 'a.b[0]'; None when a step is missing."""
    for key, idx in re.findall(r"([^.\[\]]+)|\[(\d+)\]", path or ""):
        try:
            value = value[int(idx)] if idx else value[key]
        except (KeyError, IndexError, TypeError, ValueError):
            return None
    return value


def vec(v):
    """(x, y, z) from [x, y, z] or {X, Y, Z}."""
    try:
        p = tuple(float(v[k]) for k in ("X", "Y", "Z")) if isinstance(v, dict) else tuple(float(x) for x in v)
    except (KeyError, TypeError, ValueError):
        p = ()
    if len(p) != 3:
        raise StepFail(f"not a location: {short(v)}")
    return p


def entries(lines):
    """DebugLogger entries, (level, text): a timestamped line plus the untimed lines after it."""
    out = []
    for line in lines:
        m = ENTRY.match(line)
        if m:
            out.append([m.group(1), line[m.end():]])
        elif line.strip() and out:
            out[-1][1] += "\n" + line.strip()
    return [tuple(e) for e in out]


def is_problem(level, text):
    """Errors, warnings and Accessed None, but not getall answers (the flush included), which log as errors."""
    return not GETALL.search(text) and (level in ("error", "critical", "warning") or "Accessed None" in text)


def normalise(text):
    """An entry for compare_log: first line, object numbers and addresses blanked, spaces collapsed."""
    line = re.sub(r"0x[0-9a-fA-F]+", "0x?", re.sub(r"_\d+\b", "_N", text.split("\n")[0]))
    return re.sub(r"\s+", " ", line).strip()


def load_problems(st, want, allow_party):
    """What keeps a live_status reply from counting as loaded; empty when it does (docs/scenarios.md, wait_loaded)."""
    why = []
    if want and str(st.get("map") or "").casefold() != str(want).casefold():
        why.append(f"map is {st.get('map')}")
    if not st.get("begunPlay"):
        why.append("the world has not begun play")
    if not st.get("pawn"):
        why.append("no pawn")
    streaming = st.get("streaming") or {}
    if streaming.get("pending"):
        why.append(f"{streaming['pending']} streaming levels pending ({', '.join(streaming.get('pendingNames') or [])})")
    movie = st.get("loadingMovie")
    if movie and (movie.get("playing") or movie.get("state") != "LMS_NotPlaying"):
        why.append(f"loading movie {movie.get('state')}")
    party = st.get("partySelectOpen") is True
    if party and not allow_party:
        why.append("the squad screen is open")
    if st.get("bPlayersOnly") and not (party and allow_party):
        why.append("the world is paused (bPlayersOnly)")
    if st.get("keepTicking") is False:
        why.append("keep_ticking is off")
    return why


def restore_command(pending=None, backup=None):
    """The --restore-saves command line for a backup folder, or for the one a PENDING file names."""
    if pending:
        try:
            with open(pending, encoding="utf-8") as f:
                backup = f.read().strip()
        except OSError:
            pass
    return "python3 -I scenario.py --restore-saves" + (f" '{to_win(backup)}'" if backup else "")


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def file_stat(path):
    try:
        s = os.stat(path)
        return s.st_mtime_ns, s.st_size
    except OSError:
        return None


# ---- saves (D1): backup before the first save, restore with the game closed ----

def backup_saves(save_root, career, dest):
    """Copies the career folder, PROFILE/GamerProfile.pcsav and Launcher_Profile* into dest, checks every copy, and
    writes manifest.json there. Returns the manifest."""
    if not os.path.isdir(os.path.join(save_root, career)):
        raise SuiteError(f"no career folder {career} in {save_root}")
    rels = [os.path.relpath(os.path.join(d, n), save_root) for d, _, ns in os.walk(os.path.join(save_root, career))
            for n in ns]
    rels += [r for r in (os.path.join("PROFILE", "GamerProfile.pcsav"),) if os.path.isfile(os.path.join(save_root, r))]
    rels += [n for n in os.listdir(save_root) if n.startswith("Launcher_Profile")
             and os.path.isfile(os.path.join(save_root, n))]
    files = []
    for rel in sorted(rels):
        src, dst = os.path.join(save_root, rel), os.path.join(dest, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        st = os.stat(src)
        files.append({"rel": rel.replace(os.sep, "/"), "size": st.st_size, "mtime": st.st_mtime, "sha256": sha(src)})
    bad = [e["rel"] for e in files if sha(os.path.join(dest, e["rel"])) != e["sha256"]]
    if bad:
        raise SuiteError(f"the save backup in {dest} differs from the originals: {bad}")
    manifest = {"created": now_iso(), "save_root": to_win(save_root), "career": career, "files": files,
                "top_level": sorted(os.listdir(save_root))}
    with open(os.path.join(dest, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1)
    return manifest


def restore_saves(backup):
    """Puts a backup back, the game closed: files the run added or changed move to <backup>/displaced/<stamp>/ (nothing
    is deleted), manifest files that are missing or differ are copied back. Returns what happened, with 'ok'."""
    if le1_running():
        raise GuardError("close the game first: saves are restored only while LE1 is closed")
    with open(os.path.join(backup, "manifest.json"), encoding="utf-8") as f:
        man = json.load(f)
    root = to_wsl(man["save_root"])
    away = os.path.join(backup, "displaced", time.strftime("%Y%m%d-%H%M%S"))
    known = {e["rel"] for e in man["files"]}
    moved, copied, warns = [], [], []

    def displace(rel):
        dst = os.path.join(away, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.move(os.path.join(root, rel), dst)
        moved.append(rel)

    for d, _, names in os.walk(os.path.join(root, man["career"])):
        for n in names:
            rel = os.path.relpath(os.path.join(d, n), root).replace(os.sep, "/")
            if rel not in known:
                displace(rel)
    for n in sorted(os.listdir(root)):
        if n not in man["top_level"]:
            displace(n)
            warns.append(f"moved out {n}: it appeared in the save folder during the run")
    for e in man["files"]:
        dst = os.path.join(root, e["rel"])
        if not os.path.isfile(dst) or sha(dst) != e["sha256"]:
            if os.path.exists(dst):
                displace(e["rel"])
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(os.path.join(backup, e["rel"]), dst)
            copied.append(e["rel"])
    bad = [e["rel"] for e in man["files"] if not os.path.isfile(os.path.join(root, e["rel"]))
           or sha(os.path.join(root, e["rel"])) != e["sha256"]]
    return {"backup": to_win(backup), "restored": copied, "displaced": moved, "warns": warns, "mismatch": bad,
            "ok": not bad}


# ---- the MCP session ----

class Session:
    """One long-lived mele-mcp server over stdio (like mcp_smoke.py); a dead or silent one is respawned once per call."""

    def __init__(self, s, record):
        self.dotnet = s.get("MELE_DOTNET") or "dotnet.exe"
        self.dll = s.get("MELE_DLL") or (s.get("MELE_OUT") or default_out()).rstrip("\\") + r"\MeleMcp.dll"
        self.env = dict(os.environ)
        # WSL hands only the variables WSLENV names to a Windows process: every MELE_* setting but this script's.
        passed = sorted(k for k, v in s.items() if v and k not in ("MELE_DOTNET", "MELE_DLL", "MELE_OUT",
                                                                    "MELE_SCENARIO_OUT"))
        self.env.update((k, s[k]) for k in passed)
        self.env["WSLENV"] = ":".join(passed)
        self.record, self.proc, self.n, self.buf = record, None, 0, b""
        self.start()

    def start(self):
        try:
            self.proc = subprocess.Popen([self.dotnet, self.dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                         stderr=subprocess.DEVNULL, env=self.env)
        except FileNotFoundError:
            raise SuiteError(f"{self.dotnet} not found: set MELE_DOTNET to the Windows dotnet.exe (or put it in build.env)")
        except OSError as e:
            raise SuiteError(f"cannot start {self.dotnet}: {e}")
        self.buf = b""
        try:
            init = self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                           "clientInfo": {"name": "scenario", "version": "1"}}, 60)
            self.server = init.get("serverInfo")
            self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            self.tools = {t["name"] for t in self.rpc("tools/list", {}, 30).get("tools", [])}
        except (ServerDead, ToolError, KeyError) as e:
            raise SuiteError(f"no mele-mcp server at {self.dll} ({e}): run deploy.sh, or set MELE_DLL or MELE_OUT")

    def send(self, msg):
        try:
            self.proc.stdin.write((json.dumps(msg) + "\n").encode())
            self.proc.stdin.flush()
        except (BrokenPipeError, OSError):
            raise ServerDead("server closed stdin")

    def readline(self, deadline):
        while b"\n" not in self.buf:
            left = deadline - time.time()
            if left <= 0:
                raise ServerDead("no answer before the deadline")
            if select.select([self.proc.stdout], [], [], left)[0]:
                chunk = os.read(self.proc.stdout.fileno(), 65536)
                if not chunk:
                    raise ServerDead("server closed stdout")
                self.buf += chunk
        line, _, self.buf = self.buf.partition(b"\n")
        return line

    def rpc(self, method, params, timeout):
        self.n += 1
        self.send({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params})
        deadline = time.time() + timeout
        while True:
            try:
                msg = json.loads(self.readline(deadline))
            except ValueError:  # dotnet.exe's own text, e.g. for a missing DLL
                if method == "initialize":
                    raise ServerDead("not a JSON-RPC answer")
                continue
            if msg.get("id") == self.n:
                if "error" in msg:
                    raise ToolError(f"JSON-RPC error: {msg['error']}")
                return msg["result"]

    def close(self):
        if self.proc and self.proc.poll() is None:
            try:
                self.proc.stdin.close()
                self.proc.wait(timeout=10)
            except (OSError, subprocess.TimeoutExpired):
                self.proc.kill()

    def call(self, tool, args, timeout=None):
        """The tool's reply, parsed as JSON (raw text if it is not); isError raises ToolError with the text."""
        if tool not in TOOLS:
            raise GuardError(f"tool {tool} is not on the runner's allowlist")
        if timeout is None:
            timeout = args.get("timeoutSeconds", 240) * 2 + 150 if tool == "game_cycle" else \
                float(args.get("timeoutSeconds", args.get("seconds", 10)))
        entry, t0 = {"t": now_iso(), "tool": tool, "args": args}, time.time()
        try:
            for attempt in (1, 2):
                try:
                    result = self.rpc("tools/call", {"name": tool, "arguments": args}, timeout + 30)
                    break
                except ServerDead as e:
                    if attempt == 2:
                        raise ToolError(f"mele-mcp: {e}")
                    self.close()
                    try:  # e.g. deploy.sh is publishing: a step FAILs, and the finally paths still run
                        self.start()
                    except SuiteError as again:
                        raise ToolError(f"mele-mcp could not restart: {again}")
            text = "".join(b.get("text", "") for b in result.get("content", []))
            try:
                value = json.loads(text)
            except ValueError:
                value = text
            if result.get("isError"):
                raise ToolError(text)
            entry.update(ok=True, result=value)
            return value
        except ToolError as e:
            entry.update(ok=False, error=str(e))
            raise
        finally:
            entry["ms"] = int((time.time() - t0) * 1000)
            self.record({k: v if len(json.dumps(v, default=str)) <= 4000 else json.dumps(v, default=str)[:4000]
                         for k, v in entry.items()})


# ---- suite files ----

def read_doc(path):
    try:
        with open(path, encoding="utf-8") as f:
            text = f.read()
        if path.lower().endswith(".json"):
            return json.loads(text)
        if yaml is None:
            raise SuiteError(f"{path}: PyYAML is not installed (python3-yaml); .json suites still load")
        return yaml.safe_load(text)
    except (OSError, ValueError) as e:
        raise SuiteError(f"{path}: {e}")
    except Exception as e:  # yaml.YAMLError
        raise SuiteError(f"{path}: {e}")


def load_file(path, seen=None):
    """A suite file with its includes' vars and macros merged in (later wins, the file's own last)."""
    doc = read_doc(path)
    if not isinstance(doc, dict):
        raise SuiteError(f"{path}: the top level is a mapping")
    seen = (seen or set()) | {os.path.abspath(path)}
    vars_, macros = {}, {}
    for inc in doc.get("include") or []:
        p = os.path.join(os.path.dirname(path), inc)
        if os.path.abspath(p) in seen:
            raise SuiteError(f"{path}: {inc} includes itself")
        sub = load_file(p, seen)
        vars_.update(sub["vars"])
        macros.update(sub["macros"])
    vars_.update(doc.get("vars") or {})
    macros.update(doc.get("macros") or {})
    stem = os.path.splitext(os.path.basename(path))[0]
    return {"path": path, "doc": doc, "id": str(doc.get("id") or stem), "vars": vars_, "macros": macros}


def suite_paths(paths):
    out = []
    for p in paths:
        if os.path.isdir(p):
            out += sorted(os.path.join(p, n) for n in os.listdir(p) if n.lower().endswith((".yaml", ".yml", ".json"))
                          and not n.startswith("_") and not n.upper().startswith("README"))
        elif os.path.isfile(p):
            out.append(p)
        else:
            raise SuiteError(f"{p}: no such file or folder")
    if not out:
        raise SuiteError("no suite files found")
    return out


def split_step(step):
    """(kind, args, common keys) of a step; raises SuiteError when it has not exactly one kind key."""
    if not isinstance(step, dict):
        raise SuiteError(f"a step is a mapping with one kind key, not {short(step, 60)}")
    kinds = [k for k in step if k in KINDS]
    other = [k for k in step if k not in KINDS and k not in COMMON]
    if len(kinds) != 1 or other:
        raise SuiteError(f"a step needs exactly one kind key ({', '.join(map(str, kinds + other)) or 'none'} given)")
    kind, value = kinds[0], step[kinds[0]]
    keys, scalar, _ = KINDS[kind]
    common = {k: v for k, v in step.items() if k in COMMON}
    if value is None:
        args = {}
    elif isinstance(value, dict) and kind not in ("keep_ticking", "sleep", "mark"):
        args = dict(value)
        for k in ("save_as", "optional"):  # also taken inside the mapping
            if k in args and k not in keys:
                common[k] = args.pop(k)
    elif scalar and not isinstance(value, (dict, list)):
        args = {scalar: value}
    else:
        raise SuiteError(f"{kind} takes {'a mapping' if not scalar else 'a scalar' if kind in ('keep_ticking', 'sleep', 'mark') else 'a scalar or a mapping'}")
    return kind, args, common


def flatten(steps, macros, scope=None, depth=0):
    """[(step, scope)] with each use replaced by its macro's steps; scope holds the use's 'with' values."""
    out = []
    for step in steps or []:
        if isinstance(step, dict) and "use" in step:
            u = step["use"]
            name, given = (u, {}) if not isinstance(u, dict) else (u.get("macro"), u.get("with") or {})
            if name not in macros:
                raise SuiteError(f"unknown macro {name}")
            if depth > 10:
                raise SuiteError(f"macro {name}: macros use each other in a loop")
            out += flatten(macros[name], macros, {**(scope or {}), **given}, depth + 1)
        else:
            out.append((step, scope or {}))
    return out


def all_steps(f):
    """(where, steps) of every step list of a file, macros last."""
    doc = f["doc"]
    lists = [("setup", doc.get("setup")), ("teardown", doc.get("teardown"))]
    for c in doc.get("checks") or []:
        if isinstance(c, dict):
            lists += [(f"{c.get('id')}.steps", c.get("steps")), (f"{c.get('id')}.cleanup", c.get("cleanup"))]
    return lists + [(f"macros.{m}", body) for m, body in f["macros"].items()]


def walk(value):
    """Every string inside a value."""
    if isinstance(value, str):
        yield value
    elif isinstance(value, list):
        for v in value:
            yield from walk(v)
    elif isinstance(value, dict):
        for v in value.values():
            yield from walk(v)


def condition_errors(cond):
    if not isinstance(cond, dict):
        return ["a condition is a mapping with one source key"]
    src = [k for k in cond if k in SOURCES]
    if len(src) != 1:
        return [f"a condition needs exactly one of {', '.join(sorted(SOURCES))}"]
    errs = expect_errors(cond.get("expect"))
    if src[0] in ("all", "any"):
        if not isinstance(cond[src[0]], list):
            return [f"{src[0]} takes a list of conditions"]
        for c in cond[src[0]]:
            errs += condition_errors(c)
    return errs


def expect_errors(expect):
    if isinstance(expect, dict):
        bad = [k for k in expect if k is not None and k not in EXPECT_OPS]
        return [f"unknown expect key {', '.join(map(str, bad))}"] if bad else []
    return []


def step_errors(step):
    """Syntax errors of one step (no file context)."""
    try:
        kind, a, common = split_step(step)
    except SuiteError as e:
        return [str(e)]
    keys, _, required = KINDS[kind]
    errs = [f"{kind}: unknown argument {k}" for k in a if k not in keys]
    errs += [f"{kind}: {k} is required" for k in sorted(required - set(a))]
    if common.get("save_as") and kind not in SAVE_AS:
        errs.append(f"{kind} has no value for save_as")
    errs += expect_errors(a.get("expect"))
    check = {
        "load": ("slot" in a) != ("save" in a), "teleport": sum(k in a for k in ("to", "near", "ahead")) == 1,
        "raise_event": "object" in a or "name" in a, "assert_kismet": "contains" in a or "order" in a,
        "compare": "snapshot" in a or ("objects" in a and "props" in a),
        "keep_ticking": a.get("on") in (True, False, "on", "off"),
        "wait_loaded": a.get("expect_load", True) in (True, False, "auto"),
        "distance": a.get("axis", "xyz") in ("xyz", "xy", "z"),
        "compare_log": a.get("mode", "no_extra") in ("no_extra", "same"),
        "squad_pick": isinstance(a.get("members"), list) and 1 <= len(a["members"]) <= 2
        and a.get("open", "console") in ("console", "none", "event") and (a.get("open") != "event" or "event" in a),
        "sleep": isinstance(a.get("seconds"), (int, float)) or "${" in str(a.get("seconds")),
        "game_cycle": isinstance(a.get("commands", []), list),
    }
    if not check.get(kind, True):
        errs.append(f"{kind}: bad arguments {short(a, 100)} (docs/scenarios.md)")
    if kind in ("plot", "assert_plot"):
        for k, ids in a.items():
            if not isinstance(ids, dict) or not all(re.fullmatch(r"\d+", str(i)) for i in ids):
                errs.append(f"{kind}: {k} takes {{id: value}}")
    if kind in ("assert", "wait_until"):
        errs += condition_errors({k: v for k, v in a.items() if k not in ("timeout", "stable", "poll")})
    if kind == "prompt" and a.get("until") is not None:
        errs += condition_errors(a["until"])
    if kind == "count" and isinstance(a.get("find"), dict):
        errs += finder_errors(a["find"].get("find", a["find"]))
    return errs


def finder_errors(f):
    if not isinstance(f, dict):
        return ["a finder is a mapping"]
    errs = [f"unknown finder key {k}" for k in f if k not in FINDER]
    if not any(f.get(k) for k in ("class", "name_contains", "path_contains")):
        errs.append("a finder needs class, name_contains or path_contains")
    return errs


def context_errors(step, scope, f, names, marks, snaps):
    """Guards and references of one runnable step in its file: what --dry-run can check offline."""
    kind, a, common = split_step(step)
    doc, errs = f["doc"], []
    career = doc.get("career")
    for text in walk(a):
        for ref in VAR.findall(text):
            head = re.match(r"[A-Za-z_]\w*", ref.strip())
            if not head or (head.group(0) not in names and head.group(0) not in scope):
                errs.append(f"unknown variable ${{{ref}}}")
    try:
        if kind == "console":
            console_guard(VAR.sub("x", str(a.get("command", ""))))
        if kind == "live_call":
            function_guard(a.get("function"))
        if kind == "game_cycle":
            for c in a.get("commands") or []:
                console_guard(VAR.sub("x", str(c)), career)
    except GuardError as e:
        errs.append(str(e))
    if kind in ("game_cycle", "close_game") and not doc.get("cold_start"):
        errs.append(f"{kind} runs only in a cold_start file")
    if (kind in CAREER_KINDS or kind == "load" and "save" in a) and not career:
        errs.append(f"{kind} needs career: in the file")
    if kind == "load" and career and "save" in a and "${" not in str(a["save"]) \
            and not str(a["save"]).lower().startswith(career.lower() + "_"):
        errs.append(f"load: save {a['save']} is not in career {career}")
    if kind == "quicksave" and not doc.get("saves"):
        errs.append("quicksave needs saves: true in the file")
    conds = conditions(a) if kind in ("assert", "wait_until", "prompt") else []
    for since in [a.get("since")] + [dig(c, f"{k}.since") for c in conds for k in ("kismet", "log")]:
        if since is not None and since not in ("file", "check", "step", "load") and since not in marks:
            errs.append(f"since: {since}: no such mark (file, check, step, load or mark:<name>)")
    if kind == "compare" and "snapshot" in a and a["snapshot"] not in snaps:
        errs.append(f"compare: no snapshot named {a['snapshot']}")
    if kind == "compare_log" and a["with"] not in names and a["with"] not in scope:
        errs.append(f"compare_log: {a['with']} is not set by any save_as")
    for b in ([a.get("find")] if kind == "count" else []) + [c.get("count") for c in conds]:
        if isinstance(b, str) and "${" not in b and b not in scope and \
                not (isinstance(f["vars"].get(b), dict) and "find" in f["vars"][b]):
            errs.append(f"{b} is not a finder variable")
    return errs


def conditions(a):
    """The condition mappings in a step's arguments: the step itself (assert, wait_until), prompt.until, all/any."""
    out, todo = [], [a, a.get("until")]
    while todo:
        c = todo.pop()
        if isinstance(c, dict) and any(k in c for k in SOURCES):
            out.append(c)
            todo += [x for k in ("all", "any") if isinstance(c.get(k), list) for x in c[k]]
    return out


def validate(f):
    """Every error of a suite file: shape, steps, references and the guards --dry-run can check."""
    doc, errs = f["doc"], []

    def err(text):
        errs.append(f"{f['path']}: {text}")

    for k in doc:
        if k not in TOP:
            err(f"unknown top-level key {k}")
    if not IDENT.fullmatch(str(doc.get("suite", ""))):
        err("suite: required, [a-z0-9_-]+")
    if not doc.get("title"):
        err("title: required")
    if doc.get("game", GAME) != GAME:
        err(f"game: only {GAME}")
    for kind, ids in (doc.get("restore") or {}).items():
        if kind not in ("bool", "int", "float") or not all(isinstance(i, int) for i in ids or []):
            err(f"restore: {kind} takes a list of plot ids (bool, int or float)")
    for name, v in f["vars"].items():
        if isinstance(v, dict) and "find" in v:
            errs += [f"{f['path']}: vars.{name}: {e}" for e in finder_errors(v["find"])]
    checks = doc.get("checks")
    if not isinstance(checks, list) or not checks:
        err("checks: a non-empty list")
        checks = []
    ids = [c.get("id") if isinstance(c, dict) else None for c in checks]
    for c in checks:
        if not isinstance(c, dict) or not IDENT.fullmatch(str(c.get("id", ""))):
            err(f"check {short(c, 40)}: id required, [a-z0-9_-]+")
            continue
        if ids.count(c["id"]) > 1:
            err(f"check {c['id']}: the id is used twice")
        for k in c:
            if k not in CHECK_KEYS:
                err(f"check {c['id']}: unknown key {k}")
        if not set(c.get("needs") or []) <= {"user"}:
            err(f"check {c['id']}: needs takes [user]")
    names, marks, snaps = set(BUILTINS) | set(f["vars"]), set(), set()
    lists = all_steps(f)
    for where, steps in lists:
        if steps is not None and not isinstance(steps, list):
            err(f"{where}: a list of steps")
            continue
        for i, step in enumerate(steps or [], 1):
            errors = step_errors(step)
            for e in errors:
                err(f"{where}[{i}]: {e}")
            if errors:
                continue
            kind, a, common = split_step(step)
            if common.get("save_as"):
                names.add(common["save_as"])
            if kind == "mark":
                marks.add(f"mark:{a['name']}")
            if kind == "snapshot":
                snaps.add(a["name"])
    for where, steps in lists:
        if where.startswith("macros.") or not isinstance(steps, list):
            continue
        try:
            flat = flatten(steps, f["macros"])
        except SuiteError as e:
            err(f"{where}: {e}")
            continue
        for i, (step, scope) in enumerate(flat, 1):
            try:
                errs += [f"{f['path']}: {where} step {i}: {e}" for e in context_errors(step, scope, f, names, marks, snaps)]
            except SuiteError:
                pass  # reported by the syntax pass
    return errs


def load_all(paths):
    files = [load_file(p) for p in suite_paths(paths)]
    errs = [e for f in files for e in validate(f)]
    if errs:
        raise SuiteError(f"{len(errs)} problem(s) in the suite:\n  " + "\n  ".join(errs))
    return files


def select_checks(files, only, start):
    """[(file, [checks])] after --only and --from; a file with no check left is dropped."""
    pats = [p.strip().casefold() for p in (only or "").split(",") if p.strip()]
    started, out = not start, []
    for f in files:
        chosen = []
        for c in f["doc"]["checks"]:
            keys = (f["id"].casefold(), f"{f['id']}/{c['id']}".casefold())
            if not started and any(fnmatch.fnmatchcase(k, start.casefold()) for k in keys):
                started = True
            if started and (not pats or any(fnmatch.fnmatchcase(k, p) for k in keys for p in pats)):
                chosen.append(c)
        if chosen:
            out.append((f, chosen))
    if start and not started:
        raise SuiteError(f"--from {start}: no file or check matches")
    if not out:
        raise SuiteError("no check selected")
    if len({f["doc"]["suite"] for f, _ in out}) != 1:
        raise SuiteError("every file of a run must have the same suite")
    run = [f for f, _ in out if not f["doc"].get("skip")]
    cold = [i for i, f in enumerate(run) if f["doc"].get("cold_start")]
    if cold and cold != [len(run) - 1]:
        raise SuiteError("a cold_start file must be the last file selected, and only one")
    return out


def dry_run(selected):
    files = len(selected)
    print(f"suite {selected[0][0]['doc']['suite']}: {files} file(s), {sum(len(c) for _, c in selected)} check(s) "
          "(dry run: validated offline, nothing sent)")
    for f, checks in selected:
        doc = f["doc"]
        flags = [k for k in ("saves", "cold_start") if doc.get(k)] + ([f"career {doc['career']}"] if doc.get("career") else [])
        flags += [f"restore {k} {v}" for k, v in (doc.get("restore") or {}).items() if v]
        print(f"\n{f['id']}  {doc['title']}" + (f"  [{', '.join(flags)}]" if flags else ""))
        if doc.get("skip"):
            print(f"  SKIP (whole file, nothing runs): {doc['skip']}")
        parts = [("setup", None, doc.get("setup"))] + [(c["id"], c, c.get("steps")) for c in checks] + \
                [("teardown", None, doc.get("teardown"))]
        for label, c, steps in parts:
            if not steps and not c:
                continue
            head = f"  {label}" + (f"  {c.get('title', '')}" if c else "")
            if c and c.get("skip"):
                head += f"  SKIP: {c['skip']}"
            elif c and "user" in (c.get("needs") or []):
                head += "  (needs a person: SKIP with --unattended)"
            print(head)
            for i, (step, _) in enumerate(flatten(steps, f["macros"]), 1):
                kind, a, common = split_step(step)
                print(f"    {i:2} {kind} {short(a, 110) if a else ''}" + (f"  # {common['name']}" if common.get("name") else ""))
            if c and c.get("cleanup"):
                print(f"    cleanup: {len(flatten(c['cleanup'], f['macros']))} step(s)")
    return 0


# ---- logs ----

class Logs:
    """DebugLogger.log and KismetLogger.log of the game, read by byte offset through /mnt (never written)."""

    def __init__(self, game_dir=None, paths=None):
        base = to_wsl(game_dir.rstrip("\\") + "\\Binaries\\Win64\\Logs") if game_dir else ""
        self.paths = paths or (os.path.join(base, "DebugLogger.log"), os.path.join(base, "KismetLogger.log"))

    def offsets(self):
        return tuple(file_stat(p)[1] if file_stat(p) else 0 for p in self.paths)

    def lines(self, which, start, end=None):
        """(whole lines from start to end or the end of log 0 (debug) or 1 (kismet), the offset after them). A start
        past the end means the next game session rewrote the log, so it reads from 0."""
        try:
            with open(self.paths[which], "rb") as f:
                size = os.fstat(f.fileno()).st_size
                start = 0 if start > size else start
                f.seek(start)
                data = f.read(max(0, min(size, end if end is not None else size) - start))
        except OSError:
            return [], start
        cut = data.rfind(b"\n") + 1
        return data[:cut].decode("utf-8", "replace").splitlines(), start + cut


# ---- the runner ----

class Runner:
    """Runs selected suite files step by step (docs/scenarios.md). Every k_<kind> method is one step kind."""

    def __init__(self, session, unattended=False):
        self.s, self.unattended = session, unattended
        self.vars, self.scope, self.macros, self.cache, self.marks, self.snapshots = {}, {}, {}, {}, {}, {}
        self.file, self.check_id, self.cur, self.rec, self.st, self.st_t = None, "", None, {}, None, 0
        self.logs, self.scan_at, self.trigger, self.load_window, self.expect_save = None, [0, 0], None, False, None
        self.warns, self.notes, self.shot_n, self.review = [], set(), 0, False
        self.install = self.run_dir = self.run_win = self.backup = self.saves_root = self.progress = None
        self.game_down_ok, self.game_closed, self.load_aborted, self.alive_t = False, False, False, time.time()
        self.stamp, self.bridge, self.saves = time.strftime("%Y%m%d-%H%M%S"), None, {}
        self.suite = self.suite_root = self.calls_file = None
        self.game = GAME

    # -- tools, variables, objects --

    def call(self, tool, args=None, timeout=None):
        args = {k: v for k, v in (args or {}).items() if v is not None or k == "value"}
        if tool not in NO_GAME and "game" not in args:  # the server has no default game; a given one stays
            args["game"] = self.game
        if tool == "game_remote_event" and re.match(r"(Live|Poll)_NPC_", str(args.get("eventName", "")), re.I):
            raise GuardError("Live_NPC_* and Poll_NPC_* events hung LE1 once")
        if tool == "game_cycle" and (not (self.file and self.file["doc"].get("cold_start")) or args.get("mod")
                                     or args.get("loadSlot") is not None):
            raise GuardError("game_cycle runs only in a cold_start file, without mod or loadSlot")
        if tool == "game_screenshot":
            assert_writable(args.get("path", ""), self.install)
        try:
            return self.s.call(tool, args, timeout)
        except ToolError as e:
            if "is not running" in str(e) and not self.game_down_ok and not le1_running():
                raise Aborted("the game closed or crashed")
            raise

    def status(self):
        st = self.call("live_status", {"timeoutSeconds": 3})
        self.st, self.st_t = st, time.time()
        self.bridge = self.bridge or st.get("bridge")
        return st

    def builtin(self, name):
        if name in ("map", "world", "pc", "player"):
            if self.st is None or time.time() - self.st_t > 2:
                self.status()
            v = self.st.get("pawn" if name == "player" else name)
            if v is None:
                raise StepFail(f"${{{name}}} is not known now (live_status has no {name})")
            return v
        if name == "gvt":
            key = ("gvt", self.builtin("map"))
            if key not in self.cache:
                objs = self.call("live_find", {"className": "BioGlobalVariableTable", "limit": 5}).get("objects") or []
                if not objs:
                    raise StepFail("no BioGlobalVariableTable")
                self.cache[key] = objs[0]["path"]
            return self.cache[key]
        if name == "gui":
            pc = self.builtin("pc")
            if ("gui", pc) not in self.cache:
                self.cache[("gui", pc)] = self.call("live_call", {"objectPath": pc, "function": "GetScaleFormManager",
                                                                  "args": []}).get("return")
            return self.cache[("gui", pc)]
        return {"run_dir": self.run_win, "stamp": self.stamp, "file": self.file and self.file["id"],
                "check": self.check_id, "career": self.file and self.file["doc"].get("career") or ""}[name]

    def lookup(self, ref, depth=0):
        m = re.match(r"^\s*([A-Za-z_]\w*)(.*?)\s*$", ref)
        name = m.group(1) if m else ref
        if name in self.scope:
            v = self.interp(self.scope[name], depth + 1)
        elif name in self.vars:
            v = self.interp(self.vars[name], depth + 1)
        elif name in BUILTINS:
            v = self.builtin(name)
        else:
            raise StepFail(f"unknown variable {name}")
        if isinstance(v, dict) and set(v) == {"find"}:
            v = self.find_one(v["find"], name)
        if m and m.group(2):
            got = dig(v, m.group(2))
            if got is None:
                raise StepFail(f"${{{ref}}}: nothing at {m.group(2)} in {short(v, 80)}")
            v = got
        return v

    def interp(self, value, depth=0):
        """${name} replaced in every string; a string that is only ${name} keeps the value's type."""
        if depth > 8:
            raise StepFail("variables refer to each other in a loop")
        if isinstance(value, str):
            whole = VAR.fullmatch(value.strip())
            if whole:
                return self.lookup(whole.group(1), depth)
            return VAR.sub(lambda m: (lambda v: v if isinstance(v, str) else json.dumps(v) if isinstance(v, (dict, list))
                                      else str(v))(self.lookup(m.group(1), depth)), value)
        if isinstance(value, list):
            return [self.interp(v, depth) for v in value]
        if isinstance(value, dict):
            return {k: self.interp(v, depth) for k, v in value.items()}
        return value

    def soft(self, value):
        """value with only the cheap ${} filled in (plain variables, known built-ins), for the report."""
        def one(m):
            name = re.match(r"\s*([A-Za-z_]\w*)", m.group(1))
            v = self.scope.get(name.group(1), self.vars.get(name.group(1))) if name else None
            return v if isinstance(v, str) and "${" not in v else m.group(0)
        if isinstance(value, str):
            return VAR.sub(one, value)
        return [self.soft(v) for v in value] if isinstance(value, list) else \
            {k: self.soft(v) for k, v in value.items()} if isinstance(value, dict) else value

    def get(self, obj, prop, depth=None):
        return self.call("live_get", {"objectPath": obj, "property": prop, "depth": depth}).get("value")

    def finder(self, spec):
        """A finder mapping from an inline mapping, {find: ...}, ${var} or a finder variable's bare name."""
        if isinstance(spec, str):
            name = VAR.fullmatch(spec).group(1).strip() if VAR.fullmatch(spec) else spec
            v = self.scope.get(name, self.vars.get(name))
            if not (isinstance(v, dict) and "find" in v):
                raise StepFail(f"{spec} is not a finder variable")
            spec = v
        return self.interp(spec.get("find", spec) if isinstance(spec, dict) else spec)

    def find_all(self, spec):
        """Paths of the live objects a finder matches: class/name/path through live_find, then tag, where and alive."""
        f = self.finder(spec)
        found = self.call("live_find", {"className": f.get("class"), "name": f.get("name_contains"),
                                        "path": f.get("path_contains"), "limit": 500}).get("objects") or []
        keep = []
        for o in found:
            path = o["path"]
            try:
                if f.get("tag") is not None and not equal(self.get(path, "Tag"), f["tag"]):
                    continue
                if not all(check_expect(self.get(path, p), want)[0] for p, want in (f.get("where") or {}).items()):
                    continue
                if f.get("alive", True):
                    try:
                        if self.get(path, "bDeleteMe") is True:
                            continue
                    except ToolError as e:
                        if "no property 'bDeleteMe'" not in str(e):  # not an actor: alive
                            raise
            except ToolError as e:
                # Gone since live_find, or without the filtered property: no match. Anything else (a timeout, a dead
                # pipe) fails the step, so a count of 0 is never a guess.
                if "no object at " in str(e) or "no property '" in str(e):
                    continue
                raise
            keep.append(path)
        return keep

    def find_one(self, spec, label):
        paths = self.find_all(spec)
        if len(paths) != 1:
            raise StepFail(f"{label}: {len(paths)} live objects match, need exactly one ({short(paths, 120)})")
        return paths[0]

    def obj(self, spec):
        """An object path from a path, ${var}, a variable's bare name, player, a finder, or a chain X>Prop>Prop."""
        spec = self.interp(spec)
        if isinstance(spec, dict):
            if "find" in spec:
                return self.find_one(spec, "finder")
            raise StepFail(f"not an object: {short(spec)}")
        head, *hops = [p.strip() for p in str(spec).split(">")]
        if head == "player":
            head = self.builtin("player")
        elif re.fullmatch(r"[A-Za-z_]\w*", head) and (head in self.vars or head in self.scope):
            head = self.lookup(head)
        return self.hop(head, hops)

    def hop(self, obj, props):
        for prop in props:
            if not isinstance(obj, str) or obj in ("", "None"):
                raise StepFail(f"not an object before >{prop}: {short(obj)}")
            obj = self.get(obj, prop)
        if not isinstance(obj, str) or obj in ("", "None"):
            raise StepFail(f"not an object: {short(obj)}")
        return obj

    def read(self, obj_spec, prop, depth=None):
        """A property through a chain ('a>b>c'); a '[*]' segment maps the rest over a dynamic array."""
        return self.chain(self.obj(obj_spec), [p.strip() for p in str(self.interp(prop)).split(">")], depth)

    def chain(self, obj, segs, depth=None):
        seg, rest = segs[0], segs[1:]
        if "[*]" in seg:
            head, _, tail = seg.partition("[*]")
            items = self.get(obj, head, 2)
            if not isinstance(items, list):
                raise StepFail(f"{head} is not an array: {short(items)}")
            return [self.follow(dig(it, tail.lstrip(".")) if tail else it, rest, depth) for it in items]
        return self.follow(self.get(obj, seg, None if rest else depth), rest, depth)

    def follow(self, value, rest, depth):
        if not rest:
            return value
        if value in (None, "", "None"):
            return None
        if not isinstance(value, str):
            raise StepFail(f"not an object before >{rest[0]}: {short(value)}")
        return self.chain(value, rest, depth)

    def loc(self, spec):
        spec = self.interp(spec)
        if isinstance(spec, (list, tuple)) or isinstance(spec, dict) and "X" in spec:
            return vec(spec)
        return vec(self.get(self.obj(spec), "Location"))

    # -- marks, logs, scanning --

    def flush_mark(self):
        """Log offsets after a forced DebugLogger flush, so no older line can land after them."""
        try:
            self.call("live_console", {"command": FLUSH, "timeoutSeconds": 10})
        except ToolError as e:
            self.note(f"log flush failed ({short(str(e), 80)}): a mark may be early")
        return self.logs.offsets()

    def mark_at(self, since):
        if since not in self.marks:
            raise StepFail(f"no mark {since} yet")
        return self.marks[since]

    def log_entries(self, since, errors=True, regex=None, ignore=None):
        found = []
        for level, text in entries(self.logs.lines(0, self.mark_at(since)[0])[0]):
            if GETALL.search(text) or errors and not is_problem(level, text):
                continue
            if any(re.search(rx, text, re.I) for rx in ignore or []) or regex and not re.search(regex, text, re.I):
                continue
            found.append((level, text))
        return found

    def kismet_lines(self, since):
        return self.logs.lines(1, self.mark_at(since)[1])[0]

    def warn(self, text):
        if self.cur is not None and text not in self.cur["warns"]:
            self.cur["warns"].append(text)
        if text not in self.warns:
            self.warns.append(text)

    def note(self, text):
        if text not in self.notes:
            self.notes.add(text)
            self.warn(text)

    def scan(self):
        """After every step and on every poll: abort on a save load nobody asked for or a dead game; flag saves."""
        if self.logs is None:
            return
        lines, self.scan_at[0] = self.logs.lines(0, self.scan_at[0])
        for line in lines:
            m = SAVE_LOAD.search(line)
            if m and not self.load_window:
                self.load_aborted = True
                raise Aborted(f"a save loaded that the runner did not ask for (Shepard died?): {m.group(1)}")
            m = SAVED.search(line)
            if m and not self.expect_save:
                self.warn(f"autosave or save the runner did not ask for: {m.group(1)}; " +
                          ("restored with the backup" if self.backup else "check Save\\ME1 for a new career folder"))
        lines, self.scan_at[1] = self.logs.lines(1, self.scan_at[1])
        for line in lines:
            m = KISMET.match(line)
            if m and m.group(2) == "BioSeqAct_AutoSave":
                self.warn(f"Kismet activated an autosave: {m.group(3)}")
        if not self.game_down_ok and time.time() - self.alive_t > 5:
            self.alive_t = time.time()
            if not le1_running():
                raise Aborted("the game closed or crashed")

    def sleep(self, seconds):
        end = time.time() + float(seconds)
        while True:
            self.scan()
            left = end - time.time()
            if left <= 0:
                return
            time.sleep(min(1.0, left))

    def fail(self, text, seen=None, expected=None):
        self.rec["seen"], self.rec["expected"] = short(seen), short(expected)
        raise StepFail(text)

    def expect(self, value, expect, what):
        ok, text = check_expect(value, self.interp(expect))
        self.rec["seen"], self.rec["expected"] = short(value), text
        if not ok:
            raise StepFail(f"{what}: {short(value)}, expected {text}")
        return value

    # -- conditions --

    def measure(self, cond):
        src = next(k for k in cond if k in SOURCES)
        a = cond[src]
        if src == "status":
            return dig(self.status(), str(a))
        if src == "get":
            return self.read(a["object"], a["property"], a.get("depth"))
        if src == "call":
            return self.do_call(a).get("return")
        if src == "count":
            return len(self.find_all(a))
        if src == "plot":
            (kind, pid), = a.items()
            return plotval(self.plot_read(kind, [self.interp(pid)])[int(self.interp(pid))])
        if src == "distance":
            return self.distance(a)
        if src == "kismet":
            text = str(self.interp(a["contains"])).casefold()
            return sum(text in line.casefold() for line in self.kismet_lines(a.get("since", "check")))
        if src == "log":
            return len(self.log_entries(a.get("since", "check"), a.get("errors_only", False), self.interp(a["regex"])))
        results = (self.holds(c)[0] for c in a)
        return all(results) if src == "all" else any(results)

    def holds(self, cond):
        """(ok, value, the expectation as text)."""
        v = self.measure(cond)
        if "expect" in cond:
            ok, text = check_expect(v, self.interp(cond["expect"]))
        else:
            ok, text = bool(v) and v not in ("None", "False"), "true"
        return ok, v, text

    def holds_safe(self, cond):
        try:
            return self.holds(cond)[0]
        except (StepFail, ToolError):
            return False

    # -- running --

    def progress_line(self, label, i, n, rec):
        line = (f"{time.strftime('%H:%M:%S')} {label} {i}/{n} {rec['kind']} {rec['name']} {rec['status']} "
                f"({rec['ms'] / 1000:.1f} s) {short(rec['detail'], 160)}").replace("\n", " | ")
        print(line, flush=True)
        if self.progress:
            self.progress.write(line + "\n")
            self.progress.flush()

    def run_list(self, steps, label, records, soft=False):
        """Runs steps (macros inlined); stops at the first failing non-optional one unless soft (cleanup: WARN)."""
        flat = flatten(steps, self.macros)
        for i, (step, scope) in enumerate(flat, 1):
            nxt = flat[i][0] if i < len(flat) else None
            if isinstance(nxt, dict) and "wait_loaded" in nxt:
                self.trigger = self.flush_mark()
            try:
                ok = self.run_step(step, scope, i, len(flat), label, records, soft)
            except SkipCheck:
                if not soft:
                    raise
                continue  # a prompt in cleanup or teardown under --unattended: that step is SKIP, the rest runs
            if not ok and not soft:
                return False
        return True

    def run_step(self, step, scope, i, n, label, records, soft=False):
        kind, a, common = split_step(step)
        self.scope = scope
        rec = self.rec = {"i": i, "kind": kind, "name": common.get("name") or "", "status": "ok", "t": now_iso(),
                          "ms": 0, "args": self.soft(a), "seen": None, "expected": None, "detail": ""}
        records.append(rec)
        self.marks["step"] = self.logs.offsets() if self.logs else (0, 0)
        t0 = time.time()
        try:
            value = getattr(self, "k_" + kind)(a)
            if common.get("save_as"):
                self.vars[common["save_as"]] = value
            self.scan()
        except GuardError as e:
            rec["status"], rec["detail"] = "FAIL", f"refused: {e}"
        except (StepFail, ToolError) as e:
            # A screenshot is evidence, not a check: a minimized window must not stop the steps after it.
            lenient = common.get("optional") or soft or kind == "screenshot"
            rec["status"], rec["detail"] = "WARN" if lenient else "FAIL", str(e)
        except (Aborted, SkipCheck, KeyboardInterrupt) as e:
            rec["status"], rec["detail"] = "SKIP" if isinstance(e, SkipCheck) else "ABORTED", str(e) or "Ctrl-C"
            raise
        except (SuiteError, RestorePending):
            raise
        except Exception as e:  # the runner's own bug: ERROR, never a crash of the whole run
            rec["status"], rec["detail"] = "ERROR", f"runner error: {type(e).__name__}: {e}"
        finally:
            rec["ms"] = int((time.time() - t0) * 1000)
            self.progress_line(label, i, n, rec)
            self.scope = {}
        if rec["status"] == "WARN":
            self.warn(f"{label} step {i} {kind}: {short(rec['detail'], 120)}")
        return rec["status"] in ("ok", "WARN")

    def run_check(self, f, c):
        res = self.cur = {"id": c["id"], "title": c.get("title", ""), "row": c.get("row", ""), "status": "PASS",
                          "reason": "", "warns": [], "duration": 0, "steps": [], "cleanup": [], "shots": []}
        self.check_id, self.review, self.trigger = c["id"], False, None
        if c.get("skip"):
            res.update(status="SKIP", reason=str(c["skip"]))
            return res
        if "user" in (c.get("needs") or []) and self.unattended:
            res.update(status="SKIP", reason="needs a person")
            return res
        t0, label = time.time(), f"{f['id']}/{c['id']}"
        self.marks["check"] = self.flush_mark()
        try:
            if not self.run_list(c.get("steps"), label, res["steps"]):
                bad = next(r for r in res["steps"] if r["status"] in ("FAIL", "ERROR"))
                res.update(status=bad["status"], reason=f"step {bad['i']} {bad['kind']}: {bad['detail']}")
            elif self.review:
                res["status"] = "REVIEW"
        except SkipCheck as e:
            res.update(status="SKIP", reason=str(e))
        except (Aborted, KeyboardInterrupt) as e:
            res.update(status="ABORTED", reason=str(e) or "Ctrl-C", duration=round(time.time() - t0, 1))
            raise
        if c.get("cleanup"):
            self.run_list(c["cleanup"], label + ":cleanup", res["cleanup"], soft=True)
        res["duration"] = round(time.time() - t0, 1)
        return res

    def run_file(self, f, checks):
        self.file, doc = f, f["doc"]
        self.vars, self.macros, self.snapshots = dict(f["vars"]), f["macros"], {}
        fres = self.cur = {"id": f["id"], "title": doc["title"], "status": "PASS", "skip": doc.get("skip"),
                           "setup": [], "teardown": [], "checks": [], "warns": [], "shots": []}
        if doc.get("skip"):
            fres["status"] = "SKIP"
            fres["checks"] = [{"id": c["id"], "title": c.get("title", ""), "row": c.get("row", ""), "status": "SKIP",
                               "reason": str(doc["skip"]), "warns": [], "duration": 0, "steps": [], "shots": []}
                              for c in checks]
            return fres
        self.marks = {"file": self.flush_mark()}
        self.check_id, restore = "setup", []

        def none_ran(status, reason):
            fres["checks"] = [{"id": c["id"], "title": c.get("title", ""), "row": c.get("row", ""), "status": status,
                               "reason": reason, "warns": [], "duration": 0, "steps": [], "shots": []} for c in checks]
        try:
            try:
                ready = self.run_list(doc.get("setup"), f"{f['id']}/setup", fres["setup"])
            except SkipCheck as e:  # a prompt in setup under --unattended
                ready = None
                none_ran("SKIP", f"setup: {e}")
            if ready is False:
                bad = next(r for r in fres["setup"] if r["status"] in ("FAIL", "ERROR"))
                none_ran("ERROR", f"setup step {bad['i']} {bad['kind']}: {bad['detail']}")
            elif ready:
                try:
                    self.read_restore(doc.get("restore") or {}, f["id"], restore)
                except (StepFail, ToolError) as e:
                    none_ran("ERROR", f"no check ran: the restore plot values could not be read ({e})")
                else:
                    for c in checks:
                        fres["checks"].append(self.run_check(f, c))
            self.cur, self.check_id = fres, "teardown"
            self.run_list(doc.get("teardown"), f"{f['id']}/teardown", fres["teardown"], soft=True)
        except BaseException as e:
            if self.cur is not fres and self.cur not in fres["checks"]:
                if self.cur.get("status") == "PASS" and not isinstance(e, (Aborted, KeyboardInterrupt)):
                    self.cur.update(status="ERROR", reason=f"stopped: {type(e).__name__}: {e}")
                fres["checks"].append(self.cur)
            raise
        finally:
            self.cur = fres
            self.write_restore(restore)
            try:
                self.copy_slices(f["id"])
            except OSError as e:
                self.warn(f"log slices of {f['id']} not copied: {e}")
            order = ["ABORTED", "ERROR", "FAIL", "REVIEW", "PASS", "SKIP"]
            fres["status"] = min((c["status"] for c in fres["checks"]), key=order.index, default="SKIP")
        return fres

    def read_restore(self, restore, fid, out):
        """Appends {file, kind, id, value} to out for each plot id in restore."""
        for kind, ids in restore.items():
            if ids:
                out += [{"file": fid, "kind": kind, "id": i, "value": v} for i, v in self.plot_read(kind, ids).items()]
        return out

    def write_restore(self, values):
        """Writes the restore plot values back, unless an unexpected load replaced them or the game is gone. Never
        raises: it runs in a finally."""
        for r in values:
            try:
                if self.load_aborted or self.game_closed or not le1_running():
                    r["restored"] = "skipped: " + ("an unexpected load replaced the plot table" if self.load_aborted
                                                   else "the game is not running")
                else:
                    self.call("game_plot", {"kind": r["kind"], "ids": [r["id"]], "value": str(r["value"])})
                    r["restored"] = "ok"
            except Exception as e:
                r["restored"] = f"failed: {type(e).__name__}: {e}"
            self.saves.setdefault("plot_restore", []).append(r)

    def copy_slices(self, fid):
        if not (self.logs and self.run_dir and "file" in self.marks):
            return
        os.makedirs(os.path.join(self.run_dir, "logs"), exist_ok=True)
        for which, name in ((0, "DebugLogger"), (1, "KismetLogger")):
            lines = self.logs.lines(which, self.marks["file"][which])[0]
            with open(os.path.join(self.run_dir, "logs", f"{fid}.{name}.log"), "w", encoding="utf-8") as out:
                out.write("\n".join(lines) + ("\n" if lines else ""))

    def backup_now(self, career):
        """backup_saves at the start of a run with a file that may save; PENDING marks it until it is restored. A
        PENDING backup of the same career is reused: it still holds the saves from before any run touched them, and a
        new backup would copy the saves the earlier run changed."""
        pending = os.path.join(self.suite_root, "save-backup", "PENDING")
        if not career:
            raise SuiteError("a file with saves: true needs career:")
        self.saves_root = os.path.join(documents(), "BioWare", "Mass Effect Legendary Edition", "Save", "ME1")
        if os.path.exists(pending):
            with open(pending, encoding="utf-8") as f:
                dest = to_wsl(f.read().strip())
            with open(os.path.join(dest, "manifest.json"), encoding="utf-8") as f:
                man = json.load(f)
            if str(man.get("career", "")).lower() != str(career).lower():
                raise RestorePending(f"the pending backup is of career {man.get('career')}, not {career}: restore it "
                                     f"first, game closed: {restore_command(pending)}")
            self.backup = dest
            self.saves.update(backup=to_win(dest), files=len(man["files"]), restored=None, reused=True)
            print(f"saves: reusing the pending backup {to_win(dest)} (taken before the first run)", flush=True)
            return
        dest = os.path.join(self.suite_root, "save-backup", self.stamp)
        assert_writable(dest, self.install)
        assert_writable(self.saves_root, self.install)
        man = backup_saves(self.saves_root, career, dest)
        with open(pending, "w", encoding="utf-8") as f:
            f.write(to_win(dest) + "\n")
        self.backup = dest
        self.saves.update(backup=to_win(dest), files=len(man["files"]), restored=None)
        print(f"saves backed up to {to_win(dest)} ({len(man['files'])} files)", flush=True)

    def restore_now(self):
        """restore_saves of this run's backup (game closed); PENDING goes once every file checks out."""
        result = restore_saves(self.backup)
        self.saves.update(restored=result)
        for w in result["warns"]:
            self.warn(w)
        pending = os.path.join(self.suite_root, "save-backup", "PENDING")
        if result["ok"] and os.path.exists(pending):
            os.remove(pending)
        return result

    # -- step kinds --

    def k_use(self, a):  # flatten() inlines macros; a use reaching here has no macro
        raise StepFail(f"unknown macro {a.get('macro')}")

    def k_console(self, a):
        cmd = str(self.interp(a["command"]))
        console_guard(cmd)
        lines = self.call("live_console", {"command": cmd, "timeoutSeconds": a.get("timeout", 10)}).get("lines") or []
        for rx in a.get("expect_lines") or []:
            if not any(re.search(rx, line, re.I) for line in lines):
                self.fail(f"no line matches {rx}", lines[-5:], rx)
        for rx in a.get("forbid_lines") or []:
            bad = [line for line in lines if re.search(rx, line, re.I)]
            if bad:
                self.fail(f"a line matches {rx}: {bad[0]}", bad[:5], f"no {rx}")
        self.rec["detail"] = " | ".join(lines[-3:])
        return lines

    def trigger_load(self, cmd, window):
        """flush_mark, then the load command through the bridge console; a reply lost to the load is fine."""
        mark = self.flush_mark()
        self.load_window = self.load_window or window
        try:
            self.call("live_console", {"command": cmd, "timeoutSeconds": 10})
        except ToolError as e:
            if not LOADING.search(str(e)):
                raise
            self.rec["detail"] = "the console's answer was lost to the load"
        return mark

    def career(self):
        career = self.file["doc"].get("career")
        if not career:
            raise GuardError("the file has no career")
        return career

    def k_load(self, a):
        if "slot" in a:
            cmd = f"BioLoadGame {int(self.interp(a['slot']))}"
        else:
            name, career = str(self.interp(a["save"])), self.career()
            if not name.casefold().startswith(career.casefold() + "_"):
                raise GuardError(f"save {name} is not in career {career}")
            cmd = f"nextframedefer loadgame {name} DevId=-1"
        mark = self.trigger_load(cmd, True)
        return self.wait_loaded({"map": a["map"], "timeout": a.get("timeout", 240)}, mark)

    def career_guard(self):
        """CurrentGame's m_sCharacterID must be the file's career, and this run must hold a backup."""
        career = self.career()
        if not self.backup:
            raise GuardError("no save backup in this run")
        game = self.get(self.builtin("world"), "CurrentGame")
        cid = self.get(game, "m_sCharacterID") if game else None
        if str(cid or "").casefold() != career.casefold():
            raise GuardError(f"career is '{cid or ''}', not {career}")
        return game

    def k_quickload(self, a):
        self.career_guard()
        mark = self.trigger_load("QuickLoad", True)
        return self.wait_loaded({"map": a["map"], "timeout": a.get("timeout", 240)}, mark)

    def k_at(self, a):
        where, start = self.interp(a["map"]), self.interp(a["start"])
        console_guard(f"at {where} {start}")
        mark = self.trigger_load(f"at {where} {start}", False)
        return self.wait_loaded({"map": where, "allow_party": a.get("allow_party", False),
                                 "timeout": a.get("timeout", 240)}, mark)

    def k_wait_loaded(self, a):
        mark, self.trigger = self.trigger or self.marks["step"], None
        return self.wait_loaded(a, mark)

    def wait_load_start(self, mark, want, until):
        while True:
            for line in self.logs.lines(0, mark[0])[0]:
                m = LOAD_FAIL.search(line)
                if m:
                    self.fail(f"the game says: {m.group(1)}", m.group(1), "a load")
                m = SYNC_LOAD.search(line)
                if m and (m.group(1).casefold() == str(want).casefold() if want else
                          m.group(1).upper().startswith("BIOA_") and not re.search(r"_LOC(_\w*)?$", m.group(1), re.I)):
                    return m.group(1)
            if time.time() >= until:
                return None
            self.sleep(0.5)

    def wait_loaded(self, a, mark):
        """The load started (its log line), then live_status is ok on two polls, settle, and once more."""
        want, expect = self.interp(a.get("map")), a.get("expect_load", True)
        allow, settle = a.get("allow_party", False), float(a.get("settle", 3))
        timeout = float(a.get("timeout", 240))
        deadline = time.time() + timeout
        if expect is not False:
            until = deadline if expect is True else min(deadline, time.time() + float(a.get("load_grace", 10)))
            started = self.wait_load_start(mark, want, until)
            if started is None and expect is True:
                self.fail(f"no 'Loading package synchronously: {want or 'BIOA_*'}' line after the trigger", None,
                          want or "a map load")
            want = want or started  # the map whose load started: the old map does not count as loaded
        streak, settled, last, why, revived, unknown = 0, False, None, [], False, False
        while True:
            if expect is not False and not want:  # a travel that starts after load_grace
                want = self.wait_load_start(mark, None, 0)
                if want:
                    streak, settled = 0, False
            try:
                st = self.call("live_status", {"timeoutSeconds": 2})
                last, why = st, load_problems(st, want, allow)
                if st.get("loadingMovie") is None:
                    unknown = True
                    self.note("loading movie unknown (no SFXLoadMovieManager): 5 s added to the settle of such waits")
                if st.get("keepTicking") is False and not revived:
                    self.keep_ticking_on(10)
                    revived = True
                    self.note("keep_ticking was off during a wait; turned on again")
            except ToolError as e:
                if "unknown op" in str(e):
                    raise SuiteError(BRIDGE3)
                st, why = None, [f"loading ({short(str(e), 80)})"]
            if not why:
                streak += 1
                if settled or streak >= 2 and allow and st.get("partySelectOpen"):
                    break
                if streak >= 2:
                    self.sleep(settle + (5 if unknown else 0))
                    settled = True
                    continue
            else:
                streak, settled = 0, False
            if time.time() >= deadline:
                self.fail(f"not loaded after {timeout:g} s: {'; '.join(why)}",
                          {k: last.get(k) for k in ("map", "begunPlay", "pawn", "streaming", "loadingMovie",
                                                    "partySelectOpen", "bPlayersOnly")} if last else None, "loaded")
            self.sleep(1)
        self.marks["load"], self.load_window = mark, False
        self.cache.clear()
        self.st, self.st_t = st, time.time()
        self.rec["detail"] = f"{st.get('map')} loaded"
        return st

    def plot_read(self, kind, ids):
        r = self.call("game_plot", {"kind": kind, "ids": [int(i) for i in ids]})
        vals = {int(v["id"]): v["value"] for v in r.get("values") or []}
        missing = [i for i in ids if int(i) not in vals]
        if missing:
            raise StepFail(f"game_plot did not read {kind} {missing}: {r.get('note') or ''}")
        return vals

    def k_plot(self, a):
        for kind, items in a.items():
            for pid, value in (items or {}).items():
                value = self.interp(value)
                text = ("True" if value else "False") if isinstance(value, bool) else str(value)
                self.call("game_plot", {"kind": kind, "ids": [int(pid)], "value": text})
                back = self.plot_read(kind, [pid])[int(pid)]
                if not equal(back, value):
                    self.fail(f"{kind} {pid} reads {back} after the set (post-load Kismet?)", back, value)
        self.rec["detail"] = short(a)

    def k_assert_plot(self, a):
        seen = {}
        for kind, items in a.items():
            vals = self.plot_read(kind, list(items))
            for pid, want in items.items():
                seen[f"{kind} {pid}"] = vals[int(pid)]
                ok, text = check_expect(vals[int(pid)], self.interp(want))
                if not ok:
                    self.fail(f"{kind} {pid} is {vals[int(pid)]}, expected {text}", seen, a)
        self.rec["seen"] = short(seen)
        return seen

    def k_assert(self, a):
        ok, v, text = self.holds(a)
        self.rec["seen"], self.rec["expected"] = short(v), text
        if not ok:
            raise StepFail(f"{short(v)}, expected {text}")
        return v

    def k_wait_until(self, a):
        cond = {k: v for k, v in a.items() if k not in ("timeout", "stable", "poll")}
        timeout, stable, poll = float(a.get("timeout", 30)), float(a.get("stable", 0)), float(a.get("poll", 1))
        deadline, since, v, text = time.time() + timeout, None, None, ""
        while True:
            try:
                ok, v, text = self.holds(cond)
            except (StepFail, ToolError) as e:
                ok, v = False, f"error: {e}"
            now = time.time()
            since = (since or now) if ok else None
            if ok and now - since >= stable:
                self.rec["seen"], self.rec["expected"] = short(v), text
                return v
            if now >= deadline:
                self.fail(f"not true within {timeout:g} s (last {short(v, 100)}, expected {text})", v, text)
            self.sleep(poll)

    def k_sleep(self, a):
        self.sleep(float(self.interp(a["seconds"])))

    def k_mark(self, a):
        self.marks["mark:" + str(a["name"])] = self.flush_mark()

    def k_live_get(self, a):
        v = self.read(a["object"], a["property"], a.get("depth"))
        if "expect" in a:
            return self.expect(v, a["expect"], a["property"])
        self.rec["seen"] = short(v)
        return v

    def k_live_set(self, a):
        hops = [p.strip() for p in str(self.interp(a["property"])).split(">")]
        obj, value = self.hop(self.obj(a["object"]), hops[:-1]), self.interp(a["value"])
        back = self.call("live_set", {"objectPath": obj, "property": hops[-1], "value": value}).get("value")
        if not equal(back, value):
            self.fail(f"{hops[-1]} reads {short(back)} after the set", back, value)
        self.rec["detail"] = f"{obj}.{hops[-1]} = {short(back)}"

    def do_call(self, a):
        fn = str(self.interp(a["function"]))
        function_guard(fn)
        return self.call("live_call", {"objectPath": self.obj(a["object"]), "function": fn,
                                       "args": self.interp(a.get("args") or []), "timeoutSeconds": a.get("timeout", 5)})

    def k_live_call(self, a):
        r = self.do_call(a)
        if "expect" in a:
            self.expect(r.get("return"), a["expect"], f"{a['function']} returned")
        self.rec["detail"] = short(r)
        return r

    def k_count(self, a):
        paths = self.find_all(a["find"])
        if "expect" in a:
            self.expect(len(paths), a["expect"], "matching live objects")
        self.rec["detail"] = short(paths)
        return paths

    def distance(self, a):
        p, q = self.loc(a["a"]), self.loc(a["b"])
        d = [p[i] - q[i] for i in range(3)]
        axis = a.get("axis", "xyz")
        return abs(d[2]) if axis == "z" else math.hypot(d[0], d[1]) if axis == "xy" else math.sqrt(sum(x * x for x in d))

    def k_distance(self, a):
        v = round(self.distance(a), 1)
        return self.expect(v, a["expect"], "distance") if "expect" in a else v

    def k_teleport(self, a):
        who = self.obj(a.get("object", "player"))
        if "ahead" in a:
            x, y, z = vec(self.get(who, "Location"))
            yaw = float((self.get(who, "Rotation") or {}).get("Yaw", 0)) * 2 * math.pi / 65536
            d = float(self.interp(a["ahead"]))
            tries = [(x + math.cos(yaw) * d * k, y + math.sin(yaw) * d * k, z) for k in (1, 0.75, 0.5)]
        else:
            base = vec(self.interp(a["to"])) if "to" in a else \
                tuple(p + o for p, o in zip(self.loc(a["near"]), vec(self.interp(a.get("offset", [0, 0, 0])))))
            tries = [(base[0], base[1], base[2] + 50 * k) for k in range(3)]
        for x, y, z in tries:
            r = self.call("live_call", {"objectPath": who, "function": "SetLocation",
                                        "args": [{"X": x, "Y": y, "Z": z}]})
            if r.get("return") is True:
                self.rec["detail"] = f"{who} at {x:.0f} {y:.0f} {z:.0f}"
                return [x, y, z]
        self.fail("SetLocation returned false at every try", tries, True)

    def k_raise_event(self, a):
        if a.get("object"):
            events = [self.obj(a["object"])]
        else:
            name = str(self.interp(a["name"]))
            found = self.call("live_find", {"className": a.get("class", "SeqEvent_RemoteEvent"),
                                            "path": self.interp(a.get("path_contains", "${map}.TheWorld")),
                                            "limit": 500}).get("objects") or []
            events = []
            for o in found:
                try:
                    if equal(self.get(o["path"], "EventName"), name):
                        events.append(o["path"])
                except ToolError:
                    pass
        if a.get("pick", "one") == "one" and len(events) != 1:
            self.fail(f"{len(events)} events match (pick: one needs exactly 1)", events[:5], 1)
        orig, inst = self.interp(a.get("originator", "${world}")), self.interp(a.get("instigator", "${player}"))
        for e in events:
            r = self.call("live_call", {"objectPath": e, "function": "CheckActivate", "args": [orig, inst, False]})
            if r.get("return") is not True:
                self.fail(f"CheckActivate returned {r.get('return')} on {e}", r.get("return"), True)
        self.rec["detail"] = ", ".join(events)
        return events

    def wait_status(self, test, timeout, failure):
        """The first live_status reply (polled every 0.5 s) that passes test; else FAIL with failure."""
        deadline = time.time() + timeout
        while True:
            try:
                st = self.status()
                if test(st):
                    return st
            except ToolError:
                st = None
            if time.time() >= deadline:
                self.fail(f"{failure} (after {timeout:g} s)", st and {k: st.get(k) for k in (
                    "partySelectOpen", "partySelect", "bPlayersOnly")}, failure)
            self.sleep(0.5)

    def k_squad_pick(self, a):
        """Opens the squad screen if needed, waits for its init, checks the picks as the movie would, sets them and
        calls FinalizeParty, as a click on Accept does."""
        st, timeout = self.status(), float(a.get("timeout", 30))
        if st.get("partySelectOpen") is not True:
            if a.get("open", "console") == "console":
                self.call("live_console", {"command": "ce Hench_PickSquad", "timeoutSeconds": 10})
            elif a.get("open") == "event":
                self.k_raise_event(a["event"])
            else:
                self.fail("the squad screen is not open (open: none)", False, True)
        st = self.wait_status(lambda s: s.get("partySelectOpen") is True, min(20, timeout), "the squad screen did not open")
        # init: membersAvailable > 0 on two polls 0.5 s apart; no such variable for 2 s: 'init unverified'
        opened, seen, init = time.time(), [], "verified"
        while True:
            ps = st.get("partySelect") or {}
            seen.append(ps.get("membersAvailable"))
            if len(seen) > 1 and all(isinstance(x, int) and x > 0 for x in seen[-2:]):
                break
            if ps.get("varPath") is None and time.time() - opened >= 2:
                init = "unverified"
                self.note("squad screen init unverified (no nMembersAvailable in the movie): waited 2 s")
                break
            if time.time() - opened > 15:
                self.fail("the squad screen did not initialise (membersAvailable stayed 0)", seen[-3:], "> 0")
            self.sleep(0.5)
            st = self.wait_status(lambda s: s.get("partySelectOpen") is True, 5, "the squad screen closed by itself")
        handler = ps.get("handler")
        if handler and "PartySelection" not in str(ps.get("handlerClass") or "PartySelection"):
            self.fail(f"the panel's handler is a {ps.get('handlerClass')}", ps.get("handlerClass"),
                      "a BioSFHandler_PartySelection")
        if not handler:
            for o in self.call("live_find", {"className": "BioSFHandler_PartySelection", "limit": 50}).get("objects") or []:
                panel = self.get(o["path"], "oPanel")
                if panel not in (None, "None") and (not ps.get("panel") or equal(panel, ps["panel"])):
                    handler = o["path"]
        if not handler:
            self.fail("no BioSFHandler_PartySelection for the open panel", ps, "a handler")
        cards = self.get(handler, "lstMemberInfo", 2) or []
        forced = self.get(handler, "nmForcedHenchman")
        forced = None if forced in (None, "", "None") else forced
        picks = []
        for m in self.interp(a["members"]):
            i = m if isinstance(m, int) else next((k for k, c in enumerate(cards) if equal(c.get("Tag"), m)), None)
            if i is None or not 0 <= i < len(cards):
                self.fail(f"no card {m} on the squad screen", [c.get("Tag") for c in cards], m)
            picks.append(i)
        gvt = self.builtin("gvt")

        def flag(label):
            return self.call("live_call", {"objectPath": gvt, "function": "GetBoolByName", "args": [label]}).get("return")
        available = [k for k, c in enumerate(cards) if equal(c.get("Tag"), forced) or flag(c.get("AvailableLabel")) is True]
        if len(picks) != min(2, len(available)) or len(set(picks)) != len(picks):
            self.fail(f"pick {min(2, len(available))} different members", picks, available)
        if any(i not in available for i in picks):
            self.fail("a pick is not available", picks, available)
        if forced and not any(equal(cards[i].get("Tag"), forced) for i in picks):
            self.fail(f"the forced member {forced} is not picked", [cards[i].get("Tag") for i in picks], forced)
        mark = self.flush_mark()
        self.call("live_set", {"objectPath": handler, "property": "m_nPartyMember1", "value": picks[0]})
        self.call("live_set", {"objectPath": handler, "property": "m_nPartyMember2",
                               "value": picks[1] if len(picks) > 1 else -1})
        self.call("live_call", {"objectPath": handler, "function": "FinalizeParty", "args": [True, 0, False]})
        self.wait_status(lambda s: s.get("partySelectOpen") is False, 10, "panel still open")
        for i in picks:
            if flag(cards[i].get("InPartyLabel")) is not True:
                self.fail(f"{cards[i].get('InPartyLabel')} is not true after the pick", False, True)
        if a.get("expect_spawn", True):
            self.wait_loaded({"expect_load": "auto", "map": self.st.get("map"), "settle": 3}, mark)
        self.rec["detail"] = f"picked {[cards[i].get('Tag') for i in picks]} on {handler} (init {init})"
        return {"handler": handler, "indexes": picks, "forced": forced, "init": init}

    def k_snapshot(self, a):
        values = {p: self.read(a["object"], p) for p in a["props"]}
        self.snapshots[a["name"]] = {"object": a["object"], "values": values}
        self.rec["seen"] = short(values)
        return values

    def k_compare(self, a):
        tol, ignore = float(a.get("tol", 1e-4)), a.get("ignore") or []
        if "snapshot" in a:
            snap = self.snapshots.get(a["snapshot"]) or self.fail(f"no snapshot {a['snapshot']} in this file")
            pairs = [(p, v, self.read(snap["object"], p)) for p, v in snap["values"].items() if p not in ignore]
        else:
            x, y = a["objects"]
            pairs = [(p, self.read(x, p), self.read(y, p)) for p in a["props"] if p not in ignore]
        diffs = [f"{p}: {short(u, 60)} vs {short(v, 60)}" for p, u, v in pairs if not equal(u, v, tol)]
        if diffs:
            self.fail("; ".join(diffs), [d for d in diffs], "equal")
        self.rec["detail"] = f"{len(pairs)} equal"

    def k_quicksave(self, a):
        """QuickSave in the career; done when the file is rewritten and stable, or the game says the save finished."""
        game, career = self.career_guard(), self.career()
        name = f"{career}_QuickSave"
        path = os.path.join(self.saves_root, career, name + ".pcsav")
        before, mark = file_stat(path), self.flush_mark()
        self.expect_save = name
        try:
            self.call("live_console", {"command": "QuickSave", "timeoutSeconds": 10})
            deadline, saw, held, held_t = time.time() + float(a.get("timeout", 60)), False, None, 0
            saving = False  # m_bSavingGame seen true since the command: the flags then belong to this save
            while True:
                for line in self.logs.lines(0, mark[0])[0]:
                    m = CANT_SAVE.search(line)
                    if m:
                        self.fail(f"the game says: {m.group(1)}", m.group(1), "a save")
                    m = SAVED.search(line)
                    saw = saw or bool(m and m.group(1).casefold() == name.casefold())
                now, stat = time.time(), file_stat(path)
                if stat and stat != before:
                    if stat == held and now - held_t >= 1:
                        break
                    if stat != held:
                        held, held_t = stat, now
                try:
                    busy = self.get(game, "m_bSavingGame")
                    saving = saving or busy is True
                    if saw and saving and busy is False and self.get(game, "m_bLastSaveSuccess") is True:
                        break
                except ToolError:
                    pass
                if now >= deadline:
                    self.fail("no save happened (a MessageBox open makes QuickSave return silently)" if not saw
                              and not held else "the save did not finish", {"line": saw, "file": held}, name)
                self.sleep(0.5)
            self.sleep(3)  # the profile write follows the save
            self.status()
            if not le1_running():
                raise Aborted("the game closed after the save")
        finally:
            self.expect_save = None
        self.rec["detail"] = f"{name} saved"

    def k_screenshot(self, a):
        self.shot_n += 1
        name = re.sub(r"[^\w.-]+", "_", f"{self.shot_n:02d}-{self.file['id']}-{self.check_id}-{self.interp(a['name'])}")
        os.makedirs(os.path.join(self.run_dir, "shots"), exist_ok=True)
        self.call("game_screenshot", {"path": f"{self.run_win}\\shots\\{name}.png", "maxWidth": a.get("max_width", 1280)})
        self.cur["shots"].append(f"shots/{name}.png")
        self.review = self.review or bool(a.get("review"))
        self.rec["detail"] = f"shots/{name}.png"

    def k_assert_log(self, a):
        found = self.log_entries(a.get("since", "check"), a.get("errors", True), self.interp(a.get("regex")),
                                 a.get("ignore"))
        self.rec["detail"] = "\n".join(f"[{lv}] {t.splitlines()[0]}" for lv, t in found[:12])
        self.expect(len(found), a.get("expect", {"max": 0}), "log entries")
        return sorted({normalise(t) for _, t in found})

    def k_compare_log(self, a):
        base = set(self.lookup(a["with"]) or [])
        now = {normalise(t) for _, t in self.log_entries(a.get("since", "load"), True, None, a.get("ignore"))}
        extra, missing = sorted(now - base), sorted(base - now)
        self.rec["detail"] = f"{len(now)} now, {len(base)} before"
        if extra or a.get("mode") == "same" and missing:
            self.fail(f"{len(extra)} new, {len(missing)} gone: {short(extra or missing, 200)}", extra, missing)

    def k_assert_kismet(self, a):
        lines = self.kismet_lines(a.get("since", "check"))
        if "order" in a:
            pos = 0
            for text in a["order"]:
                t = str(self.interp(text)).casefold()
                at = next((i for i in range(pos, len(lines)) if t in lines[i].casefold()), None)
                if at is None:
                    self.fail(f"'{text}' not found after the previous one", None, a["order"])
                pos = at + 1
            return len(lines)
        t = str(self.interp(a["contains"])).casefold()
        return self.expect(sum(t in line.casefold() for line in lines), a.get("expect", {"min": 1}),
                           f"Kismet lines with '{a['contains']}'")

    def k_prompt(self, a):
        until = a.get("until")
        if until is not None and self.holds_safe(until):
            self.rec["detail"] = "already true: no box"
            return
        if self.unattended:
            raise SkipCheck("needs a person")
        text = str(self.interp(a["text"]))
        env = dict(os.environ, SCN_TEXT=text)
        env["WSLENV"] = (env.get("WSLENV", "") + ":SCN_TEXT").strip(":")
        box = subprocess.Popen(["powershell.exe", "-NoProfile", "-Command", "Add-Type -AssemblyName PresentationFramework; "
                                "[void][System.Windows.MessageBox]::Show($env:SCN_TEXT,'scenario.py')"],
                               env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        timeout = float(a.get("timeout", 300))
        deadline = time.time() + timeout
        try:
            while time.time() < deadline:
                if until is not None and self.holds_safe(until) or until is None and box.poll() is not None:
                    return
                self.sleep(1)
            self.fail(f"not done within {timeout:g} s: {text}", None, "done")
        finally:
            if box.poll() is None:
                box.kill()

    def k_watch(self, a):
        r = self.call("live_watch", {"functions": self.interp(a["functions"]), "seconds": a.get("seconds", 10),
                                     "objectContains": self.interp(a.get("object_contains"))})
        events = r.get("events") or []
        self.expect(len(events), a.get("expect", {"min": 1}), "watched calls")
        return events

    def keep_ticking_on(self, within=120):
        end = time.time() + within
        while True:
            try:
                return self.call("live_keep_ticking", {"on": True, "timeoutSeconds": 5})
            except ToolError as e:
                if "unknown op" in str(e):
                    raise SuiteError(BRIDGE3)
                if time.time() > end:
                    raise
                time.sleep(2)

    def k_keep_ticking(self, a):
        on = a["on"] if isinstance(a["on"], bool) else str(a["on"]).lower() == "on"
        return self.keep_ticking_on(30) if on else self.call("live_keep_ticking", {"on": False})

    def k_game_cycle(self, a):
        """Closes and boots the game through M3 with no mod, then runs commands at the main menu."""
        career = self.file["doc"].get("career")
        cmds = [str(c) for c in self.interp(a.get("commands") or [])]
        for c in cmds:
            console_guard(c, career)
        self.load_window = self.game_down_ok = True
        try:
            timeout = int(a.get("timeout", 300))
            r = self.call("game_cycle", {"commands": cmds, "waitForMap": self.interp(a.get("wait_for_map")),
                                         "settleSeconds": a.get("settle", 8), "screenshot": False,
                                         "timeoutSeconds": timeout})
            self.rec["detail"] = " | ".join((r.get("steps") or [])[-3:])
            if not r.get("ready"):
                self.fail("the game did not come back: " + "; ".join((r.get("steps") or [])[-2:]), r.get("map"), "ready")
            self.scan_at, self.trigger = [0, 0], (0, 0)
            self.marks = {k: (0, 0) for k in self.marks}
            self.st, self.cache = None, {}
            self.keep_ticking_on(120)
        finally:
            self.game_down_ok = False

    def k_close_game(self, a):
        """The only place 'exit' is sent: keep_ticking off, console exit, wait until LE1 is gone."""
        if not self.file["doc"].get("cold_start"):
            raise GuardError("close_game runs only in a cold_start file")
        self.game_down_ok = True
        for tool, args in (("live_keep_ticking", {"on": False}), ("live_console", {"command": "exit", "timeoutSeconds": 5})):
            try:
                self.call(tool, args)
            except ToolError:
                pass
        deadline, killed = time.time() + float(a.get("timeout", 60)), False
        while le1_running():
            if time.time() > deadline:
                if not a.get("force") or killed:
                    self.fail("the game did not close", "running", "closed")
                subprocess.run(["taskkill.exe", "/IM", "MassEffect1.exe", "/F"], stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL)
                deadline, killed = time.time() + 15, True
            time.sleep(1)
        self.game_closed = True
        self.rec["detail"] = "killed" if killed else "closed"

    def k_backup_saves(self, a):
        if not self.backup:
            self.backup_now(self.career())

    def k_restore_saves(self, a):
        if not self.backup:
            raise StepFail("no backup in this run")
        r = self.restore_now()
        self.rec["detail"] = f"{len(r['restored'])} restored, {len(r['displaced'])} displaced"
        if not r["ok"]:
            self.fail(f"restored files differ: {r['mismatch']}", r["mismatch"], [])

    def k_status(self, a):
        st = self.status()
        self.rec["detail"] = short({k: st.get(k) for k in ("map", "pawn", "bPlayersOnly", "keepTicking",
                                                           "partySelectOpen")})
        return st

    # -- the run --

    def go(self, selected, args, s):
        """The whole run: prerequisites, keep_ticking, files, then the finally part (docs/scenarios.md)."""
        if not le1_running():
            raise SuiteError("LE1 is not running: start it and load anything (the main menu is fine)")
        first = selected[0][0]["doc"]
        self.suite, self.game = first["suite"], first.get("game") or GAME
        saving = [f["doc"] for f, _ in selected if f["doc"].get("saves") and not f["doc"].get("skip")]
        if len({d.get("career") for d in saving}) > 1:
            raise SuiteError("every file with saves: true of one run must name the same career")
        root = args.out or first.get("out") or s.get("MELE_SCENARIO_OUT") or \
            ntpath.join(ntpath.dirname(default_out()), "scenarios")
        self.suite_root = os.path.join(to_wsl(root), self.suite)
        buffered, self.calls_file = [], None

        def record(entry):
            if self.calls_file:
                self.calls_file.write(json.dumps(entry, default=str) + "\n")
                self.calls_file.flush()
            else:
                buffered.append(entry)
        self.s = Session(s, record)
        results, status, prereq = [], "PASS", None
        try:
            missing = [t for t in REQUIRED if t not in self.s.tools]
            if missing:
                raise SuiteError(f"the server has no {', '.join(missing)}: deploy mele-mcp first "
                                 "(tools/mele-mcp/scripts/deploy.sh)")
            le = self.call("le_status")
            self.install = le.get("legendaryEditionDir")
            self.logs = Logs(le.get("le1GamePath") or "")
            self.run_dir = os.path.join(self.suite_root, self.stamp)
            self.run_win = to_win(self.run_dir)
            assert_writable(self.run_dir, self.install)
            os.makedirs(self.run_dir, exist_ok=True)
            self.progress = open(os.path.join(self.run_dir, "progress.log"), "a", encoding="utf-8")
            self.calls_file = open(os.path.join(self.run_dir, "calls.jsonl"), "a", encoding="utf-8")
            for e in buffered:
                record(e)
            print(f"run folder: {self.run_win}", flush=True)
            self.scan_at = list(self.logs.offsets())
            end = time.time() + 120
            while True:
                try:
                    self.status()
                    break
                except ToolError as e:
                    if "unknown op" in str(e):
                        raise SuiteError(BRIDGE3)
                    if time.time() > end:
                        raise SuiteError(f"live_status did not answer within 120 s: {e}")
                    time.sleep(2)
            self.keep_ticking_on(120)
            t1 = self.status().get("worldTicks") or 0
            time.sleep(1.5)
            if (self.status().get("worldTicks") or 0) <= t1:
                raise Aborted("keep_ticking does not see BioWorldInfo.Tick")
            if saving:  # before the first file, so the career-less sessions' autosaves are undone too
                self.backup_now(saving[0].get("career"))
            for f, checks in selected:
                results.append(self.run_file(f, checks))
        except (Aborted, KeyboardInterrupt) as e:
            status = "ABORTED"
            self.warn(f"ABORTED: {e or 'Ctrl-C'}")
            print(f"ABORTED: {e or 'Ctrl-C'}", flush=True)
        except (SuiteError, RestorePending, GuardError, StepFail, ToolError, SkipCheck, OSError,
                subprocess.SubprocessError) as e:  # a prerequisite, the server or the machine: STOPPED, exit 2
            prereq = e
            self.warn(f"stopped: {type(e).__name__}: {e}")
            print(f"scenario.py: stopped: {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        except Exception as e:  # the runner's own bug: ERROR, exit 1, and the finally part still runs
            status = "ERROR"
            self.warn(f"runner error: {type(e).__name__}: {e}")
            traceback.print_exc()
        finally:
            if self.cur and "setup" in self.cur and self.cur not in results:
                results.append(self.cur)  # the file that was running
            code = self.finish(results, status, args, prereq)
        return code

    def finish(self, results, status, args, prereq):
        """keep_ticking off, the saves back when the game is closed, the report; returns the exit code. Each part
        catches its own errors, so the next one still runs."""
        if not self.game_closed and le1_running():
            try:
                self.call("live_keep_ticking", {"on": False})
            except Exception as e:
                self.warn(f"keep_ticking not turned off ({type(e).__name__}: {short(str(e), 120)}): it goes off "
                          "when the game restarts")
        restore_code = 0
        if self.backup and not self.saves.get("restored"):
            if le1_running():
                cmd = restore_command(backup=self.backup)
                self.warn(f"saves not restored: close the game, then run {cmd}")
                print(f"saves NOT restored yet: close the game, then: {cmd}", flush=True)
                restore_code = 4
            else:
                try:
                    restore_code = 0 if self.restore_now()["ok"] else 4
                except Exception as e:
                    self.warn(f"save restore failed ({type(e).__name__}: {e}): {restore_command(backup=self.backup)}")
                    restore_code = 4
        elif self.saves.get("restored") and not self.saves["restored"]["ok"]:
            restore_code = 4
        checks = [c for f in results for c in f["checks"]]
        bad_setup = any(r["status"] in ("FAIL", "ERROR") for f in results for r in f["setup"] + f["teardown"])
        code = restore_code or (4 if isinstance(prereq, RestorePending) else 2 if prereq else 3 if status == "ABORTED"
                                else 1 if status == "ERROR" or bad_setup
                                or any(c["status"] in ("FAIL", "ERROR") for c in checks) else 0)
        res = {"suite": self.suite, "stamp": self.stamp, "args": args.paths, "bridge": self.bridge,
               "server": getattr(self.s, "server", None), "status": "STOPPED" if prereq else status,
               "files": results, "saves": {k: v for k, v in self.saves.items() if k != "plot_restore"},
               "plot_restore": self.saves.get("plot_restore", []), "warns": self.warns, "exit": code}
        if self.run_dir and os.path.isdir(self.run_dir):
            try:
                write_report(self.run_dir, res)
            except OSError as e:
                print(f"scenario.py: the report was not written: {e}", file=sys.stderr, flush=True)
        for fh in (self.progress, self.calls_file):
            if fh:
                fh.close()
        self.s.close()
        totals = {}
        for c in checks:
            totals[c["status"]] = totals.get(c["status"], 0) + 1
        print(f"done: {', '.join(f'{k} {v}' for k, v in totals.items()) or 'nothing ran'}; exit {code}"
              + (f"; report {self.run_win}\\report.md" if self.run_win else ""), flush=True)
        return code


def write_report(run_dir, res):
    """results.json and report.md in the run folder."""
    with open(os.path.join(run_dir, "results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    cell = lambda t: str(t or "").replace("|", "\\|").replace("\n", " ")  # noqa: E731
    checks = [(f, c) for f in res["files"] for c in f["checks"]]
    totals = {}
    for _, c in checks:
        totals[c["status"]] = totals.get(c["status"], 0) + 1
    server = res.get("server") or {}
    out = [f"# Scenario run {res['suite']} {res['stamp']}", "",
           f"- Paths: {', '.join(res.get('args') or [])}",
           f"- MeleBridge {res.get('bridge') or '?'}, mele-mcp {server.get('version', '?') if isinstance(server, dict) else server}",
           f"- Totals: {', '.join(f'{k} {v}' for k, v in sorted(totals.items())) or 'nothing ran'}"
           + (f" (run {res['status']})" if res.get("status") in ("ABORTED", "STOPPED", "ERROR") else ""),
           f"- Exit code: {res.get('exit')}", ""]
    if res.get("warns"):
        out += ["## Warnings", ""] + [f"- {cell(w)}" for w in res["warns"]] + [""]
    out += ["| # | file/check | row | status | seen vs expected (first failure) | time | shots |",
            "|---|---|---|---|---|---|---|"]
    for n, (f, c) in enumerate(checks, 1):
        bad = next((s for s in c.get("steps", []) if s["status"] in ("FAIL", "ERROR")), None)
        seen = f"{bad['seen']} vs {bad['expected']}" if bad and (bad["seen"] or bad["expected"]) else c.get("reason", "")
        shots = " ".join(f"[{i + 1}]({p})" for i, p in enumerate(c.get("shots", [])))
        out.append(f"| {n} | {f['id']}/{c['id']} | {cell(c.get('row'))} | {c['status']} | {cell(short(seen, 140))} | "
                   f"{c.get('duration', 0)} s | {shots} |")
    for f in res["files"]:
        if f.get("setup") or f.get("warns"):
            out += ["", f"## {f['id']} setup", ""] + [step_line(s) for s in f.get("setup", [])]
            out += [f"- WARN: {cell(w)}" for w in f.get("warns", [])]
        for c in f["checks"]:
            out += ["", f"## {f['id']}/{c['id']}: {c['status']}", "", f"{c.get('title', '')}", ""]
            if c.get("reason"):
                out += [f"Reason: {cell(c['reason'])}", ""]
            out += [step_line(s) for s in c.get("steps", [])]
            out += ["", "Cleanup:", ""] + [step_line(s) for s in c["cleanup"]] if c.get("cleanup") else []
            out += [f"- WARN: {cell(w)}" for w in c.get("warns", [])]
            out += [f"![{p}]({p})" for p in c.get("shots", [])]
        if f.get("teardown"):
            out += ["", f"## {f['id']} teardown", ""] + [step_line(s) for s in f["teardown"]]
    if res.get("saves"):
        out += ["", "## Saves", "", "```", json.dumps(res["saves"], indent=1, default=str), "```"]
    if res.get("plot_restore"):
        out += ["", "## Plot restore", ""] + [f"- {r['file']}: {r['kind']} {r['id']} = {r['value']}: {r.get('restored')}"
                                              for r in res["plot_restore"]]
    with open(os.path.join(run_dir, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(out) + "\n")


def step_line(s):
    extra = f" (seen {s['seen']}, expected {s['expected']})" if s["status"] in ("FAIL", "WARN") and s.get("expected") else ""
    detail = str(s.get("detail") or "").replace("\n", " | ")
    return f"{s['i']}. {s['t'][11:19]} {s['ms'] / 1000:.1f} s **{s['status']}** {s['kind']} {s['name']} " \
           f"`{short(s['args'], 100)}` {short(detail, 200)}{extra}"


def cmd_restore(args, s):
    """--restore-saves: put a backup back, the game closed."""
    if args.restore_saves:
        backup = to_wsl(args.restore_saves)
    else:
        root = to_wsl(args.out or s.get("MELE_SCENARIO_OUT") or ntpath.join(ntpath.dirname(default_out()), "scenarios"))
        pending = glob.glob(os.path.join(root, "*", "save-backup", "PENDING"))
        if len(pending) != 1:
            raise SuiteError(f"{len(pending)} PENDING backups under {root}: name the backup folder")
        with open(pending[0], encoding="utf-8") as f:
            backup = to_wsl(f.read().strip())
    try:
        r = restore_saves(backup)
    except GuardError as e:
        print(f"scenario.py: {e}", file=sys.stderr)
        return 4
    print(json.dumps(r, indent=1))
    pending = os.path.join(os.path.dirname(backup.rstrip("/")), "PENDING")
    if r["ok"] and os.path.exists(pending):
        os.remove(pending)
    return 0 if r["ok"] else 4


# ---- self-test (offline) ----

class FakeSession:
    """Canned tool replies for --selftest: {tool: reply or function(args)}."""

    def __init__(self, replies):
        self.replies, self.calls, self.server = replies, [], {"name": "fake"}

    def call(self, tool, args, timeout=None):
        if tool not in TOOLS:
            raise GuardError(tool)
        self.calls.append((tool, args))
        r = self.replies.get(tool)
        r = r(args) if callable(r) else r
        if isinstance(r, Exception):
            raise r
        return json.loads(json.dumps(r))

    def close(self):
        pass


def selftest():
    passed = [0]

    def ok(cond, what):
        if not cond:
            raise SystemExit(f"selftest FAILED: {what}")
        passed[0] += 1

    def raises(exc, fn, what):
        try:
            fn()
        except exc:
            passed[0] += 1
            return
        raise SystemExit(f"selftest FAILED: {what} did not raise {exc.__name__}")

    # expect and equality
    ok(check_expect("True", True)[0] and check_expect("hench_EXTRA", "Hench_Extra")[0], "plot text and case")
    ok(check_expect(1.00001, 1)[0] and not check_expect(1.01, 1)[0], "float tolerance")
    ok(check_expect(3, {"min": 1, "max": 3})[0] and not check_expect(4, {"max": 3})[0], "min/max")
    ok(check_expect(None, {None: True})[0] and check_expect("None", {"null": True})[0], "null key")
    ok(check_expect([1, 2], {"len": 2})[0] and check_expect("BioPawn_3", {"regex": "biopawn"})[0], "len, regex")
    ok(check_expect("BIOA_ICE00", {"in": ["bioa_ice00", "x"]})[0] and check_expect([4, 6], {"contains": 6})[0], "in")
    ok(not check_expect("text", {"gt": 0})[0], "a number op on text is false")
    # guards
    raises(GuardError, lambda: console_guard("savegame x"), "savegame")
    raises(GuardError, lambda: console_guard("nextframedefer quicksavegame Car00_QuickSave"), "quicksavegame")
    raises(GuardError, lambda: console_guard("god | exit"), "exit after |")
    raises(GuardError, lambda: console_guard("CleanDuplicatePlayerSaves"), "cleanduplicate")
    console_guard("ce Hench_PickSquad")
    console_guard("nextframedefer loadgame Car00_QuickSave DevId=-1", "Car00")
    raises(GuardError, lambda: console_guard("nextframedefer loadgame Other_01 DevId=-1", "Car00"), "other career")
    raises(GuardError, lambda: function_guard("ConsoleCommand"), "ConsoleCommand")
    raises(GuardError, lambda: function_guard("SaveGameEx"), "SaveGame*")
    for fn in ("AutoSave", "TryAutoSaving", "LoadGameFromCallback", "SaveProfile", "DumpSaveGame", "NewCharacterProfile",
               "CleanDuplicatePlayerSaves", "quicksave"):
        raises(GuardError, lambda: function_guard(fn), fn)
    raises(GuardError, lambda: console_guard("exec tests.txt"), "exec")
    raises(GuardError, lambda: console_guard("defer bioautosave"), "a save word")
    function_guard("FinalizeParty")
    function_guard("IsAbleToSave")
    function_guard("GetPlayerSelectable")
    raises(GuardError, lambda: assert_writable("/mnt/c/Games/LE/Game/ME1/x.png", r"C:\Games\LE"), "install path")
    raises(GuardError, lambda: assert_writable(r"c:\games\le", r"C:\Games\LE"), "install root")
    assert_writable(r"C:\Games\LE2\x.png", r"C:\Games\LE")
    ok(to_win("/mnt/d/a/b") == r"D:\a\b" and to_wsl(r"C:\Users\x") == "/mnt/c/Users/x", "path conversion")

    # canned game: three pawns, one henchman alive, one destroyed
    pawns = {"L.BioPawn_1": {"Tag": "hench_extra", "bDeleteMe": False, "m_oBehavior": "L.Beh_1", "Location": {"X": 0, "Y": 0, "Z": 0}},
             "L.BioPawn_2": {"Tag": "hench_extra", "bDeleteMe": True},
             "L.BioPawn_3": {"Tag": "hench_quarian", "bDeleteMe": False, "Location": {"X": 300, "Y": 400, "Z": 10}},
             "L.Beh_1": {"m_PawnAttributes": "L.Attr_1", "m_oEquipment": "L.Eq_1"},
             "L.Attr_1": {"m_TalentPoints": 37},
             "L.Eq_1": {"m_oEquipmentSlots": [{"oEquippedItem": "L.Item_1"}, {"oEquippedItem": None}]},
             "L.Item_1": {"m_nID": 12}}
    status = {"bridge": "3", "map": "BIOA_ICE00", "world": "L.BioWorldInfo_0", "begunPlay": True, "bPlayersOnly": False,
              "pc": "L.BioPlayerController_0", "pawn": "L.BioPawn_9", "keepTicking": True, "worldTicks": 5,
              "streaming": {"levels": 3, "pending": 0, "pendingNames": []}, "partySelectOpen": True,
              "partySelect": {"membersAvailable": 4}, "loadingMovie": {"playing": False, "state": "LMS_NotPlaying"}}

    def get(a):
        if a["property"] not in pawns.get(a["objectPath"], {}):
            return ToolError(f"InvalidOperationException: no property '{a['property']}' in X")
        return {"value": pawns[a["objectPath"]][a["property"]]}
    fake = FakeSession({"live_status": status, "live_get": get, "le_status": {},
                        "live_find": {"total": 3, "objects": [{"path": p} for p in pawns if "Pawn" in p]},
                        "game_plot": lambda a: {"values": [{"kind": a["kind"], "id": i, "value": "True"} for i in a["ids"]]},
                        "live_call": {"return": True}})
    r = Runner(fake)
    r.file = {"id": "t", "doc": {"career": "Car00"}}
    r.vars = {"n": 5, "loc": {"X": 1, "Y": 2, "Z": 3}, "names": ["a", "b"], "attr": "m_oBehavior>m_PawnAttributes",
              "hench": {"find": {"class": "BioPawn", "tag": "hench_extra"}}, "chain": "${attr}"}
    ok(r.interp("${n}") == 5 and r.interp("v=${n}") == "v=5", "interpolation types")
    ok(r.interp("${loc.X}") == 1 and r.interp("${names[1]}") == "b", "${a.b} and ${a[0]}")
    ok(r.interp("${chain}>m_TalentPoints") == "m_oBehavior>m_PawnAttributes>m_TalentPoints", "nested variable")
    raises(StepFail, lambda: r.interp("${nope}"), "unknown variable")
    ok(r.interp("${hench}") == "L.BioPawn_1", "finder variable resolves to the live one")
    ok(r.interp("${map}") == "BIOA_ICE00" and r.interp("${player}") == "L.BioPawn_9", "built-ins")
    ok(r.read("${hench}>m_oBehavior>m_PawnAttributes", "m_TalentPoints") == 37, "object chain")
    ok(r.read("hench", "${attr}>m_TalentPoints") == 37, "bare name and property chain")
    ok(r.read("${hench}", "m_oBehavior>m_oEquipment>m_oEquipmentSlots[*].oEquippedItem>m_nID") == [12, None], "[*]")
    ok(r.holds({"status": "partySelect.membersAvailable", "expect": {"gt": 0}})[0], "status condition")
    ok(r.holds({"count": "hench", "expect": 1})[0], "count skips destroyed pawns")
    ok(r.holds({"plot": {"bool": 4771}, "expect": True})[0], "plot condition")
    ok(r.holds({"all": [{"count": "hench", "expect": 1}, {"status": "map", "expect": "bioa_ice00"}]})[0], "all")
    ok(not r.holds({"any": [{"count": "hench", "expect": 0}, {"status": "bPlayersOnly", "expect": True}]})[0], "any")
    ok(round(r.distance({"a": "${hench}", "b": {"X": 300, "Y": 400, "Z": 0}}), 3) == 500, "distance")
    # every call names its game, but le_status, which takes none; a game the caller gave stays
    ok(all(a.get("game") == "LE1" for t, a in fake.calls if t != "le_status"), "game on every earlier call")
    del fake.calls[:]
    r.call("live_status", {"timeoutSeconds": 3})
    r.call("live_find", {"className": "BioPawn", "game": "LE2"})
    r.call("le_status")
    ok([a.get("game") for _, a in fake.calls] == ["LE1", "LE2", None], "game added, kept, or left out")
    # find_all: an object gone, or without the filtered property, does not match; a bridge that does not answer fails
    real_get = fake.replies["live_get"]
    fake.replies["live_get"] = lambda a: ToolError("IOException: MeleBridge did not answer 'get' within 7 s.")
    raises(ToolError, lambda: r.find_all("hench"), "a silent bridge is no count")
    ok(not r.holds_safe({"count": "hench", "expect": 0}), "count 0 not true while the bridge is silent")
    fake.replies["live_get"] = lambda a: ToolError("InvalidOperationException: no object at X (paths as find returns them)") \
        if a["objectPath"] == "L.BioPawn_1" else real_get(a)
    ok(r.find_all("hench") == [], "an object gone since live_find does not match")
    fake.replies["live_get"] = real_get
    # a respawn that fails is a ToolError (a FAIL), never a SuiteError through the finally paths
    sess = Session.__new__(Session)
    sess.record, sess.proc = (lambda e: None), None
    sess.rpc = lambda *a: (_ for _ in ()).throw(ServerDead("server closed stdout"))
    sess.start = lambda: (_ for _ in ()).throw(SuiteError("MeleMcp.dll is locked"))
    raises(ToolError, lambda: sess.call("live_status", {}), "failed respawn")
    ok(load_problems(status, "BIOA_ICE00", True) == [] and "the squad screen is open" in load_problems(status, None, False),
       "load_problems with the squad screen")
    ok(any("map is" in w for w in load_problems(dict(status, map="X", pawn=None), "BIOA_ICE00", True)), "wrong map")
    ok(any("loading movie" in w for w in load_problems(dict(status, loadingMovie={"playing": True}), None, True)), "movie")

    with tempfile.TemporaryDirectory() as tmp:
        # logs: problems, getall answers left out, normalising, Kismet order, the abort scan
        dl, kl = os.path.join(tmp, "DebugLogger.log"), os.path.join(tmp, "KismetLogger.log")
        with open(dl, "w") as f:
            f.write("[10:00:00.000 info] old line\n")
        with open(kl, "w") as f:
            f.write("[0001.000] SeqAct_Log L.TheWorld:PersistentLevel.Main_Sequence.SeqAct_Log_0 In\n")
        r.logs = Logs(paths=(dl, kl))
        r.marks = {"check": r.logs.offsets()}
        r.scan_at = list(r.logs.offsets())
        with open(dl, "a") as f:
            f.write("[10:00:01.000 error] appLogErrorf: 0) BioWorldInfo L.BioWorldInfo_0.bHasPathNodes = True\n"
                    "[10:00:02.000 warning] Accessed None 'Foo' in BioPawn_12 at 0x1F00\n  Function x\n"
                    "[10:00:03.000 info] Loading package synchronously: BIOA_ICE00\n"
                    "[10:00:04.000 error] ScriptWarning BIOA_ICE20_00_SND bad\n")
        with open(kl, "a") as f:
            f.write("[0002.000] SeqAct_X L.TheWorld:PersistentLevel.SpawnHenchman_15 In\n"
                    "[0003.000] SeqEvent_RemoteEvent L.TheWorld:PersistentLevel.re_henchSpawnComplete Out\n")
        found = r.log_entries("check")
        ok(len(found) == 2 and normalise(found[0][1]) == "Accessed None 'Foo' in BioPawn_N at 0x?", "log entries")
        ok(len(r.log_entries("check", ignore=["ICE20_00_SND"])) == 1, "ignore")
        r.rec = {}
        r.k_assert_kismet({"since": "check", "order": ["SpawnHenchman_15", "re_henchSpawnComplete"]})
        raises(StepFail, lambda: r.k_assert_kismet({"since": "check", "order": ["re_henchSpawnComplete", "SpawnHenchman_15"]}),
               "Kismet order")
        r.alive_t = time.time() + 3600
        r.cur = {"warns": []}
        r.scan()
        with open(dl, "a") as f:
            f.write("[10:00:05.000 info] Saved Car00_AutoSave on device Id 1\n")
        r.scan()
        ok(any("Car00_AutoSave" in w for w in r.warns), "unexpected save flagged")
        with open(dl, "a") as f:
            f.write("[10:00:06.000 error] appLogErrorf: Loading Car00_AutoSave on device Id 0...\n")
        raises(Aborted, r.scan, "unexpected load aborts")
        ok(r.load_aborted, "plot restore skipped after it")
        # wait_loaded without map: the map of the load line must be the one loaded
        r.load_aborted, r.rec = False, {}
        mark = r.logs.offsets()
        with open(dl, "a") as f:
            f.write("[10:00:07.000 info] Loading package synchronously: BIOA_NOR10_LOC\n"
                    "[10:00:07.100 info] Loading package synchronously: BIOA_NOR10\n")
        try:
            r.wait_loaded({"expect_load": "auto", "allow_party": True, "timeout": 1.5, "load_grace": 0}, mark)
            ok(False, "the old map passed after a load line")
        except StepFail as e:
            ok("map is BIOA_ICE00" in str(e), "wait_loaded wants the map of the load line")
        mark = r.logs.offsets()
        with open(dl, "a") as f:
            f.write("[10:00:08.000 info] Loading package synchronously: BIOA_ICE00\n")
        ok(r.wait_loaded({"expect_load": "auto", "allow_party": True, "timeout": 10}, mark)["map"] == "BIOA_ICE00",
           "wait_loaded passes on the map of the load line")
        # write_restore never raises, whatever the server does
        fake.replies["game_plot"] = lambda a: SuiteError("no mele-mcp server")
        globals()["le1_running"], real = (lambda: True), globals()["le1_running"]
        try:
            r.saves = {}
            r.write_restore([{"file": "t", "kind": "bool", "id": 4771, "value": "True"}])
        finally:
            globals()["le1_running"] = real
        ok(r.saves["plot_restore"][0]["restored"].startswith("failed: SuiteError"), "write_restore records the failure")

        # suite validation
        suite = os.path.join(tmp, "suite")
        os.makedirs(suite)
        with open(os.path.join(suite, "_bases.yaml"), "w") as f:
            f.write("vars:\n  hench: {find: {class: BioPawn, tag: hench_extra}}\nmacros:\n"
                    "  B1:\n    - load: {slot: 99, map: BIOA_ICE00}\n    - plot: {bool: {4775: true}}\n")
        good = ("suite: s4\ntitle: t\ninclude: [_bases.yaml]\nrestore: {bool: [4771]}\nsetup:\n  - use: B1\nchecks:\n"
                "  - id: one\n    steps:\n      - mark: pick\n      - squad_pick: {members: [hench_extra, 5]}\n"
                "      - wait_until: {count: hench, expect: 1, stable: 4, timeout: 30}\n"
                "      - live_get: {object: '${hench}>m_oBehavior', property: m_TalentPoints, expect: 37}\n"
                "        save_as: tp\n      - assert_kismet: {since: 'mark:pick', contains: '${tp}'}\n"
                "      - keep_ticking: on\n      - screenshot: shot\n")
        with open(os.path.join(suite, "01-good.yaml"), "w") as f:
            f.write(good)
        ok(len(load_all([suite])) == 1, "a good suite validates")
        bad = good.replace("mark: pick", "fly: away").replace("stable: 4", "stabel: 4").replace("${tp}", "${tq}") + \
            "      - console: savegame x\n      - game_cycle: {commands: []}\n      - quicksave: {}\n"
        with open(os.path.join(suite, "02-bad.yaml"), "w") as f:
            f.write(bad)
        try:
            load_all([suite])
            ok(False, "a bad suite fails")
        except SuiteError as e:
            for frag in ("exactly one kind key", "unknown argument stabel", "unknown variable ${tq}",
                         "console command refused", "cold_start", "needs career", "no such mark"):
                ok(frag in str(e), f"validation reports '{frag}'")

        # report
        rec = {"i": 1, "kind": "count", "name": "", "status": "FAIL", "t": now_iso(), "ms": 1200, "args": {"find": "hench"},
               "seen": "2", "expected": "== 1", "detail": "matching live objects: 2"}
        res = {"suite": "s4", "stamp": "x", "args": ["a"], "bridge": "3", "server": {"version": "0"}, "status": "PASS",
               "files": [{"id": "01", "setup": [], "teardown": [], "checks": [
                   {"id": "c", "title": "t", "row": "r|1", "status": "FAIL", "reason": "step 1", "warns": ["w"],
                    "duration": 1.2, "steps": [rec], "cleanup": [], "shots": ["shots/01.png"]}]}],
               "saves": {}, "plot_restore": [], "warns": ["w"], "exit": 1}
        write_report(tmp, res)
        with open(os.path.join(tmp, "report.md")) as f:
            md = f.read()
        ok("| 1 | 01/c | r\\|1 | FAIL | 2 vs == 1 |" in md and json.load(open(os.path.join(tmp, "results.json")))["exit"] == 1,
           "report.md and results.json")

        # saves: back up, change, restore
        save = os.path.join(tmp, "Save", "ME1")
        for rel, data in (("Car00/Car00_01.pcsav", "a"), ("Car00/Car00_AutoSave.pcsav", "b"),
                          ("PROFILE/GamerProfile.pcsav", "c"), ("Launcher_Profile", "d"), ("Other00/x.pcsav", "e")):
            os.makedirs(os.path.dirname(os.path.join(save, rel)), exist_ok=True)
            with open(os.path.join(save, rel), "w") as f:
                f.write(data)
        backup = os.path.join(tmp, "backup")
        man = backup_saves(save, "Car00", backup)
        ok(len(man["files"]) == 4 and "Other00" in man["top_level"], "backup manifest")
        for rel, data in (("Car00/Car00_QuickSave.pcsav", "q"), ("Car00/Car00_AutoSave.pcsav", "B"),
                          ("Launcher_Profile", "D"), ("Save/Save_01.pcsav", "s")):
            os.makedirs(os.path.dirname(os.path.join(save, rel)), exist_ok=True)
            with open(os.path.join(save, rel), "w") as f:
                f.write(data)
        real = globals()["le1_running"]
        globals()["le1_running"] = lambda: False
        try:
            out = restore_saves(backup)
        finally:
            globals()["le1_running"] = real
        ok(out["ok"] and open(os.path.join(save, "Car00/Car00_AutoSave.pcsav")).read() == "b", "restore")
        ok(not os.path.exists(os.path.join(save, "Car00/Car00_QuickSave.pcsav")) and not os.path.exists(
            os.path.join(save, "Save")) and len(out["displaced"]) == 4, "restore displaces, deletes nothing")
    print(f"selftest: {passed[0]} checks passed")
    return 0


def on_signal(signum, frame):
    """SIGTERM (a stopped background task) and SIGHUP (a closed terminal) take Ctrl-C's path: ABORTED, then
    keep_ticking off, the plot values and the report. A second signal kills at once."""
    for sig in (signal.SIGTERM, signal.SIGHUP):
        signal.signal(sig, signal.SIG_DFL)
    if signum == signal.SIGHUP:  # no terminal left: printing would raise
        try:
            sys.stdout = sys.stderr = open(os.devnull, "w")
        except OSError:
            pass
    raise KeyboardInterrupt(signal.Signals(signum).name)


def main():
    p = argparse.ArgumentParser(description="Run in-game test suites against LE1 (docs/scenarios.md).")
    p.add_argument("paths", nargs="*", help="suite files or folders")
    p.add_argument("--only", help="comma list of fnmatch patterns on <file id> and <file id>/<check id>")
    p.add_argument("--from", dest="start", help="skip everything before the first file or check matching this")
    p.add_argument("--dry-run", action="store_true", help="validate offline and print the plan")
    p.add_argument("--out", help="report root (Windows or /mnt path)")
    p.add_argument("--unattended", action="store_true", help="checks that need a person become SKIP")
    p.add_argument("--restore-saves", nargs="?", const="", metavar="BACKUP_DIR", help="restore a save backup, game closed")
    p.add_argument("--selftest", action="store_true", help="check the runner's own logic offline")
    args = p.parse_args()
    try:
        if args.selftest:
            return selftest()
        if args.restore_saves is not None:
            return cmd_restore(args, settings())
        if not args.paths:
            p.print_usage(sys.stderr)
            return 2
        selected = select_checks(load_all(args.paths), args.only, args.start)
        if args.dry_run:
            return dry_run(selected)
        s = settings()
        for sig in (signal.SIGTERM, signal.SIGHUP):
            signal.signal(sig, on_signal)
        return Runner(None, args.unattended).go(selected, args, s)
    except SuiteError as e:
        print(f"scenario.py: {e}", file=sys.stderr)
        return 2
    except RestorePending as e:
        print(f"scenario.py: {e}", file=sys.stderr)
        return 4
    except GuardError as e:
        print(f"scenario.py: refused: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
