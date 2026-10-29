#!/usr/bin/env bash
# Build MeleBridge.asi with the Visual Studio Build Tools, inside this tool's own checkout of ME3Tweaks/LExASIs on the
# Windows disk (it provides the SDK, the ASI loader interface and the CMake helpers), then stage it in MELE_STAGE.
#   ./build.sh              build and stage
#   ./build.sh --install    also copy it to the game's ASI folder (the game must be closed)
#   ./build.sh --uninstall  remove it from the ASI folder (the game must be closed)
# Runs in WSL. Settings come from the environment or from build.env (not in git, see build.env.example); a variable the
# caller set wins over build.env. All are optional:
#   MELE_LE_DIR   the Legendary Edition folder, as a Windows path; only --install and --uninstall use it
#                 (default: the install folder in the registry)
#   MELE_VS       the Visual Studio or Build Tools folder with the C++ tools and CMake (default: found with vswhere)
#   MELE_LEXASIS  the LExASIs checkout, cloned on the first build; the editor overlay's libraries go next to it in
#                 third_party. Keep it on the Windows disk: CMake and MSBuild do not work well on \\wsl.localhost paths
#                 (default: %LOCALAPPDATA%\mele-asi-build\MeleBridge\LExASIs)
#   MELE_STAGE    where MeleBridge.asi and its .pdb are copied (default: dist/ next to this script)
# MeleBridge.asi in Game\ME1\Binaries\Win64\ASI is the only file this tool writes inside the game install.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The caller's values are put back after build.env, so a test can override one of them.
if [[ -f "$HERE/build.env" ]]; then
    saved="$(declare -p MELE_LE_DIR MELE_VS MELE_LEXASIS MELE_STAGE 2>/dev/null || true)"
    . "$HERE/build.env"
    eval "$saved"
fi

LEXASIS_COMMIT=aaad03a
IMGUI_COMMIT=c1aaac65e896a45f81f47a4d68f9959e0f86c859      # v1.93.0
IMGUIZMO_COMMIT=105e4fde45e4e74aae8973d98750820ef7aca7e2

die() { echo "build.sh: $*" >&2; exit 1; }

# A Windows environment variable as a WSL path; empty if it is not set. PowerShell prints it in UTF-8 (cmd.exe would use
# the OEM code page and break a non-ASCII user name).
win_env() {
    local v
    v="$(powershell.exe -NoProfile -NonInteractive -Command \
        "[Console]::OutputEncoding = [Text.Encoding]::UTF8; [Environment]::GetEnvironmentVariable('$1')" 2>/dev/null \
        | tr -d '\r' || true)"
    if [[ -n "$v" ]]; then wslpath -u "$v" 2>/dev/null || true; fi
}

# The Legendary Edition folder as a WSL path: MELE_LE_DIR, else the two registry entries LegendaryExplorerCore reads
# (LEDirectory.LookupDefaultPath): "Install Dir" of HKLM\SOFTWARE\BioWare\*Legendary Edition, then Steam's uninstall entry.
le_dir() {
    local win="${MELE_LE_DIR:-}" dir
    if [[ -z "$win" ]]; then
        win="$(reg.exe query 'HKLM\SOFTWARE\BioWare' /s /v 'Install Dir' 2>/dev/null | tr -d '\r' \
            | awk '/^HKEY_/ { le = /Legendary Edition$/ } le && sub(/^ +Install Dir +REG_SZ +/, "") { print; exit }' || true)"
    fi
    if [[ -z "$win" ]]; then
        win="$(reg.exe query 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1328670' /v InstallLocation \
            2>/dev/null | tr -d '\r' | sed -n 's/^ *InstallLocation *REG_SZ *//p' || true)"
    fi
    [[ -n "$win" ]] || die "Legendary Edition not found in the registry: set MELE_LE_DIR to its folder, as a Windows path"
    win="${win%\\}"
    dir="$(wslpath -u "$win" 2>/dev/null || true)"
    [[ -n "$dir" && -d "$dir/Game/ME1" ]] || die "no Game\\ME1 in '$win': set MELE_LE_DIR to the Legendary Edition folder"
    echo "$dir"
}

# Closed only when tasklist answers and lists no MassEffect1.exe: a failed tasklist counts as running.
game_closed() {
    local out
    out="$(tasklist.exe /FO CSV /NH /FI "IMAGENAME eq MassEffect1.exe" 2>/dev/null)" || return 1
    ! grep -qi 'MassEffect1.exe' <<< "$out"
}

if [[ "${1:-}" == "--uninstall" ]]; then
    ASI_DIR="$(le_dir)/Game/ME1/Binaries/Win64/ASI"
    game_closed || { echo "MassEffect1.exe is running: close the game first." >&2; exit 1; }
    rm -f "$ASI_DIR/MeleBridge.asi"
    echo "removed $ASI_DIR/MeleBridge.asi"
    exit 0
fi

# Visual Studio 18 (2026) with the C++ x64 tools: LExASIs' preset uses the "Visual Studio 18 2026" generator, which the
# CMake of older versions does not know.
VS="${MELE_VS:-}"
if [[ -z "$VS" ]]; then
    NEED_VS="the Visual Studio 18 (2026) Build Tools with C++ are needed (or set MELE_VS)"
    VSWHERE="$(win_env 'ProgramFiles(x86)')/Microsoft Visual Studio/Installer/vswhere.exe"
    [[ -f "$VSWHERE" ]] || die "no vswhere.exe: $NEED_VS"
    VS="$("$VSWHERE" -latest -products '*' -version '[18.0,19.0)' \
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')"
    [[ -n "$VS" ]] || die "vswhere found no Visual Studio 18 (2026) with the C++ x64 tools: $NEED_VS"
    VS="$(wslpath -u "$VS")"
fi
CMAKE="$VS/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
[[ -f "$CMAKE" ]] || die "no CMake in $VS: add its \"C++ CMake tools for Windows\" component, or set MELE_VS"

LEXASIS="${MELE_LEXASIS:-}"
if [[ -z "$LEXASIS" ]]; then
    LOCAL="$(win_env LOCALAPPDATA)"
    [[ -n "$LOCAL" ]] || die "no %LOCALAPPDATA% from PowerShell: set MELE_LEXASIS to a folder on the Windows disk"
    LEXASIS="$LOCAL/mele-asi-build/MeleBridge/LExASIs"
fi
# The editor overlay's libraries (MIT, see THIRD_PARTY.md), next to the LExASIs checkout.
THIRD_PARTY="$(dirname "$LEXASIS")/third_party"
STAGE="${MELE_STAGE:-$HERE/dist}"

# One build of the tree at a time (two runs, or two worktrees with the same MELE_LEXASIS, would write the same objects
# and PDB). Held until the script exits.
mkdir -p "$(dirname "$LEXASIS")"
exec 9> "$(dirname "$LEXASIS")/.lexasis-build.lock"
flock -n 9 || { echo "another build of $LEXASIS is running: waiting for it" >&2; flock 9; }

# LExASIs at the pinned commit with all its submodules, checked on every run: an interrupted first clone or a new pin is
# repaired here.
lexasis_git() { git.exe -C "$(wslpath -w "$LEXASIS")" "$@"; }
if [[ ! -d "$LEXASIS/.git" ]]; then
    git.exe clone https://github.com/ME3Tweaks/LExASIs.git "$(wslpath -w "$LEXASIS")"
fi
pinned="$(lexasis_git rev-parse -q --verify "$LEXASIS_COMMIT^{commit}" 2>/dev/null | tr -d '\r' || true)"
current="$(lexasis_git rev-parse -q --verify HEAD 2>/dev/null | tr -d '\r' || true)"
# A submodule line that starts with - is not checked out, + is at another commit, U has a conflict.
subs="$(lexasis_git submodule status --recursive 2>/dev/null)" || subs=-
if [[ -z "$pinned" || "$current" != "$pinned" ]] || grep -q '^[-+U]' <<< "$subs"; then
    echo "checking out LExASIs $LEXASIS_COMMIT and its submodules in $LEXASIS" >&2
    # The project lines our builds add to CMakeLists.txt would block a checkout that changes the file: they go (this
    # script's line is added again below). Any other change is someone's own work: the checkout keeps it, or refuses.
    CML="$LEXASIS/CMakeLists.txt"
    if [[ -f "$CML" ]]; then
        sed -i -e '/^if (EXISTS "${CMAKE_CURRENT_SOURCE_DIR}\/Mele[A-Za-z]*")$/,/^endif ()$/d' \
            -e '/^add_subdirectory ("Mele[A-Za-z]*")$/d' "$CML"
        lexasis_git diff --quiet -- CMakeLists.txt \
            || die "CMakeLists.txt in $LEXASIS has changes of your own: commit or stash them, then build again"
    fi
    { [[ -n "$pinned" ]] || lexasis_git fetch -q origin; } \
        && lexasis_git checkout -q --recurse-submodules "$LEXASIS_COMMIT" \
        && lexasis_git submodule update -q --init --recursive \
        || die "could not check out LExASIs $LEXASIS_COMMIT in $LEXASIS (git says why above): commit or stash your own changes there; with none, delete that folder and build again"
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
fetch_pinned https://github.com/ocornut/imgui.git "$THIRD_PARTY/imgui" "$IMGUI_COMMIT"
fetch_pinned https://github.com/CedricGuillemet/ImGuizmo.git "$THIRD_PARTY/ImGuizmo" "$IMGUIZMO_COMMIT"

# Our sources become one more ASI project of that tree. The project line is guarded, so the tree still configures if
# LExASIs/MeleBridge is ever deleted; an unguarded line from an earlier build is replaced.
mkdir -p "$LEXASIS/MeleBridge"
rsync -a --delete "$HERE/src/" "$LEXASIS/MeleBridge/"
CML="$LEXASIS/CMakeLists.txt"
if grep -qx 'add_subdirectory ("MeleBridge")' "$CML"; then sed -i '/^add_subdirectory ("MeleBridge")$/d' "$CML"; fi
grep -qF 'EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/MeleBridge"' "$CML" \
    || printf '%s\n' 'if (EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/MeleBridge")' '  add_subdirectory ("MeleBridge")' 'endif ()' >> "$CML"

cd "$LEXASIS"
# A LEASI_GAME<n>_WIN64 variable makes the ASI builds of every game turned on copy themselves into that game. env -u
# removes one WSLENV would forward to cmake.exe; one set on the Windows side still reaches it, so the configure output
# is checked too. Every game flag is passed, so a value cached by an earlier configure never sticks.
log="$(mktemp)"
if ! env -u LEASI_GAME1_WIN64 -u LEASI_GAME2_WIN64 -u LEASI_GAME3_WIN64 -u LEASI_GAMEL_WIN64 "$CMAKE" \
    --preset VisualStudio -DLEASI_BUILD_LE1=ON -DLEASI_BUILD_LE2=OFF -DLEASI_BUILD_LE3=OFF -DLEASI_BUILD_LEL=OFF \
    "-DMELE_THIRD_PARTY=$(wslpath -m "$THIRD_PARTY")" > "$log" 2>&1; then
    rm -f "$log"
    die "cmake configure failed: run it by hand in $LEXASIS"
fi
if grep -q 'User-provided Mass Effect' "$log"; then
    rm -f "$log"
    die "a LEASI_GAME*_WIN64 variable is set in the Windows environment, and it makes the build copy itself into the game: remove it (System Properties, Environment Variables), then open a new terminal and build again"
fi
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
    ASI_DIR="$(le_dir)/Game/ME1/Binaries/Win64/ASI"
    game_closed || { echo "MassEffect1.exe is running: close the game first (built, not installed)." >&2; exit 1; }
    cp "$STAGE/MeleBridge.asi" "$ASI_DIR/MeleBridge.asi"
    echo "installed $ASI_DIR/MeleBridge.asi"
fi
