"""Minimal MCP stdio client: initialize, list tools, call le_status and pkg_info. mcp_smoke.py [package]

Settings as in mcp_call.py: the environment, then tools/mele-mcp/build.env. deploy.sh sets MELE_DLL to the DLL it
just published. Exits 1 when the server finds no LE1 folder or pkg_info fails.
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
PACKAGE = sys.argv[1] if len(sys.argv) > 1 else "BIOA_CRD00_LOC_FR.pcc"

env = dict(os.environ)
# WSL hands only the variables WSLENV names to a Windows process: every MELE_* setting but the ones for this script.
passed = sorted(k for k, v in S.items() if v and k not in ("MELE_DOTNET", "MELE_DLL", "MELE_OUT"))
env.update((k, S[k]) for k in passed)
env["WSLENV"] = ":".join(passed)

print("server dll:", DLL)
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
        if b"\r" in line:
            print("NOTE: CR found in stdout line")
        msg = json.loads(line)
        if msg.get("id") == want_id:
            return msg


send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
    "protocolVersion": "2025-06-18", "capabilities": {},
    "clientInfo": {"name": "smoke", "version": "0"}}})
init = recv(1)["result"]
print("server:", init["serverInfo"], "protocol:", init["protocolVersion"])
send({"jsonrpc": "2.0", "method": "notifications/initialized"})

send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
for t in recv(2)["result"]["tools"]:
    print("tool:", t["name"], "| params:", list(t["inputSchema"].get("properties", {}).keys()),
          "| readOnly:", t.get("annotations", {}).get("readOnlyHint"))

failed = []

send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "le_status", "arguments": {}}})
text = recv(3)["result"]["content"][0]["text"]
print("le_status:", text)
try:
    le1 = json.loads(text).get("le1GamePath")
except (ValueError, AttributeError):
    le1 = None
if not le1:
    failed.append("le_status has no LE1 folder (set MELE_LE_DIR to the Legendary Edition folder, as a Windows path)")

send({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
      "params": {"name": "pkg_info", "arguments": {"package": PACKAGE, "topClasses": 3}}})
r = recv(4)["result"]
print("pkg_info isError:", r.get("isError"), "->", r["content"][0]["text"][:600])
if r.get("isError"):
    failed.append(f"pkg_info {PACKAGE} failed")

send({"jsonrpc": "2.0", "id": 5, "method": "tools/call",
      "params": {"name": "pkg_info", "arguments": {"package": "DOES_NOT_EXIST.pcc"}}})
r = recv(5)["result"]
print("missing file isError:", r.get("isError"), "->", r["content"][0]["text"][:300])
if not r.get("isError"):
    failed.append("pkg_info of a missing file did not fail")

proc.stdin.close()
proc.wait(timeout=20)
if failed:
    raise SystemExit("smoke test failed: " + "; ".join(failed))
