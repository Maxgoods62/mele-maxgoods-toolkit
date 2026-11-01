# Shared formats

MeleBridge (the ASI in the game) and mele-mcp (the MCP server) share four contracts: the named-pipe protocol, `levels.json`,
the editor's session files and the prop catalog's `catalog.json`. Each has a version number. A reader refuses a version it
does not know, so a bridge and a server from different releases fail early, with a message that says what to rebuild. Each
contract below is given as the code reads and writes it: the pipe's ops and their replies, and every file's fields.

A reference written "design §10" is a section of the [editor design](design/in-game-editor.md), which explains why the
editor works as it does; a bare section number (§3.3) is one of this page. The examples use the editor design's example
names (the map `BIOA_MYM01`, the NPC `guard`); use your own. §7 is the game's own save format, which only mele-mcp reads.

## 1. Versions

| Contract | Version | Written by | Read by | A version it doesn't know |
|---|---|---|---|---|
| Pipe protocol | 1 (`MELE_PROTOCOL_VERSION`, `tools/mele-bridge/src/SharedVersion.h`) | MeleBridge (`ping`) | mele-mcp (`Versions.BridgeProtocol`) | mele-mcp refuses every request to that bridge but an `editor` one, or a `keep_ticking` one that turns the switch off or reads it (§3.3). |
| `levels.json` | 1 | `level_build` (also through `mod_build` and `npc_place`) | MeleBridge; mele-mcp's mesh index, for its `dlc` keys (§4) | "levels.json can't be read (version N, and this bridge reads version 1): run mod_build"; every actor keeps Phase 3's rules. |
| `sessions/<map>.json` | 1, 2 or 3 | MeleBridge (Save, passes), `editor_import` (marks) | both | The bridge leaves the file as it is: Save refuses, a pass skips it. `editor_import` refuses it. |
| `catalog.json` | 1 | `catalog_build` | MeleBridge | The Props window says the file can't be read; sessions re-apply without catalog paths. |

The bridge's own version is `ASI_VERSION` in the same header: `"bridge": "3"` in the `ping` and `status` replies. It is for people and logs;
programs check `"protocol"`.

When to bump: the pipe protocol goes up when an op changes its arguments or its reply. A new op needs no bump, since an
older bridge answers it `unknown op` (§3.2): MeleBridge 3 added `status` and `keep_ticking` under protocol 1. A file's version goes up when an
older reader would do something wrong with the new content. A reader that would only skip an unknown key needs no bump:
Phase 5 added `npcs` and `characterLights` to `levels.json` and kept version 1, because a Phase 4 bridge skips them. It added
session version 3, because a Phase 4 bridge would rewrite an NPC's light with its own writer.

## 2. Where the files are

```
%LOCALAPPDATA%\MeleBridge\LE1\
  levels.json                  written by level_build, read by the bridge (and its dlc keys by mele-mcp)
  sessions\<map>.json          the editor's work on one persistent map
  sessions\<map>.dropped.json  entries editor_import dropped from that session
  catalog\catalog.json         the prop catalog
  catalog\thumbs\*.png         its thumbnails, 128×128
```

- Nothing here is in the game install. Both tools find the folder from the Windows account's local application data
  folder. The bridge reads the `LOCALAPPDATA` variable and refuses without it ("LOCALAPPDATA is not set").
- `MELE_LEVELS_JSON` (mele-mcp only) names another `levels.json`, for tests. `editor_import`'s default sessions folder is
  then `sessions` beside that file. The bridge ignores the variable: it always reads the folder above.
- `catalog_build`'s `outDir` writes the catalog elsewhere. The bridge reads only `catalog\` in the folder above.
- Every writer writes a temporary file beside the target, then moves it over the target, so a reader never sees half a
  file. The temporary names differ per writer: `.tmp` (the bridge, `catalog_build`), `.build.tmp` (`level_build`),
  `.import.tmp` (`editor_import`).
- A file that can't be parsed is moved aside, never overwritten: a session by the bridge to `<map>.json.bad-<n>` (n:
  milliseconds since Windows started), `levels.json` by `level_build` and a dropped file by `editor_import` to
  `<file>.bad-<n>` (n: .NET UTC ticks). A new file is then started.

## 3. Pipe protocol

### 3.1 Transport

- Name: `\\.\pipe\MeleBridge_LE1`. The suffix is the game's name. On the LE2 and LE3 builds the `editor` op
  refuses, `status` gives `null` for `loadingMovie.inLoad`, and for the squad screen
  on LE3.
- Pipe: byte mode, local clients only (`PIPE_REJECT_REMOTE_CLIENTS`), any number of connections at once, each served on
  its own thread.
- Lines: UTF-8 JSON, one request per line, each ending in `\n`. One reply line per request, in order. A line of only
  spaces, tabs or `\r` is skipped. More than 1 MB waiting without a newline closes the connection.
- Threads: every op but `editor` runs on the game thread, in the `ProcessEvent` hook, at the next call the game makes
  through it. `keep_ticking` with `on: false`, or without `on`, does not wait for the game thread either.

### 3.2 Requests and replies

```json
{"op": "get", "id": 7, "timeoutMs": 5000, "object": "TheWorld.PersistentLevel.BioPawn_3", "property": "Location"}
{"ok": true, "value": {"X": 120.5, "Y": -88.0, "Z": 40.25}, "id": 7}
```

| Key | Where | Meaning |
|---|---|---|
| `op` | request, required | The op name (§3.4). |
| `id` | request, optional | Any JSON value. Echoed in the reply, an error reply included, when the request parsed as an object. |
| `timeoutMs` | request, optional | How long the bridge waits for the game thread to take the request. Default 5000. |
| other keys | request | The op's arguments. A `null` argument is the same as a missing one. One of the wrong type is refused: `"command" is a string`. |
| `ok` | reply | `true`, with the op's reply keys beside it; or `false`, with `error`. |
| `error` | reply | What went wrong, in words, ready to show. |

- Timeout: when the game thread doesn't take the request in time (a load screen, or a menu that runs no script), the
  reply is `"the game thread did not take the request in time (game loading, or not running script?)"`. A request the game
  thread has started always runs to the end, even when its reply then comes back late.
- Faults: an access violation while a request runs is caught; the reply is
  `"access violation in the game while running the request"`.
- Unknown op: `"unknown op X (ping, console, find, get, set, call, describe, subscribe, editor, status, keep_ticking)"`.
- mele-mcp's client (`LiveBridge.cs`) drops `null` keys (a `set`'s `value` excepted, where `null` means None), sets
  `timeoutMs` from the tool's timeout, and waits 2 s longer than that for the reply line.

### 3.3 The version check

`ping` returns:

```json
{"ok": true, "bridge": "3", "protocol": 1, "map": "BIOA_NOR00", "objects": 512345}
```

- mele-mcp pings before its first request to a game process (known by process id and start time), on the same connection,
  and caches the answer for that process. A restarted game is checked again. The cache lives in the server process, so a
  new server checks again: `scripts/mcp_call.py` starts one for every call. When Windows does not say which process serves
  the pipe, every connection pings.
- `ping` runs on the game thread (§3.1), with the request's timeout. So the first request to a freshly started game, and
  every request through `mcp_call.py`, waits for the game thread and can time out on a load screen (§3.2). A ping that
  times out or is refused is not cached: the error says to try again once the game has loaded, and the next request pings
  again.
- A request made only of `editor` ops skips the check, so `live_editor` still answers while the game loads or the world is
  paused (§3.7). So does one made only of `keep_ticking` ops without `on: true`.
- A bridge that reports another protocol, or none (MeleBridge 1 sent none), is refused, and so is every later request to that
  process but an `editor` one or a `keep_ticking` one without `on: true`. The error names the bridge's version and protocol, mele-mcp's version and the protocol it
  needs, and says to build and install the MeleBridge.asi of the same toolkit release, game closed, then start the game
  again.

### 3.4 Ops

| Op | Arguments | Reply |
|---|---|---|
| `ping` | none | `bridge` (version string), `protocol` (int), `map` (the persistent map's name), `objects` (the length of `GObjObjects`). |
| `console` | `command` (required) | Nothing else. LE1 hands no console output back: mele-mcp's `live_console` reads it from the DebugLogger log. |
| `find` | `class`, `name`, `path` (at least one); `defaults` (false); `limit` (100) | `total` (all matches), `objects`: `[{path, class}]`, the first `limit`. |
| `get` | `object` (required); `property`; `depth` (1 without `property`, 3 with) | `value`: the property, or every property of the object by name. |
| `set` | `object`, `property`, `value` (all required) | `value`: the property read back after the write. |
| `call` | `object`, `function` (required); `args` (a list, default `[]`) | `return` when the function returns a value; `out` (`{name: value}`) when it has out parameters. |
| `describe` | `object` or `class` (exactly one); `functions` (true) | `class` (path), `chain` (class names up to `Object`), `properties`: `[{name, type, owner}]`, `functions`: `[{name, owner, signature}]`. |
| `subscribe` | `functions` (a non-empty list of function paths); `objectContains`; `params` (true) | `functions`: the paths resolved. Then the connection becomes an event stream (§3.6). |
| `editor` | none | The in-game editor's state (§3.7). |
| `status` | none | One look at the game for tests (MeleBridge 3), keys below. |
| `keep_ticking` | `on` (a bool; missing reads the switch) | `keepTicking`, `worldTicks`, `focusPausesCleared`, `timerRaises`; `clearedFocusPause` and `world` (`null` unless `on` is true). |

- `find`: `class` is a class name (any loaded class of that name, subclasses included); `name` and `path` are
  case-insensitive substrings of the object's name and full path. Class default objects are left out unless `defaults` is
  true. Without a filter: `"give a class, a name or a path filter"`.
- `call`: the function is found by name up the class chain. Opcode natives work (design §3.1). Arguments are given in
  order; optional and out parameters may be left out. Latent and iterator functions are refused:
  `"Sleep is latent or an iterator: it cannot be called from outside script"`.
- `describe`: a function a subclass overrides is listed once, with the subclass as `owner`. A `signature` reads
  `<return type> Name(optional out <type> Param, ...)`, `void` when nothing is returned. Types read `int`, `name`, `string`,
  `struct Vector`, `array<int>`, `class<Actor>`, an enum's name, or the class of an object property.
- `subscribe`: a path may leave out the package (`SequenceOp.Activated` finds `Engine.SequenceOp.Activated`).
- `status` runs on the game thread, so it times out while a map loads. Every key is always there; a part that can't be
  read is `null`, with why in `notes` (a list of strings).
  - `bridge`, `map`; `world` (the map's `BioWorldInfo`, `<map>.TheWorld.PersistentLevel.…`), its `begunPlay`,
    `bPlayersOnly` and `pausedByFocusLoss` (`m_bPausedByFocusLoss`).
  - `keepTicking`, `worldTicks`, `focusPausesCleared`, `timerRaises`: as `keep_ticking` returns them.
  - `pc`: the map's live player controller (not being destroyed, under `<map>.TheWorld.`), else the first one in
    memory; `console` sends to the same one. `pawn`: its `Pawn`.
  - `streaming`: `{levels, pending, pendingNames}` over the world's `StreamingLevels`. A level is pending when a load or
    unload is pending, it should be loaded and has no `LoadedLevel`, or `bShouldBeVisible` differs from `bIsVisible`.
    `pendingNames`: the first 5 package names.
  - `loadingMovie`: `{object, playing, state, inLoad}` of the first `SFXLoadMovieManager` (`IsLoadingMoviePlaying()`,
    `PlaybackState`, `bIsInLoad`), or `null` when there is none (then looked for again after 5 s at the earliest).
  - `partySelectOpen`: whether the player's GUI manager (`GetScaleFormManager()`) has a `PartySelect` panel
    (`GetPanelByTag`). `partySelect`: `null` unless it is open; else `{panel, handler, handlerClass, membersAvailable,
    varPath}`. `handler` is the panel's `BioSFHandler_PartySelection` (its default handler, else a script handler).
    `membersAvailable` is the movie's `nMembersAvailable` read at `varPath` (`_root.nMembersAvailable`, else
    `nMembersAvailable`): it is 0 until the screen has initialised its cards.
- `keep_ticking` is a test switch, off at every game start. While it is on, `BioWorldInfo.Tick` never takes its focus-loss
  pause, so the world and Kismet run with the game window in the background:
  - Around each `BioWorldInfo.Tick` that goes through `ProcessEvent`, a `m_fNoBrowserWheelTimer` at 0 or below is raised
    to 1e9, so `CanPause()` is false inside that Tick, then set back to 0 if it still reads 1e9. Outside Tick the pause
    menu behaves as vanilla.
  - `bPlayersOnly` is cleared only together with a set `m_bPausedByFocusLoss`, before and after each Tick. Pauses that
    don't set that flag (menus, the squad screen, photo mode, the F8 editor) stay.
  - `on: true` runs on the game thread: it finds the two fields by reflection (refused when one is missing), turns the
    switch on and ends a focus pause already in place on the map's world (`clearedFocusPause`). `on: false` and a read
    answer at once, even during a load.
  - The counters run since the game started, and only while the switch is on: `worldTicks` (Ticks seen),
    `timerRaises`, `focusPausesCleared`.

### 3.5 Objects, properties and values

- Object paths are full paths as `find` returns them, `Package.Group.Name`, compared without case. The `Class Path` form
  some tools print is taken too (the part after the first space). Not found: `"no object at X (paths as find returns them)"`.
- Property paths: `A.B[2].C`. A dot enters a struct; `[i]` indexes a static array or a dynamic array.

| Property type | In a reply | Accepted by `set` and `call` |
|---|---|---|
| bool | `true` / `false` | a bool, a number (0 is false), `"true"`, `"false"`, `"1"`, `"0"` |
| byte | the enum value's name when the property has an enum, else a number | an enum value's name (any case) or 0 to 255 |
| int, float | a number | a number, a bool or a numeric string; an int must be whole |
| name, string | a string | a string |
| object | its path, or `null` | a path; `null`, `""` or `"None"` for none. The class is checked. |
| delegate | `Object.Function`, or `null` | not settable |
| struct | an object of its members | an object of the members to change; the others keep their values |
| dynamic array | a list, at most 64 items, then `"... N more"` | a list of the same length; else set one element as `Name[i]` |
| static array | a list of every element | one element at a time: `Name[0]` |
| past `depth` | a struct as `"{StructName}"`, an array as `"[N items]"` | |
| other types | `"<PropertyClass>"` | refused: `"cannot set a ..."` |

### 3.6 Events

After a `subscribe` succeeds the bridge reads no more requests on that connection. It writes one line per watched call until
the client closes the connection:

| Line | Meaning |
|---|---|
| `{"t": 12.345, "function": "...", "object": "...", "class": "...", "params": {...}, "pin": "In"}` | A watched function was called. `t`: seconds since the bridge started, to the millisecond. `params`: the call's parameters by name, at depth 1, when `params` is true. `pin`: for a Kismet `SequenceOp`, the input link that fired. |
| `{"heartbeat": true}` | Nothing happened for 2 s. |
| `{"dropped": n}` | n events were dropped because 10,000 were already waiting to be sent. |

- A watch matches its function by name on any object of the function's class or a subclass, so a watch on
  `Engine.SequenceOp.Activated` sees every Kismet op that fires.
- `objectContains` keeps only calls on objects whose path contains it, without case.

### 3.7 The editor op

`editor` takes nothing and does not wait for the game thread: it reads atomics and a snapshot the editor publishes once a
frame, so it answers while the world is paused or loading. mele-mcp's `live_editor` sends it alone, without the protocol
check's `ping` (§3.3), and returns the reply as is. It is for checks and tests; nothing ships from it.

| Key | What it holds |
|---|---|
| `open`, `freeCam`, `frame`, `speed` | Whether F8 is open and the free cam on; the editor's frame count; the fly speed. |
| `camera` | `{location, rotation, fov}` of the view. |
| `canvas`, `backbuffer` | Their sizes in pixels, `[width, height]`. |
| `lastPick`, `status` | The last click's result and the status line, as text. |
| `edits`, `undo`, `session` | `{count, unsaved, saveProblem}`; `{undo, redo}`: the name of the step Ctrl+Z or Ctrl+Y would run (`"move ed_Crate_…"`), or `null` when there is none; the session file and its pending entries. |
| `levelsJson` | What the bridge read of `levels.json`: the file, its problem if any, the levels of ours, counts. |
| `meshesInMemory`, `catalog` | The Props window's two lists: the meshes in memory; `{entries, thumbnails}`. |
| `autoLight`, `lights`, `npcs`, `effects`, `selection` | Auto light's state; the tracked lights, NPCs and effects; the selected actor with its pose, light and refusals. |
| `calibration` | The projection check: the editor's projection against `Canvas.Project` (design §3.4). |
| `threads`, `hooks`, `counters` | Diagnostics: which thread ran `present`, `postRender`, `updateCamera` and `window`; which hooks are in; counts of presents, overlay frames, swallowed messages, zeroed DirectInput reads and blocked cursor calls. |

## 4. levels.json (version 1)

`level_build` writes the key of the level it built, last, after its forks; other keys stay. The bridge reads the file on each
map change, when F8 opens, and once a second while the editor is open when the file's write time changed (`npc_place` builds,
and moves the pawn live). The file says which levels are ours, which vanilla files each streams, and which built props,
lights and NPCs the editor may change (design §11.4, §12.1). It describes the last build, not the install.

```json
{
  "version": 1,
  "dlc": "DLC_MOD_MyMod",
  "levels": {
    "BIOA_MYM01": {
      "dlc": "DLC_MOD_MyMod",
      "streams": ["BIOA_FRE31_00_LAY", "BIOA_FRE31_00_DSG"],
      "props": ["ed_Crate_XLarge_02_ff9xa_1"],
      "lights": ["ed_light_1"],
      "npcs": {
        "mymod_npc_01": {
          "name": "guard",
          "location": [-310.5, -15120.25, -362.15],
          "yaw": 16384,
          "lights": { "mymod_npc_01_light_ff9xa_1": [60, 40, 80] }
        }
      },
      "characterLights": { "StaticLightCollectionActor_68": "BIOA_FRE31_00_DSG" }
    }
  }
}
```

| Field | Meaning |
|---|---|
| `version` | 1. A missing version reads as 1. |
| `dlc` | The DLC folder of the last built level that lies in one. With it, a level whose file lies there and whose name is not a fork is ours too. |
| `levels.<key>` | One level built from a YAML: the package file name without `.pcc`. Replaced whole by each build of that level, matched without case. |
| `levels.<key>.dlc` | The DLC folder this level lies in: the nearest folder above its package whose name starts with `DLC_`, or `null`. The bridge skips it. |
| `streams` | The vanilla files the level streams, by their vanilla names, also when a fork stands in. The editor's "ships" line reads it. |
| `props` | Tags of the YAML's props that are not removed: the built props the bridge may switch to prop lights (design §11.1). |
| `lights` | Tags of the YAML's `lights:` entries, switched-off ones too: the lights the editor lists. NPC lights are under their NPC. |
| `npcs.<tag>` | Every NPC of the YAML by her pawn's tag, with or without lights: `name` (the YAML key), `location` (the built pawn's `Location`, written short: `-36739.9`), `yaw` (Unreal units, 65536 = 360°), `lights` (light tag → offset `[forward, right, up]`). |
| `characterLights` | The light collections the build copies into the level: the copy's name in the level → the file it was copied from, without `.pcc`. |

How the bridge reads it: a missing key, or one of another type, reads as empty, and an unknown key is skipped. Tags are
compared without case. An NPC without `name`, `location` or a numeric `yaw` is skipped, and so is an NPC light whose offset is
not three numbers. A missing file says "levels.json missing: run mod_build"; a broken one says "levels.json can't be read
(...): run mod_build". Either way every actor keeps Phase 3's rules (design §10).

How mele-mcp reads it: the mesh index (`catalog_index`) leaves our own mods' files out, as it does the catalog's. With
`MELE_OWN_DLC` unset, those are the top `dlc` and every level's `dlc`; with it set, only the folders it lists. A missing or
broken file gives none.

## 5. Session files (versions 1 to 3)

`sessions\<map>.json` holds the editor's work on one persistent map (the game's `GWorld`), sideloaded levels included.
One entry per actor, with its latest state, never a history. The bridge's Save writes it, and its passes drop entries the
installed level shows; `editor_import` marks the entries it applied and drops the ones no YAML builds (design §3.7, §10).

```json
{
  "version": 2,
  "map": "BIOA_MYM01",
  "saved": "2026-10-28T14:03:12Z",
  "actors": [
    {
      "tag": "ed_Crate_XLarge_02_ff9xa_1",
      "path": "BIOA_MYM01.TheWorld.PersistentLevel.DynamicSMActor_Spawnable_0",
      "level": "BIOA_MYM01",
      "class": "DynamicSMActor_Spawnable",
      "spawned": true,
      "mesh": {
        "path": "BIOA_FRE31_00_LAY.Crate_XLarge_02",
        "file": "BIOA_FRE31_00_LAY",
        "load": "MeleCatalog_Core.Crates.Crate_XLarge_02"
      },
      "location": [-210.73, -15227.6, -408.58],
      "rotation": [0, -8192, 0],
      "scale": 1.0,
      "scale3d": [1.0, 1.0, 1.5],
      "deleted": false,
      "base": null
    }
  ]
}
```

### 5.1 The file

| Field | Meaning |
|---|---|
| `version` | 1, 2 or 3 (§5.4). The bridge reads a missing version as 1, but `editor_import` refuses a file without one (it reads as version 0), so a file written by hand needs it. |
| `map` | The persistent map's name. |
| `saved` | When the bridge last saved, UTC, `yyyy-MM-ddTHH:mm:ssZ`. |
| `actors` | The entries, in the order they were first saved. |

### 5.2 An entry

| Field | Type | Meaning |
|---|---|---|
| `tag` | string | The actor's `Tag`. A spawn's tag is unique (`ed_<mesh>_<run>_<n>`), and is its key. |
| `path` | string | The actor's full path. For an actor in a fork, the path in the vanilla file. For a level light, its component's path (`<file>.TheWorld.PersistentLevel.<collection>.<light>`). |
| `level` | string | The level file the actor belongs to, by its vanilla name. |
| `class` | string | The actor's class name; a level light's component class. |
| `spawned` | bool | The editor spawned it. |
| `mesh` | object or `null` | A spawned prop only: `path` (the vanilla mesh's path), `file` (a level that shows it: a hint, `null` when unknown), `load` (the catalog path for `DynamicLoadObject`, `null` when unknown). |
| `location` | `[x, y, z]` | World location. For a level light: its place, `LightToWorld.WPlane`. |
| `rotation` | `[pitch, yaw, roll]` | Integers in Unreal units (65536 = 360°). Zeros for a level light. |
| `scale` | number | `DrawScale`. 1 for a level light. |
| `scale3d` | `[x, y, z]` | `DrawScale3D`. |
| `deleted` | bool | Hidden by the editor (design §10). A light is switched off with `light.enabled` instead; `deleted` on a light means only a spawned light the editor deleted. |
| `base` | object or `null` | What the YAML holds for the actor, for the three-way merge: a state (below). `null` for a spawn never imported. |
| `light` | object | Lights only: `kind`, `radius`, `brightness`, `color`, `falloff`, `enabled`, and `type` for a level light. |
| `npc` | object | NPC lights only: `tag` (the NPC's pawn tag), `offset` (`[forward, right, up]` from the NPC's pose, turned by her yaw). |
| `owner` | string | Level lights only: the name of the light collection that holds the light. |
| `imported` | object | Written by `editor_import` only: `{ "before": <state> }`, what the installed level may still show. |
| `installed` | list of states | Written by the bridge only: earlier `imported.before` states an install may still show. |

- Key: a spawn is matched by `tag`; any other entry by `path`, a fork's path read as the vanilla file's path. One entry
  per key: a later Save replaces it. When the bridge saves an edit of a built copy (a level actor with a spawn's tag), the
  copy's entry replaces the spawn's.
- A state (in `base`, `imported.before` and `installed`) holds `location`, `rotation`, `scale`, `scale3d`, and as the
  entry has them `deleted`, `light` and `npc`. A field it lacks takes the actor's own value.
- `base.deleted` says whether the YAML removes the actor (`removed: true`); in `imported.before`, whether the installed
  level hides it.
- The light block:

| Field | Values |
|---|---|
| `kind` | `props` (prop light), `room` (room light), `npc` (an NPC's light), `vanilla` (a level light). Compared exactly. |
| `radius`, `brightness`, `falloff` | Numbers: `Radius`, `Brightness`, `FalloffExponent`. |
| `color` | `[r, g, b]`, integers 0 to 255. The light's alpha is not stored. |
| `enabled` | Whether the light is on. |
| `type` | Level lights only: `B` (baked), `C` (characters only), `U` (special channel) or `D` (dynamic) (design §12.4). |

### 5.3 Rules both tools keep

- Two writers: the bridge's Save reads the file first and keeps what an import changed in fields the editor
  didn't change since (design §10). `editor_import` rewrites the file only when it didn't change since it read it; if the
  bridge saved meanwhile, the import merges again, up to 3 times.
- Matched as equal: location within 0.01 after rounding to 2 decimals, rotation as an orientation (axes within 0.02°),
  scales within 0.001; a light's radius within 0.01, brightness and falloff within 0.001, colour and kind exact. An NPC light:
  the bridge takes its place within 0.05 per axis, `editor_import` its offset within 0.02 per axis.
- Dropped entries are kept: before `editor_import` removes an entry from a session, it adds it to
  `<map>.dropped.json` beside it:

  ```json
  {
    "about": "Session entries editor_import dropped from the in-game editor's session, ...",
    "dropped": [
      { "at": "2026-10-28T14:10:00Z", "session": "...\\sessions\\BIOA_MYM01.json", "file": "...\\BIOA_MYM01.yaml", "why": "...", "entry": { } }
    ]
  }
  ```

  To add one back: copy its `entry` into the session's `actors` with `"base": null` and without `imported` or `installed`,
  game closed, then import again. `editor_import` skips `*.dropped.json` when it looks for sessions.
- Session choice: with no `session` given, `editor_import` takes the level's own `sessions\<level>.json` when it exists,
  however old. Else (a level the game loads into another map) it takes the newest session in the sessions folder with an
  actor of the level that the editor didn't spawn, an entry whose tag the YAML builds (an NPC's light included), or a light
  on one of the YAML's NPCs. Files that can't be parsed are skipped. With none, it refuses and names the sessions that hold
  spawned meshes.

### 5.4 Versions

| Version | Written by | What it has |
|---|---|---|
| 1 | The first editor builds (Phases 1 and 2) | `tag`, `path`, `level`, `class`, `spawned`, `mesh` (`path`, `file`; Phase 2 added `load`), `location`, `rotation`, `deleted`. No `base`, no scales. |
| 2 | Phase 3 on: the default | Version 1's fields and `scale`, `scale3d`, `base`, `imported`, `installed`. Phase 4 added `light` blocks of kind `props` and `room`, and vanilla actors. Effects (`class: Emitter`) are plain vanilla entries. |
| 3 | Phase 5 on, when needed | An entry, its `base`, its `imported.before` or one of its `installed` holds a `light` of kind `npc` or `vanilla`. Adds `npc`, `owner` and `light.type`. |

- The bridge writes 3 when an entry needs it, else 2, at every Save. A pass that drops entries rewrites a version 2 or 3
  file at the version of what is left; a version 1 file stays 1.
- `editor_import` never lowers a version: a file it marks becomes at least 2, and 3 when an entry holds an `npc` or
  `vanilla` light.
- Another version: the bridge's Save says `"<file> is version N, and this bridge reads versions 1 to 3: left as it is"`,
  and its passes skip the file. `editor_import` says `"<file> is a version N session; editor_import reads versions 1 to 3.
  Deploy mele-mcp and MeleBridge from the same commit."` A Phase 4 bridge or import (it reads versions 1 and 2) refuses a
  version 3 file, so it never rewrites an NPC's or a level's light with Phase 4's writer.

## 6. catalog.json (version 1)

`catalog_build` writes the prop catalog for the editor's Props window: `catalog\catalog.json`, its thumbnails in
`catalog\thumbs\`, and `MeleCatalog_Core.pcc` in the dev mod Mele Catalog (`DLC_MOD_MeleCatalog`). The overlay reads the
file the first time it draws the Catalog tab, and again on Reload; the game thread reads it once more, for the `load` paths
of sessions' spawns (design §3.5, §10).

```json
{
  "version": 1,
  "built": "2026-10-28T09:00:00Z",
  "package": "MeleCatalog_Core",
  "categories": ["Crates", "Furniture", "Medical", "Consoles", "Lights", "Industrial", "Debris", "Alien", "Small items", "Signs", "Plants", "Other"],
  "entries": [
    {
      "name": "Crate_XLarge_02",
      "category": "Crates",
      "mesh": "BIOA_FRE31_00_LAY.Crate_XLarge_02",
      "source": "BIOA_FRE31_00_LAY",
      "load": "MeleCatalog_Core.Crates.Crate_XLarge_02",
      "thumb": "thumbs/Crates_Crate_XLarge_02.png",
      "size": [180, 180, 160],
      "tris": 412,
      "placed": 37,
      "large": false
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `version` | 1, required. |
| `built` | When `catalog_build` wrote it, UTC. |
| `package` | The catalog package's name. |
| `categories` | Required. The category list, in the order the Props window shows it. |
| `entries` | Required. One per mesh. |
| `name` | Required. The mesh's object name, as the grid shows it. |
| `category` | One of `categories`. Missing: `Other`. One the list doesn't name joins it at the end. |
| `mesh` | Required. The vanilla mesh's full path. The bridge uses a copy already in memory at that path; sessions record it. |
| `source` | A file the game loads with the mesh as an export, without `.pcc`: the smallest one. `editor_import` and `level_build` port the mesh from it. |
| `load` | The path `DynamicLoadObject` loads the mesh by: `MeleCatalog_Core.<Group>.<Name>`, the group being the category in one word (`SmallItems`). |
| `thumb` | The thumbnail's path relative to the catalog folder, `thumbs/<Group>_<Name>.png` (characters outside `A-Z a-z 0-9 _ . -` become `_`), or `null`. An absolute path or one with `..` is ignored. |
| `size` | The bounds box `[x, y, z]`, in Unreal units. |
| `tris` | LOD0 triangles. |
| `placed` | How many StaticMeshComponents of the game's levels use the mesh. |
| `large` | Over 600 units on its longest side. |

- An entry without `name` or `mesh` is skipped and counted. A missing number reads as 0.
- A missing file shows "No catalog yet (...): build it with catalog_build in mele-mcp, then Reload."
- `catalog_build` writes the thumbnails into `thumbs.new` and swaps the folders, then writes `catalog.json`, so the overlay
  never reads entries whose thumbnails are missing.

## 7. LE2 and LE3 saves

The `.pcsav` career saves, as the `save_*` tools read them. Field order comes from
[Trilogy Save Editor](https://github.com/KarlitosVII/trilogy-save-editor), whose format code is the reference; its
own code is not used here. LE2 is format 30, LE3 format 59. LE1 is another layout and is not read.

- Little-endian. A bool is 4 bytes, 0 or 1. A list or map starts with a 4-byte count. A string starts with a 4-byte length
  that counts its terminator: positive for Latin-1, negative for UTF-16 (in characters), 0 for an empty one.
- The last 4 bytes are a checksum of everything before them: CRC-32 with polynomial `04C11DB7`, most significant bit
  first, start and final XOR `FFFFFFFF` (UE3's `appMemCrc`, CRC-32/BZIP2, LEC's `Crc32`). Plain zlib CRC-32 does not match.
- Nothing holds a file offset, so a field can change size: only the checksum is redone.

| Part | Fields, in order |
|---|---|
| Header | format, debug name, seconds played, disc, map; LE3: map override; difficulty (1 byte), end state, time (seconds since midnight, day, month, year), location, rotation, loading tip |
| World lists | levels, streaming states, Kismet records, doors; LE3: placeables; pawns |
| Player | female, class; LE3: combat, injured, casual; level, XP, first name, last name string, origin, notoriety (1 byte each), talent points, 3 mapped powers, appearance; LE3: emissive id; powers; LE3: war assets; weapons; LE3: weapon mods; loadout (6 strings); LE3: primary, secondary, weapon groups; hotkeys; LE3: health; credits, medigel, eezo, iridium, palladium, platinum, probes, fuel; LE3: grenades; face code, class name string; LE3: character guid |
| After the player | LE2: ME1 import bonus (2 ints, 5 floats); squad, plot table, journal, codex, ME1 plot table; LE3: player variables; galaxy map, dependent DLC; LE3: treasures, use modules, conversation mode, objective markers, saved objective text |

Appearance is the combat appearance (1 byte), 13 ints (casual, full body, torso, shoulder, arm, leg, specular, 3 tints,
pattern, pattern colour, helmet), then `bHasMorphHead`. With the flag set a morph follows: hair mesh, accessory meshes,
morph features, offset bones, 4 lists of vertices (12 bytes each, only lod 0 filled here), scalar, vector and texture
parameters. A morph takes about 33 KB. With the flag off nothing follows, and `SFXSaveGame.LoadAppearance` leaves the pawn's
`MorphHead` at `None`. LE2's `SFXPawn_Player` then keeps its template head, `HMM_HED_PROSheppard_MDL` for a man, and LE3's
takes `SFXPlayerCustomization`'s iconic head and FaceFX asset.

The plot table's bools are a list of 32-bit words, lowest bit first. LE2's ints and floats are lists indexed by id. LE3's
are id and value pairs, and an id never set has no pair.
