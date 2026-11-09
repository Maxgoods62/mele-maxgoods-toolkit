# Games

This page lists what the toolkit does on LE1, LE2 and LE3, then what has run in each game.
It is the record of test status.

## What works

"Accepted" means the tool takes that game with no guard, but it has never run there. "No" means the tool refuses that
game, or the feature does not exist there. Every tool that takes `game` requires it, the LE1-only ones too: `LE1`, `LE2`
or `LE3`.

| Area | LE1 | LE2 | LE3 |
|---|---|---|---|
| Read and write packages, new empty level, Kismet | yes | yes | yes |
| Compile and decompile script | yes | yes | yes |
| Strings (`tlk_get`, `tlk_set`) | `BioTlkFile` in packages | loose `.tlk` | loose `.tlk` |
| Read conversations | yes | yes | yes |
| Write conversations (`conv_*` writers) | yes | no | no |
| FaceFX (`facefx_get`, `facefx_copy_lines`) | yes | accepted | accepted |
| Read plot maps | yes | yes | yes |
| Write plot maps | yes | no | no |
| Read journal (`quest_map_get`) | yes | yes, plus codex | yes, plus codex |
| Write journal, `id_alloc` | yes | no | no |
| Conditionals | script functions | script functions | `.cnd` files |
| YAML builds, `mod_check`, prop catalog | yes | no | no |
| Start a mod | M3's Starter Kit, then `mod_build` | `dlc_new` | `dlc_new` |
| Merge mods, `mod_install` | yes | yes | yes |
| Config | `ConfigDelta-*.m3cd` | loose ini | `Default_<DLC>.bin` |
| GFx reading | yes | yes | yes |
| Native research (`exe_dump`, `native_query`) | accepted | yes | accepted |
| Saves (`save_*`) | no | yes | yes |
| `game_cycle`, `game_close` | LEX Interop, `loadSlot` | MeleBridge, `loadLatest` | MeleBridge, `loadLatest` |
| LEX Interop `game_*` tools | yes | accepted | accepted |
| `game_log`, `game_kismet_log`, each with its logger ASI | yes | yes | yes |
| `game_screenshot`, no ASI | yes | yes | yes |
| `game_plot`, `live_save`, `npc_place` | yes | no | no |
| `live_*` read, write, call, console, watch, status, keep ticking | yes | yes | yes |
| `live_place`, `live_remove` | yes | accepted | accepted |
| In-game editor (F8), scenario runner | yes | no | no |
| lectool | all but `wwise` | `wwise`, `coalesced`, `gfxpackage` and the commands its README marks Any | same as LE2 |

Some rows have limits:

- LE2 and LE3 keep conversations only in `_LOC_<LANG>` packages.
- On LE2 and LE3, `plot_map_get` prints `BioOutcomeMap` as int pairs, and their meaning is unverified.
- On LE2 and LE3, `live_status` gives null for `inLoad`. On LE3 it also gives null for the squad screen.
- On LE2, `le_find_file` names a DLC copy of `SFXGame`, `PlotManager` or `Startup` as the winner, but it never loads.

## What has run

Tested in game means the tool ran against the running game, or the game loaded its output. Tested on files means it
ran on game files, and the game never loaded the result.

### LE1, tested in game

- `game_status`, `game_actors`, `game_actor_get`, `game_actor_set`, `game_player_location`, `game_get`, `game_log`,
  `game_kismet_log`, `game_screenshot`, and `game_plot` through both of its routes.
- `game_console`, `game_stream_level`, `game_remote_event`: sent, effect not checked.
- `game_hot_reload`: partly. It loads, then the level reverts in 2 to 4 s on the Normandy.
- All `live_*` tools, and `npc_place`, whose NPC stayed after `mod_install`.
- `game_cycle` from a closed game to a screenshot in 38 s.
- `scenario.py`: a suite of about 40 checks, with a cold start through `game_cycle`.
- [Editor](design/in-game-editor.md) Phases 0 to 5a. Phase 5b is built, and its test is pending. Phase 5c is not built.
- Catalog props spawn on the Normandy. `editor_import` ran through build and install, and it refused a conflict.
- `le_find_file` matches the live TOC for 9,777 files.
- `mergemod_build`: Sound Settings' merge, applied by M3, passed in game (2026-11-02).

The LE1 runs used M3 9.2.1 build 137, LE1CP 2.0, `AutoTOCLE-v2`, `LE1AutoloadEnabler-v13`, `LE1DebugLogger-v8`,
`LE1KismetLogger-v4`, `LE1LEXInterop-v10`.

### LE2, tested in game

- MeleBridge, one run on a Normandy save: `live_find`, `live_get`, `live_set`, `live_describe`, `live_status`,
  `live_watch` on `BioWorldInfo.Tick`, `live_call` (return, out, name, bool and int args), `live_console`,
  `live_keep_ticking` with the game in the background.
- `game_cycle` with a mod: map at 33 s, screenshot at 41 s.
- `exe_dump`. `exe_dump_pe` output opened in a disassembler.
- `mergemod_build`: one merge mod applied by M3 and proved. Its M3 error and timeout paths have not run.
- A save from `save_head_default` loaded with `MorphHead` None.
- One test mod: M3 parsed `dlc_new` output, a `config_set` value read back, the `.tlk` loaded, and a function from
  `script_compile` returned its string through `live_call`.

### LE3, tested in game

- MeleBridge, one run on `BioP_Nor`, with the same list as LE2.
- `game_cycle` without a mod: boot to `BioP_Nor` in 31 s, restart in 40 s.
- `game_cycle` with mods: main menu at 24 s, a save's map at 32 s, a screenshot at 40 s (2026-11-09).
- One mod, Sound Settings (2026-11-02): M3 installed the `dlc_new` skeleton, the game showed the options rows that
  `config_set` wrote with their names from the `.tlk` files `tlk_set` wrote, and M3 applied its `mergemod_build` merge.

### All three, tested in game

- `game_close` in 4 to 14 s. Its `force` option has not run.
- MeleBridge string reference properties: `live_get`, `live_set` and `live_call`.

### Tested on files only

- `cnd_set`, `config_get`, `tlk_get` on shipped files, `gfx_scan` (196 movies), `pkg_to_text` round trip, `save_set`.
- Save parsing on 263 saves of 6 careers. `save_head_default` matched Trilogy Save Editor's bytes on one LE3 save.
- `script_compile` recompiles: LE3 `SFXGame` 7429/7429, LE2 4353/4355, LE1 2854/2854.
- `examples/hello-npc` builds, and a second build changes nothing. Nobody has played it.

### Not run

- LE2 and LE3: `live_place`, `live_remove`, `live_watch` while Kismet runs, `live_call` with string or array args,
  `live_status` during a level change, keep ticking with the game focused, LEX Interop `game_*` tools, FaceFX tools.
- LE2: `live_status` with the squad screen open.
- LE3: `script_compile` output loaded by the game.
- LE1 and LE3: `exe_dump`.
