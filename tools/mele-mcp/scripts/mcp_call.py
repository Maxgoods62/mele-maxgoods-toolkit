"""Calls one mele-mcp tool from the shell: mcp_call.py <tool> ['<json arguments>'].

  mcp_call.py le_status '{}'
  mcp_call.py pkg_info '{"game": "LE1", "package": "BIOA_CRD00.pcc"}'
  mcp_call.py live_status '{"game": "LE3"}'

Every tool that takes game requires it (LE1, LE2 or LE3), and this script adds none. le_status takes no arguments.

Starts a fresh server process, so it works while the Claude Code session's own server is stopped or stale.
Settings come from the environment, then from tools/mele-mcp/build.env (not in git, see build.env.example):
MELE_DOTNET (default: dotnet.exe on PATH), MELE_DLL (default: <MELE_OUT>\\MeleMcp.dll, where deploy.sh publishes).
Every other MELE_* setting that is set goes on to the server: MELE_LE_DIR (unset, the server finds the game in the
registry), MELE_OWN_DLC, MELE_LEVELS_JSON, MELE_BACKUP_DIR.
"""
import json
import os
import subprocess
import sys

BUILD_ENV = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "build.env")


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


S = settings()
DOTNET = S.get("MELE_DOTNET") or "dotnet.exe"
DLL = S.get("MELE_DLL") or (S.get("MELE_OUT") or default_out()).rstrip("\\") + r"\MeleMcp.dll"

tool = sys.argv[1]
arguments = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}

env = dict(os.environ)
# WSL hands only the variables WSLENV names to a Windows process: every MELE_* setting but the ones for this script.
passed = sorted(k for k, v in S.items() if v and k not in ("MELE_DOTNET", "MELE_DLL", "MELE_OUT"))
env.update((k, S[k]) for k in passed)
env["WSLENV"] = ":".join(passed)

try:
    proc = subprocess.Popen([DOTNET, DLL], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, env=env)
except FileNotFoundError:
    raise SystemExit(f"{DOTNET} not found: set MELE_DOTNET to the Windows dotnet.exe (or put it in build.env)")


def send(msg):
    proc.stdin.write((json.dumps(msg) + "\n").encode())
    proc.stdin.flush()


def recv(want_id):
    while True:
        line = proc.stdout.readline()
        if not line:
            raise SystemExit("server closed stdout")
        try:
            msg = json.loads(line)
        except ValueError:    # dotnet.exe's own text, e.g. for a missing DLL
            raise SystemExit(f"no mele-mcp server at {DLL}: run deploy.sh, or set MELE_DLL or MELE_OUT")
        if msg.get("id") == want_id:
            return msg


send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
    "protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "mcp_call", "version": "0"}}})
recv(1)
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
send({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": tool, "arguments": arguments}})
result = recv(2)
proc.stdin.close()
proc.wait(timeout=30)

if "error" in result:
    raise SystemExit(f"JSON-RPC error: {result['error']}")
body = result["result"]
for block in body.get("content", []):
    text = block.get("text", "")
    try:
        print(json.dumps(json.loads(text), indent=2))
    except ValueError:
        print(text)
sys.exit(1 if body.get("isError") else 0)
