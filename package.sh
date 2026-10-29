#!/usr/bin/env bash
# Build the mele-mcp release zip: dist/mele-mcp-v<version>-win-x64.zip and dist/SHA256SUMS.
#   ./package.sh
# Runs in WSL and finds the Windows dotnet.exe as tools/mele-mcp/scripts/deploy.sh does: MELE_DOTNET from the
# environment, then from tools/mele-mcp/build.env. The version is <Version> in tools/mele-mcp/src/MeleMcp/MeleMcp.csproj.
# The zip holds a framework-dependent win-x64 publish (the user installs the .NET 8 runtime), LICENSE, THIRD_PARTY.md,
# README.txt and licenses\: the texts of the NuGet packages (taken from the NuGet cache), of the native DLLs and of
# Legendary Explorer's notices. It never holds Oodle, a native DLL whose license nobody checked (THIRD_PARTY.md), or
# this machine's paths.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MCP="$HERE/tools/mele-mcp"
LEX="$MCP/external/LegendaryExplorer"
if [[ -f "$MCP/build.env" ]]; then
    declare -A caller=()
    for v in $(compgen -e MELE_ || true); do caller[$v]="${!v}"; done
    set -a; . "$MCP/build.env"; set +a
    for v in "${!caller[@]}"; do export "$v=${caller[$v]}"; done
fi

die() { echo "package.sh: $*" >&2; exit 1; }

# Every native file of the publish output is in one of these lists, or the script fails. THIRD_PARTY.md says why.
SHIP_NATIVE=(MeleMcp.exe CompressionWrappers.dll TexConverter.dll)
LEAVE_OUT=(ISACTTools.dll)    # no source and no license; LEC and mele-mcp never load it (lectool's ogg command does)

DOTNET="${MELE_DOTNET:-dotnet.exe}"
command -v "$DOTNET" >/dev/null || die "$DOTNET not found: set MELE_DOTNET to the Windows dotnet.exe (SDK: global.json)"
[[ -f "$LEX/LICENSE" ]] || die "no Legendary Explorer submodule: run git submodule update --init"
VERSION="$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' "$MCP/src/MeleMcp/MeleMcp.csproj")"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "no X.Y.Z <Version> in MeleMcp.csproj (got '$VERSION')"
NAME="mele-mcp-v$VERSION-win-x64"
DIST="$HERE/dist"
STAGE="$DIST/stage/$NAME"
BUILD="$DIST/build"
COMMIT="$(git -C "$HERE" describe --tags --always --dirty --abbrev=7)"
LEC_COMMIT="$(git -C "$LEX" rev-parse --short=7 HEAD)"
[[ "$COMMIT" != *-dirty ]] || echo "package.sh: warning: uncommitted changes, so README.txt names $COMMIT" >&2
NUGET="$(cd "$MCP" && "$DOTNET" nuget locals global-packages --list | tr -d '\r' | sed -n 's/^global-packages: *//p')"
[[ -n "$NUGET" ]] || die "cannot find the NuGet package folder (dotnet nuget locals global-packages --list)"
NUGET="$(wslpath -u "$NUGET")"

rm -rf "$DIST/stage" "$BUILD"
mkdir -p "$STAGE"
log="$(mktemp)"
# No symbols: the PDBs and the DLLs' debug directory would carry this machine's paths. The build runs from scratch in
# dist/build: a DebugType change does not make MSBuild compile again, so the shared obj folders could hand the publish
# DLLs with symbols that deploy.sh or another build left there.
if ! (cd "$MCP" && "$DOTNET" publish src/MeleMcp/MeleMcp.csproj -c Release -r win-x64 --self-contained false \
        --artifacts-path "$(wslpath -w "$BUILD")" -p:DebugType=none -p:DebugSymbols=false -nologo -v q \
        -o "$(wslpath -w "$STAGE")") > "$log" 2>&1; then
    errors="$(tr -d '\r' < "$log" | grep -E ' error ' | sort -u || true)"
    if [[ -n "$errors" ]]; then echo "$errors"; else echo "publish failed:"; tr -d '\r' < "$log" | tail -n 20; fi
    rm -f "$log"
    exit 1
fi
rm -f "$log"
rm -rf "$BUILD"

for f in "${LEAVE_OUT[@]}"; do rm -f "$STAGE/$f"; done
cp "$HERE/LICENSE" "$HERE/THIRD_PARTY.md" "$STAGE/"
sed -e "s/@VERSION@/$VERSION/g" -e "s/@COMMIT@/$COMMIT/g" -e "s/@LEC_COMMIT@/$LEC_COMMIT/g" \
    "$HERE/packaging/README.txt" > "$STAGE/README.txt"
mkdir -p "$STAGE/licenses/LegendaryExplorer"
cp "$HERE/packaging/licenses/LICENSE_Compressonator.txt" "$STAGE/licenses/"
# Legendary Explorer's notices, Peregrine66's aside: that code is in the Legendary Explorer app, not in LEC.
find "$LEX/Licenses" -maxdepth 1 -type f ! -name 'License_Peregrine66.txt' -exec cp {} "$STAGE/licenses/LegendaryExplorer/" \;

# This machine's paths, which no file of the zip may hold: the repo and the home folder, as WSL and Windows see them.
MACHINE_PATHS="$(printf '%s\n' "$HERE" "$(wslpath -w "$HERE")" "$HOME" "$(wslpath -w "$HOME")")"

python3 -I - "$STAGE" "$NUGET" "$HERE/packaging/licenses" "$DIST/$NAME.zip" "${SHIP_NATIVE[*]}" "${LEAVE_OUT[*]}" \
    "$MACHINE_PATHS" <<'PY'
import glob, hashlib, json, os, re, struct, sys, zipfile

stage, nuget, texts, zip_path = sys.argv[1:5]
ship, leave_out = set(sys.argv[5].lower().split()), set(sys.argv[6].lower().split())
machine_paths = sorted({p for p in sys.argv[7].splitlines() if len(p) > 3})
licenses = os.path.join(stage, "licenses")
partial = zip_path + ".part"
# MIT code in the shipped native files: the app host comes from dotnet/runtime, like the System.* packages, and
# TexConverter.cpp includes DirectXTex, whose headers carry this notice.
NATIVE_MIT = ["MeleMcp.exe, the .NET app host from dotnet/runtime: © Microsoft Corporation. All rights reserved.",
              "DirectXTex, compiled into TexConverter.dll: Copyright (c) Microsoft Corporation."]

def die(message):
    if os.path.exists(partial):
        os.remove(partial)
    sys.exit(f"package.sh: {message}")

def files(root):
    return sorted(os.path.relpath(os.path.join(d, n), root).replace(os.sep, "/") for d, _, ns in os.walk(root) for n in ns)

def check(names, where):
    for name in names:
        base = name.rsplit("/", 1)[-1].lower()
        if base.startswith("oo2core"):
            die(f"Oodle in the {where}: {name}")
        if base in leave_out:
            die(f"{name} must stay out of the {where}")

def managed(path):
    """True for a .NET assembly: its PE optional header has a CLR runtime header (data directory 14)."""
    with open(path, "rb") as f:
        head = f.read(4096)
    pe = struct.unpack_from("<I", head, 0x3C)[0] if head[:2] == b"MZ" else 0
    if not pe or head[pe:pe + 4] != b"PE\0\0":
        die(f"{path} is not a PE file")
    optional = pe + 24
    dirs = optional + (112 if struct.unpack_from("<H", head, optional)[0] == 0x20B else 96)
    return struct.unpack_from("<I", head, dirs - 4)[0] > 14 and struct.unpack_from("<II", head, dirs + 14 * 8) != (0, 0)

names = files(stage)
check(names, "staging folder")
native = {n.lower(): n for n in names if n.lower().endswith((".dll", ".exe")) and not managed(os.path.join(stage, n))}
if native.keys() - ship:
    die(f"native files nobody checked: {', '.join(sorted(native[n] for n in native.keys() - ship))}. Check each license, then add it to "
        "SHIP_NATIVE or LEAVE_OUT in package.sh and to THIRD_PARTY.md")
if ship - native.keys():
    die(f"SHIP_NATIVE names files the publish no longer has: {', '.join(sorted(ship - native.keys()))}")

# NuGet packages: copy the license and notice files each ships (once per content), and list the MIT and Apache-2.0
# ones, with their copyright lines, above the standard text of their license.
deps = json.load(open(os.path.join(stage, "MeleMcp.deps.json"), encoding="utf-8-sig"))
listed = {"MIT": [], "Apache-2.0": []}
saved = {}
for key, lib in sorted(deps["libraries"].items(), key=lambda kv: kv[0].lower()):
    if lib["type"] != "package":
        continue
    package, version = key.split("/")
    folder = os.path.join(nuget, lib["path"])
    nuspec = glob.glob(os.path.join(folder, "*.nuspec"))
    if not nuspec:
        die(f"{package} {version} is not in the NuGet cache {nuget}: run dotnet restore")
    spec = open(nuspec[0], encoding="utf-8-sig").read()
    found = re.search(r'<license type="(\w+)">([^<]+)</license>', spec)
    copyright = re.search(r"<copyright>([^<]*)</copyright>", spec)
    own = []
    for name in sorted(os.listdir(folder)):
        path = os.path.join(folder, name)
        if not os.path.isfile(path) or not re.search(r"licen[cs]e|notice", name, re.I):
            continue
        data = open(path, "rb").read()
        digest = hashlib.sha256(data).hexdigest()
        if digest not in saved:
            saved[digest] = f"{package}.{name}" + ("" if "." in name else ".txt")
            open(os.path.join(licenses, saved[digest]), "wb").write(data)
        own.append(saved[digest])
    if found is None:
        die(f"{package} {version}: its nuspec has no <license>")
    kind, value = found.groups()
    if kind == "file":
        if not own:
            die(f"{package} {version}: its license file {value} is missing from {folder}")
        continue
    text = "MIT" if value in ("MIT", "MIT OR Unlicense") else value
    if text not in listed:
        die(f"{package} {version}: license {value} is not handled. Check it, then add its text to packaging/licenses")
    line = f"{package} {version}" + (f" ({value})" if value != text else "")
    line += f": {copyright.group(1).strip() if copyright else 'the package names no copyright holder'}"
    if own:
        line += f"; see also {', '.join(own)}"
    listed[text].append(line)
listed["MIT"] += NATIVE_MIT

for text, lines in listed.items():
    with open(os.path.join(licenses, f"{text}.txt"), "w", encoding="utf-8", newline="\n") as out:
        out.write(f"The {text} license below covers these parts of mele-mcp, each under the copyright notice shown:\n\n")
        out.write("".join(f"  {line}\n" for line in lines) + "\n")
        out.write(open(os.path.join(texts, f"{text}.txt"), encoding="utf-8").read())

names = files(stage)
for name in names:
    data = open(os.path.join(stage, name), "rb").read().lower()
    for path in machine_paths:
        if any(path.lower().encode(code) in data for code in ("utf-8", "utf-16-le")):
            die(f"{name} holds this machine's path {path} (a PDB path or a source path compiled in)")
with zipfile.ZipFile(partial, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name in names:
        z.write(os.path.join(stage, name), name)
with zipfile.ZipFile(partial) as z:
    if z.testzip() is not None:
        die(f"{partial} fails its CRC check")
    inside = z.namelist()
check(inside, "zip")
missing = {"MeleMcp.exe", "MeleMcp.dll", "LICENSE", "THIRD_PARTY.md", "README.txt"} - set(inside)
if missing:
    die(f"the zip lacks {', '.join(sorted(missing))}")
os.replace(partial, zip_path)
print(f"{len(inside)} files, native: {', '.join(sorted(native.values()))}")
PY

(cd "$DIST" && sha256sum "$NAME.zip" > SHA256SUMS)
echo "wrote $DIST/$NAME.zip"
cat "$DIST/SHA256SUMS"
