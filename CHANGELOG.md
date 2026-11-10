# Changelog

Notable changes to mele-maxgoods-toolkit. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The
version is mele-mcp's (`<Version>` in `tools/mele-mcp/src/MeleMcp/MeleMcp.csproj`), the one a mod YAML's
`requires: mele-mcp >= X.Y.Z` is checked against. MeleBridge has its own `ASI_VERSION` and pipe protocol number
(`tools/mele-bridge/src/SharedVersion.h`).

## 0.1.0 (unreleased)

### Added

mele-mcp, an MCP server on LegendaryExplorerCore 6.5, with 91 tools:

- Game files: `le_status`, `le_refresh_files`, and `le_find_file`, which says which copy of a file the game loads. For
  LE1 it follows the installed AutoTOC ASI.
- Packages, read only: `pkg_info`, `pkg_search`, `pkg_find_name`, `pkg_to_text` (properties as text LEC compiles
  back, with a `roundTrip` check), `export_get`, `export_refs`, `export_decompile`, `export_diff`.
- Packages, write: `export_set`, `export_set_batch`, `export_clone`, `export_import`, `export_rename`, `level_new`.
  Every write tool backs the file up first and refuses files inside the Legendary Edition folder. Files other than
  packages are written beside the target, then moved over it.
- Strings, conversations and lip sync: `tlk_get`, `tlk_set`, `conv_read`, `conv_find`, `conv_set_ids`,
  `conv_add_node`, `conv_link`, `conv_audio` (lines without a voice), `facefx_get`, `facefx_copy_lines`.
- Kismet: `kismet_read` (a sequence in flow order, with the Kismet Logger's trace), `kismet_add`, `kismet_link`.
- Plot and journal: `plot_map_get`, `plot_map_new`, `plot_event_set`, `quest_map_get`, `quest_set`, and `id_alloc` for
  free plot and string IDs.
- Builders from YAML: `conv_build` (`from:` starts from a template conversation), `plot_build`, `level_build` and
  `mod_build`, which builds a whole mod from a folder of YAML files. `level_build` covers NPCs, `idle:` poses with
  fidgets, `streaming:` and `startPoints:` for a location of your own, `usables:` (terminals that change map, with a
  `goTo` list picked by plot conditionals), `squad:`, props, `lights:`, forks of vanilla levels (`vanilla:`) and NPC
  lights. `npc_place` puts an NPC where Shepard stands.
- Mods: `mod_check` (references, streaming states, start points, strings, FaceFX and IDs, before an install),
  `mod_install` through ME3Tweaks Mod Manager, and `game_cycle`, which quits the game, checks and installs the mod,
  boots, loads a save, runs console commands and takes a screenshot. A career save loads through its commands
  (`nextframedefer loadgame <name> DevId=-1`); a name the game can't find (`Unable to load … on device Id`) stops the
  cycle at once instead of at the timeout.
- `game_cycle` runs on LE2 and LE3 through MeleBridge, and loads the latest save with `loadLatest`. `game_close` closes a game
  without saving.
- The running game without MeleBridge, through the LEX Interop ASI, the logs of the DebugLogger and Kismet Logger
  ASIs, and the game window: `game_status`, `game_actors`, `game_actor_get`, `game_actor_set`, `game_player_location`,
  `game_console`, `game_get`, `game_stream_level`, `game_remote_event`, `game_hot_reload`, `game_log`,
  `game_kismet_log` and `game_screenshot`.
- The running game, through MeleBridge: `live_find`, `live_get`, `live_set`, `live_call`, `live_describe`,
  `live_console`, `live_watch`, `live_place`, `live_remove`, `live_save`, `live_editor`, `live_status` (one look for
  unattended tests: map, world, player, streaming, loading movie, squad screen) and `live_keep_ticking` (a test switch
  that stops the focus-loss pause). `game_plot` reads and sets
  plot values through MeleBridge, or without it through BioWare's cheat commands and the DebugLogger log.
- The in-game editor's side: `catalog_index` and `catalog_build` (the prop catalog, its thumbnails and the Mele Catalog
  dev mod), and `editor_import`, a three-way merge of an editor session into a level YAML.
- Saves, LE2 and LE3: `save_list`, `save_read`, `save_set` (fields and plot values) and `save_head_default`, which gives
  one save or a whole career the game's default head. Trilogy Save Editor has no command line. Each file is read field
  by field to its checksum, and every edit backs the file up first.
- `scripts/scenario.py` runs in-game test suites (YAML or JSON) unattended against LE1 through `live_status` and
  `live_keep_ticking`, refuses save and load commands, backs a career's saves up before a run that saves and restores
  them with the game closed, and writes a report (`docs/scenarios.md`). Used in game on a suite of about 40 checks,
  with the cold start through `game_cycle`.
- LE2 and LE3 support, offline and not tested in game: `tlk_get` and `tlk_set` on loose `.tlk` files with their female
  table; `conv_read` and `conv_find`, with text from the base game and DLC `.tlk` files, highest mount winning, and
  female variants; `quest_map_get`, which also reads a `BioCodexMap`; `plot_map_get`, which also reads a
  `BioConsequenceMap` and an LE2/LE3 `BioOutcomeMap`. LE1 output of these tools is unchanged.
- Script write loop: `script_compile` compiles UnrealScript into a function, state or class of a mod's package copy in
  LE1, LE2 and LE3, or adds one member to a class. Compiler errors carry line and column, and the file is saved only on
  success. LE1 and LE2 plot conditionals are `F<id>` functions, so it edits those too.
- LE3 conditionals: `cnd_get` and `cnd_set` read and write `.cnd` files. `cnd_set` creates a mod's `Conditionals<DLC
  folder name>.cnd`, and refuses text LEC's compiler would hang on or drop.
- Config: `config_get` reads game config file by file for LE1, LE2 and LE3: base Coalesced, then DLC by rising mount,
  with each row's file, mount, operation and native form. `config_set` writes entries into a mod's DLC folder in the
  game's own form (LE1 `ConfigDelta-*.m3cd`, LE2 loose inis, LE3 `Default_<DLC>.bin`).
- New mods: `dlc_new` starts an LE2 or LE3 mod in M3's layout and the forms of its Starter Kit (`moddesc.ini`,
  `Mount.dlc`, a `.tlk` per language, the config skeleton), and refuses a mount, module or folder name already
  installed. Every file is read back with LEC; not parsed by M3's mod loader yet, not tested in game.
- Merge mods: `mergemod_build` builds an M3 `.m3m` for SFXGame and the other core files in LE1, LE2 and LE3, from
  `changes` or an existing manifest. A dry check on a scratch copy comes first, so a script or property error stops
  before M3 is touched; then M3 compiles it and `moddesc.ini` lists it. Merge mods applied by M3 and proved in LE1, LE2 and LE3.
- Cross-game script: `xgame_decompile`, one class, function, state, struct or enum in LE1, LE2 and LE3, with where each
  winning file holds it and a unified diff against the first game that has it.
- GFx, read only: `gfx_scan`, a C# inspector for a package's movies or a loose `.gfx`: movies, tags, images, and
  ActionScript 2 as a disassembly or rough pseudo-code with constant pools resolved.
- Native code research, for hooks in code the exes keep encrypted on disk: `exe_dump` reads the running game's mapped
  image into a per-game cache, `exe_dump_pe` turns a dump into a PE that REA can open, and `native_query` answers
  disassembly, callers, references, strings, byte patterns, class-to-vtable, vtable slots and name entries from a dump.
  `exe_dump` has run on LE2, not on LE1 or LE3.
- MeleBridge builds for LE2 and LE3: `tools/mele-bridge/build.sh --game LE2|LE3` stages `LE2MeleBridge.asi` and
  `LE3MeleBridge.asi` with the pipe, reflection, console, status and keep ticking, without the editor or overlay. They
  passed one test run on each game, on a Normandy save. `live_place` and `live_remove` have not run there.
  `--install` and `--uninstall` take `--game` too.
- lectool: `coalesced` reads LE3's `Coalesced.bin` and `Default_<DLC>.bin`.
- lectool: `cubeshrink` makes the faces of a `TextureCube` smaller, in place, as DXT1 or DXT5 with a full mip chain.
- lectool: `forcepackage` gives ported art back the game's own paths (`ForcedExport`), so texture overrides reach it.
- lectool: `morphface` gives a `BioMorphFace` the look of another, in place: LOD0 and LOD1 vertices, bones and material
  parameters. The donor may be a face of another game. `--sha1` stops the run on any other donor look.
- lectool refuses to write, delete or save a file inside the Legendary Edition folder (mele-mcp's guard), refuses an
  output that is also an input, and exits 1 on an unknown command or an unknown `buildsound` flag.
- Tests: `tools/mele-mcp/scripts/test.sh` runs `tests/MeleMcp.Core.Tests` (MSTest) and `scenario.py --selftest`. They
  cover the write guard, lectool's exit codes and guard, and the bugs below. Tests that read game files skip without
  the game, and `test.sh` then fails unless `--allow-skips` is given. No CI: run it before a push.
- Fixed before release: `mod_check` took a sibling folder whose name starts with the mod's (`DLC_MOD_XB` for
  `DLC_MOD_X`) for the mod itself, and missed its id clashes. `conv_build`'s lip-sync copy replaced an existing line of
  the new strref, voiced or not. `mergemod_build` gave two exports' arrays of the same name one shared `.uc` file.
- release-kit: `kit.sh` renders one set of release files into each mod, checks them for drift, and scans a repo for game files
  and private strings.

Set-up and settings:

- The scripts of `tools/mele-mcp` and `tools/mele-bridge` read their settings from an ignored `build.env`, next to a
  tracked `build.env.example`. A variable exported in the environment wins over the file.
- mele-mcp finds the Legendary Edition folder in the registry when `MELE_LE_DIR` is unset: BioWare's `Install Dir`,
  then Steam's uninstall entry.
- `scripts/deploy.sh` builds first, then publishes to `MELE_OUT` (default `%LOCALAPPDATA%\mele-mcp\server`; a git
  worktree must name its own) and stops only the servers that run that folder's `MeleMcp.dll`. A build error stops
  none.
- Backups go to `%LOCALAPPDATA%\mele-mcp\backups` unless `MELE_BACKUP_DIR` says otherwise.
- `MELE_OWN_DLC` names your mods' DLC folders, which `catalog_index` leaves out; unset, they come from `levels.json`.
- A mod YAML may start with `requires: mele-mcp >= X.Y.Z`. `mod_build` checks every file first, and an older server
  builds nothing.
- A YAML's `package` may be a path from the DLC folder on (`DLC_MOD_MyMod\CookedPCConsole\BIOA_MYM01.pcc`), found
  beside the YAML or in the mod library of ME3Tweaks Mod Manager.
- `mod_build out=` builds into another mod root, such as a temp folder, and changes nothing outside it apart from
  backups.
- `.mcp.json.example`: an example entry that registers the server with an MCP client.
- `package.sh` builds the release zip `dist/mele-mcp-v<version>-win-x64.zip`, with its license texts and no Oodle.

MeleBridge 5, pipe protocol 1:

- `MeleBridge.asi` serves `\\.\pipe\MeleBridge_LE1`: one JSON request per line, with the ops `ping`, `console`,
  `find`, `get`, `set`, `call`, `describe`, `subscribe`, `editor`, `status` and `keep_ticking`.
- `ping` returns the pipe protocol. mele-mcp checks it once per game process and refuses a bridge with another
  number, `editor` requests and `keep_ticking` turning off or reading excepted.
- MeleBridge 3 added `status` and `keep_ticking` (off at every game start: while on, `BioWorldInfo.Tick` takes no
  focus-loss pause), and `console` now sends to the current map's live player controller. Used in game by
  `scenario.py`.
- MeleBridge 4 reads and writes `stringref` properties and parameters, as ints. `live_get` used to show
  `<StringRefProperty>`, and `live_set` and `live_call` refused them.
- MeleBridge 5 answers a request still running 1.5 s after its timeout with `"running": true` (it may still complete)
  instead of leaving the client to time out; fails a request on an integer division by zero, integer overflow or
  in-page error as it does on an access violation; reports `gameStarted` in `status` and `keep_ticking`; notes when
  `loadingMovie: null` comes from its 5 s cache; says in `subscribe`'s `skipped` what the stream leaves out; and
  watches functions declared in a state. mele-mcp sends an `id` with each request, skips replies with another one,
  waits 4 s past the timeout, and names the answered and never-sent requests when one of several fails.
- `build.sh` builds in its own LExASIs checkout (default `%LOCALAPPDATA%\mele-asi-build\MeleBridge\LExASIs`), stages
  in `dist/` (`MELE_STAGE`), finds Visual Studio 18 with vswhere and the game in the registry, and runs one build of a
  checkout at a time.

The in-game editor, on F8 ([design §4](docs/design/in-game-editor.md#4-phases)):

- Phase 0: the hooks, the overlay and input capture, the free camera, click to select, the move gizmo, opcode natives
  through the pipe, and `live_editor`.
- Phase 1: spawn meshes in memory, turn, delete, save a session file, and `editor_import`.
- Phase 2: the prop catalog and the Catalog tab's thumbnail grid; a click loads a prop from the catalog package and
  spawns it.
- Phase 3: the session re-applied on load, the tilt and scale gizmos, undo and redo, delete as hide, and a three-way
  `editor_import` that refuses conflicts.
- Phase 4: vanilla props moved and hidden through forks, prop lights (Auto light and L), the Lights tab, and
  `levels.json`.
- Phase 5a: effects moved and hidden, actors the loaded Kismet holds refused, light kinds by source with a writer that
  keeps a light as built, NPC lights as offsets from their NPC, and session version 3.
- Phase 5b: the lights of the levels' static light collections, edited in game and shipped in the map or a fork.
  Built; not tested in game yet.

lectool, a command-line tool on LEC: `compileclass`, `addactor`, `portmesh`, `buildsound`, `wwise` and research dumps.
It builds against the LEC submodule and finds the game through `MELE_LE_DIR` or the registry.
`2dapart` builds the package of an M3 Bio2DA merge (`.m3da`) from a YAML recipe of rows; `2damerge` runs M3's merge of
one manifest on a copy of a game file and checks that every vanilla row is unchanged and every added row is there.
`actortype` builds a package holding one actor type from the values of one package's actor type and the look of
another's; `pkgcheck` checks a built package's references, shaders and imports; `pkglist` lists its exports and imports.
`gfxpackage` copies a GFx movie into a package of its own, or puts a patched movie's bytes there, its textures kept as
imports or copied; it adds textures for the images a patched movie adds, and gives one a PNG's image or a colour.
`swf` extracts one movie by path, and `texpng` takes a texture by path.

Docs: [docs/design/in-game-editor.md](docs/design/in-game-editor.md), [docs/formats.md](docs/formats.md) (the pipe
protocol, `levels.json` version 1, session files versions 1 to 3, `catalog.json` version 1) and
[docs/le1-lighting.md](docs/le1-lighting.md). [docs/translating.md](docs/translating.md) tells translators how to
make a mod that translates one of my mods, in LE1, LE2 and LE3; each mod's own page links it.

License: GPL-3.0.
