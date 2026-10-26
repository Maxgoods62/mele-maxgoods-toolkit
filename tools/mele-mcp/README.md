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
| `export_decompile` | UnrealScript source of a class, state or function, e.g. `SFXGame.pcc` `SideloaderFramework` (LE1CP) |
| `level_new` | Empty level package (`TheWorld.PersistentLevel` + `BioWorldInfo`) from LEC's embedded template; the file name is the level name. Refuses an existing file and the game install |
| `export_set` | Set one leaf property by path (`VectorParameterValues[11].ParameterValue.G`), save. A property the export lacks (still at its class default) is created from LEC's class info |
| `export_set_batch` | Several leaf properties, any exports of one package, validated together, one save |
| `export_clone` | Duplicate an export and its children (references inside the copy relinked); a level actor is added to the level's `Actors` list |
| `export_import` | Copy an export from another package with everything it references (LEX "clone all references"); globally loaded classes and assets become imports. Optional add to `Actors`. Refuses if the target already has that path |
| `export_rename` | Rename an export (children follow). Use it after cloning or importing a forced export (actor type, conversation package): two loaded files with the same path share one object |
| `tlk_get` / `tlk_set` | Read, add or replace strings in an LE1 package's `BioTlkFile` exports (all of them by default: male/female copies, every language of a conversation's set) |
| `conv_read` | LE1 conversation as a text graph: start entries, entries and wheel choices, replies, plot conditions/transitions, node data (interp length, VO track and its strref) |
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
| `level_build` | Build a level's NPCs from a YAML file ([format below](#level_build-yaml)): pawn found by tag or imported from a template, place, own name, skin/hair/eye colors, lights that follow her, the Kismet that starts her conversation (listed in `missingConversations` while the level has none of that name), the Sideloader config; creates a missing level; writes only what differs |
| `mod_build` | Build every YAML file of a mod folder: plot files through `plot_build`, levels through `level_build`, conversations through `conv_build`, then again the levels that were missing a conversation. A new NPC builds from her YAML files alone |
| `mod_install` | Install a mod with ME3Tweaks Mod Manager (`--installmod`, optional `--bootgame`): DLC copy, Coalesced merge of ConfigDelta files, AutoTOC. Waits on M3's log, then compares every mod file with its installed copy. Refuses while the game runs |
| `conv_set_ids` | New `m_nResRefID` and node ids for a copied LE1 conversation (port of LEX `ChangeConvoIDandConvNodeIDs`) |
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

Requirements: the **LEX Interop ASI** (not listed in M3's ASI Manager; install it from LEX → Live Level Editor → requirements → "Interop ASI", which asks M3 to install ASI group 42), and **LEX closed**. The game sends replies to the first window titled "Legendary Explorer", and the server owns a hidden one.

Hot-reload notes:
- The ASI serves precached bytes only for package names **not found on disk** (`FindPackageFile` hook), so reloading under the same name silently loads the disk file. Hence the `_LIVE` name.
- The bytes must be **uncompressed** (LEX sends `SaveToStream(false)`). Sending a compressed file hung LE1: the engine's memory reader cannot decompress, falls back to opening the file by name, and that file is not on disk.
- On LE1 + LE1CP the copy does not last. `BioTriggerStream.Touch`/`UnTouch` and Kismet `BioSeqAct_SetStreamingState` re-apply the area's streaming state (hand-streamed levels unload) and then call `SideloaderFramework.RunSideloader`, which re-forces every Sideloader level by name (`ClientUpdateLevelStreamingStatus`). Tested on the Normandy: reverted in 2–4 s with Shepard standing still. **Never fire LE1CP framework events by hand** (`Poll_NPC_*`, `Live_NPC_*`): it re-spawned several NPC files twice and hung LE1 in a loading loop. Streaming a framework-managed `BIONPC_*` file out and in also leaves the framework's NPC dormant (Chakwas vanished).

**Write rule:** the write tools (`export_set*`, `export_clone`, `export_import`, `export_rename`, `tlk_set`, `conv_*` writers, `facefx_copy_lines`, `kismet_*`, `level_new`, `plot_map_new`, `plot_event_set`, `quest_set`, `plot_build`, `level_build`, `mod_build`) back the package up (and `AutoLoad.ini`, which `plot_build` edits) to `D:\MELE\backups\mele-mcp` (`MELE_BACKUP_DIR`) and refuse any file inside the game install. A file name can resolve to the base game or to another mod's installed copy (LE1CP overrides many level files). Edit the mod's source in the M3 library, then install it (`mod_install`, through M3) or copy it into the game.

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

Example: [`mods/claude-chakwas/level.yaml`](../../mods/claude-chakwas/level.yaml).

```yaml
package: 'D:\...\CookedPCConsole\ClaudeNPC_NOR10.pcc'   # created (empty level) if missing; full path only
sideloader: [BIOA_NOR10_07]    # optional: ConfigDelta .m3cd so LE1CP loads the level with files starting so (M3 merges it at install)
defaultLanguage: fr
globalTlk: [DLC_MOD_X_GlobalTlk.pcc, DLC_MOD_X_GlobalTlk_FR.pcc]   # where the NPC's name goes
lightTemplate: { package: BIOA_NOR10_07_DSG.pcc, actor: PointLightToggleable_7 }   # copied when the level has no light yet

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
    lights:
      - { tag: claude_npc_01_light, offset: [0, 0, 120], radius: 180, brightness: 1.2 }   # offset: forward, right, up; turns with her
```

Every value given is enforced; a value left out stays as it is. Only values that differ are written, so a rebuild changes nothing. Errors name the YAML line.

## Layout

| Path | Content |
|---|---|
| `src/MeleMcp.Core/` | Facade over LEC (`LecHost` init, `GamePaths`, `PackageInspector`, `ExportInspector`, `NameFinder`, `ScriptDecompiler`, `LevelCreator`, `TalkFiles`, `ConversationReader`/`ConversationEditor`, `FaceFxSets`, `KismetEditor`). Writes go through `PackageWriter.Edit` (guard, mutate, backup, save). Live link: `GameBridge` (pipe + reply window), `GameLink` (commands). No MCP dependency. |
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
