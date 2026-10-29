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

Build commands (the Galaxy Map Normandy build runs the first five):

| Command | What it does |
|---|---|
| `compileclass <outPcc> <packageName> <Class.uc>...` | Writes a new LE1 package with the classes under a `Package` export, plus a `CombinedStartupReferencer` that keeps them loaded when the file is an AutoLoad startup package (LE1CP's layout). |
| `addactor <levelPcc> <package> <class>` | Places one actor of a script class at the level's origin, with its class defaults. The class must be in an AutoLoad package. |
| `portmesh <srcPcc> <srcUIndex> <pcc> <newName>` | Ports a `StaticMesh` with its materials, textures and the materials' local shaders to the top level of an existing package. |
| `buildsound <donorPcc> <donorCue> <ogg> <samplesPerChannel> <channels> <rate> <outPcc> <newCue> <wavTitle> [volume]` | Clones a donor cue into `outPcc` (created if missing) and puts the Ogg into the waves under the new cue. `--loop` adds a looping node. |
| `wwise <pcc> <bankUIndex> [eventId]...` | Prints a Wwise bank's embedded files and objects, and follows each event ID given to its sounds. With `WEMDIR` (a folder) and `WEMIDS` (comma-separated IDs) set, it also writes those embedded files there as `.wem`. The galaxy map build takes its travel sound from LE3 this way, with `LECGAME=LE3`. |
| `2dapart <src.pcc> <recipe.yaml> <out.pcc>` | Writes a package for ME3Tweaks Mod Manager's Bio2DA merge (`.m3da`): per table of the recipe, a `<Table>_part_1` export of the table's class under `Package` exports named like the table's parents in `src.pcc`, with the same columns and only the recipe's rows. Prints the tables as read back from `out.pcc`. The recipe format is below. |
| `2damerge [--add-only] <m3da> <modDir> <base.pcc> <out.pcc>` | Runs M3's Bio2DA merge of one `.m3da` manifest the way ME3TweaksCore's `Bio2DAMerge` does (Newtonsoft reads the manifest, the same table lookup, then LEC's `Bio2DA.MergeInto`) on a copy of `base.pcc`, saved as `out.pcc`; the merge package is found in `modDir` by file name. Then it reads `out.pcc` back and checks each merged table once: the columns, every base row no part names unchanged, every part row there with its cells (the last part to name a row decides it). It lists the base rows a part replaces; with `--add-only`, replacing a row with other cells is an error. It prints the merged rows and exits 1 on an error or when nothing merged. M3 first resets the tables to its own vanilla copies, so pass a vanilla `base.pcc` when you have one, such as the `Engine.pcc` of M3's game backup. |

A `2dapart` recipe:

```yaml
tables:
  BIOG_2DA_PlotManager_X.PlotManagerGameData:   # the table's full path in src.pcc
    rows:
      InSquadBonus:                             # the new row's name (a number in a Bio2DANumberedRows table)
        from: InSquadJenkins                    # optional: start as a copy of this row
        set: { Index: 4771 }                    # cells by column name; ~ empties a cell
```

A `set` value takes the type of the cell it replaces in the copied row, or else the type most of the column's cells
have. A row that `src.pcc` already has is reported, since M3 replaces it with the recipe's. In a table stored without
cell indexes (every cell filled, as in `PlotManagerGameData`), an empty cell is an error: the cells after it would
shift. A row given twice (`InSquadBonus` and `insquadbonus`, `10` and `010`), a duplicate key or an unknown key is an
error too.

Research dumps: `2da`, `snd`, `swf`, `reflect`, `meshinfo`, `texpng`, `coalesced`, `bankdump`, `eventflags`,
`preprop`, `dumpwave`. `Program.cs` shows their arguments. `ogg` (ISACT's own encoder, through `ISACTTools.dll`)
returned 0 bytes when tried and is kept only as a record.

## License

GPL-3.0 ([LICENSE](../../LICENSE)). What it builds with: [THIRD_PARTY.md](../../THIRD_PARTY.md).
