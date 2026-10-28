#!/usr/bin/env bash
# Build MeleBridge.asi with the Visual Studio Build Tools, inside a checkout of ME3Tweaks/LExASIs on the Windows disk (it
# provides the SDK, the ASI loader interface and the CMake helpers), then stage it in D:\MELE\tools\mele-bridge.
#   ./build.sh              build and stage
#   ./build.sh --install    also copy it to the game's ASI folder (the game must be closed)
#   ./build.sh --uninstall  remove it from the ASI folder (the game must be closed)
# MeleBridge.asi and MelePause.asi (tools/mele-pause) in Game\ME1\Binaries\Win64\ASI are the only files this repo may write
# inside the game install.
set -euo pipefail

LEXASIS="${MELE_LEXASIS:-/mnt/d/MELE/asi-build/LExASIs}"
LEXASIS_COMMIT=aaad03a
# The editor overlay's libraries (MIT, see THIRD_PARTY.md), next to the LExASIs checkout.
THIRD_PARTY="$(dirname "$LEXASIS")/third_party"
IMGUI_COMMIT=c1aaac65e896a45f81f47a4d68f9959e0f86c859      # v1.93.0
IMGUIZMO_COMMIT=105e4fde45e4e74aae8973d98750820ef7aca7e2
GAME="${MELE_GAME:-/mnt/d/SteamLibrary/steamapps/common/Mass Effect Legendary Edition}"
ASI_DIR="$GAME/Game/ME1/Binaries/Win64/ASI"
STAGE=/mnt/d/MELE/tools/mele-bridge
VS='/mnt/c/Program Files (x86)/Microsoft Visual Studio/18/BuildTools'
CMAKE="$VS/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Closed only when tasklist answers and lists no MassEffect1.exe: a failed tasklist counts as running.
game_closed() {
    local out
    out="$(tasklist.exe /FO CSV /NH /FI "IMAGENAME eq MassEffect1.exe" 2>/dev/null)" || return 1
    ! grep -qi 'MassEffect1.exe' <<< "$out"
}

if [[ "${1:-}" == "--uninstall" ]]; then
    game_closed || { echo "MassEffect1.exe is running: close the game first." >&2; exit 1; }
    rm -f "$ASI_DIR/MeleBridge.asi"
    echo "removed $ASI_DIR/MeleBridge.asi"
    exit 0
fi

if [[ ! -d "$LEXASIS/.git" ]]; then
    mkdir -p "$(dirname "$LEXASIS")"
    git.exe clone --recurse-submodules https://github.com/ME3Tweaks/LExASIs.git "$(wslpath -w "$LEXASIS")"
    git.exe -C "$(wslpath -w "$LEXASIS")" checkout --recurse-submodules "$LEXASIS_COMMIT"
fi

# A source checkout at an exact commit; fetched again if the pin moved.
fetch_pinned() {
    local url="$1" dir="$2" commit="$3"
    if [[ ! -d "$dir/.git" ]]; then
        mkdir -p "$dir"
        git.exe -C "$(wslpath -w "$dir")" init -q
        git.exe -C "$(wslpath -w "$dir")" remote add origin "$url"
    fi
    if [[ "$(git.exe -C "$(wslpath -w "$dir")" rev-parse HEAD 2>/dev/null | tr -d '\r')" != "$commit" ]]; then
        git.exe -C "$(wslpath -w "$dir")" fetch -q --depth 1 origin "$commit"
        git.exe -C "$(wslpath -w "$dir")" checkout -q --detach FETCH_HEAD
    fi
}

# One build of the tree at a time (mele-pause's build.sh takes the same lock): two configures and two MSBuilds would
# write the same Common-LE1/LESDK-LE1 objects and PDB. Held until the script exits.
exec 9> "$(dirname "$LEXASIS")/.lexasis-build.lock"
flock -n 9 || { echo "another build of $LEXASIS is running (mele-bridge or mele-pause): waiting for it" >&2; flock 9; }
# Under the lock: mele-pause's build.sh checks out the same imgui.
fetch_pinned https://github.com/ocornut/imgui.git "$THIRD_PARTY/imgui" "$IMGUI_COMMIT"
fetch_pinned https://github.com/CedricGuillemet/ImGuizmo.git "$THIRD_PARTY/ImGuizmo" "$IMGUIZMO_COMMIT"

# Our sources become one more ASI project of that tree.
mkdir -p "$LEXASIS/MeleBridge"
rsync -a --delete "$HERE/src/" "$LEXASIS/MeleBridge/"
grep -q 'add_subdirectory ("MeleBridge")' "$LEXASIS/CMakeLists.txt" || echo 'add_subdirectory ("MeleBridge")' >> "$LEXASIS/CMakeLists.txt"

cd "$LEXASIS"
# LEASI_GAME1_WIN64 would make every ASI build copy itself into the game: keep it unset.
env -u LEASI_GAME1_WIN64 "$CMAKE" --preset VisualStudio -DLEASI_BUILD_LE2=OFF -DLEASI_BUILD_LE3=OFF -DLEASI_BUILD_LEL=OFF \
    "-DMELE_THIRD_PARTY=$(wslpath -m "$THIRD_PARTY")" > /dev/null 2>&1 \
    || { echo "cmake configure failed: run it by hand in $LEXASIS" >&2; exit 1; }
log="$(mktemp)"
if ! "$CMAKE" --build Build --config RELEASE --target MeleBridge-LE1 -- /m /v:minimal /nologo > "$log" 2>&1; then
    tr -d '\r' < "$log" | grep -E 'error|warning' | sed -E 's|^.*LExASIs\\||; s| \[[^]]*vcxproj\]||' | sort -u
    rm -f "$log"
    exit 1
fi
rm -f "$log"

mkdir -p "$STAGE"
cp Build/RELEASE/MeleBridge.asi Build/RELEASE/MeleBridge.pdb "$STAGE/"
echo "built $STAGE/MeleBridge.asi"

if [[ "${1:-}" == "--install" ]]; then
    game_closed || { echo "MassEffect1.exe is running: close the game first (built, not installed)." >&2; exit 1; }
    cp "$STAGE/MeleBridge.asi" "$ASI_DIR/MeleBridge.asi"
    echo "installed $ASI_DIR/MeleBridge.asi"
fi
