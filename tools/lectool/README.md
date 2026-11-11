# lectool

lectool is a small command-line program on Legendary Explorer Core (LEC). This page covers when to use it, how to build
and run it, and all 26 commands with their games.

## When to use it

Start with [mele-mcp](../mele-mcp/README.md): its package tools make backups and refuse the game install. Use lectool
when a build script needs a package with no server running, as `mele-galaxy-map-normandy` does. Use it also for quick dumps,
and for jobs mele-mcp has no tool for.

lectool makes no backups. It writes a package beside the file, then moves it over it, so a save that fails leaves the
old file. It refuses to write, delete or save a file inside the game install (the same check as mele-mcp's), and refuses
an output that is also one of its inputs. Build into your mod's folder and install with ME3Tweaks Mod Manager (M3).

## Build

lectool is a Windows program (`net10.0-windows`, x64) that needs the .NET 10 x64 runtime. Building needs the Windows .NET
SDK 10.0.402 or a later 10.0.x, and the LEC submodule it shares with mele-mcp. [THIRD_PARTY.md](../../THIRD_PARTY.md)
lists what it builds with. From WSL, `-o` takes a Windows path.

```bash
git submodule update --init
cd tools/lectool
dotnet.exe build lectool.csproj -c Release -o 'C:\tools\lectool'
```

## Run

```bash
MELE_LE_DIR='<LE folder>' WSLENV="$WSLENV:MELE_LE_DIR" /mnt/c/tools/lectool/lectool.exe <command> <args>
```

| Variable | Used by | Meaning |
|---|---|---|
| `MELE_LE_DIR` | all | Legendary Edition folder, as a Windows path. Without it, LEC looks in the registry. |
| `LECGAME` | all | `LE1`, `LE2` or `LE3`, default `LE1`. Picks the object database: set it to your package's game. Any other value is an error. |
| `WEMDIR`, `WEMIDS` | `wwise` | An existing folder, and the comma-separated IDs of the files to write there as `.wem`. |
| `EXTRADLLDIR` | `ogg` | A folder to add to the DLL search path. |

From WSL, name each variable you set in `WSLENV`, and pass paths as Windows paths (`wslpath -w`).

There is no help command. An unknown command, a `--flag` that `buildsound` does not know, or a wrong number of arguments for `2dapart`, `2damerge`, `actortype`, `addactor`, `portmesh`, `texpng` or `ogg` prints `ERROR` and exits 1, so a build script that is newer than your lectool fails.
Commands with checks print `ERROR` lines and exit 1. A package that cannot be saved (a file another program holds open, a full disk) ends the command with an unhandled `PackageSaveException` and a non-zero exit code. A `uIndex` is an export's number, the first column of `pkglist`.

`LE1` below means the command writes an LE1 package or reads LE1 data, whatever `LECGAME` says. `Any` means the code has
no game check and follows your package. Per-game status is in [games.md](../../docs/games.md).

## Script classes and actors

| Command | Games | What it does |
|---|---|---|
| `compileclass <outPcc> <packageName> <Class.uc>... [--pkg=<name> <Class.uc>...] [--unforce=<name>] [--no-referencer]` | LE1 | Replaces `<outPcc>` with a new package, the classes under a `Package` export. A class takes its file's name. A compile that fails leaves no `<outPcc>`. |
| `addactor <levelPcc> <package> <class>` | Written for LE1 | Adds one actor of a script class at the level's origin, in place. `<package>` must be an AutoLoad package, which the game loads at startup. |

`--pkg` starts another `Package` export for the `.uc` files after it. `--unforce` clears `ForcedExport` on one package,
so its classes load from the file as `<file>.<name>.<class>`. `--no-referencer` leaves out `CombinedStartupReferencer`,
which keeps an AutoLoad package's classes loaded.

## Meshes, textures, faces and GFx movies

| Command | Games | What it does |
|---|---|---|
| `portmesh <srcPcc> <srcUIndex> <pcc> <newName>` | Any | Ports a `StaticMesh` with its materials, textures and local shaders to the top level of an existing package. It exits 1, before saving, when LEC's porter reports something it could not link (`ERROR relink:`). It exits 0 even after printing `BROKEN material`. |
| `portfx <srcPcc> <srcUIndex> <pcc> <newName>` | Any; source of any game | Ports any export, such as a `ParticleSystem`, with what it uses to the top level of an existing package. An object the target game can't load, such as an LE3 material that only LE3's `SFXGame.pcc` holds, is first copied from the source game's file that holds it. Exits 1, with `<pcc>` unchanged, on a relink error, a broken material or an import the target game can't load. Shaders port between LE1 and LE3 only. |
| `addtexture <pcc> <name> <png> [--argb]` | Any | Adds a new top-level UI `Texture2D` from a PNG, as DXT5 with one mip. `--argb` stores it uncompressed, since DXT5 bands dark colours. |
| `cubeshrink <pcc> <cubePath> <size> <DXT1\|DXT5>` | Any | Makes the six faces of a `TextureCube` smaller, in place: each face's top mip halved with a box filter down to `<size>`, then stored in the package in the new format with a full mip chain. DXT1 faces are made opaque. Reads the package back and checks every face. |
| `forcepackage <pcc> <package> [--move-out <path>]...` | Any | Marks a top-level `Package` export and everything under it `ForcedExport`, as the game's levels store shared art: its objects load under their own path, so the game uses its copy already in memory and a texture override (`.m3to`) matches them. `--move-out` first moves an export and its children to the top level, not forced. Run it after `portmesh`, `portfx` and `buildsound`, which clear the flag on every export of the file. |
| `morphface <donor.pcc> <donorFace> <pcc> <face> [--mic <path>] [--sha1 <hex>]` | Any; donor of any game | Gives a `BioMorphFace` the donor's look, in place: LOD0 and LOD1 vertices, bones, and material values. It prints the look's SHA-1. |
| `gfxpackage <src.pcc> <movie> <out.pcc> [--raw-data <file>] [--copy-textures] [--add-texture <name> <like>]... [--texture <name> <png\|#RRGGBB>]...` | `LECGAME` | Replaces `<out.pcc>` with a package holding a top-level copy of a `GFxMovieInfo` (a Scaleform UI movie), so `PanelResource="<out file name>.<movie name>"` opens it. |
| `swf <pcc> <outDir> [movie]` | Any | Writes each movie's `RawData` as `<name>.gfx` into an existing folder. With a movie path, only that one. |

`morphface` stops with the file unchanged unless both base heads have the same vertices in the same order, and both
faces the same bones and parameters. `--mic` also sets a baked material instance of the head. `--sha1` stops on any other
donor look.

For `gfxpackage`, `--raw-data` uses that file's bytes, for a patched movie. `--copy-textures` makes referenced textures
top-level copies instead of imports. `--add-texture` copies the referenced texture `<like>` under a new name. `--texture`
makes a referenced or added texture a top-level copy, its image from a same-size PNG or one colour.

## Strings

`tlkpackage <outPcc> <strings.tsv> [--ids-from <other.tsv>]` (LE1) replaces `<outPcc>` with one language of a mod's
GlobalTlk. The strings file is UTF-8, each line an id, one tab and the text; empty and `#` lines are skipped.
`--ids-from` requires exactly the ids of another file.

## Bio2DA data tables

| Command | Games | What it does |
|---|---|---|
| `2da <pcc> <uIndex>` | Any | Prints a table, tab-separated. |
| `2dapart <src.pcc> <recipe.yaml> <out.pcc>` | LE1 | Writes a package for M3's Bio2DA merge: per table, a `<Table>_part_1` export with the recipe's rows. It first deletes `<out.pcc>`, and refuses an `<out.pcc>` that is `<src.pcc>` or the recipe. |
| `2damerge [--add-only] <m3da> <modDir> <base.pcc> <out.pcc>` | LE1 file list | Runs M3's merge of one `.m3da` manifest on `<base.pcc>`, saves `<out.pcc>`, reads it back and checks each table. |

```yaml
tables:
  BIOG_2DA_PlotManager_X.PlotManagerGameData:  # full path in src.pcc
    rows:
      InSquadMyMate:          # new row; a number in a Bio2DANumberedRows table
        from: InSquadJenkins  # optional: start as a copy of this row
        set: { Index: 1234 }  # cells by column name; ~ empties a cell
```

A `set` value takes the copied cell's type, else the type most of the column has. In a table stored without cell indexes,
an empty cell is an error.

A manifest entry has `packagefile`, `mergepackagefile` (found in `<modDir>` by name) and `mergetables`. The base must be
`Engine`, `SFXGame`, `EntryMenu` or one of M3's `BIOG_2DA_UNC_*_X` files. Use a vanilla base if you have one, as M3
resets tables first. `--add-only` makes replacing a base row with other cells an error.

## Actor types

`actortype <recipe.yaml> <template.pcc> <look.pcc> <out.pcc>` (LE1) replaces `<out.pcc>` with a package holding one actor
type at its top level. Both source types must have the same class, and properties the recipe does not name are left out.
No shader cache is copied, so run `pkgcheck` after it.

```yaml
name: my_mate
template:
  actorType: hench_jenkins       # full path in template.pcc
  keep: [m_eRank, AIController]  # plain values or imports
look:
  actorType: <full path in look.pcc>
  take:                          # objects to copy, with what they reference
    m_oAppearance: <full path it must name in look.pcc>
  moveUnder: MyMod               # optional: new Package export for moveRoots
  moveRoots: [<top-level tree>]
set: { ActorGameNameStrRef: 12345 }  # plain values
```

## Package checks and dumps

| Command | Games | What it does |
|---|---|---|
| `pkgcheck <pcc> [--like <ref.pcc>]... [--allow-import <path>]... [--forbid-root <name>]...` | Any | Exits 1 on reference errors, two exports with one path, a material with no shaders, or an import that does not resolve. |
| `pkglist <pcc>` | Any | Prints one tab-separated line per export and import. |
| `preprop <pcc> <uIndex>` | Any | Prints an export's pre-property bytes and class index. |
| `meshinfo <pcc> <uIndex>` | Any | Prints a `StaticMesh`'s bounds, LODs and materials. |
| `texpng <pcc> <uIndex\|path> <out.png>` | Any | Writes a `Texture2D`'s top mip as PNG. |
| `coalesced <bin> <outDir> [filter]` | LE1, LE2, LE3 | Writes every ini of a Coalesced file and prints the lines holding `filter`. |
| `reflect <namespacePrefix>` | No game | Prints LEC's public members, for type names containing the text. |

`--like` requires each import's path and class in a reference package. `--allow-import` exempts one path. `--forbid-root`
fails on an import or top-level export with that root. For LE3 files, `coalesced` marks each value's type: `>` overwrite,
`!` clear, none add, `+` add unique, `-` remove.

## Sound

ISACT is LE1's sound system. LE2 and LE3 use Wwise.

| Command | Games | What it does |
|---|---|---|
| `buildsound <donorPcc> <donorCueName> <ogg> <samplesPerChannel> <channels> <rate> <outPcc> <newCueName> <wavTitle> [volume] [--loop] [--unique-wave]` | LE1 | Copies a donor `SoundCue` to the top level of `<outPcc>`, created if missing, and puts the Ogg in its waves. It exits 1, before saving, on a relink error. |
| `wwise <pcc> <bankUIndex> [eventId]...` | LE2, LE3 | Prints a bank's contents and follows each event, a signed 32-bit number, to its sounds. |
| `snd <pcc> <filter...>` | LE1 | Prints the main ISACT chunks of waves whose names hold a filter. |
| `bankdump <pcc> <waveNameFilter>` | LE1 | Prints every ISACT chunk of the matching waves. |
| `eventflags <pcc>...` | LE1 | Prints each wave's event and track flags. |
| `dumpwave <pcc> <outdir> <nameFilter...>` | LE1 | Writes `<name>.raw` and `<name>.data<i>.bin` into an existing folder. |
| `ogg <wav> <out>` | Any | Calls `CreateIPSOgg` in the `ISACTTools.dll` that the build copies from LEC. It prints the byte count. When that count is 0 or less it writes nothing and exits 1. |

For `buildsound`, the argument after `<wavTitle>` is always read as the volume, so give one before either flag.
`<wavTitle>` must be present, but the code does not read it. `--loop` adds a looping node. `--unique-wave` puts the new
cue's name before the colon of each `<bank>:<title>` wave name. Without it, the next cue from the same donor reuses this
wave.
