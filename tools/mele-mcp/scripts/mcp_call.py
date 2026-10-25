"""Calls one mele-mcp tool from the shell: mcp_call.py <tool> ['<json arguments>'].

Starts a fresh server process, so it works while the Claude Code session's own server is stopped or stale.
"""
import json
import os
import subprocess
import sys

DOTNET = os.environ.get("MELE_DOTNET", "/mnt/d/MELE/dotnet/dotnet.exe")
DLL = os.environ.get("MELE_DLL", r"D:\MELE\tools\mele-mcp\MeleMcp.dll")

tool = sys.argv[1]
arguments = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}

env = dict(os.environ)
env.setdefault("MELE_LE_DIR", r"D:\SteamLibrary\steamapps\common\Mass Effect Legendary Edition")
env["WSLENV"] = "MELE_LE_DIR"

proc = subprocess.Popen([DOTNET, DLL], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL, env=env)


def send(msg):
    proc.stdin.write((json.dumps(msg) + "\n").encode())
    proc.stdin.flush()


def recv(want_id):
    while True:
        line = proc.stdout.readline()
        if not line:
            raise SystemExit("server closed stdout")
        msg = json.loads(line)
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
