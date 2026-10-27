# mele-mcp

MCP server that gives Claude Code headless access to Mass Effect Legendary Edition packages through
[LegendaryExplorerCore](https://github.com/ME3Tweaks/LegendaryExplorer) (LEC). Plan and milestones: [docs/05](../../docs/05-toolkit-roadmap.md).

```
Claude Code (WSL) ─stdio─► D:\MELE\dotnet\dotnet.exe D:\MELE\tools\mele-mcp\MeleMcp.dll (Windows) ─► LEC ─► .pcc files
```

Private for now. LEC is GPLv3, so a published version must be GPLv3 too.

## Tools

| Tool | What it does |
|---|---|
| `le_status` | LEC version, resolved LE1/LE2/LE3 game paths, Oodle loaded, init time |
| `le_refresh_files` | Forget the cached game/DLC file list (call after installing or copying files) |
| `pkg_info` | Package summary: names/imports/exports, embedded LE1 talk files, top export classes. Takes a file name (highest mounted copy) or a Windows or `/mnt/<drive>/` path |
| `pkg_search` | Exports filtered by exact class and/or path substring → UIndex, class, instanced full path |
| `pkg_find_name` | Loaded packages whose name table contains a substring (tags, object and Kismet names), optional file wildcard. Partial load: no export data read |
| `export_get` | One export's header (NetIndex, flags, archetype, data offset and size) and properties as an indented tree; object references resolved to class, path, UIndex |
| `export_refs` | Exports in the same package that reference an export, and via which property (function bytecode included, so it answers "who calls X") |
| `export_diff` | Two exports compared property by property (e.g. our actor vs the vanilla one it was cloned from): class, archetype, flags, every property flattened (`StreamingStates[0].InChunkName`), subobjects paired by class and order; references by path, `<self>` inside the export; `(not set)` = class default |
| `export_decompile` | UnrealScript source of a class, state or function, e.g. `SFXGame.pcc` `SideloaderFramework` (LE1CP) |
| `level_new` | Empty level package (`TheWorld.PersistentLevel` + `BioWorldInfo`) from LEC's embedded template; the file name is the level name. Refuses an existing file and the game install |
| `export_set` | Set one leaf property by path (`VectorParameterValues[11].ParameterValue.G`), save. A property the export lacks (still at its class default) is created from LEC's class info |
| `export_set_batch` | Several leaf properties, any exports of one package, validated together, one save |
| `export_clone` | Duplicate an export and its children (references inside the copy relinked); a level actor is added to the level's `Actors` list |
| `export_import` | Copy an export from another package with everything it references (LEX "clone all references"); globally loaded classes and assets become imports. Optional add to `Actors`. Refuses if the target already has that path |
| `export_rename` | Rename an export (children follow). Use it after cloning or importing a forced export (actor type, conversation package): two loaded files with the same path share one object |
| `tlk_get` / `tlk_set` | Read, add or replace strings in an LE1 package's `BioTlkFile` exports (all of them by default: male/female copies, every language of a conversation's set) |
| `conv_read` | LE1 conversation as a text graph: start entries, entries and wheel choices, replies, plot conditions/transitions, node data (interp length, VO track and its strref) |
| `conv_find` | Lines of every conversation the game loads (base, DLC, mods) by speaker tag, text or wheel text, plot id, conversation name, file wildcard; only the asked language's `_LOC_` copies; mod lines read the global strings. 3,100 files in ~3 s |
| `conv_add_node` | Append a line copied from a template node (E or R): new strref and export id, plot cleared, node data (ConvNode → Interp → InterpData → EndCurrentConvNode) and FaceFX lines copied, VO track set to the strref |
| `conv_build` | Build a conversation from a YAML file ([format below](#conv_build-yaml)): a missing conversation copied from a `from` template (own names, resref and node ids, vanilla plot removed), new text for kept nodes (voice track and lip sync follow), new lines from templates, text in every language, plot conditions and transitions, wheels, next lines, starting list, in one save; running it again changes nothing new |
| `conv_link` | Add or remove a link: start → E, E → R (wheel choice: paraphrase strref, category), R → E |
| `facefx_get` / `facefx_copy_lines` | List the lip-sync lines of a `FaceFXAnimSet` (name, sound cue path and index, length); copy lines under new names, silent by default (LE1 plays `FXA_<strref>[_M]` for a VO track) |
| `plot_map_get` | State events of an LE1 `BioStateEventMap` (base game: `BIOC_Materials.pcc` `PlotManagerAuto.StateTransitionMap`): id and elements (`bool 8421 = True [v0]`, `int 47 += 2 [v1]`, consequence, function), filters by id range or by the variable written |
| `plot_map_new` | New DLC plot package `PlotManagerAutoDLC_<name>.pcc` with empty `StateTransitionMap` and `ConsequenceMap`; returns the `AutoLoad.ini` lines to add |
| `plot_event_set` | Add, replace or remove one state event from elements in `plot_map_get` notation; int and float elements are written as InstanceVersion 1, as the game expects |
| `id_alloc` | Free LE1 plot ids (bool, int, float, conditional, transition, consequence), quest ids or string ids from a start id, or a check of given ids. Sources: LEX's base-game plot database and `ModPlotsLE1` mod databases, the base game's plot maps, conditionals and GlobalTlk, and every installed DLC's `AutoLoad.ini` wiring; optional extra DLC folders (the M3 library copy) and packages (conversation TLKs). Each used id names its users |
| `quest_map_get` | An LE1 journal map (`BioQuestMap`): quests with goals, tasks and plot items (strrefs with their global text, states, InstanceVersions) and the evaluations a plot change runs |
| `quest_set` | Add, replace or remove one journal quest and its evaluations, with the base game's InstanceVersions; creates the `QuestMap` export (and returns the `AutoLoad.ini` line) when the package has none |
| `plot_build` | Build a DLC plot package from a YAML file ([format below](#plot_build-yaml)): transitions, consequences, journal quests with their GlobalTlk text, and the missing `AutoLoad.ini` lines; creates the package if missing; reports each event and quest as added, replaced or unchanged |
| `level_build` | Build a level from a YAML file ([format below](#level_build-yaml)). The level: vanilla sub-levels it shows inside a stream volume copied from a vanilla map, its post-process, start points (a location of our own: `at <map> <tag>`). Its usables: placeables (e.g. a terminal) that take Shepard to another map and start point. Its NPCs: pawn found by tag or imported from a template, place, own name, skin/hair/eye colors, lights that follow her, the Kismet that starts her conversation (listed in `missingConversations` while the level has none of that name), an idle pose with fidgets (a vanilla action-station prefab: left to talk, resumed after), the Sideloader config. Its props: copied, placed, turned (`yaw` or `rotation`) and scaled (`scale`, `scale3d`), or hidden (`removed`); creates a missing level; writes only what differs |
| `mod_build` | Build every YAML file of a mod folder: plot files through `plot_build`, levels (`npcs`, `usables`, `props`, `streaming` or `startPoints`) through `level_build`, conversations through `conv_build`, then again the levels that were missing a conversation. A new NPC builds from her YAML files alone |
| `mod_check` | Check a mod's built files in M3's library before installing: `refs` (LEX's reference check), `streaming` (a stream state without `InChunkName`: pawns frozen and black), `startpoints` (a pathless, unblocked start point: henchmen "destination blocked"), `strings` (a strref without text, per language), `facefx` (a line of the mod's own strings without `FXA_<strref>[_M]`), `ids` (plot, quest or string ids another installed mod or the base game defines). Planted bugs of each kind are caught; our mod passes |
| `mod_install` | Install a mod with ME3Tweaks Mod Manager (`--installmod`, optional `--bootgame`): DLC copy, Coalesced merge of ConfigDelta files, AutoTOC. Waits on M3's log, then compares every mod file with its installed copy. Refuses while the game runs |
| `conv_set_ids` | New `m_nResRefID` and node ids for a copied LE1 conversation (port of LEX `ChangeConvoIDandConvNodeIDs`) |
| `kismet_read` | A sequence as text in flow order (events first, then along output links): settings, `Out → Target.In` links (input names from class defaults when not serialized), variables with values; with the Kismet Logger's log, each op marked ✓ (input pins × count) or ✗, and nested sequences with how many of their ops fired; `recurse` follows `SequenceReference`s; `show` fired/unfired |
| `kismet_add` / `kismet_link` | Create a sequence object with class defaults (returns its link names); link output → input, variable slot → variable, event slot → event |

**Live link** (`game_*`, through ME3Tweaks' LEX Interop ASI, same protocol as LEX):

| Tool | What it does | Tested on LE1 |
|---|---|---|
| `game_status` | Game running, interop pipe present, reply round trip | ✅ |
| `game_actors` | Every visible level with its actors (name, tag), filters | ✅ 21 levels, 1,051 actors |
| `game_actor_get` / `game_actor_set` | Live location, rotation, scale, hidden; move/turn/hide one actor (not saved) | ✅ |
| `npc_place` | Put an NPC of a `level_build` YAML where Shepard stands: writes `location`/`yaw` into the file, builds the level, moves her and her lights live as a preview | ✅ medbay, then kept after `mod_install` |
| `game_player_location` | Shepard's position and rotation (reported only after a change, so it may ask you to take a step) | ✅ |
| `game_console`, `game_stream_level`, `game_remote_event` | Fire-and-forget commands | sent, effect not checked |
| `game_hot_reload` | Swap a streamed level for a package file under the name `<level>_LIVE<hhmmss>` | ⚠️ loads, but reverted within 2–4 s, see below |
| `game_get` | A property of every live object of a class (subclasses too), through the console's `getall`, read back from `DebugLogger.log`: values, structs, arrays (one element per line); a wrong name returns the game's "Unrecognized ..." | ✅ `bTickIsDisabled`, `Location`, `StreamingStates` |
| `game_plot` | LE1 plot bools, ints and floats in the running game, by id or by name from LEX's plot databases (base game + `ModPlotsLE1`); reads, then sets one id (`Was` keeps the old value); no transition or consequence runs. With MeleBridge: the plot table's own `GetBool`/`SetBool` (and Int, Float), answered on the pipe (`via: MeleBridge`). Without: BioWare's cheat execs `DumpPlotManagerValueByIndex` / `InitPlotManagerValueByIndex`, whose LogInternal answers are read from `DebugLogger.log`; that ASI writes info lines out only on a warning or every 5 s, so the wait sends a `getall` (logged as an error) each second. Commands queued together over LEX run nested, last first (LEX Interop runs them inside its `ProcessEvent` hook), so `GameBridge` leaves 50 ms between sends and the set waits for the read | ✅ both paths: ints 46/47 four times in a row, 10 bools, `Paragon` by name, bool 8440 False→True→False; 0.8 s through the bridge |
| `game_log` | The DebugLogger ASI's log (`Binaries\Win64\Logs\DebugLogger.log`): from the last load of a map, errors only, text filter. Reads the file, no pipe | ✅ found the stasis in 06 §A.24 |
| `game_kismet_log` | The Kismet Logger ASI's trace (`Binaries\Win64\Logs\KismetLogger.log`): every sequence op activated, in order, with level, instanced path (for `export_get`) and input pin; text filter, last N seconds, grouped with counts. Needs that ASI (M3 ASI Manager, dev-only). Conversation node ops show under the conversation's `_N` package | ✅ Dorn's squad prefab and conversation traced; paths resolve in `export_get` |
| `game_screenshot` | PNG of the game window's client area (PrintWindow with `PW_RENDERFULLCONTENT`, so a covered windowed game still captures; screen copy as fallback), scaled to 1280 wide by default; returns the WSL path to open with Read | ✅ windowed, freighter |
| `game_cycle` | Quit (console `exit`, `force` kills) → `mod_check` → `mod_install` with boot (or M3 `--bootgame` alone) → main menu (DebugLogger log of the new session) → `BioLoadGame <n>` → console commands → map loaded → screenshot. Slots are developer saves: `BioSaveGame <n>` (console, in game) writes `Game\ME1\BioGame\Save\BioSaveGame<n>.sav` where Shepard stands, `BioLoadGame <n>` loads it, even over a career; career saves (`Insane00_10.pcsav`) are not slots ("Can not find specified save slot"). M3 is now started with shell execute, so neither M3 nor the game holds the MCP stdio pipes open | ✅ quit, install, boot, menu, slot 99 from the menu: 38 s from a closed game to a screenshot. A boot crashed once at start (call through address 0 while the ASI loader hooked `ProcessEvent`); the cycle now boots again once |

Requirements: the **LEX Interop ASI** (not listed in M3's ASI Manager; install it from LEX → Live Level Editor → requirements → "Interop ASI", which asks M3 to install ASI group 42), and **LEX closed**. The game sends replies to the first window titled "Legendary Explorer", and the server owns a hidden one.

Hot-reload notes:
- The ASI serves precached bytes only for package names **not found on disk** (`FindPackageFile` hook), so reloading under the same name silently loads the disk file. Hence the `_LIVE` name.
- The bytes must be **uncompressed** (LEX sends `SaveToStream(false)`). Sending a compressed file hung LE1: the engine's memory reader cannot decompress, falls back to opening the file by name, and that file is not on disk.
- On LE1 + LE1CP the copy does not last. `BioTriggerStream.Touch`/`UnTouch` and Kismet `BioSeqAct_SetStreamingState` re-apply the area's streaming state (hand-streamed levels unload) and then call `SideloaderFramework.RunSideloader`, which re-forces every Sideloader level by name (`ClientUpdateLevelStreamingStatus`). Tested on the Normandy: reverted in 2–4 s with Shepard standing still. **Never fire LE1CP framework events by hand** (`Poll_NPC_*`, `Live_NPC_*`): it re-spawned several NPC files twice and hung LE1 in a loading loop. Streaming a framework-managed `BIONPC_*` file out and in also leaves the framework's NPC dormant (Chakwas vanished).

**Live objects** (`live_*`, through our own ASI, [MeleBridge](../mele-bridge/README.md): request/reply on one pipe, run on the game thread, LEX may stay open):

| Tool | What it does | Tested on LE1 |
|---|---|---|
| `live_find` | Live objects by class (subclasses, any package), name and path filters; paths for the tools below | ✅ 4 pawns in the freighter, the plot table |
| `live_get` | One object's properties, all or one by path (`Location.X`, `Arr[2]`): enums as names, objects as paths, structs nested | ✅ pawn `Location`, `Tag`, `Physics` = `PHYS_Walking`, all 26 KB |
| `live_set` | One property of one object in memory (JSON value; struct members, `Arr[i]`); returns the new value | ✅ `DrawScale` 1.5 then back to 1.0; a wrong name returns "no property 'Nope' in BioPawn". `bHidden true` changed the value but Dorn stayed visible: components take it in at attach time. `live_call SetHidden [true]` hid him (after setting the field back to false: SetHidden does nothing when the value does not change); his name and Talk prompt stay |
| `live_call` | An UnrealScript function on one object through `ProcessEvent`, args as JSON; return and out values | ✅ `BioGlobalVariableTable.GetInt 46` = 272, `SetBool 8440` true then false |
| `live_describe` | A class's chain, typed properties with their declaring class, function signatures | ✅ `BioGlobalVariableTable`, `BioPawn` |
| `live_console` | A console command (player chain, cheats included) and what it printed or logged, read from the DebugLogger log between the command and a flushing `getall` | ✅ `getall BioPawn bTickIsDisabled`, `DumpPlotManagerValueByIndex 47 int` |
| `live_watch` | `ProcessEvent` calls of functions for N seconds: time, object, class, params, Kismet input pin; overrides match (`Actor.Tick` catches `BioPawn.Tick`) | ✅ `Engine.Actor.Tick` |
| `live_place` | Moves an actor, or spawns a copy of one (`Actor.Spawn` with it as the template), in front of Shepard facing her, where she stands, or at coordinates; `yaw`, `turn`, `raise`. Props land on the floor (Shepard's Z − 94), pawns at her height. A StaticMeshActor can't spawn: a BioInert copy shows its mesh, materials and scale. Pawn copies are refused (the engine crashed) | ✅ a freighter crate (stand-in) next to Dorn; a VI terminal copy; Shepard teleported (the camera follows the controller's `Rotation`, set apart) |
| `live_remove` | Destroys an actor through Kismet's Destroy handler (`OnDestroy` with `Default__SeqAct_Destroy`); `bNoDelete` level actors are shut down (hidden, no collision), `bStatic` ones hidden | ✅ spawned copies removed, no script warning |
| `live_editor` | The in-game editor's state (F8): open, free cam, camera, selection (tag, yaw, spawned, why Delete refuses it), edits and unsaved count, status line, meshes in memory, last click, projection check against `Canvas.Project`, hooks, threads, input counters; no game-thread round trip | ✅ open, free cam, crate selected, projection error 0.1–0.6 px, 10,587 DirectInput reads held back |
| `editor_import` | Merges the in-game editor's session file (F8 → Save: `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json`, version 1 or 2) into the level YAML by tag, keeping comments. A `props` entry gets the location, rotation (`yaw` while upright and the entry has no `rotation`, else `rotation` in place of `yaw`), `scale` and `scale3d` the editor changed; `npcs`/`usables` a location and a yaw (a pitch, roll or scale is noted, not written). Field by field against the session entry's `base` (what the YAML held when the editor first changed the actor): the YAML still has the base's value → the editor's is written; the editor still has it → the YAML's stays (and goes into the session entry); both changed → a `conflict`, which `onConflict` settles: `refuse` (default: the actor is left out and listed with base, YAML and editor values), `editor` or `yaml`. Locations are equal within 0.01 (2 decimals), rotations as orientations (each axis within 0.02°: (180, 0, 0) is (0, 180, 180), 200° is −160°), scales within 0.001. A version 1 entry, or a spawn never imported, has no base: the editor's values win, as in phase 1. A mesh spawned in the editor gets a `props` entry with `mesh` (and `propTemplate` if missing), with `rotation`, `scale` and `scale3d` when not upright or not 1; a deleted prop gets `removed: true` (the build hides it; `npcs`/`usables` are only noted, not removed), an undone delete `removed: false`, both merged against the base like the other fields (`removed` changed by hand since the last import is kept). A spawn an import already put in a YAML file is never added again to a file without its tag (removed by hand, or in another level file of the map); on the map's own file it leaves the session when unchanged since. Every entry an import drops is kept in `<session>.dropped.json` beside the session (when, the YAML file, why): a YAML reverted alone reads as removed by hand, and copied back into `actors` with `"base": null` and no `imported`, the entry is added again. To undo an import, restore the session file together with the YAML (or try options with `keepSession`). Vanilla actors are skipped (phase 4). Each entry applied is marked in the session (`base` = what the YAML holds now, `imported: { before }` = what the installed level still shows), so MeleBridge re-applies it until the install shows it, then drops it; a spawn deleted before any import is dropped; `keepSession` leaves the file untouched; a session the editor saves during the import is merged again. Importing again changes nothing. A level the game loads into another map (sideloaded, the Normandy) uses the newest session with an actor of it, or with an entry whose tag the file has (a mesh imported into it before); with none, the error names the sessions with spawned meshes. Spawned meshes go into the map's own file, or the one given `takeSpawned: true`. Then `level_build`. Offline | ✅ freighter (phase 1): a spawned crate `added`, `claude_prop_crate` `removed`; built, installed, both as placed in game. Phase 3 (three-way, rotation, scale, marking): on scratch copies only |
| `live_save` | Writes a live actor's location and rotation into the level YAML entry with its tag (`npcs`, `usables`: a yaw; `props`: a yaw while upright, else `rotation` in place of `yaw`, never both), keeping comments; with none, adds a `props` entry from its template (and `propTemplate` for a static mesh); then `level_build` | ✅ the crate: YAML entry, built, installed, there and lit after a restart; Dorn's entry found by tag (no change) |

Requirements: `MeleBridge.asi` (`tools/mele-bridge/build.sh --install`, game closed). `game_plot` uses it when it is there.

**Prop catalog** (the in-game editor's prop menu, [docs/07](../../docs/07-ingame-editor.md) §3.5; offline, the game need not run):

| Tool | What it does | Tested on LE1 |
|---|---|---|
| `catalog_index` | Every StaticMesh of the files LE1 loads (LE1CP's copy wins; `DLC_MOD_ClaudeChakwas` and `DLC_MOD_MeleCatalog` left out), one row per path: the smallest file that has it (the copy source), bounds size, triangles, how many StaticMeshComponents of the game's levels place it, and what the curation rules (`CatalogRules`) make of it: `apl`, `apl-large`, `levelprop` are the default list by category; `arch-*`, `excluded-*`, `unknown` are left out. Stats; with `search`, `category` or `curatedOnly`, the meshes. Reused while no game file changes | offline-tested: 5,588 files, 956 with meshes, 13,517 mesh exports, 5,226 unique, 354 curated (Crates 32, Furniture 53, Medical 9, Consoles 25, Lights 60, Industrial 67, Debris 46, Alien 19, Small items 10, Signs 15, Plants 13, Other 5) in 2.6 s, 0 errors; same set as the catalog study |
| `catalog_build` | `MeleCatalog_Core.pcc`: each mesh copied from its source file (one `PackageCache`) under a group per category, not forced, so the bridge loads `MeleCatalog_Core.<Group>.<Name>` (a name taken twice gets `_2`); materials and textures keep their vanilla paths. Meshes whose material has no shader in the game (`GetBrokenMaterials`) are left out. Written to the dev mod `Mele Catalog` in M3's library (`moddesc.ini`, `AutoLoad.ini` mount 7420 when missing; the old package backed up). Then `catalog.json` (format 1) and 128×128 thumbnails (LOD0, diffuse texture with the material's tint, simple light; translucent and additive elements not drawn; masked ones cut where their opacity mask is under the clip value when it can be traced: the diffuse alpha of foliage and grates, the APL masters' `OpacityMask` texture, else drawn solid), both swapped in whole, in `%LOCALAPPDATA%\MeleBridge\LE1\catalog`. `meshes` adds paths; `thumbs=false` or `package=false` skip a part | offline-tested: 354 entries from 200 files in 11.8 s; package 24.7 MB, 2,828 exports, 73 imports (`BIOC_Materials`, `Engine`, `SFXGame`, `SFXVehicleResources`), 0 unresolved, 0 broken materials; 345 thumbnails (3.8 MB), 9 meshes without one (all see-through: glass, holograms, sign text); `catalog.json` 148 KB. Not yet loaded in game |

**Write rule:** the write tools (`export_set*`, `export_clone`, `export_import`, `export_rename`, `tlk_set`, `conv_*` writers, `facefx_copy_lines`, `kismet_*`, `level_new`, `plot_map_new`, `plot_event_set`, `quest_set`, `plot_build`, `level_build`, `mod_build`, `catalog_build`) back the package up (and `AutoLoad.ini`, which `plot_build` edits) to `D:\MELE\backups\mele-mcp` (`MELE_BACKUP_DIR`) and refuse any file inside the game install. A file name can resolve to the base game or to another mod's installed copy (LE1CP overrides many level files). Edit the mod's source in the M3 library, then install it (`mod_install`, through M3) or copy it into the game.

## conv_build YAML

Example: [`mods/claude-chakwas/ClaudeReyes_dlg.yaml`](../../mods/claude-chakwas/ClaudeReyes_dlg.yaml).

```yaml
package: 'D:\...\CookedPCConsole\ClaudeNPC_NOR10.pcc'   # absolute, relative to this file, or a loaded file name
conversation: ClaudeReyes_dlg   # found by name (or path); created from 'from' when the package has none
from:                          # optional: the conversation to copy the first time
  package: BIOA_NOR10_11_DSG_LOC_FR.pcc   # a loaded file name or a path
  conversation: nor20_req_officer_dlg
  resref: 742101               # its own m_nResRefID
  nodeIds: 74210100            # its own node ids, from here
defaultLanguage: fr            # text for TLKs whose language the node does not give (en = INT)
globalTlk: [DLC_MOD_X_GlobalTlk.pcc, DLC_MOD_X_GlobalTlk_FR.pcc]   # optional; language from the file suffix
start: [warm_hello, E16]       # replaces m_StartingList; first entry whose condition passes plays

nodes:
  E16:                         # E<n>/R<n>: a node that exists; only the keys given change
    strref: 74210010           # new text strref; its VO track and FaceFX line (FXA_<strref>[_M], silent) follow
    transition: 8421
    replies: [R1, normandy, R5]
  normandy:                    # a name: a new line, found again by its strref on the next build
    reply: 74210050            # reply (Shepard) or entry (NPC) + the strref of its text
    template: R7               # node to copy (cameras, gestures, timing, lip sync); E<n>/R<n> or a name above
    length: 3                  # seconds (InterpData length)
    text: { fr: "Que pensez-vous du Normandy ?", en: "What do you think of the Normandy?" }   # or a plain string
    wheel: { strref: 74210051, category: INVESTIGATE, fr: "Le Normandy", en: "The Normandy" }
    if: not bool 8422          # bool <id> | not bool <id> | bool <id> false | conditional <id> [param <n>] | none
    transition: 8422           # or none
    then: [normandy_answer]    # replies only: next entries; [] ends the conversation
  normandy_answer:
    entry: 74210052
    template: E9
    text: "Le vaisseau le plus avancé de l'Alliance..."
    replies:                   # entries only: the wheel, replaced as a whole
      - R1                     # wheel text and category from the reply's 'wheel', else from an existing choice to it
      - { to: R5, category: DEFAULT, paraphrase: 74210014 }   # or set them per choice
```

Text goes to every `BioTlkFile` of the conversation's TLK set: its own language, else the language it shows (FE → FR, GE → DE, IE → IT, RA → RU, PLPC → PL), else `defaultLanguage`. A TLK whose strings already match is left as it is. Errors name the YAML line. All target files are checked before any is written.

Squad comments (06 §A.25): copy a template whose speaker list names the henchmen (`hench_asari`, `hench_krogan`, ...). Give each henchman's line `if: conditional 415 param -1` (passes when that speaker is in the squad), and list them in priority order in the node before; the first that passes plays. A kept or templated node keeps its speaker.

`from` copies the template's package (`<name>_D`: the conversation and its TLK set) with everything it references (the `<name>_N` node data, FaceFX sets, sounds), then renames the packages to `<base>_D` / `<base>_N`, the conversation to `conversation` and the set to `TlkSet_<base>` (base: `conversation` without `_dlg`), gives it `resref` and node ids from `nodeIds`, and removes every node's plot condition and transition. It refuses when the package already has the template's package or the new names. The template's nodes stay in the file; the ones the YAML does not reach are never played.

## plot_build YAML

Example: [`mods/claude-chakwas/plot.yaml`](../../mods/claude-chakwas/plot.yaml).

```yaml
package: 'D:\...\CookedPCConsole\PlotManagerAutoDLC_X.pcc'   # created if missing (full path only)
autoload: ..\AutoLoad.ini      # optional; default: the DLC folder's, next to CookedPCConsole; none: leave it alone
defaultLanguage: fr
globalTlk: [DLC_MOD_X_GlobalTlk.pcc, DLC_MOD_X_GlobalTlk_FR.pcc]   # journal text goes here (language from the file suffix)

transitions:                   # StateTransitionMap: what a conversation node's transition runs
  8421: [bool 8421 = true]
  8422: [int 47 += 2, bool 8422 = true]   # elements as plot_map_get prints them; [] removes the event
consequences: {}               # ConsequenceMap, same format

quests:
  8425:
    mission: false             # true: Missions tab instead of Assignments
    title: { strref: 74210040, fr: "...", en: "..." }   # any text: a bare strref, or {strref, <language>: text}
    goals:                     # shown while its state bool is true; the last true goal wins
      - { state: 8421, description: { strref: 74210041, fr: "..." } }   # also: title, conditional
    tasks:
      - name: { strref: 74210044, fr: "Parler à Reyes" }
        description: { strref: 74210045, fr: "..." }    # also: plotItems [indexes], planet, waypoint
      - { name: 74210046, complete: true }              # moves the quest to Completed
    plotItems: []              # name, icon, state, conditional, targetItems
    evals:                     # when the variable changes, the first evaluation that passes shows its task
      - { when: bool 8421, task: 0 }                    # also: state (bool that must be true), conditional
```

Every event and quest in the file replaces the package's; the others are kept and listed in `notes`. Ids, states and conditionals take `none` for -1. Errors name the YAML line. All target files (package, GlobalTlk files, `AutoLoad.ini`) are checked before any is written. `mod_build folder=<dir>` runs every YAML of a folder: plot files first, then conversations.

## level_build YAML

Examples: [`mods/claude-chakwas/level.yaml`](../../mods/claude-chakwas/level.yaml) (an NPC), [`mods/claude-chakwas/cabin.yaml`](../../mods/claude-chakwas/cabin.yaml) (a location), [`mods/claude-chakwas/freighter.yaml`](../../mods/claude-chakwas/freighter.yaml) (a location with the squad). Every top-level key but `package` is optional.

```yaml
package: 'D:\...\CookedPCConsole\ClaudeNPC_NOR10.pcc'   # created (empty level) if missing; full path only
sideloader: [BIOA_NOR10_07]    # optional: ConfigDelta .m3cd so LE1CP loads the level with files starting so (M3 merges it at install)
defaultLanguage: fr
globalTlk: [DLC_MOD_X_GlobalTlk.pcc, DLC_MOD_X_GlobalTlk_FR.pcc]   # where the NPC's name goes
lightTemplate: { package: BIOA_NOR10_07_DSG.pcc, actor: PointLightToggleable_7 }   # copied when the level has no light yet

# A location: vanilla sub-levels shown inside a stream volume copied from a vanilla level (same place and shape)
streaming:
  volume: { package: BIOA_NOR00.pcc, actor: BioTriggerStream_27 }   # a BioTriggerStream around the room
  levels: [BIOA_NOR10_09_ART, BIOA_NOR10_09_LAY]                     # file names; listed in BioWorldInfo.StreamingLevels
postProcess: BIOA_NOR00.pcc                                          # copy that level's DefaultPostProcessSettings
startPointTemplate: { package: BIOA_NOR00.pcc, actor: BioStartPoint_23 }   # imported for a start point the level does not have
startPoints:
  start_cld00: { location: [-6266.2, 12822.5, -36737], yaw: 0 }   # Tag → at <map> start_cld00
characterLights:               # light actors copied as they are, e.g. a map's character lights from its _DSG file
  - { package: BIOA_FRE31_00_DSG.pcc, actor: StaticLightCollectionActor_68 }
squad: { package: BIOA_FRE31.pcc }   # party selection on load, then the picked henchmen spawn and follow (that file's squad prefab)

npcs:
  reyes:                       # a label for the result
    tag: claude_npc_01         # Tag and UniqueTag: how the build finds her again
    template: { package: BIONPC_Chakwas.pcc, actor: BioPawn_3 }   # imported when no pawn has the tag; or an actor of this level to clone
    location: [-3965, 13160, -36739.445]
    yaw: 90                    # degrees: 0 faces +X, 90 faces +Y
    active: true               # default: shown (bHidden=False) and behavior on (bActive=True)
    name: { type: ClaudeNurseReyes, strref: 74210001, fr: "Infirmière Reyes", en: "Nurse Reyes" }   # own actor type copy
    skin: [0.4, 0.19, 0.1]     # [r, g, b] or [r, g, b, a]
    hair: [0.4, 0.02, 0.8]
    eyes: [0.03, 0.15, 0.6]
    conversation: ClaudeReyes_dlg   # a BioConversation in the level; Shepard uses her → it starts (missing: noted, linked on a later build)
    idle:                      # ambient pose: a copy of the first action-station prefab of 'package' entering 'pose'
      package: BIOA_NOR10_03_DSG.pcc
      pose: HMM_DP_ArmsCrossed # also there: HMM_WI_Railing, HMM_WI_StandTyping; a wrong pose lists the file's poses
      weights: { Talk: 0, Point: 0, HeadShake: 0 }   # fidget weights (Wt_<name> slots); the ones left out keep the prefab default
    lights:
      - { tag: claude_npc_01_light, offset: [0, 0, 120], radius: 180, brightness: 1.2 }   # offset: forward, right, up; turns with her

propTemplate: { package: BIOA_FRE31_00_LAY.pcc, actor: BioInert_0 }   # a BioInert: copies of it show the props' static meshes
props:                         # any actor copied from a template, or a mesh; live_save and editor_import write these
  crate:                       # a label for the result
    tag: claude_prop_crate     # how the build finds it again (an actor of the level, any class)
    template: { package: BIOA_FRE31_00_LAY.pcc, actor: StaticMeshActor_171 }   # or an actor of this level to clone
    location: [-1670, -15864.6, -452.2]   # its pivot: for most meshes, the floor
    yaw: 200                   # degrees; pitch and roll stay as the actor has them
  ed_Crate_XLarge_03_fmk3a_1:  # editor_import names it by its tag: ed_<mesh>_<game run>_<n>
    tag: ed_Crate_XLarge_03_fmk3a_1
    mesh: { package: BIOA_FRE31_00_LAY.pcc, export: biog_apl_phy_crate_xlarge_01_l.Crate_XLarge_03 }   # instead of template: a StaticMesh of that file
    location: [-1257, -15850, -365]
    rotation: [10, 180, 0]     # instead of yaw (not both): [pitch, yaw, roll] in degrees
    scale: 1.25                # optional: DrawScale
    scale3d: [1, 1, 2]         # optional: DrawScale3D, per axis
    removed: true              # optional: the build hides its actor without collision (none: creates nothing); false or left out shows it again

usables:
  cabinTerminal:               # a label for the result
    tag: claude_to_cabin       # how the build finds it again (a BioUseable of the level)
    template: { package: BIOA_NOR10_04_DSG.pcc, actor: BioUseable_10 }   # the comm room's VI terminal; or an actor of this level to clone
    location: [-3933.6, 13936.8, -36833.9]   # its base: the floor (a standing pawn's Z - 94)
    yaw: -94.2                 # this mesh's front is its +Y: (direction to the player) - 90
    name: { strref: 74210060, fr: "Cabine (copie)", en: "Cabin (copy)" }   # shown when Shepard aims at it (GlobalTlk)
    goTo: { map: BIOA_CLD00, startPoint: start_cld00 }                     # Shepard uses it → that map, at that start point
```

Every value given is enforced; a value left out stays as it is. Only values that differ are written, so a rebuild changes nothing. Errors name the YAML line.

A prop whose template is a StaticMeshActor is not copied as one: a StaticMeshActor is lit by light baked where it stands, so a copy elsewhere renders black (with or without its lightmap). The build copies `propTemplate` instead, a BioInert whose `DynamicLightEnvironment` lights it where it is, and gives its `PlaceableMesh` the template's mesh and materials, and the actor its `DrawScale`/`DrawScale3D`. A `mesh` (what the in-game editor spawns: a StaticMesh export, by its path in the file, e.g. a forced-export package inside a level file) goes on a copy of `propTemplate` the same way, with the mesh's own materials, at scale 1; give `template` or `mesh`, not both. A new copy of `propTemplate` starts turned as the template StaticMeshActor (its prefab archetype's rotation included), or not at all for a `mesh`, not as the BioInert it was copied from; then `yaw` sets the yaw and keeps that pitch and roll, `rotation` sets all three, and `scale`/`scale3d` set `DrawScale`/`DrawScale3D`. A prop entry with both `yaw` and `rotation` is an error (with its line). A `removed: true` prop needs neither: its actor gets `bHidden` true and `bCollideActors`/`bBlockActors` false (as `live_remove` does in game; `CollisionType` is transient in LE1, so a file can't set it). Without it, a prop hidden that way is shown again with the values of what it is copied from (its template, or `propTemplate`); one with neither is left hidden, with a note. An imported template is first flattened: what it inherits from prefab archetypes (most props of a level are prefab instances) is written into it and its subobjects, which then point at the class defaults; otherwise the import brings the whole prefab (8 MB for one crate).

A usable is made targetable (its behavior's `m_bTargetable` override: vanilla terminals wait for their story's Kismet). `goTo` Kismet in `Main_Sequence`: `BioSeqEvt_ArtPlaceableUsed` (Originator = the usable) → `BioSeqAct_AreaTransition`.

`streaming` gives the volume one `Default` state whose `VisibleChunkNames` are the `levels`: they show while the player is inside it, including when spawned there. Its `InChunkName` is the first level: without one, pawns that touch the volume stay in stasis (no AI, no light update). The volume is found again by its source name; a start point by its tag, and it is put on the level's navigation list, blocked (`bBlocked`): it has no paths, so it must not be where AI paths start. The level's `BioWorldInfo` says it has a path network (`bPathsRebuilt`, `bHasPathNodes`), as vanilla persistent levels do.

`squad` Kismet in `Main_Sequence`: level loaded and visible → `SeqAct_WaitForLevelsVisible` (the `streaming` levels) → cinematic mode on → black screen → party selection screen → cinematic mode off → remote event `Hench_SetupSquad`, which runs the copied squad prefab (the `SequenceReference` around the package's `BioSeqAct_ShowPartySelectionGUI`); its `Setup Done` → screen back on.

`idle` Kismet in `Main_Sequence`: level loaded and visible → prefab `In`; with a conversation, use → `BioSeqAct_LeaveActionStation` and conversation `Out` (its end: the start action is latent) → prefab `In`. The prefab's `Teleport`, `Snap To Pose`, `Coll/PhysOFF` and `SnapToFloor` get a false bool, so she stays where `location` puts her. A new `pose` replaces the prefab (the old one is trashed, as LEX does).

## Layout

| Path | Content |
|---|---|
| `src/MeleMcp.Core/` | Facade over LEC (`LecHost` init, `GamePaths`, `PackageInspector`, `ExportInspector`, `NameFinder`, `ScriptDecompiler`, `LevelCreator`, `TalkFiles`, `ConversationReader`/`ConversationEditor`, `FaceFxSets`, `KismetEditor`; prop catalog: `CatalogIndex`, `CatalogRules`, `CatalogBuilder`, `ThumbnailRenderer`, `MaterialDiffuse`). Writes go through `PackageWriter.Edit` (guard, mutate, backup, save). Live link: `GameBridge` (pipe + reply window), `GameLink` (commands), `LiveBridge` (MeleBridge client). No MCP dependency. |
| `src/MeleMcp/` | stdio MCP host (`Program.cs`) and tool classes (`Tools/`) |
| `external/LegendaryExplorer/` | LEX submodule, pinned at tag `6.5.0.6522` (commit `43bf73a`) |
| `scripts/deploy.sh` | Stop running server → publish to `D:\MELE\tools\mele-mcp` → smoke test |
| `scripts/mcp_smoke.py` | JSON-RPC client: initialize, tools/list, calls each tool |
| `scripts/mcp_call.py` | Call one tool from the shell with a fresh server: `mcp_call.py export_get '{"package":"...","export":"35"}'` |

## Setup (already done on this machine, 2026-10-25)

1. .NET SDKs 8.0.426 and 10.0.402 in `D:\MELE\dotnet` (no admin, installed with Microsoft's signed `dotnet-install.ps1 -InstallDir D:\MELE\dotnet`).
2. `git submodule update --init` (shallow).
3. `./scripts/deploy.sh`
4. Registered at project scope in `/.mcp.json` (`claude mcp add mele -s project ...`). Approve it when Claude Code starts in this repo.

## Dev loop

1. Edit code.
2. `./scripts/deploy.sh` (about 5 s; optional argument: a package for the `pkg_info` smoke call).
3. In Claude Code: `/mcp` → `mele` → Reconnect (the deploy stopped the old process).

## Known LEC 6.5 traps for upcoming tools

From [docs/06 §B.1](../../docs/06-le1-first-mod.md#b1-how-lec-65-resolves-le1-dlc-mod-files):

- `AutoloadIni` round-trips lose `PlotManagerConsequenceMap<n>`, `DotU<n>` and `GlobalPackages<n>`. Write our own `AutoLoad.ini` reader/writer before `dlc_new`.
- LEC treats `DLC_MOD*` as a mod folder, but the game's ASIs need `DLC_MOD_` (case-sensitive). Match the game.
- File lookups are cached (`GamePaths.ResolvePackage`). Any tool that installs or copies files must call `MELoadedFiles.InvalidateCaches()`.
- `CommonStructs.LinearColorProp(r, g, b, a)` writes `r` into all four channels. Build the `LinearColor` struct by hand (`level_build` does).

## Gotchas found in the M0 spike

| Symptom | Cause | Fix in place |
|---|---|---|
| `CS0121` ambiguous `CopyTo` in `EntryImporter.cs` with SDK 8 | LEC 6.5 targets `net8.0` but needs the C# 14 compiler (first-class spans) that upstream CI gets from a newer SDK | `global.json` pins SDK 10.0.402; output still targets `net8.0` |
| LEC built without registry lookup / native DLLs | LEC only defines `WINDOWS` in its `Win*` configurations | `ProjectReference ... SetConfiguration="Configuration=WinRelease"` |
| Server hangs at startup, no output at all | Default host builder watches `appsettings.json` in the working directory; Claude Code starts us in a `\\wsl.localhost\` directory, where that watcher hangs | `Host.CreateEmptyApplicationBuilder` with `ContentRootPath = AppContext.BaseDirectory` |
| `MELE_LE_DIR` ignored | WSL only passes env vars named in `WSLENV` to Windows processes | `.mcp.json` sets `WSLENV=MELE_LE_DIR` |
| `Permission denied` running `MeleMcp.exe` from WSL | Windows build output on the WSL filesystem has no exec bit | Run via `dotnet.exe MeleMcp.dll` |
| Publish fails: file locked by `.NET Host` | Running server (or a hung test) holds `MeleMcp.dll` | `scripts/stop-server.ps1` runs first in `deploy.sh` |
| `dotnet-install.ps1` IOException when adding an SDK | MSBuild/compiler server processes keep files open in `D:\MELE\dotnet` | `dotnet.exe build-server shutdown` before installing |
| Generic "An error occurred invoking" errors | SDK hides non-`McpException` messages | `ToolErrors.Run` forwards type + message (local server, nothing to hide) |
