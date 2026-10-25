#!/usr/bin/env bash
# Build mele-mcp with the Windows .NET SDK and publish it to the Windows disk, then smoke-test the stdio server.
# After a deploy, reconnect the server in Claude Code with /mcp (the old process was stopped).
set -euo pipefail

DOTNET="${MELE_DOTNET:-/mnt/d/MELE/dotnet/dotnet.exe}"
OUT_WIN='D:\MELE\tools\mele-mcp'
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$HERE/stop-server.ps1")" | tr -d '\r'

cd "$ROOT"
log="$(mktemp)"
if ! "$DOTNET" publish src/MeleMcp/MeleMcp.csproj -c Release -nologo -v q -o "$OUT_WIN" > "$log" 2>&1; then
    tr -d '\r' < "$log" | grep -E ' error ' | sort -u
    rm -f "$log"
    exit 1
fi
rm -f "$log"
echo "published to $OUT_WIN"

python3 -I -u "$HERE/mcp_smoke.py" "$OUT_WIN\\MeleMcp.dll" "${1:-BIOA_CRD00_LOC_FR.pcc}"
