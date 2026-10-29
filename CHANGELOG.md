# Changelog

Notable changes to mele-maxgoods-toolkit. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The
version is mele-mcp's (`<Version>` in `tools/mele-mcp/src/MeleMcp/MeleMcp.csproj`), the one a mod YAML's
`requires: mele-mcp >= X.Y.Z` is checked against. MeleBridge has its own `ASI_VERSION` and pipe protocol number
(`tools/mele-bridge/src/SharedVersion.h`).

## 0.1.0 (unreleased)

### Added

mele-mcp, an MCP server on LegendaryExplorerCore 6.5, with 72 tools:

- Game files: `le_status`, `le_refresh_files`, and `le_find_file`, which says which copy of a file the game loads. For
  LE1 it follows the installed AutoTOC ASI.
- Packages, read only: `pkg_info`, `pkg_search`, `pkg_find_name`, `pkg_to_text` (properties as text LEC compiles
  back, with a `roundTrip` check), `export_get`, `export_refs`, `export_decompile`, `export_diff`.
- Packages, write: `export_set`, `export_set_batch`, `export_clone`, `export_import`, `export_rename`, `level_new`.
  Every write tool backs the file up first and refuses files inside the Legendary Edition folder.
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
  boots, loads a save, runs console commands and takes a screenshot.
- The running game without MeleBridge, through the LEX Interop ASI, the logs of the DebugLogger and Kismet Logger
  ASIs, and the game window: `game_status`, `game_actors`, `game_actor_get`, `game_actor_set`, `game_player_location`,
  `game_console`, `game_get`, `game_stream_level`, `game_remote_event`, `game_hot_reload`, `game_log`,
  `game_kismet_log` and `game_screenshot`.
- The running game, through MeleBridge: `live_find`, `live_get`, `live_set`, `live_call`, `live_describe`,
  `live_console`, `live_watch`, `live_place`, `live_remove`, `live_save` and `live_editor`. `game_plot` reads and sets
  plot values through MeleBridge, or without it through BioWare's cheat commands and the DebugLogger log.
- The in-game editor's side: `catalog_index` and `catalog_build` (the prop catalog, its thumbnails and the Mele Catalog
  dev mod), and `editor_import`, a three-way merge of an editor session into a level YAML.

Set-up and settings:

- The scripts of `tools/mele-mcp` and `tools/mele-bridge` read their settings from an ignored `build.env`, next to a
  tracked `build.env.example`. A variable exported in the environment wins over the file.
- mele-mcp finds the Legendary Edition folder in the registry when `MELE_LE_DIR` is unset: BioWare's `Install Dir`,
  then Steam's uninstall entry.
- `scripts/deploy.sh` publishes to `MELE_OUT` (default `%LOCALAPPDATA%\mele-mcp\server`) and stops only the servers
  that run that folder's `MeleMcp.dll`.
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

MeleBridge 2, pipe protocol 1:

- `MeleBridge.asi` serves `\\.\pipe\MeleBridge_LE1`: one JSON request per line, with the ops `ping`, `console`,
  `find`, `get`, `set`, `call`, `describe`, `subscribe` and `editor`.
- `ping` returns the pipe protocol. mele-mcp checks it once per game process and refuses a bridge with another
  number, `editor` requests excepted.
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

Docs: [docs/design/in-game-editor.md](docs/design/in-game-editor.md), [docs/formats.md](docs/formats.md) (the pipe
protocol, `levels.json` version 1, session files versions 1 to 3, `catalog.json` version 1) and
[docs/le1-lighting.md](docs/le1-lighting.md).

License: GPL-3.0.
