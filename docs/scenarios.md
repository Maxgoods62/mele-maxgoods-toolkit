# Scenario runner

`tools/mele-mcp/scripts/scenario.py` runs in-game tests from suite files, unattended, and writes a report. It runs on
LE1 only: `game` accepts only `LE1`, and the runner reads `MassEffect1.exe`, LE1's logs and `Save\ME1`. See
[games](games.md) for the other games. A suite lives with the mod it tests.

## Prerequisites

- LE1 running on any map, the main menu included.
- [MeleBridge](../tools/mele-bridge/README.md), with the DebugLogger and Kismet Logger ASIs. A bridge older than
  version 3 lacks the `status` and `keep_ticking` ops, and the run stops with "MeleBridge 3 is not installed".
- For a `cold_start` file, M3 and the LEX Interop ASI.
- mele-mcp deployed by `tools/mele-mcp/scripts/deploy.sh`. The runner starts its own server.
- WSL with Python 3. `.yaml` suites need PyYAML (`python3-yaml`); `.json` suites do not.

`MELE_DOTNET`, `MELE_DLL` or `MELE_OUT`, and `MELE_SCENARIO_OUT` come from the environment, then from
`tools/mele-mcp/build.env`. Every other `MELE_*` setting goes on to the server.

## Running

```
python3 -I scenario.py PATH [PATH...] [--only IDS] [--from ID] [--dry-run] [--out DIR] [--unattended]
python3 -I scenario.py --restore-saves [BACKUP_DIR] [--out DIR]
python3 -I scenario.py --selftest
```

| Argument | Meaning |
|---|---|
| `PATH` | A suite file (`.yaml`, `.yml`, `.json`) or a folder. A folder runs in name order and skips `_*` and `README*`. |
| `--only` | Comma list of `fnmatch` patterns on `<file id>` and `<file id>/<check id>`. A file with no check left runs nothing, not even its setup. |
| `--from` | Skips everything before the first file or check that matches. |
| `--dry-run` | Validates offline and prints the plan. No server starts. |
| `--out` | Report root, a Windows or `/mnt` path. Else the first file's `out`, `MELE_SCENARIO_OUT`, `%LOCALAPPDATA%\mele-mcp\scenarios`. |
| `--unattended` | A check with `needs: [user]`, or one reaching a `prompt` that does not hold yet, is SKIP. |
| `--restore-saves` | Puts a backup back, game closed. Without a folder it needs exactly one `PENDING` under the root. |
| `--selftest` | Checks the runner's own logic offline. |

Exit codes: 0 no FAIL, ERROR or ABORTED; 1 a FAIL or ERROR; 2 a usage, suite or prerequisite error; 3 ABORTED; 4 a save
restore pending or failed. Code 4 wins over the others.

## What a run does

1. Validates every file, then starts the server and waits up to 120 s for `live_status`.
2. Turns `keep_ticking` on and checks that `worldTicks` grows, so the game runs with its window in the background.
3. Backs up the career when a selected file has `saves: true`.
4. For each file: `setup`, reads the `restore` plot values, runs each check with its `cleanup`, then `teardown`. The
   plot values go back at the end of the file, unless an unexpected load replaced them or the game is gone.
5. Turns `keep_ticking` off, restores saves if the game is closed, and writes the report.

A failed `setup` or unreadable `restore` values make every check ERROR, and only `teardown` runs. A check stops at its
first failing step. Failures in `cleanup` and `teardown` are WARN, and so is a failed `screenshot`. A guard refusal is
always FAIL, even on an `optional` step. Ctrl-C, SIGTERM and SIGHUP end as ABORTED, which skips `cleanup` and
`teardown`. A failing server or prerequisite stops the run with exit 2.

## A suite file

| Key | Meaning |
|---|---|
| `suite` | Required, `[a-z0-9_-]+`, the same in every file of a run. Names the report folder. |
| `title` | Required. |
| `checks` | Required list of `{id, title, row, skip, needs: [user], steps, cleanup}`. `id` is required, `[a-z0-9_-]+`. |
| `id` | The file's id. Default: its name without extension. |
| `game` | Only `LE1`. |
| `out` | Report root. |
| `include` | Files, relative to this one, whose `vars` and `macros` merge in. Later wins; the file's own come last. |
| `career` | Career folder name. Required by `load: {save}`, `quickload`, `quicksave`, `backup_saves`, `restore_saves`. |
| `saves` | `true`: the file may `quicksave`. |
| `cold_start` | `true`: the file may use `game_cycle` and `close_game`. It must be the last file, and the only such one. |
| `skip` | A reason. Every check is SKIP and nothing runs. |
| `rows` | Free text. |
| `restore` | `{bool: [ids], int: [ids], float: [ids]}`: plot values put back at the end of the file. |
| `vars`, `macros` | Variables, and named step lists for `use`. |
| `setup`, `teardown` | Step lists. |

A step is a mapping with one kind key. Beside it: `name`, `optional: true` (a failure is WARN and the check goes on) and
`save_as: var`. `save_as` works on `live_get`, `live_call`, `count`, `distance`, `snapshot`, `assert_log`, `watch`,
`status` and `squad_pick`. `save_as` and `optional` may also sit inside the mapping.

```yaml
suite: my_mod
title: The guard stands by the door
vars:
  guard: {find: {class: BioPawn, tag: my_guard}}
checks:
  - id: spawned
    steps:
      - at: {map: BIOA_MYM01, start: start_mym01}
      - wait_until: {count: guard, expect: 1, stable: 4, timeout: 30}
      - distance: {a: "${guard}", b: player, expect: {max: 800}}
        save_as: gap
```

### Variables and chains

`vars` values are literals or finders:
`{find: {class, tag, name_contains, path_contains, where: {Prop: expect}, alive: true}}`. A finder needs `class`,
`name_contains` or `path_contains`. `alive` drops objects with `bDeleteMe`. Where a step takes a finder, give the
variable's bare name or a finder mapping.

`${name}` works in any string. A string that is only `${name}` keeps the value's type, and `${a.b}` and `${a[0]}` index
into it. A finder in `${}` must match exactly one live object. Built-ins: `map`, `world`, `pc`, `player` (the pawn or
vehicle), `gvt` (the `BioGlobalVariableTable`), `gui` (`GetScaleFormManager`), `run_dir`, `stamp`, `file`, `check`,
`career`.

An object or property may hop through object properties with `>`: `"${guard}>m_oBehavior"` is an object. `[*]` maps the
rest over an array, and `.field` digs into a struct element: `m_oEquipmentSlots[*].oEquippedItem>m_nID` is a list.

### expect, conditions and marks

`expect` is a value (equality) or a mapping of `eq`, `ne`, `min`, `max`, `lt`, `gt`, `in`, `contains`, `regex`,
`null: true|false`, `len: n|{min, max}`. Strings compare without case, numbers within 1e-4, and plot text as a bool or
number.

A condition (`assert`, `wait_until`, `prompt.until`) has one source and an `expect`; without one the value must be true.
Sources: `status: <dotted key of live_status>`, `get: {object, property}`, `call: {object, function, args}`,
`count: <finder>`, `plot: {bool|int|float: id}`, `distance: {a, b, axis}`, `kismet: {contains, since}`,
`log: {regex, since, errors_only}`, and `all` or `any` with a list of conditions.

Marks are positions in both logs: `file`, `check`, `step`, `load` (the trigger of the last `wait_loaded` that passed)
and `mark:<name>`. Marks reset per file. `since:` takes one, default `check`, which does not exist during a file's
`setup`.

## Steps

Required keys come before the semicolon. `key: value` shows a default.

| Kind | Arguments | What it does |
|---|---|---|
| `use` | macro name, or `macro; with: {var: value}` | The macro's steps, with `with` set while they run. |
| `console` | command, or `command; expect_lines, forbid_lines, timeout: 10` | `live_console`, with regexes over its lines. |
| `load` | `slot` or `save`, `map; timeout: 240` | `BioLoadGame <slot>`, or `nextframedefer loadgame <career>_<n> DevId=-1`, then `wait_loaded`. |
| `quickload` | `map; timeout: 240` | Career guard, `QuickLoad`, then `wait_loaded`. |
| `at` | `map, start; allow_party: false, timeout: 240` | The cheat `at <map> <start>`, then `wait_loaded`. |
| `wait_loaded` | `map, expect_load: true, allow_party: false, settle: 3, load_grace: 10, timeout: 240` | See below. |
| `plot` | `bool: {id: value}`, `int`, `float` | Sets each value and reads it back. |
| `assert_plot` | `bool: {id: expect}`, `int`, `float` | Reads and checks. |
| `assert` | a condition | Checks once. |
| `wait_until` | a condition; `timeout: 30, stable: 0, poll: 1` | Polls until it holds for `stable` seconds. |
| `sleep` | seconds | Waits, still watching the logs. |
| `mark` | name | Sets `mark:<name>`. |
| `live_get` | `object, property; depth, expect` | Reads a property, through chains. |
| `live_set` | `object, property, value` | Sets it. The read-back must equal `value`. |
| `live_call` | `object, function; args: [], expect, timeout: 5` | `expect` checks the return. `save_as` keeps `{return, out}`. |
| `count` | `find; expect` | Live objects a finder matches. `save_as` keeps their paths. |
| `distance` | `a, b; axis: xyz, expect` | Between objects, `player`, `{X, Y, Z}` or `[x, y, z]`. `axis`: `xyz`, `xy`, `z`. |
| `teleport` | `to`, `near` or `ahead`; `object: player, offset` | `SetLocation`. Retries higher, or closer for `ahead`. |
| `raise_event` | `object` or `name`; `class: SeqEvent_RemoteEvent, path_contains: "${map}.TheWorld", originator, instigator, pick: one` | `CheckActivate`, which also reaches events nested in prefabs. `pick: all` fires every match. |
| `squad_pick` | `members; open: console, event, expect_spawn: true, timeout: 30` | See below. |
| `snapshot` | `name, object, props` | Keeps the props for `compare`, in this file. |
| `compare` | `snapshot` or `objects: [a, b], props`; `ignore, tol: 1e-4` | Lists every difference. |
| `quicksave` | `timeout: 60` | See Saves. |
| `screenshot` | name, or `name; review: false, max_width: 1280` | `shots/NN-<file>-<check>-<name>.png`. `review` makes the check REVIEW. |
| `assert_log` | `since: check, errors: true, regex, ignore, expect: {max: 0}` | Counts DebugLogger entries, problems only with `errors`. `save_as` keeps them normalised. |
| `compare_log` | `with; since: load, mode: no_extra, ignore` | Problems against a saved set. `mode: same` also fails on one gone. |
| `assert_kismet` | `contains; expect: {min: 1}` or `order`; `since: check` | KismetLogger lines. `order` wants each text after the last. |
| `prompt` | text, or `text; until, timeout: 300` | A message box for a person. It ends when `until` holds, or else when the box closes. |
| `watch` | `functions; seconds: 10, object_contains, expect: {min: 1}` | `live_watch`, counting the calls. |
| `keep_ticking` | `on` or `off` | Only for checks that test focus behaviour. |
| `game_cycle` | `commands, wait_for_map, settle: 8, timeout: 300` | `cold_start` only. Reboots through M3 with no mod and runs `commands` at the main menu. Follow it with `wait_loaded`. |
| `close_game` | `timeout: 60, force: false` | `cold_start` only, and the only place `exit` is sent. `force` kills the game. |
| `backup_saves`, `restore_saves` | none | See Saves. |
| `status` | none | Records one `live_status`. |

### wait_loaded

It first waits for `Loading package synchronously: <map>` after the step before it. `expect_load: true` requires that
line, `auto` waits `load_grace` seconds for it, and `false` skips it. A load error in the log fails at once. Then it
polls `live_status` every second, and a bridge timeout counts as loading.

A poll is ok when the map is right, the world has begun play and a pawn exists. No streaming level may be pending, the
loading movie must be off, and the world must not be paused. The squad screen must be closed unless `allow_party` is
set, and `keep_ticking` must be on. Two ok polls, `settle` seconds and one more ok poll mean loaded. Without `map`, the
map of that line must be the one loaded.

Send nothing between a load and its `wait_loaded`, because console input during a load screen is lost.

### squad_pick

It opens the squad screen unless it is open (`ce Hench_PickSquad`, a `raise_event` mapping in `event`, or `open: none`)
and waits for the movie's member list. `members` must be two names, or every available one. Each must be available,
and a forced member must be among them. It sets them on the handler, calls `FinalizeParty` and waits for the panel to
close. It then checks the InParty flags, and `expect_spawn` waits for loading.

## Guards

- Tools: an allowlist of live, plot, log, screenshot, status and `game_cycle` tools. `game_remote_event` refuses
  `Live_NPC_*` and `Poll_NPC_*`.
- Console: refuses a first word containing `save`, and `quickload`, `loadgame`, `bioloadgame`, `exec`, `exit`, `quit`,
  `disconnect`, also behind `nextframedefer`, `defer` or `|`. `game_cycle` commands may load `<career>_*` saves.
- `live_call`: refuses `ConsoleCommand`, `QuickLoad`, `BioLoadGame`, `LoadGame*`, `NewCharacterProfile`, and any name
  with `Save` or `Saving`, except `IsAbleToSave`.
- Paths: nothing is written under the game install. Developer save slots live there, so they are only read.
- The run stops as ABORTED when a save loads that no step asked for (Shepard died, for example), or when the game
  closes. An autosave is a WARN.

## Saves

With a `saves: true` file selected, the runner backs up before the first file. It copies the career folder,
`PROFILE\GamerProfile.pcsav` and `Launcher_Profile*` from `Documents\BioWare\Mass Effect Legendary Edition\Save\ME1` to
`<root>\<suite>\save-backup\<stamp>\`, checks each copy, and writes a `PENDING` file. A `backup_saves` step does the
same when the run has no backup yet.

`quicksave` and `quickload` refuse unless `CurrentGame.m_sCharacterID` is the file's career and the run holds a backup.
`quicksave` ends when `<career>_QuickSave.pcsav` is rewritten and still for 1 s, or the game reports a finished save.

A restore needs the game closed. It runs at the end of the run, by a `restore_saves` step after `close_game`, or later
with `--restore-saves '<backup folder>'`. Changed files move to `<backup>\displaced\<stamp>\`. A `PENDING` backup of
the same career is reused, and one of another career stops the run.

## The report

Each run writes `<root>\<suite>\<yyyymmdd-hhmmss>\`: `report.md` (totals, warnings, every check, every step),
`results.json` (the same as data), `progress.log` (one line per step), `calls.jsonl` (every tool call and reply),
`shots/`, and `logs/` with each file's DebugLogger and KismetLogger lines.

Check statuses: PASS; FAIL (a step failed or a guard refused); ERROR (`setup`, `restore` or a runner bug); SKIP; REVIEW
(passed, with screenshots to look at); ABORTED.
