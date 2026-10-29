# lectool

A small command-line tool on LegendaryExplorerCore (LEC), for jobs mele-mcp has no tool for yet: compiling script
classes into a package, adding a script actor to a level, porting a mesh and building an LE1 (ISACT) sound. It grew
out of the galaxy map work, and the [Galaxy Map Normandy](https://github.com/Maxgoods62/galaxy-map-normandy) build runs
it to make that mod's packages.

## Build

It builds against the same LEC as mele-mcp, the `tools/mele-mcp/external/LegendaryExplorer` submodule
(`git submodule update --init` in the toolkit checkout), with the Windows .NET SDK. `global.json` asks for SDK 10.0.402
or a later 10.0 feature band; the program targets `net8.0-windows`, x64.

```bash
cd tools/lectool
"$MELE_DOTNET" build lectool.csproj -c Release -o '<out dir>'
```

`MELE_DOTNET` is the Windows `dotnet.exe` as a WSL path, the same setting as in mele-mcp's `build.env`. A `dotnet.exe`
on your PATH works too.

## Run

```bash
MELE_LE_DIR='<LE folder>' WSLENV=MELE_LE_DIR '<out dir>/lectool.exe' <command> <args>
```

`MELE_LE_DIR` is the Legendary Edition folder, as a Windows path. It is optional: without it LEC finds the game through
the registry. `WSLENV` passes it from WSL to the Windows process. `LECGAME` (LE1, LE2 or LE3; default LE1) picks the game
whose object database loads; add it to `WSLENV` as well when you set it.

## Commands

Build commands, the ones the Galaxy Map Normandy build runs:

| Command | What it does |
|---|---|
| `compileclass <outPcc> <packageName> <Class.uc>...` | Writes a new LE1 package with the classes under a `Package` export, plus a `CombinedStartupReferencer` that keeps them loaded when the file is an AutoLoad startup package (LE1CP's layout). |
| `addactor <levelPcc> <package> <class>` | Places one actor of a script class at the level's origin, with its class defaults. The class must be in an AutoLoad package. |
| `portmesh <srcPcc> <srcUIndex> <pcc> <newName>` | Ports a `StaticMesh` with its materials, textures and the materials' local shaders to the top level of an existing package. |
| `buildsound <donorPcc> <donorCue> <ogg> <samplesPerChannel> <channels> <rate> <outPcc> <newCue> <wavTitle> [volume]` | Clones a donor cue into `outPcc` (created if missing) and puts the Ogg into the waves under the new cue. `--loop` adds a looping node. |
| `wwise <pcc> <bankUIndex> [eventId]...` | Prints a Wwise bank's embedded files and objects, and follows each event ID given to its sounds. With `WEMDIR` (a folder) and `WEMIDS` (comma-separated IDs) set, it also writes those embedded files there as `.wem`. The galaxy map build takes its travel sound from LE3 this way, with `LECGAME=LE3`. |

Research dumps: `2da`, `snd`, `swf`, `reflect`, `meshinfo`, `texpng`, `coalesced`, `bankdump`, `eventflags`,
`preprop`, `dumpwave`. `Program.cs` shows their arguments. `ogg` (ISACT's own encoder, through `ISACTTools.dll`)
returned 0 bytes when tried and is kept only as a record.

## License

GPL-3.0 ([LICENSE](../../LICENSE)). What it builds with: [THIRD_PARTY.md](../../THIRD_PARTY.md).
