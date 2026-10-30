# Scenario runner

`tools/mele-mcp/scripts/scenario.py` runs in-game tests from suite files, unattended, against a running LE1. It talks to
the game through one mele-mcp server that it starts itself, and writes a report with every step, the screenshots and
the log slices. A suite lives with the mod it tests; the runner knows nothing about any mod.

## Prerequisites

- LE1 running, on any map (the main menu is fine).
- MeleBridge 3 (`tools/mele-bridge/build.sh --install`, game closed), and the DebugLogger and Kismet Logger ASIs.
- mele-mcp deployed with `live_status` and `live_keep_ticking` (`tools/mele-mcp/scripts/deploy.sh`). Server settings come
  from the environment, then `tools/mele-mcp/build.env`, as for `mcp_call.py`.
- Python 3 with PyYAML (`python3-yaml`) for `.yaml` suites. Without it only `.json` suites load.

## Running

```
python3 -I scenario.py PATH [PATH...] [--only IDS] [--from ID] [--dry-run] [--out DIR] [--unattended]
python3 -I scenario.py --restore-saves [BACKUP_DIR] [--out DIR]
python3 -I scenario.py --selftest
```

- `PATH` is a suite file (`.yaml`, `.yml`, `.json`) or a folder. A folder runs its files in name order and skips files
  that start with `_` (shared bases to include) and `README*`.
- `--only` takes a comma list of `fnmatch` patterns matched against `<file id>` and `<file id>/<check id>`, e.g.
  `--only 01-dock/repick,03-*`. A file with no check left runs nothing, not even its setup.
- `--from ID` skips everything before the first file or check that matches.
- `--dry-run` loads and validates everything offline (shape, step kinds, arguments, variables, marks, the guards that
  can be checked without the game) and prints the plan, with what would SKIP and why. No server starts.
- `--out` is the report root, a Windows or `/mnt` path. Otherwise the first file's `out`, then `MELE_SCENARIO_OUT`, then
  `%LOCALAPPDATA%\mele-mcp\scenarios`. Each run writes `<root>\<suite>\<yyyymmdd-hhmmss>\`.
- `--unattended`: a check with `needs: [user]`, or one that reaches a `prompt` that is not already satisfied, is SKIP.
- A run takes minutes to an hour. From Claude Code, start it in the background and follow `progress.log`.

Exit codes: 0 no FAIL, ERROR or ABORTED (SKIP and REVIEW are fine); 1 a FAIL or ERROR; 2 a usage, suite or prerequisite
error; 3 ABORTED; 4 a save restore pending or failed. 4 wins over the others, since it needs you to act.

## What a run does

1. Validates every selected file, as `--dry-run` does. A `cold_start` file must be the last one.
2. Checks that LE1 runs, starts the server, makes the run folder, waits up to 120 s for `live_status`.
3. Turns `keep_ticking` on and checks that the bridge sees `BioWorldInfo.Tick`, so the game keeps running while its
   window is in the background. It stays on for the run and goes off at the end.
4. Backs up the career's saves, when a selected file has `saves: true` (see Saves).
5. For each file: `setup`, then the plot values listed in `restore` are read, then each check (its steps, then its
   `cleanup`), then `teardown`. The plot values are written back at the end of the file, also after a failure. When
   they cannot be read, every check of the file is ERROR and only `teardown` runs.
6. At the end: `keep_ticking` off, saves restored if the game is closed, `report.md` and `results.json`. Ctrl-C,
   SIGTERM (a stopped background task) and SIGHUP take the same path, as ABORTED. So does an error that stops the
   run: the server or a prerequisite failing (STOPPED, exit 2), or a bug in the runner (ERROR, exit 1).

## A suite file

| Key | Meaning |
|---|---|
| `suite` | Required, `[a-z0-9_-]+`, the same in every file of a run. Names the report folder. |
| `id` | The file's id (default: its name without extension). |
| `title` | Required. |
| `game` | Only `LE1`. |
| `out` | Report root (see Running). |
| `include` | Files, relative to this one, whose `vars` and `macros` are merged in. Later wins; the file's own last. |
| `career` | The career folder name. Required for `load: {save}`, `quickload`, `quicksave` and the save backup. |
| `saves` | `true`: the file may `quicksave`. Its career is backed up first. |
| `cold_start` | `true`: the file may use `game_cycle` and `close_game`. It must run last. |
| `skip` | A reason: every check is SKIP and nothing runs (the steps are still validated). |
| `rows` | Free text for the report. |
| `restore` | `{bool: [ids], int: [ids], float: [ids]}`: plot values put back at the end of the file. |
| `vars`, `macros` | Variables, and named step lists for `use`. |
| `setup`, `teardown` | Steps. A setup failure makes every check ERROR. Teardown runs after a failure, not after ABORTED. |
| `checks` | A list of `{id, title, row, skip, needs: [user], steps, cleanup}`. A check stops at its first failing step, then runs its `cleanup` (failures there are WARN). |

A step is a mapping with one kind key. Its arguments sit in that key's value, a mapping, or a scalar where the table says
so. Beside the kind key: `name` (a label), `optional: true` (a failure is WARN and the check goes on) and `save_as: var`
(the step's value goes into a variable). `save_as` and `optional` may also sit inside the mapping.

```yaml
- squad_pick: {members: [hench_quarian, hench_turian]}
- wait_until: {count: hench, expect: 1, stable: 4, timeout: 30}
- live_get: {object: "${hench}>m_oBehavior>m_PawnAttributes", property: m_TalentPoints}
  save_as: points
```

**Variables.** `vars` values are literals or finders, `{find: {class, tag, name_contains, path_contains, where: {Prop:
value}, alive: true}}`. `${name}` works in any string; a string that is only `${name}` keeps the value's type, and
`${a.b}` and `${a[0]}` index into it. A finder in `${}` must match exactly one live object (`alive` drops objects with
`bDeleteMe`). Where a step takes a finder (`count`, a `count` condition) give the variable's bare name or a finder
mapping. Built-ins: `map`, `world`, `pc`, `player` (the pawn, or the vehicle while driving), `gvt` (the
`BioGlobalVariableTable`), `gui` (the controller's `GetScaleFormManager`), `run_dir`, `stamp`, `file`, `check`, `career`.

**Chains.** An object or property may hop through object properties with `>`: `"${hench}>m_oBehavior"` is an object,
`"m_oBehavior>m_PawnAttributes>m_TalentPoints"` a property. `[*]` maps the rest over an array, dynamic or static, and
`.field` digs into a struct element: `m_oBehavior>m_oEquipment>m_aEquipped[*]>m_nID` and
`Members[*].SquadMember` are lists.

**expect** is a value (equality) or a mapping of `eq`, `ne`, `min`, `max`, `lt`, `gt`, `in`, `contains`, `regex`,
`null: true|false`, `len: n|{min, max}`. Strings compare without case, numbers within 1e-4, and plot text (`"True"`,
`"12"`) as a bool or number.

**Conditions** (for `assert`, `wait_until`, `prompt.until`) have one source key and an `expect` (without one, the value
must be true or non-zero): `status: <dotted key of live_status>`, `get: {object, property}`, `call: {object, function,
args}` (its return), `count: <finder>`, `plot: {bool|int|float: id}`, `distance: {a, b, axis}`, `kismet: {contains,
since}` (matching KismetLogger lines), `log: {regex, since, errors_only}` (matching DebugLogger entries), and `all` /
`any` with a list of conditions.

**Marks** are positions in both logs: `file`, `check`, `step`, `load` (the trigger of the last `wait_loaded` that
passed) and `mark:<name>` for a `mark` step. `since:` takes one of them.

## Steps

| Kind | Arguments [default] | What it does |
|---|---|---|
| `use` | macro name, or `{macro, with: {var: value}}` | The macro's steps, with `with` set while they run. |
| `console` | command, or `{command, expect_lines, forbid_lines, timeout: 10}` | `live_console`, after the console guard; regexes over the lines it printed. |
| `load` | `{slot: n}` or `{save: <career>_NN}`, `map`, `timeout: 240` | `BioLoadGame n` (developer slots are only read), or `nextframedefer loadgame <save> DevId=-1`, then `wait_loaded`. A load error in the log fails at once. |
| `quickload` | `{map, timeout: 240}` | Career guard, `QuickLoad`, then as `load`. |
| `at` | `{map, start, allow_party: false, timeout: 240}` | The cheat `at <map> <start>`, then `wait_loaded`. |
| `wait_loaded` | `{map, expect_load: true\|false\|auto, allow_party: false, settle: 3, load_grace: 10, timeout: 240}` | See below. |
| `plot` | `{bool: {id: value}}` (also `int`, `float`) | Sets each value, reads it back. |
| `assert_plot` | `{bool: {id: expect}}` | Reads and checks. |
| `assert` | a condition | Checks once. |
| `wait_until` | a condition, `timeout: 30, stable: 0, poll: 1` | Until it holds for `stable` seconds. |
| `sleep` | seconds | Waits, still watching the logs. |
| `mark` | name | A named mark. |
| `live_get` | `{object, property, depth, expect}` | A property, through chains. |
| `live_set` | `{object, property, value}` | Sets it; the read-back must equal `value`. |
| `live_call` | `{object, function, args: [], expect, timeout: 5}` | `expect` checks the return; `save_as` keeps `{return, out}`. |
| `count` | `{find, expect}` | Live objects a finder matches; `save_as` keeps their paths. An object that is gone, or lacks a filtered property, does not match; any other bridge error fails the step. |
| `distance` | `{a, b, axis: xyz\|xy\|z, expect}` | Between two objects, `player`, or `{X, Y, Z}` / `[x, y, z]`, in Unreal units. |
| `teleport` | `{object: player, to: [x,y,z]}`, `{near, offset}` or `{ahead: units}` | `SetLocation`; retries 50 units higher, or closer for `ahead`. |
| `raise_event` | `{object}` or `{name, class: SeqEvent_RemoteEvent, path_contains: "${map}.TheWorld"}`, `originator`, `instigator`, `pick: one\|all` | `CheckActivate` on the event, which also reaches events nested in prefabs. |
| `squad_pick` | `{members: [tag or index, ...], open: console\|none\|event, event, expect_spawn: true, timeout: 30}` | See below. |
| `snapshot` | `{name, object, props}` | Reads the props; `compare` re-reads them on the object found again. |
| `compare` | `{snapshot, ignore, tol}` or `{objects: [a, b], props}` | Lists every difference. |
| `quicksave` | `{timeout: 60}` | See Saves. |
| `screenshot` | name, or `{name, review: false, max_width: 1280}` | `shots/NN-<file>-<check>-<name>.png`. `review` makes the check REVIEW. |
| `assert_log` | `{since: check, errors: true, regex, ignore, expect: {max: 0}}` | Counts DebugLogger entries (problems only with `errors`); `save_as` keeps them normalised. |
| `compare_log` | `{with: var, since: load, mode: no_extra\|same, ignore}` | Problems now against a saved set. |
| `assert_kismet` | `{since: check, contains, expect: {min: 1}}` or `{since, order: [...]}` | KismetLogger lines; `order` wants each text after the previous one. |
| `prompt` | text, or `{text, until, timeout: 300}` | A message box for a person, closed once `until` holds. Passes at once when `until` already holds. |
| `watch` | `{functions, seconds: 10, object_contains, expect: {min: 1}}` | `live_watch`; counts the calls. |
| `keep_ticking` | `on` / `off` | Only for checks that test focus behaviour. |
| `game_cycle` | `{commands, wait_for_map, settle: 8, timeout: 300}` | `cold_start` only: closes the game, boots it through M3 with no mod, runs `commands` at the main menu. Follow it with `wait_loaded`. |
| `close_game` | `{timeout: 60, force: false}` | `cold_start` only, and the only place `exit` is sent. |
| `backup_saves`, `restore_saves`, `status` | none | See Saves; `status` records one `live_status`. |

**wait_loaded.** It first waits for `Loading package synchronously: <map>` in the log after the trigger (the step
before it): `true` requires it, `auto` waits `load_grace` seconds for it, `false` skips it. Then it polls `live_status`
every second. A bridge timeout counts as loading. A poll is ok when the map is right, the world has begun play, there is
a pawn, no streaming level is pending, the loading movie is off, the squad screen is closed (or open, with
`allow_party`), the world is not paused and `keep_ticking` is on. Without `map`, the map of the load line it saw
must be the one loaded, and a load line that comes after `load_grace` still counts. Two ok polls, `settle` seconds,
one more ok poll: loaded. Send nothing to the game between a load and its `wait_loaded`; console input during a load
screen is lost.

**squad_pick.** Opens the squad screen unless it is open (`ce Hench_PickSquad`, a `raise_event`, or not at all), waits
until the movie has listed the members, maps tags to cards, and checks what the movie would: two members (or every
available one), each available, the forced member included. Then it sets both picks on the handler and calls
`FinalizeParty`, as Accept does, waits for the panel to close, checks the InParty flags, and with `expect_spawn` waits for
the loading phases a pick can start.

## Guards

- Tools: only the live, plot, log, screenshot, status and `game_cycle` tools; `game_remote_event` refuses `Live_NPC_*`
  and `Poll_NPC_*`.
- Console: refuses a command whose first word contains `save` (`savegame`, `quicksave`, `autosave`,
  `cleanduplicateplayersaves`...), and `quickload`, `loadgame`, `bioloadgame`, `exec`, `exit`, `quit` and
  `disconnect`, also behind `nextframedefer`, `defer` or `|`. Developer save slots live in the game install, so
  nothing writes them. `load`, `quickload`, `quicksave`, `game_cycle` and `close_game` send their own commands.
- `live_call` refuses `ConsoleCommand`, `QuickLoad`, `BioLoadGame`, `LoadGame*`, `NewCharacterProfile`, and any
  function with `Save` or `Saving` in its name (`QuickSave`, `AutoSave`, `TryAutoSaving`, `BioSaveGame`,
  `SaveProfile`, `DumpSaveGame`...), except the read-only `IsAbleToSave` (whether the game allows a save now, and
  why not).
- Paths: nothing is written under the game install, screenshots included.
- The run stops (ABORTED) when a save loads that no step asked for (Shepard died), or when the game closes or crashes.
  An autosave, or a save no step asked for, is a WARN.

## Saves

When a selected file has `saves: true`, the runner backs up before the first file, so the autosaves of earlier files
are undone too. It copies the career folder, `PROFILE\GamerProfile.pcsav` and `Launcher_Profile` from
`Documents\BioWare\Mass Effect Legendary Edition\Save\ME1` into `<root>\<suite>\save-backup\<stamp>\`, checks each
copy, and writes a `PENDING` file. `quicksave` and `quickload` refuse unless `CurrentGame.m_sCharacterID` is the
file's career and the run holds a backup. `quicksave` waits until the save file is rewritten and holds still for 1 s,
or until the game says `Saved` and `m_bSavingGame`, seen true since the command, is false again with
`m_bLastSaveSuccess` true. Then 3 s more for the profile.

The backup is restored only while the game is closed: at the end of the run when it is, by a `restore_saves` step after
`close_game`, or later with `--restore-saves '<backup folder>'` (the run prints the exact command). Files the run added
or changed move to `<backup>\displaced\<stamp>\`; nothing is deleted. While a `PENDING` backup of the same career
waits, a run with a `saves: true` file reuses it and takes no new one: it still holds the saves from before the first
run. A `PENDING` backup of another career stops the run.

## The report

| File | Content |
|---|---|
| `report.md` | Totals, warnings, a table of every check (status, first failure, time, screenshots), then every step. |
| `results.json` | The same, as data. |
| `progress.log` | One line per step as it runs, also printed. |
| `calls.jsonl` | Every tool call and reply. |
| `shots/` | Screenshots. |
| `logs/` | The DebugLogger and KismetLogger lines of each file. |

Check statuses: PASS; FAIL (a step failed or a guard refused); ERROR (the file's setup failed, its `restore` values
could not be read, or the runner hit a bug); SKIP; REVIEW (passed, with screenshots to look at); ABORTED.
