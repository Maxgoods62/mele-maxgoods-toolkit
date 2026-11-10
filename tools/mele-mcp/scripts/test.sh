#!/usr/bin/env bash
# Runs the toolkit's tests: tests/MeleMcp.Core.Tests (lectool is built first, for its tests) and scenario.py --selftest.
#   scripts/test.sh [--allow-skips] [dotnet test args]    e.g. scripts/test.sh --filter WriteGuard
# Runs in WSL. Settings as for deploy.sh: the environment, then tools/mele-mcp/build.env.
#   MELE_DOTNET   the Windows dotnet.exe, as a WSL path (default: dotnet.exe on PATH; global.json pins the SDK)
#   MELE_LE_DIR   the Legendary Edition folder, as a Windows path (unset: the registry). Tests that read game files
#                 do not run without it, and none writes there.
# A test that did not run fails the script: without the game about a third of the tests are skipped, and dotnet test
# still exits 0. --allow-skips accepts such a run; the script says how many tests did not run and why either way.
# The output keeps the toolkit's own build warnings and each NuGet audit warning once, and leaves out the build
# warnings of external/LegendaryExplorer (test_report.py).
# No CI runs this: run it before a push. What is tested and why: tests/MeleMcp.Core.Tests/README.md.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
if [[ -f "$ROOT/build.env" ]]; then
    declare -A caller=()
    for v in $(compgen -e MELE_ || true); do caller[$v]="${!v}"; done
    set -a; . "$ROOT/build.env"; set +a
    for v in "${!caller[@]}"; do export "$v=${caller[$v]}"; done
fi

die() { echo "test.sh: $*" >&2; exit 1; }

# --allow-skips is this script's own switch; everything else goes to dotnet test.
report=()
args=()
for a in "$@"; do
    if [[ "$a" == --allow-skips ]]; then report=(--allow-skips); else args+=("$a"); fi
done

DOTNET="${MELE_DOTNET:-dotnet.exe}"
command -v "$DOTNET" >/dev/null || die "$DOTNET not found: set MELE_DOTNET to the Windows dotnet.exe (SDK: global.json)"

cd "$ROOT"
LECTOOL_DIR="$ROOT/tests/MeleMcp.Core.Tests/bin/lectool"
log="$(mktemp)"
trap 'rm -f "$log"' EXIT
if ! "$DOTNET" build ../lectool/lectool.csproj -c Release -nologo -v q -o "$(wslpath -w "$LECTOOL_DIR")" > "$log" 2>&1; then
    tr -d '\r' < "$log" | grep -E ' (error|erreur) ' | sort -u || tr -d '\r' < "$log" | tail -n 20
    die "lectool did not build"
fi

# Windows processes see only the variables WSLENV names.
export LECTOOL="$(wslpath -w "$LECTOOL_DIR/lectool.exe")"
WSLENV="${WSLENV:+$WSLENV:}LECTOOL"
[[ -n "${MELE_LE_DIR:-}" ]] && WSLENV="$WSLENV:MELE_LE_DIR"
export WSLENV

status=0
# -v q keeps MSBuild quiet; the console logger still names each failed or skipped test. The trx file holds every
# test's outcome and message for test_report.py. Results go under bin/ (ignored): a trx or --blame file holds machine
# paths. An earlier run's file goes first, so a run that stops before its tests is not read from it.
RESULTS="$ROOT/tests/MeleMcp.Core.Tests/bin/TestResults"
rm -f "$RESULTS/test.trx"
"$DOTNET" test tests/MeleMcp.Core.Tests -c Release -nologo -v q --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=test.trx" --results-directory "$(wslpath -w "$RESULTS")" \
    ${args[@]+"${args[@]}"} >> "$log" 2>&1 || status=1
python3 -I "$HERE/test_report.py" "$log" "$RESULTS/test.trx" ${report[@]+"${report[@]}"} || status=1
python3 -I -u "$HERE/scenario.py" --selftest || status=1
exit $status
