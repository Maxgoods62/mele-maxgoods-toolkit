# lectool

A small command-line helper on LegendaryExplorerCore (LEC), for jobs mele-mcp has no tool for yet: compiling script
classes into a package, adding a script actor to a level, porting a mesh, building an LE1 (ISACT) sound. It grew
during the galaxy map work ([docs/09](../../docs/09-galaxy-map-normandy.md) §5, §6.1) and builds
[Galaxy Map Normandy](../../mods/galaxy-map-normandy/README.md)'s packages.

## Build

It uses the same LEC as mele-mcp (the `tools/mele-mcp/external/LegendaryExplorer` submodule) and the Windows .NET SDK:

```bash
cd tools/lectool
"$MELE_DOTNET" build lectool.csproj -c Release -o '<out dir>'
```

## Run

```bash
MELE_LE_DIR='<Legendary Edition folder>' WSLENV=MELE_LE_DIR '<out dir>/lectool.exe' <command> <args>
```

`MELE_LE_DIR` is optional: without it LEC finds the game through the registry. `WSLENV` passes it from WSL to the
Windows process. `LECGAME` (LE1, LE2 or LE3; default LE1) picks the game whose object database loads.

## Commands

Build commands (Galaxy Map Normandy):

| Command | What it does |
|---|---|
| `compileclass <outPcc> <packageName> <Class.uc>...` | New LE1 package with the classes under a `Package` export, plus a `CombinedStartupReferencer` for an AutoLoad startup package (LE1CP's layout) |
| `addactor <levelPcc> <package> <class>` | One actor of a script class at the level's origin, with class defaults; the class must be in an AutoLoad package |
| `portmesh <srcPcc> <srcUIndex> <pcc> <newName>` | Ports a `StaticMesh` with its materials, textures and local shaders to the top level of a package |
| `buildsound <donorPcc> <donorCue> <ogg> <samplesPerChannel> <channels> <rate> <outPcc> <newCue> <wavTitle> [volume]` | Clones a donor cue and puts the Ogg into its waves (`--loop` adds a looping node) |

Research dumps: `2da`, `snd`, `wwise`, `swf`, `reflect`, `meshinfo`, `texpng`, `coalesced`, `bankdump`, `eventflags`,
`preprop`, `dumpwave`; `Program.cs` shows their arguments (most branches start with a usage comment). `ogg` (ISACT's
own encoder through `ISACTTools.dll`) returned 0 bytes when tried (docs/09 §5) and is kept only as a record.
