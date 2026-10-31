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

Build commands (the Galaxy Map Normandy build runs the first seven):

| Command | What it does |
|---|---|
| `compileclass <outPcc> <packageName> <Class.uc>... [--pkg=<name> <Class.uc>...] [--unforce=<name>] [--no-referencer]` | Writes a new LE1 package with the classes under a `Package` export, plus a `CombinedStartupReferencer` that keeps them loaded when the file is an AutoLoad startup package (LE1CP's layout). `--pkg=` starts another `Package` export for the files after it, such as a copy of another mod's base class next to our subclasses. LEC marks each `Package` export and its contents `ForcedExport` (looked up in memory by their own path); `--unforce=` clears that for one package, so its classes load from the file as `<file>.<name>.<class>`, as LE1CP's Mod Settings Menu loads its pages. `--no-referencer` leaves the referencer out. |
| `addactor <levelPcc> <package> <class>` | Places one actor of a script class at the level's origin, with its class defaults. The class must be in an AutoLoad package. |
| `portmesh <srcPcc> <srcUIndex> <pcc> <newName>` | Ports a `StaticMesh` with its materials, textures and the materials' local shaders to the top level of an existing package. |
| `addtexture <pcc> <name> <png> [--argb]` | Adds a new top-level `Texture2D` to an existing package from a PNG: DXT5 (with `--argb` uncompressed, as the vanilla UI gradients are, for dark colours that DXT5 would band), one mip, not sRGB, `TEXTUREGROUP_UI`, `NeverStream`, stored in the package. Refuses a name the package already has. |
| `tlkpackage <outPcc> <strings.tsv> [--ids-from <other.tsv>]` | Writes a new LE1 package with one language of a mod's GlobalTlk: the exports `GlobalTlk_tlk` and `GlobalTlk_tlk_M`, the same strings in both, in the layout of the LE1 Community Patch's files. The strings file is UTF-8, one string per line: an id, one tab, the text; empty lines and lines starting with `#` are skipped. `--ids-from` requires exactly the ids of another strings file, so no language lacks a string. It reads the package back and exits 1 unless every string comes back unchanged. |
| `buildsound <donorPcc> <donorCue> <ogg> <samplesPerChannel> <channels> <rate> <outPcc> <newCue> <wavTitle> [volume]` | Clones a donor cue into `outPcc` (created if missing) and puts the Ogg into the waves under the new cue. `--loop` adds a looping node. |
| `wwise <pcc> <bankUIndex> [eventId]...` | Prints a Wwise bank's embedded files and objects, and follows each event ID given to its sounds. With `WEMDIR` (a folder) and `WEMIDS` (comma-separated IDs) set, it also writes those embedded files there as `.wem`. The galaxy map build takes its travel sound from LE3 this way, with `LECGAME=LE3`. |
| `2dapart <src.pcc> <recipe.yaml> <out.pcc>` | Writes a package for ME3Tweaks Mod Manager's Bio2DA merge (`.m3da`): per table of the recipe, a `<Table>_part_1` export of the table's class under `Package` exports named like the table's parents in `src.pcc`, with the same columns and only the recipe's rows. Prints the tables as read back from `out.pcc`. The recipe format is below. |
| `2damerge [--add-only] <m3da> <modDir> <base.pcc> <out.pcc>` | Runs M3's Bio2DA merge of one `.m3da` manifest the way ME3TweaksCore's `Bio2DAMerge` does (Newtonsoft reads the manifest, the same table lookup, then LEC's `Bio2DA.MergeInto`) on a copy of `base.pcc`, saved as `out.pcc`; the merge package is found in `modDir` by file name. Then it reads `out.pcc` back and checks each merged table once: the columns, every base row no part names unchanged, every part row there with its cells (the last part to name a row decides it). It lists the base rows a part replaces; with `--add-only`, replacing a row with other cells is an error. It prints the merged rows and exits 1 on an error or when nothing merged. M3 first resets the tables to its own vanilla copies, so pass a vanilla `base.pcc` when you have one, such as the `Engine.pcc` of M3's game backup. |
| `actortype <recipe.yaml> <template.pcc> <look.pcc> <out.pcc>` | Writes a new package holding one actor type at its top level, not forced (a spawned pawn's Tag is its actor type's object name), from two existing ones: plain values and imports of the template's actor type (`keep`), and objects of the look's actor type (`take`), each copied with what it references. A subobject of the look's actor type goes under the new one; other exports keep their paths, unless their tree is listed in `moveRoots`: those move under a Package export `moveUnder`, not forced, so they share no path with the look package's own copies. `set` gives plain values, typed by the class. An `ObjectReferencer` lists the actor type. No shader cache is copied; `pkgcheck` says whether the game's `RefShaderCache` covers the materials. Prints the top-level exports and the actor type's properties. The recipe format is below. |
| `pkgcheck <pcc> [--like <ref.pcc>]... [--allow-import <path>]... [--forbid-root <name>]...` | Checks a built package. Errors: anything LEC's reference check reports, two exports with one path, a material (decals included) whose shaders are neither in the package nor in the game's `RefShaderCache`, a Class import that does not resolve and is not a native class of the package it names, and any other import that LEC cannot resolve to an object of the same class and class package. With `--like`, every import that is not a Package or a Class must also have its path and class in one of the reference packages (as an import or an export). `--allow-import` exempts one path and reports it; `--forbid-root` fails on an import or a top-level export with that root. Exits 1 on an error. |
| `pkglist <pcc>` | One tab-separated line per export (UIndex, class, path, `F` when forced, object flags, data size, a texture's `TextureFileCacheName`) and per import. |
| `gfxpackage <src.pcc> <movie> <out.pcc> [--raw-data <file>] [--copy-textures] [--add-texture <name> <like>]... [--texture <name> <png\|#RRGGBB>]...` | Writes a new package holding a copy of a `GFxMovieInfo` at its top level, not forced, under the movie's own name, so a `HandlerLibrary` line with `PanelResource="<out file name>.<movie name>"` opens it. `--raw-data` gives the copy that file's bytes (a patched movie) instead of the source's. The textures in the movie's `References` stay imports of their paths in `src.pcc` (a startup package's textures are always loaded, and texture mods that change them still show); `--copy-textures` makes them top-level copies instead, as LE1CP's GUI packages have them. `--add-texture` adds to the references a top-level copy of the referenced texture `<like>` under a new name, for an image the patched movie adds. `--texture` makes a referenced or added texture a top-level copy whose image comes from a PNG of the texture's size, or is one colour, in the texture's format, mip count and `NeverStream`, stored in the package. Prints the movie's `RawData` size and SHA-1 (with `--raw-data`, the source's too), and how each reference was written. Refuses a movie with binary data or other object, array or struct properties, two references with one object name, a `--texture` that is neither referenced nor added, an `--add-texture` whose name the movie already references or whose `<like>` is not a referenced `Texture2D` export, and an output path that is also an input. |

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

An `actortype` recipe:

```yaml
name: hench_bonus                         # the new actor type's object name
template:
  actorType: hench_jenkins                # its full path in template.pcc
  keep: [m_eRank, AIController]           # properties it sets: plain values, or objects that are imports
look:
  actorType: BIOA_PR1_C.BAT.prc1_lieutenant
  take:                                   # object properties, each with the full path it must name in look.pcc
    m_oAppearance: BIOG_BAT_Appr_L.Character.BAT_Character_Appr
  moveUnder: Charn                        # optional, with moveRoots: the Package export the trees move under
  moveRoots: [BIOG_BAT_Appr_L]            # top-level trees of the copy
set: { ActorGameNameStrRef: 176039 }      # int, string ref, float, bool, name, string or enum values
```

Properties the recipe does not name are left out. The template's and the look's actor types must have the same class.
Paths are as in the files on disk. (Porting from a `BIOG_` or `GUI_` file moves its top-level exports under a package
named after the file in memory, so the copies may come out under that package.) Each error is one line, and no package
is written: a property named twice, a `keep` the template does not set or that names an export, an array or struct
`keep`, a `take` that names another path than the recipe's, a `set` the class does not have or a value of the wrong
type (an enum value must be one of its enum's names), a `moveUnder` that is the actor type's name, a `moveRoots` tree
or a top-level export of the copy, a `moveRoots` tree with an import under it, the look's own actor type pulled in by a
reference, an output path that is also an input.

Research dumps: `2da`, `snd`, `swf`, `reflect`, `meshinfo`, `texpng`, `coalesced`, `bankdump`, `eventflags`,
`preprop`, `dumpwave`. `Program.cs` shows their arguments. Two of them serve builds too: `swf <pcc> <outDir> [movie]`
writes each GFx movie's `RawData` as `<name>.gfx` (with a movie path, only that one, and an error unless exactly one
matches), and `texpng <pcc> <uIndex|path> <out.png>` writes a `Texture2D`'s top mip as PNG. `ogg` (ISACT's own encoder, through `ISACTTools.dll`)
returned 0 bytes when tried and is kept only as a record.

## License

GPL-3.0 ([LICENSE](../../LICENSE)). What it builds with: [THIRD_PARTY.md](../../THIRD_PARTY.md).
