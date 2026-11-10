#!/usr/bin/env bash
# Build mele-mcp with the Windows .NET SDK, publish it to MELE_OUT, then smoke-test the stdio server.
#   scripts/deploy.sh [package]    an LE1 package for the smoke test's pkg_info call, which passes game LE1
#                                  (default BIOA_CRD00_LOC_FR.pcc). The smoke test also fails when a tool's game is optional.
# Runs in WSL. Settings come from the environment, then from tools/mele-mcp/build.env (not in git, see
# build.env.example); a variable the caller exported wins over build.env.
#   MELE_DOTNET   the Windows dotnet.exe, as a WSL path (default: dotnet.exe on PATH; global.json pins the SDK)
#   MELE_OUT      the folder to publish to, as a Windows path (default: %LOCALAPPDATA%\mele-mcp\server; a git worktree
#                 has no default, because that folder belongs to the main checkout)
#   MELE_LE_DIR   the Legendary Edition folder, as a Windows path; passed on to the smoke test's server only when set
#                 (unset, the server finds the game in the registry)
# The server is built first. Only when that worked are the servers that run MELE_OUT's MeleMcp.dll stopped (Windows
# locks loaded DLLs) and the build published; a build error stops nothing and leaves MELE_OUT as it was. Servers of
# other folders keep running. After a deploy, reconnect the server in Claude Code with /mcp.
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

# True in a linked git worktree (git worktree add): its top folder holds a .git file that names a worktrees/ folder.
linked_worktree() {
    local top
    top="$(git -C "$ROOT" rev-parse --show-toplevel 2>/dev/null)" || return 1
    [[ -f "$top/.git" ]] && grep -q '/worktrees/' "$top/.git"
}

# Runs dotnet quietly. When it fails: its error lines, or the end of its output when no line is marked as one.
quiet_dotnet() {
    local log errors
    log="$(mktemp)"
    if "$DOTNET" "$@" > "$log" 2>&1; then rm -f "$log"; return 0; fi
    errors="$(tr -d '\r' < "$log" | grep -E ' error ' | sort -u || true)"
    if [[ -n "$errors" ]]; then echo "$errors"; else echo "dotnet $1 failed:"; tr -d '\r' < "$log" | tail -n 20; fi
    rm -f "$log"
    return 1
}

DOTNET="${MELE_DOTNET:-dotnet.exe}"
command -v "$DOTNET" >/dev/null || die "$DOTNET not found: set MELE_DOTNET to the Windows dotnet.exe (SDK: global.json)"
if [[ -z "${MELE_OUT:-}" ]] && linked_worktree; then
    die "this checkout is a git worktree and MELE_OUT is not set. The default folder is the main checkout's: give" \
        "this worktree its own, e.g. MELE_OUT='C:\\mele-mcp-test' scripts/deploy.sh"
fi
OUT="${MELE_OUT:-$(powershell.exe -NoProfile -NonInteractive -Command \
    '[Console]::OutputEncoding = [Text.Encoding]::UTF8; $env:LOCALAPPDATA' | tr -d '\r')\\mele-mcp\\server}"
[[ "$OUT" =~ ^([A-Za-z]:\\|\\\\) ]] || die "MELE_OUT must be a Windows path such as C:\\mele-mcp (got '$OUT')"
OUT="${OUT%\\}"    # a trailing \ would escape the closing quote of a Windows argument
# Before any server is stopped: an SDK that global.json does not accept fails the build with no " error " line.
SDK="$(sed -n 's/^ *"version": *"\([^"]*\)".*/\1/p' "$ROOT/global.json")"
(cd "$ROOT" && "$DOTNET" --version) > /dev/null 2>&1 \
    || die "$DOTNET has no .NET SDK $SDK or a later ${SDK%.*}.x (global.json): install it, or set MELE_DOTNET"

# The commit being published, with -dirty when tracked files differ from it. The published MeleMcp.dll keeps the
# commit too, as the part of its product version after the +.
WHAT="$(git -C "$ROOT" describe --always --dirty 2>/dev/null || echo "a copy outside git")"
echo "deploy.sh: publishing $WHAT to $OUT"

cd "$ROOT"
# Built before any server is stopped: a compile error must leave every session its server.
quiet_dotnet build src/MeleMcp/MeleMcp.csproj -c Release -nologo -v q \
    || die "the build failed: no server was stopped, and $OUT is as it was"

powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$(wslpath -w "$HERE/stop-server.ps1")" \
    -OutDir "$OUT" | tr -d '\r'

# The publish reuses the build above, so it mostly copies files. From here the servers of $OUT are stopped.
quiet_dotnet publish src/MeleMcp/MeleMcp.csproj -c Release -nologo -v q -o "$OUT" \
    || die "the publish failed after the servers of $OUT were stopped: fix the error and run deploy.sh again"
echo "published $WHAT to $OUT"

MELE_DOTNET="$DOTNET" MELE_OUT="$OUT" MELE_DLL="$OUT\\MeleMcp.dll" \
    python3 -I -u "$HERE/mcp_smoke.py" "${1:-BIOA_CRD00_LOC_FR.pcc}"
