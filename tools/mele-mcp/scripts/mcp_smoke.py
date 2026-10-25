"""Minimal MCP stdio client: initialize, list tools, call le_status and pkg_info."""
import json
import os
import subprocess
import sys

DOTNET = "/mnt/d/MELE/dotnet/dotnet.exe"
DLL = sys.argv[1]
PACKAGE = sys.argv[2] if len(sys.argv) > 2 else "BIOA_CRD00_LOC_FR.pcc"

env = dict(os.environ)
env["MELE_LE_DIR"] = r"D:\SteamLibrary\steamapps\common\Mass Effect Legendary Edition"
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

send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "le_status", "arguments": {}}})
print("le_status:", recv(3)["result"]["content"][0]["text"])

send({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
      "params": {"name": "pkg_info", "arguments": {"package": PACKAGE, "topClasses": 3}}})
r = recv(4)["result"]
print("pkg_info isError:", r.get("isError"), "->", r["content"][0]["text"][:600])

send({"jsonrpc": "2.0", "id": 5, "method": "tools/call",
      "params": {"name": "pkg_info", "arguments": {"package": "DOES_NOT_EXIST.pcc"}}})
r = recv(5)["result"]
print("missing file isError:", r.get("isError"), "->", r["content"][0]["text"][:300])

proc.stdin.close()
proc.wait(timeout=20)
