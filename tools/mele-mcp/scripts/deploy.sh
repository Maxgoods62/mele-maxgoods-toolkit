#!/usr/bin/env bash
# Build mele-mcp with the Windows .NET SDK, publish it to MELE_OUT, then smoke-test the stdio server.
#   scripts/deploy.sh [package]    the package for the smoke test's pkg_info call (default BIOA_CRD00_LOC_FR.pcc)
# Runs in WSL. Settings come from the environment, then from tools/mele-mcp/build.env (not in git, see
# build.env.example); a variable the caller exported wins over build.env.
#   MELE_DOTNET   the Windows dotnet.exe, as a WSL path (default: dotnet.exe on PATH; global.json pins the SDK)
#   MELE_OUT      the folder to publish to, as a Windows path (default: %LOCALAPPDATA%\mele-mcp\server)
#   MELE_LE_DIR   the Legendary Edition folder, as a Windows path; passed on to the smoke test's server only when set
#                 (unset, the server finds the game in the registry)
# Only the servers that run MELE_OUT's MeleMcp.dll are stopped first (Windows locks loaded DLLs); servers of other
# folders keep running. After a deploy, reconnect the server in Claude Code with /mcp.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
if [[ -f "$ROOT/build.env" ]]; then
    declare -A caller=()
    for v in $(compgen -e MELE_ || true); do caller[$v]="${!v}"; done
    set -a; . "$ROOT/build.env"; set +a    # exported, so the smoke test sees the same settings
    for v in "${!caller[@]}"; do export "$v=${caller[$v]}"; done
fi

die() { echo "deploy.sh: $*" >&2; exit 1; }

DOTNET="${MELE_DOTNET:-dotnet.exe}"
command -v "$DOTNET" >/dev/null || die "$DOTNET not found: set MELE_DOTNET to the Windows dotnet.exe (SDK: global.json)"
OUT="${MELE_OUT:-$(powershell.exe -NoProfile -NonInteractive -Command \
    '[Console]::OutputEncoding = [Text.Encoding]::UTF8; $env:LOCALAPPDATA' | tr -d '\r')\\mele-mcp\\server}"
[[ "$OUT" =~ ^([A-Za-z]:\\|\\\\) ]] || die "MELE_OUT must be a Windows path such as C:\\mele-mcp (got '$OUT')"
OUT="${OUT%\\}"    # a trailing \ would escape the closing quote of a Windows argument
# Before any server is stopped: an SDK that global.json does not accept fails the publish with no " error " line.
SDK="$(sed -n 's/^ *"version": *"\([^"]*\)".*/\1/p' "$ROOT/global.json")"
(cd "$ROOT" && "$DOTNET" --version) > /dev/null 2>&1 \
    || die "$DOTNET has no .NET SDK $SDK or a later ${SDK%.*}.x (global.json): install it, or set MELE_DOTNET"

powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$(wslpath -w "$HERE/stop-server.ps1")" \
    -OutDir "$OUT" | tr -d '\r'

cd "$ROOT"
log="$(mktemp)"
if ! "$DOTNET" publish src/MeleMcp/MeleMcp.csproj -c Release -nologo -v q -o "$OUT" > "$log" 2>&1; then
    errors="$(tr -d '\r' < "$log" | grep -E ' error ' | sort -u || true)"
    if [[ -n "$errors" ]]; then echo "$errors"; else echo "publish failed:"; tr -d '\r' < "$log" | tail -n 20; fi
    rm -f "$log"
    exit 1
fi
rm -f "$log"
echo "published to $OUT"

MELE_DOTNET="$DOTNET" MELE_OUT="$OUT" MELE_DLL="$OUT\\MeleMcp.dll" \
    python3 -I -u "$HERE/mcp_smoke.py" "${1:-BIOA_CRD00_LOC_FR.pcc}"
