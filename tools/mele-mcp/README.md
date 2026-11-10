# mele-mcp

This page covers the toolkit's MCP server: how its parts fit, all 91 tools by area, its scripts, the dev loop and the
known traps. Build and setup steps are in [getting started](../../docs/getting-started.md).

## What it is

mele-mcp is an MCP server for modding Mass Effect Legendary Edition. An MCP client such as Claude Code starts it and
calls its tools over stdio. The tools read and write game files, build mods and talk to the running game.

The server is a Windows x64 program, version 0.1.0, licensed GPL-3.0. The client can run on Windows or in WSL. Its
dependencies and their licenses are listed in [THIRD_PARTY.md](../../THIRD_PARTY.md).

## How the parts fit

```text
MCP client (Windows or WSL)
   | stdio, JSON-RPC
   v
MeleMcp ........ host and tool classes
   |
   v
MeleMcp.Core ... tool logic, no MCP dependency
   |-- LEC ................ packages, .tlk, .cnd and config files
   |-- M3 ................. mod installs, merge mods
   |-- MeleBridge pipe .... running game (live_*)
   `-- LEX Interop pipe ... running game (game_*)
```

| Part | Role |
|---|---|
| LEC | [LegendaryExplorerCore](https://github.com/ME3Tweaks/LegendaryExplorer), the library behind Legendary Explorer (LEX). A git submodule, pinned at `43bf73a`, tag `6.5.0.6522`. |
| M3 | ME3Tweaks Mod Manager. Mods reach the game through it, with `mod_install`. |
| MeleBridge | The toolkit's own ASI, one build per game: see [its README](../mele-bridge/README.md). An ASI is a DLL the game loads at start. |
| LEX Interop | An ASI from ME3Tweaks. The live link tools below speak its protocol, as LEX does. |

## Calling a tool

| Parameter | What it takes |
|---|---|
| `game` | `LE1`, `LE2` or `LE3`. Every tool that takes it requires it, the one-game tools too, and a call without it gets an error that names the games the tool takes. `le_status` and `le_refresh_files` take no `game`, and `xgame_decompile` takes `games`. |
| `package` | A file name, resolved to the highest mounted copy, or a full path. |
| `export` | A UIndex (the export's number in the package), an instanced full path, or a unique name. |
| Any path | A Windows path or `/mnt/<drive>/...`. |
| `language`, `languages` | The text language. `conv_read`, `conv_find`, `conv_audio` and `quest_map_get` default to `FR`. `mod_check` and `game_cycle` default to `INT,FR`. |

## Tools

LE1, LE2 and LE3 are all targets, and the Games column shows what the code allows for each tool. `All three` means the
tool handles each game. `LE1 only`, `LE2, LE3` and `LE3 only` mean the other games get an error. `Accepted` means the
tool takes all three with no guard. [docs/games.md](../../docs/games.md) records what has actually run on each game.

### Status

| Tool | Purpose | Games |
|---|---|---|
| `le_status` | Reports the LEC version, the LE folder and its source, all three game paths and Oodle. | No `game` parameter |
| `le_refresh_files` | Forgets the cached file list. Lookups rescan it themselves when a DLC folder, mount file or cooked folder changes; this covers a file added in a cooked subfolder. | No `game` parameter |
| `le_find_file` | Names the copy of a file the game loads, and why each other copy loses. | All three |

On LE1, `le_find_file` emulates the installed AutoTOC ASI, which writes the file table the game reads, and compares with
`PCConsoleTOC.bin`. On LE2 and LE3 it follows LEC. On LE2 a DLC copy of `SFXGame`, `PlotManager` or `Startup` is named
the winner, but it never loads.

On LE1, a mod's `AutoLoad.ini` needs a `ModMount` above 0. Without one, the AutoTOC ASI closes the game at boot with a
"Broken Mod Warning". AutoTOC v2 reads only the first `[ME1DLCMOUNT]` section. It also misses a header on line 1 after a
UTF-8 BOM, so save that file without a BOM. The Autoload ASI reads only folders whose names start with `DLC_MOD_`, in
that exact case. `le_find_file` lists each of these problems in its warnings.

### Packages

These tools only read, and they never change a package. The optional `outPath` of `pkg_to_text` refuses the game install
and any existing file that is not this package's earlier text.

| Tool | Purpose | Games |
|---|---|---|
| `pkg_info` | Sums up a package: counts, top export classes, embedded LE1 talk files. | All three |
| `pkg_search` | Lists exports by exact class and path substring. | All three |
| `pkg_find_name` | Finds loaded packages whose name table holds a substring. | All three |
| `pkg_to_text` | Writes export properties as text LEC compiles back. `roundTrip` proves it. | All three |
| `export_get` | Prints an export's properties as a tree, with references resolved. | All three |
| `export_refs` | Lists exports of the same package that reference an export. | All three |
| `export_diff` | Compares two exports property by property, such as a clone and its source. | All three |
| `export_decompile` | Prints the UnrealScript of a Class, State, Function or ScriptStruct export. | All three |

### Package writing

Each of these refuses the game install. The `export_*` tools back the file up first, and `level_new` refuses an existing
file. Two loaded files with the same path share one object, so rename a copied forced export.

| Tool | Purpose | Games |
|---|---|---|
| `export_set` | Sets one leaf property by path, then saves. | All three |
| `export_set_batch` | Sets several leaf properties of one package, validated together, with one save. | All three |
| `export_clone` | Duplicates an export and its children. A level actor joins the `Actors` list. | All three |
| `export_rename` | Renames an export, with its children. | All three |
| `export_import` | Copies an export from another package with everything it references. | All three |
| `level_new` | Creates an empty level package from LEC's template for that game. | All three |

### Script

`script_compile` saves only on success, and it refuses the game install even on `dryRun`. A core file such as `SFXGame`
or `Startup*` compiles on the copy. A warning then says that shipping it needs a merge mod.

| Tool | Purpose | Games |
|---|---|---|
| `script_compile` | Compiles UnrealScript into a Function, State or Class export of a mod's package copy. | All three |
| `xgame_decompile` | Decompiles one script object in several games and diffs them. | All three, through `games` |

### Text

A strref is the numeric id of a string. On LE2 and LE3, `tlk_set` takes a full path and creates a missing file.

| Tool | Purpose | Games |
|---|---|---|
| `tlk_get` | Reads strings: LE1 from `BioTlkFile` exports, LE2 and LE3 from one loose `.tlk`. | All three |
| `tlk_set` | Adds or replaces strings in the same two forms, with a backup. | All three |

### Conversations

LE2 and LE3 keep conversations only in `_LOC_<LANG>` packages. The `FXA_<strref>` line names of the FaceFX tools are
LE1's naming.

| Tool | Purpose | Games |
|---|---|---|
| `conv_read` | Reads a conversation as a text graph: nodes, wheels, plot gates, VO tracks. | All three |
| `conv_find` | Searches every loaded conversation by speaker, text, plot id or name. | All three |
| `conv_audio` | Lists lines with no voice yet, following strref, FaceFX, cue and ISACT sound. | LE1 only |
| `conv_add_node` | Adds a line copied from a template node, with node data and silent lip sync. | LE1 only |
| `conv_link` | Adds or removes a start, wheel or reply link. | LE1 only |
| `conv_set_ids` | Gives a copied conversation a new `m_nResRefID` and new node ids. | LE1 only |
| `conv_build` | Builds a conversation from a YAML file. | LE1 only |
| `facefx_get` | Lists the lip-sync lines of a `FaceFXAnimSet`. | Accepted |
| `facefx_copy_lines` | Copies lip-sync lines under new names, silent by default, with a backup. | Accepted |

### Plot and journal

LE1 and LE2 conditionals are `F<id>` functions of `BioAutoConditionals`. Read them with `export_decompile` and edit them
with `script_compile`.

| Tool | Purpose | Games |
|---|---|---|
| `plot_map_get` | Lists state events of a `BioStateEventMap` or `BioConsequenceMap`. | All three |
| `plot_map_new` | Creates a DLC plot package with empty maps, and returns its `AutoLoad.ini` lines. | LE1 only |
| `plot_event_set` | Adds, replaces or removes one state event. | LE1 only |
| `plot_build` | Builds a DLC plot package from a YAML file, with journal text. | LE1 only |
| `quest_map_get` | Reads a journal map. LE2 and LE3 also read the codex map, `DataCodexMap`. | All three |
| `quest_set` | Adds, replaces or removes one journal quest and its evaluations. | LE1 only |
| `id_alloc` | Finds free plot, quest or string ids, or checks given ones, and names each user. Reserves the free ids it returns for its `owner`, so other calls skip them. | LE1 only |
| `cnd_get` | Reads a `.cnd` conditionals file: its ids, one entry, or all of them. | LE3 only |
| `cnd_set` | Compiles one conditional into a mod's `.cnd`, with a backup when the file exists. | LE3 only |

### Kismet

Kismet is the engine's visual scripting system, stored in packages as sequences.

| Tool | Purpose | Games |
|---|---|---|
| `kismet_read` | Reads a sequence as text in flow order. With the Kismet Logger's log, it marks fired ops. | All three |
| `kismet_add` | Creates a sequence object with class defaults, and returns its link names. | All three |
| `kismet_link` | Links output to input, variable slot to variable, or event slot to event. | All three |

### Builds and mods

| Tool | Purpose | Games |
|---|---|---|
| `level_build` | Builds a level from a YAML file: NPCs, usables, props, lights, vanilla actor edits. | LE1 only |
| `mod_build` | Builds every YAML file at the top of a mod folder, in order. | LE1 only, through its builders |
| `mod_check` | Checks a mod's built files in M3's library before an install. | LE1 only |
| `mod_install` | Installs a mod through M3, then compares its `[CUSTOMDLC]` files with the installed copies. | All three |
| `dlc_new` | Starts a mod in an empty folder: `moddesc.ini`, DLC folder, `Mount.dlc`, `.tlk` files, config. | LE2, LE3 |
| `mergemod_build` | Builds an M3 merge mod (`.m3m`), the route to patch core files, and lists it in `moddesc.ini`. A build that does not compile takes it out. | All three |

- On LE1, M3's DLC Mod Starter Kit starts a mod, and `mod_build` fills it. `dlc_new` starts an LE2 or LE3 mod and
  refuses LE1.
- `mod_install` takes a `moddesc.ini`, a mod folder or a name in M3's library, and refuses while the game runs.
- A merge mod's install patches a core file in place, and merges stack on the file on disk. Only M3's Restore, on that
  file's row, undoes them.

### Config

The native form is `ConfigDelta-*.m3cd` on LE1, the asset's loose ini on LE2, and `Default_<DLC>.bin` on LE3.

| Tool | Purpose | Games |
|---|---|---|
| `config_get` | Reads config file by file in load order, with the replayed effective value of a key. | All three |
| `config_set` | Writes entries into a mod's DLC folder in native form, with a backup per file. | All three |

### GFx and native

The game exes keep their code encrypted on disk, which is why `exe_dump` reads the running process. Dumps are game code,
so keep them out of any repo.

| Tool | Purpose | Games |
|---|---|---|
| `gfx_scan` | Reads a GFx movie in a package or a loose file: `movies`, `tags`, `images` or `as2`. | All three |
| `exe_dump` | Copies the running game's mapped exe image to a cache per game. | Accepted |
| `exe_dump_pe` | Writes a dump as a PE file that a disassembler can open. | Accepted |
| `native_query` | Queries a dump: disassembly, callers, xrefs, strings, patterns, vtables, names. | Accepted |

### Prop catalog and editor

These tools serve the in-game editor, which exists only in the LE1 build of MeleBridge. See the
[editor guide](../../docs/editor-guide.md) and the [editor design](../../docs/design/in-game-editor.md).

| Tool | Purpose | Games |
|---|---|---|
| `catalog_index` | Indexes every StaticMesh LE1 loads, with its curation category. | LE1 only |
| `catalog_build` | Builds `MeleCatalog_Core.pcc`, `catalog.json` and thumbnails for the editor's prop menu. | LE1 only |
| `editor_import` | Merges an editor session into a level YAML by tag, then builds the level. | LE1 only |

### Saves

These tools use `.pcsav` files under `Documents\BioWare\Mass Effect Legendary Edition\Save\ME<n>`, and all four refuse
LE1, whose saves use another layout. The two write tools back up first, and they refuse while that game runs unless you
pass `allowRunning`. The save file layout is described in [docs/formats.md](../../docs/formats.md).

| Tool | Purpose | Games |
|---|---|---|
| `save_list` | Lists a game's careers, or one career's saves. A damaged file shows its error. | LE2, LE3 |
| `save_read` | Reads one save's fields, plus any plot value asked for. | LE2, LE3 |
| `save_head_default` | Gives one save, or a whole career, the game's default head. | LE2, LE3 |
| `save_set` | Writes fields or plot values in one save, all or none. No plot transition runs. | LE2, LE3 |

### Live link (LEX Interop)

Install the LEX Interop ASI from LEX (Live Level Editor, requirements), then restart the game. Keep LEX closed while you
use these tools. Replies go to the first window titled `Legendary Explorer`, and the server owns a hidden one.

| Tool | Purpose | Games |
|---|---|---|
| `game_status` | Checks the game, the interop pipe and a reply round trip. | Accepted |
| `game_console` | Sends a console command. No output comes back. | Accepted |
| `game_get` | Reads a property of every live object of a class, with `getall`. Needs DebugLogger too. | Accepted |
| `game_remote_event` | Fires every enabled Kismet remote event with that name. | Accepted |
| `game_stream_level` | Streams a level package `in`, `out`, or `only` (loaded but not visible). | Accepted |
| `game_player_location` | Gives Shepard's position and rotation, as of her last move. | Accepted |
| `game_actors` | Lists the actors of every visible level, with filters. | Accepted |
| `game_actor_get` | Reads one actor's live location, rotation, draw scale and hidden flag. | Accepted |
| `game_actor_set` | Moves, turns or hides one actor live, then reads it back. No file changes. | Accepted |
| `game_hot_reload` | Swaps a streamed level for a package file, as `<level>_LIVE<hhmmss>`. | Accepted |

- The ASI serves precached bytes only for names not on disk, which explains the `_LIVE` name.
- The hot-reload bytes must be uncompressed, or the load never finishes.
- On LE1 a streaming refresh unloads that copy, and the LE1 Community Patch's (LE1CP) Sideloader restores its own levels.
- On LE1, never fire LE1CP's NPC framework events by hand (`Live_NPC_*`, `Poll_NPC_*`), since they can hang the game.

### Logs and capture

The two log tools read files in `Binaries\Win64\Logs`, so they use no pipe, but they need their ASI on that game.
The optional ASIs are covered in [getting started](../../docs/getting-started.md).

| Tool | Purpose | Games |
|---|---|---|
| `game_log` | Reads the DebugLogger ASI's log, with map, error and text filters. | All three |
| `game_kismet_log` | Reads the Kismet Logger ASI's trace of activated sequence ops. | All three |
| `game_screenshot` | Saves a PNG of the game window, under `%LOCALAPPDATA%\mele-mcp\screenshots` by default. | All three |

### Running the game

| Tool | Purpose | Games |
|---|---|---|
| `game_plot` | Reads, or reads then sets, plot values in the running game. No transition runs. | LE1 only |
| `game_cycle` | Runs the test loop: close, check, install, boot, load, commands, wait for the map, screenshot. | All three |
| `game_close` | Closes the game without saving. With `force` it kills the game after 30 s. | All three |
| `npc_place` | Puts a YAML NPC where Shepard stands, edits the YAML, builds the level, moves her live. | LE1 only |

`game_cycle` also closes the running game without saving. It reads progress from the DebugLogger log on every game, and
its other steps differ:

| Step | LE1 | LE2, LE3 |
|---|---|---|
| Console route | LEX Interop | MeleBridge |
| Load from the menu | `loadSlot` n, through `BioLoadGame <n>`: `BioSaveGame<n>.sav`, made once in game with the console's `BioSaveGame <n>` | `loadLatest`, the latest save only, through `QuickLoad` |
| `mod_check` before the install | Yes, unless `check` is false | Skipped |
| Commands after a map load | Sent once MeleBridge's status says the load ended, then `settleSeconds`. Without MeleBridge, after `settleSeconds` only: LEX Interop drops a command sent during a load and nothing says so | Sent once MeleBridge's status says the load ended, then `settleSeconds`. A command refused during a load goes again once |

The game is booted only once the install is verified. A game that closes after the boot ends the cycle at once, with
the phase it closed in. `game_cycle`, `game_close` and `mod_install` refuse, after 5 s, while another call runs one of
them on the same game, from any session's server. Installs and boots through M3 also wait for each other.

On LE1 a career save is not a slot, so it goes through `commands`:
`nextframedefer loadgame <Career>_QuickSave DevId=-1`, with `waitForMap`. `game_plot` uses MeleBridge when it is there,
or else LEX Interop's cheat execs plus the DebugLogger log.

### Live objects (MeleBridge)

Requests run on the game thread, and replies come back on the same pipe, so LEX may stay open. Install each game's ASI
with `tools/mele-bridge/build.sh --game LE<n> --install`, game closed.

| Tool | Purpose | Games |
|---|---|---|
| `live_find` | Lists live objects by class, name and path. | All three |
| `live_get` | Reads one object's properties, all of them or one by path. | All three |
| `live_set` | Sets one property in game memory. No file changes. | All three |
| `live_call` | Calls an UnrealScript function through `ProcessEvent`, with return and out values. | All three |
| `live_console` | Runs a console command. Its output comes from the DebugLogger log, so it needs that ASI. | All three |
| `live_describe` | Shows a class's chain, typed properties and function signatures. | All three |
| `live_watch` | Records `ProcessEvent` calls of named functions for some seconds. | All three |
| `live_status` | Takes one cheap look: map, world flags, player, streaming, loading movie, squad screen. | All three |
| `live_keep_ticking` | Test switch: keeps the world and Kismet running with the game in the background. | All three |
| `live_place` | Moves an actor, or spawns a copy, near Shepard or at coordinates, until the map reloads. | Accepted |
| `live_remove` | Destroys a live actor, or hides one that cannot be destroyed, until the map reloads. | Accepted |
| `live_editor` | Reports the in-game editor's state, without using the game thread. | LE1 only |
| `live_save` | Writes a live actor's spot into a level YAML, then builds the level. | LE1 only |

- `live_set` runs no setter, so attach-time state stays. Use `live_call` with a function such as `SetHidden` instead.
  `SetHidden` does nothing when `bHidden` already holds the new value, so first set back a field you changed with
  `live_set`.
- `live_place` cannot copy pawns, since the engine crashes.
- `live_status` times out while a map loads. Its `inLoad` is null on LE2 and LE3, and its squad screen is null on LE3.

## YAML builds

`level_build`, `conv_build`, `plot_build` and `mod_build` turn YAML files into LE1 mod files. Every key, default and rule
is in [docs/yaml-reference.md](../../docs/yaml-reference.md), and [examples/README.md](../../examples/README.md) has a
small mod that builds.

## Write rule

Package writes go through `PackageWriter.Edit`, which guards, changes, backs up and saves. A tool that builds a package
itself saves it with `PackageWriter.Save`, so `level_build` checks its forks and the level, a new one too, before it
saves any of them. A package, like every other whole file (`AtomicFile`), is written beside the target, then moved over
it, so a crash or a failed save leaves the old file or the new one. `src/BannedSymbols.txt` makes any other package
save, `File.WriteAll*` call, or read of LEC's file list outside `GamePaths` a build error. The guard refuses any file
inside the Legendary Edition folder or the game path. It compares spellings, then what the path is on disk, so shares,
junctions and volume names are caught too. It does not see a hard link to a game file; a file saved over such a link
replaces the link and leaves the game's file as it was.

A file name can resolve to another mod's installed copy. Edit the mod's copy in M3's library, then run `mod_install`.
Backups go to `%LOCALAPPDATA%\mele-mcp\backups` as `yyyyMMdd-HHmmss-fff_<file>` (`-2`, `-3`... after the time when two
share a millisecond), and `MELE_BACKUP_DIR` moves them.

These writes fall outside that rule, and [getting started](../../docs/getting-started.md) lists every other location:

- `editor_import`, `npc_place` and `live_save` write the level YAML with no guard on it. Each backs it up first.
- `game_screenshot` writes to its `path` parameter with no guard.
- `level_build` makes no backup of the forks it writes beside a level, because each is rebuilt from the vanilla file.

## Layout

All paths in this table are relative to `tools/mele-mcp`.

| Path | Holds |
|---|---|
| `src/MeleMcp/Program.cs` | The stdio host. Logs go to stderr, because stdout is reserved for JSON-RPC. |
| `src/MeleMcp/Tools/` | One `*Tools.cs` class per area, each tool a thin wrapper over Core. |
| `src/MeleMcp.Core/` | The tool logic. `LecHost` starts LEC, `GameBridge` and `LiveBridge` hold the two pipes. |
| `src/BannedSymbols.txt`, `src/Directory.Build.props` | Calls allowed in one class only: a call anywhere else fails the build ([Write rule](#write-rule)). |
| `external/LegendaryExplorer/` | The LEC submodule. Fetch it with `git submodule update --init`. |
| `global.json` | Pins the .NET SDK to 10.0.402, with later 10.0.x accepted. |
| `build.env.example` | Template for the ignored `build.env`, which `deploy.sh`, `test.sh` and the Python scripts read. |
| `tests/MeleMcp.Core.Tests/` | The tests `scripts/test.sh` runs. Its README says what they cover and why. |

## Scripts

Run these scripts from WSL, inside the `tools/mele-mcp` folder. A variable in the environment wins over `build.env`.

| Script | What it does |
|---|---|
| `scripts/deploy.sh [package]` | Builds the server. Only when that worked does it stop the servers of `MELE_OUT` and publish there. Then it runs the smoke test on that package. |
| `scripts/stop-server.ps1 -OutDir <folder> [-ListOnly]` | Stops the servers of one folder, and others keep running. `deploy.sh` runs it after a good build. |
| `scripts/mcp_call.py <tool> ['<json>']` | Calls one tool with a fresh server. It exits 1 on a tool error. |
| `scripts/mcp_smoke.py [package]` | Runs `initialize`, `tools/list`, `le_status`, `pkg_info` on an LE1 package, `pkg_info` on a missing file and `pkg_info` without `game`, which must both fail. It also fails when a tool that takes `game` does not require it. |
| `scripts/scenario.py` | Runs in-game test suites, LE1 only: see [docs/scenarios.md](../../docs/scenarios.md). |
| `scripts/test.sh [--allow-skips] [dotnet test args]` | Builds lectool, then runs `tests/MeleMcp.Core.Tests` and `scenario.py --selftest`. Tests that need the game skip without it, and none writes into it. A skipped test fails the run unless `--allow-skips` is given. |

```sh
./scripts/deploy.sh
python3 -I scripts/mcp_call.py pkg_info '{"package":"BioP_Nor.pcc","game":"LE3"}'
```

`MELE_DOTNET` picks the Windows `dotnet.exe`, and `MELE_DLL` picks the server the Python scripts run. `MELE_OUT` is the
publish folder, by default `%LOCALAPPDATA%\mele-mcp\server`. The full list of settings is in
[getting started](../../docs/getting-started.md).

The server has its own check with no MCP client: `MeleMcp.exe --selftest [package]`. It starts LEC and opens an LE1
package, `BIOA_CRD00.pcc` by default.

## Dev loop

1. Edit the code. A new tool is a `[McpServerTool]` method in a `[McpServerToolType]` class under `src/MeleMcp/Tools/`.
   A tool that works on a game takes `game` as its first parameter, with no default.
2. Run `./scripts/deploy.sh`. A build error stops no server.
3. In Claude Code, run `/mcp`, pick `mele` and reconnect, because the deploy stopped the old process.

To test a second copy beside the one in use, set `MELE_OUT` to another Windows folder for `deploy.sh` and `mcp_call.py`.
In a git worktree `deploy.sh` asks for it, because the default folder belongs to the main checkout. `deploy.sh` prints
the commit it publishes, and the published `MeleMcp.dll` keeps it after the `+` of its product version.

## Known LEC traps

- `AutoloadIni` loses data on a round trip. It reads `PlotManagerStateTransitionMap<n>` keys into the consequence maps and
  never reads `DotU<n>`, so `plot_build` edits `AutoLoad.ini` line by line.
- On LE1, LEC counts a `DLC_MOD*` folder with an `AutoLoad.ini` as a mod. The AutoTOC and Autoload ASIs follow the
  rules under Status above. `le_find_file` follows the game, and the other tools resolve names through LEC.
- `MELoadedFiles.GetFilesLoadedInGame` throws when a DLC folder it counts as mounted has no `CookedPCConsole`. Every tool
  given a file name then fails, and `le_find_file` reports it as a folder warning.
- LEC caches its file list per game, for the whole process. Code reads it only through `GamePaths.LoadedFiles`, which
  rescans when the DLC folder names, an `AutoLoad.ini` or `Mount.dlc`, or a cooked folder's write time changed, so an
  install from M3's window is seen. Code that installs files still calls `GamePaths.RefreshLoadedFiles()` afterwards.
- `CommonStructs.LinearColorProp(r, g, b, a)` writes `r` into all four channels, so `LevelBuilder` builds that struct by
  hand.
- `DecompileBulkProps` stops at the first export that fails, so `PackageText` builds each block itself.
- LEC's `.cnd` parser loops forever on an unknown word, and its compiler drops what it cannot parse with no error.
  `cnd_set` checks the tokens first, then decompiles the result and writes only on a match.
- The publish holds LEC's plain `Release` build, although `MeleMcp.Core.csproj` asks for `WinRelease`. LEC's registry
  lookup is compiled out of that build, so `LecHost` reads the same keys itself.
- LEC's package `Save` catches every exception outside its DEBUG builds, passes a message to the `packageSavingFailed`
  callback of `InitLib`, and returns as if the file were saved. `LecHost` and lectool pass `PackageWriter.SaveFailed`,
  which throws, so a failed save is a `PackageSaveException` that names the file and its backup.

## Build and startup gotchas

| Symptom | Cause | What to do |
|---|---|---|
| `CS0121`, ambiguous `CopyTo` in LEC's `EntryImporter.cs` | An SDK older than 10 lacks the C# 14 compiler LEC needs | Use the SDK in `global.json`. |
| Publish fails, file locked by `.NET Host` | A running server holds `MeleMcp.dll` | Run `stop-server.ps1` for that folder. `deploy.sh` does this itself. |
| Server hangs at start, with no output | A default host watches `appsettings.json`, which hangs on a `\\wsl.localhost\` folder | `Program.cs` uses an empty builder, rooted at the DLL's folder. |
| `le_status` shows no LE folder | The registry gave nothing, or a folder with no `Game\ME1`, the only kind it accepts | Set `MELE_LE_DIR` to the Legendary Edition folder. |
| `MELE_LE_DIR` has no effect from WSL | WSL passes only variables named in `WSLENV` | Name it in `WSLENV` in the server's `env`. The Python scripts do this themselves. |
| `Permission denied` starting `MeleMcp.exe` from WSL | Output on the WSL file system has no exec bit | Publish to a Windows drive, as `deploy.sh` does by default. |
| Smoke test fails after a good publish | It needs an LE1 folder and an LE1 package, `BIOA_CRD00_LOC_FR.pcc` by default | Pass another LE1 package to `deploy.sh`. With no LE1 install, it cannot pass. |
| A tool returns a generic "An error occurred" | The MCP SDK hides other exceptions' messages | Wrap the tool in `ToolErrors.Run`, which forwards type and message. |
| `ME3TweaksModManager.exe not found` | M3 records its path in `HKCU\SOFTWARE\ME3Tweaks` when it runs | Start M3 once. |
| Calls hang until M3 exits | A child process inherits the server's stdio pipes | `ModInstaller.StartM3` uses shell execute, which avoids this. |
