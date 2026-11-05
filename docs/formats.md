# Shared formats

This page holds the contracts between MeleBridge (the ASI in the game) and mele-mcp (the MCP server), and the layout of
LE2 and LE3 saves. Each contract has a version, and a reader refuses a version it does not know.

| Section | Contract | Games |
|---|---|---|
| §3 | Pipe protocol | LE1, LE2, LE3. The `editor` op is LE1 only. |
| §4 | `levels.json` | LE1 only (`level_build`). |
| §5 | Session files | LE1 only (the in-game editor). |
| §6 | `catalog.json` | LE1 only (the prop catalog). |
| §7 | Career saves | LE2 and LE3. LE1 saves have another layout and are refused. |

"Design §10" means section 10 of the [editor design](design/in-game-editor.md). A bare section number is on this page.
The examples use the map `BIOA_MYM01` and the NPC `guard`; use your own names.

## 1. Versions

| Contract | Version | Written by | Read by | On a version it does not know |
|---|---|---|---|---|
| Pipe protocol | 1 | MeleBridge (`ping`) | mele-mcp | mele-mcp refuses that bridge, except `editor` and `keep_ticking` without `on: true` (§3.3). |
| `levels.json` | 1 | `level_build`, also through `mod_build`, `npc_place`, `live_save` and `editor_import` | MeleBridge; `catalog_index` for its `dlc` keys | "levels.json can't be read (version N, and this bridge reads version 1): run mod_build". Every actor keeps Phase 3's rules (design §10). |
| `sessions\<map>.json` | 1, 2 or 3 | MeleBridge (Save, passes), `editor_import` (marks) | both | The bridge leaves the file alone: Save refuses and a pass skips it. `editor_import` refuses it. |
| `catalog.json` | 1 | `catalog_build` | MeleBridge | The Props window says the file can't be read. Sessions re-apply without catalog paths. |
| `.pcsav` career saves | LE2 30, LE3 59 | the game, `save_set`, `save_head_default` | mele-mcp | "Save format N: LE2 saves are format 30." |

The protocol number is `MELE_PROTOCOL_VERSION` in `tools/mele-bridge/src/SharedVersion.h` and `Versions.BridgeProtocol`
in mele-mcp. The same header holds `ASI_VERSION` 4, which `ping` and `status` report as `"bridge": "4"`. It is for
people and logs; programs check `"protocol"`. mele-mcp's own version, 0.1.0, is the `<Version>` in
`tools/mele-mcp/src/MeleMcp/MeleMcp.csproj`, and a YAML's `requires: mele-mcp >= X.Y.Z` is checked against it.

The pipe protocol goes up when an op changes its arguments or its reply. A new op needs no bump, since an older bridge
answers it `unknown op` (§3.2). A file's version goes up when an older reader would do something wrong with the new
content. A key an older reader would only skip needs no bump, so `npcs` and `characterLights` sit in `levels.json`
version 1. Session version 3 exists because a reader limited to version 2 would rewrite an NPC's light with its own
writer.

## 2. Where the files are

```
%LOCALAPPDATA%\MeleBridge\LE1\
  levels.json                  written by level_build, read by the bridge (and its dlc keys by catalog_index)
  sessions\<map>.json          the editor's work on one persistent map
  sessions\<map>.dropped.json  entries editor_import dropped from that session
  catalog\catalog.json         the prop catalog
  catalog\thumbs\*.png         its thumbnails, 128×128
```

- Nothing here is in the game install. The bridge reads the `LOCALAPPDATA` variable and refuses without it
  ("LOCALAPPDATA is not set"). mele-mcp asks Windows for the same folder.
- `MELE_LEVELS_JSON` (mele-mcp only) names another `levels.json`, for tests. The default sessions folder of
  `editor_import` is then `sessions` beside that file. The bridge ignores the variable and always reads the folder
  above.
- `catalog_build`'s `outDir` writes the catalog elsewhere. The bridge reads only `catalog\` in the folder above.
- Every writer writes a temporary file beside the target, then moves it over the target. Readers never see half a file.
  The temporary names are `.tmp` (the bridge, `catalog_build`), `.build.tmp` (`level_build`), `.import.tmp`
  (`editor_import`).
- A file that can't be parsed is moved aside and a new one started. The bridge moves a session to `<map>.json.bad-<n>`,
  where n is milliseconds since Windows started. `level_build` and `editor_import` move `levels.json` and a dropped file
  to `<file>.bad-<n>`, where n is .NET UTC ticks.

Saves are in the game's own folder (§7). Backups and other files mele-mcp writes are listed in
[getting started](getting-started.md).

## 3. Pipe protocol

### 3.1 Transport

- Name: `\\.\pipe\MeleBridge_LE1`, `MeleBridge_LE2` or `MeleBridge_LE3`. Each game's ASI serves its own. A `live_*`
  tool opens the pipe of the game its required `game` names.
- Pipe: byte mode, local clients only (`PIPE_REJECT_REMOTE_CLIENTS`), any number of connections, one thread each.
- Lines: UTF-8 JSON, one request per line, each ending in `\n`. One reply line per request, in order. A line of only
  spaces, tabs or `\r` is skipped. More than 1 MB of unread input closes the connection.
- Threads: every op but `editor` runs on the game thread, in the `ProcessEvent` hook, at the next call the game makes
  through it. `keep_ticking` with `on: false`, or without `on`, does not wait for that thread either.

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
| other keys | request | The op's arguments. A `null` is the same as a missing key, except `set`'s `value`. A wrong type is refused: `"command" is a string`. |
| `ok` | reply | `true` with the op's reply keys beside it, or `false` with `error`. |
| `error` | reply | What went wrong, in words, ready to show. |

| Case | Reply |
|---|---|
| The game thread does not take it in time (a load screen, a menu that runs no script) | `"the game thread did not take the request in time (game loading, or not running script?)"`. A request already started runs to its end, even if its reply comes late. |
| An access violation while it runs | `"access violation in the game while running the request"`. The game keeps running. |
| An unknown op | `"unknown op X (ping, console, find, get, set, call, describe, subscribe, editor, status, keep_ticking)"` |

mele-mcp's client (`LiveBridge.cs`) drops `null` keys, except `set`'s `value`, where `null` means None. It sets
`timeoutMs` from the tool's timeout and waits 2 s longer for the reply line. It reports "`<game>` is not running" when
the game is closed, and "Pipe MeleBridge_LE`<n>` not found" when the ASI is missing or the game is still starting.

### 3.3 The version check

`ping` returns:

```json
{"ok": true, "bridge": "4", "protocol": 1, "map": "BIOA_NOR00", "objects": 512345}
```

- mele-mcp pings before its first request to a game process, known by process id and start time. It caches the answer
  for that process, so a restarted game is checked again. The cache lives in the server process, so a new server checks
  again, and `scripts/mcp_call.py` starts one per call. When Windows does not say which process serves the pipe, every
  connection pings.
- `ping` runs on the game thread with the request's timeout, so it can time out on a load screen. A ping that times out
  or is refused is not cached. The error says to try again once the game has loaded.
- A request made only of `editor` ops skips the check, so `live_editor` answers while the game loads. So does one made
  only of `keep_ticking` ops without `on: true`.
- A bridge that reports another protocol, or none, is refused, and so is every later request to that process but the two
  kinds above. The error names both versions and says to build and install the ASI of the same toolkit release, game
  closed, then start the game again. On LE2 and LE3 the command is `tools/mele-bridge/build.sh --game LE<n> --install`.

### 3.4 Ops

| Op | Arguments | Reply |
|---|---|---|
| `ping` | none | `bridge` (version string), `protocol` (int), `map` (the persistent map's name), `objects` (the length of `GObjObjects`). |
| `console` | `command` (required) | Nothing else. mele-mcp's `live_console` reads the output from the DebugLogger log. |
| `find` | `class`, `name`, `path` (at least one); `defaults` (false); `limit` (100) | `total` (all matches), `objects`: `[{path, class}]`, the first `limit`. |
| `get` | `object` (required); `property`; `depth` (1 without `property`, 3 with) | `value`: the property, or every property of the object by name. |
| `set` | `object`, `property`, `value` (all required) | `value`: the property read back after the write. |
| `call` | `object`, `function` (required); `args` (a list, default `[]`) | `return` when the function returns a value; `out` (`{name: value}`) when it has out parameters. |
| `describe` | `object` or `class` (exactly one); `functions` (true) | `class` (path), `chain` (class names up to `Object`), `properties`: `[{name, type, owner}]`, `functions`: `[{name, owner, signature}]`. |
| `subscribe` | `functions` (a non-empty list of function paths); `objectContains`; `params` (true) | `functions`: the paths resolved. The connection then becomes an event stream (§3.6). |
| `editor` | none | The in-game editor's state (§3.7). LE1 only. |
| `status` | none | One look at the game for tests. Keys below. |
| `keep_ticking` | `on` (a bool; missing reads the switch) | `keepTicking`, `worldTicks`, `focusPausesCleared`, `timerRaises`; `clearedFocusPause` and `world` (`null` unless `on` is true). |

What differs per game:

| | LE1 | LE2 | LE3 |
|---|---|---|---|
| `editor` | answers | "the in-game editor is built for LE1 only" | same as LE2 |
| `status`: `loadingMovie.inLoad` | a bool | `null` (the field does not exist) | `null` |
| `status`: `partySelectOpen`, `partySelect` | read | read | `null`, with a note |
| `keep_ticking` works through | `m_fNoBrowserWheelTimer` | `m_bAllowBrowserWheel` | `m_bAllowBrowserWheel` |

Notes on the ops:

- `console`: the command goes through the player controller `status` reports as `pc`, like typing it.
- `find`: `class` is a class name: any loaded class of that name, subclasses included. `name` and `path` are
  case-insensitive substrings of the object's name and full path. Destroyed objects are left out, and class default
  objects unless `defaults` is true. Without a filter: `"give a class, a name or a path filter"`.
- `call`: the function is found by name up the class chain. Opcode natives work (design §3.1). Arguments are given in
  order, and optional and out parameters may be left out. Latent and iterator functions are refused, by their flags and
  by a list of engine names: `"Sleep is latent or an iterator: it cannot be called from outside script"`.
- `describe`: a function a subclass overrides is listed once, with the subclass as `owner`. A `signature` reads
  `<return type> Name(optional out <type> Param, ...)`, `void` when nothing is returned. Types read `int`, `stringref`,
  `float`, `name`, `string`, `bool`, `byte`, `delegate`, `struct Vector`, `array<int>`, `class<Actor>`, `interface X`,
  an enum's name, or the class of an object property. A static array adds `[N]`.
- `subscribe`: a path may leave out the package (`SequenceOp.Activated` finds `Engine.SequenceOp.Activated`), and may
  start with `Function `.

`status` runs on the game thread, so it times out while a map loads. Every key is always there. A part that can't be
read is `null`, with why in `notes` (a list of strings).

| Key | Meaning |
|---|---|
| `bridge`, `map` | The bridge's version and the persistent map's name. |
| `world` | The map's `BioWorldInfo`, `<map>.TheWorld.PersistentLevel.…`. |
| `begunPlay`, `bPlayersOnly` | Those fields of `world`. |
| `pausedByFocusLoss` | Its `m_bPausedByFocusLoss`. |
| `keepTicking`, `worldTicks`, `focusPausesCleared`, `timerRaises` | As `keep_ticking` returns them. |
| `pc`, `pawn` | The map's live player controller (under `<map>.TheWorld.`, not being destroyed), else the first one in memory; its `Pawn`. |
| `streaming` | `{levels, pending, pendingNames}` over the world's `StreamingLevels`. `pendingNames` holds the first 5 package names. |
| `loadingMovie` | `{object, playing, state, inLoad}` of the first `SFXLoadMovieManager` (`IsLoadingMoviePlaying()`, `PlaybackState`, `bIsInLoad`). `null` when there is none, then looked for again after 5 s at the earliest. |
| `partySelectOpen` | Whether the player's GUI manager (`GetScaleFormManager()`) has a `PartySelect` panel (`GetPanelByTag`). |
| `partySelect` | `null` unless open, else `{panel, handler, handlerClass, membersAvailable, varPath}`. |
| `notes` | Why a part is `null`. |

A streaming level is pending in three cases: a load or unload is pending, it should be loaded and has no `LoadedLevel`,
or `bShouldBeVisible` differs from `bIsVisible`.

In `partySelect`, `handler` is the panel's `BioSFHandler_PartySelection`: its default handler, else a script handler.
`membersAvailable` is the movie's `nMembersAvailable` read at `varPath` (`_root.nMembersAvailable`, else
`nMembersAvailable`). It is 0 until the screen has set up its cards.

`keep_ticking` is a test switch, off at every game start. While it is on, `BioWorldInfo.Tick` never takes its focus-loss
pause, so the world and Kismet run with the game window in the background.

- LE1: a `m_fNoBrowserWheelTimer` this Tick would bring to 0 or below is raised to 1e9 for that Tick, so `CanPause()` is
  false inside it. It goes back to 0 if it still reads 1e9. `timerRaises` counts the raises.
- LE2 and LE3: `m_bAllowBrowserWheel` is off for the length of each Tick, then back on. `timerRaises` counts that.
  `on: true` is refused when reflection and the SDK's offsets differ:
  `"BioWorldInfo's focus flags are not where the SDK puts them: keep_ticking stays off"`.
- Requests wait for the first `ProcessEvent` call after such a Tick, so they never see the raised value. Outside Tick
  the pause menu behaves as vanilla.
- `bPlayersOnly` is cleared only together with a set `m_bPausedByFocusLoss`, before and after each Tick. Pauses that do
  not set that flag stay: menus, the squad screen, photo mode, the F8 editor. On LE3 `bPlayersOnly` also stays when an
  `SFXGRI` has `bPauseForCommand` off, as in the game's own `PauseGame(FALSE)`.
- `on: true` runs on the game thread. It finds the fields by reflection, refuses when one is missing, turns the switch
  on and ends a focus pause already on the map's world (`clearedFocusPause`). `on: false` and a read answer at once.
- The counters run since the game started, and only while the switch is on.

### 3.5 Objects, properties and values

- Object paths are full paths as `find` returns them, `Package.Group.Name`, compared without case. The `Class Path` form
  some tools print is taken too. Not found: `"no object at X (paths as find returns them)"`.
- Property paths read `A.B[2].C`. A dot enters a struct, and `[i]` indexes a static or dynamic array.

| Property type | In a reply | Accepted by `set` and `call` |
|---|---|---|
| bool | `true` / `false` | a bool, a number (0 is false), `"true"`, `"false"`, `"1"`, `"0"` in any case |
| byte | the enum value's name when the property has an enum, else a number | an enum value's name (any case) or 0 to 255 |
| int, stringref | a number | a number, a bool or a numeric string; it must be whole |
| float | a number | a number, a bool or a numeric string |
| name, string | a string | a string |
| object | its path, or `null` | a path; `null`, `""` or `"None"` for none. The class is checked. |
| interface | its object's path, or `null` | refused |
| delegate | `Object.Function`, or `null` | refused |
| struct | an object of its members | an object of the members to change; the others keep their values |
| dynamic array | a list, at most 64 items, then `"... N more"` | a list of the same length; else set one element as `Name[i]` |
| static array | a list of every element | one element at a time: `Name[0]` |
| past `depth` | a struct as `"{StructName}"`, an array as `"[N items]"` | |
| other types | `"<PropertyClass>"` | refused: `"cannot set a ..."` |

### 3.6 Events

After a `subscribe` succeeds, the bridge reads no more requests on that connection. It writes one line per watched call
until the client closes the connection.

| Line | Meaning |
|---|---|
| `{"t": 12.345, "function": "...", "object": "...", "class": "...", "params": {...}, "pin": "In"}` | A watched function was called. `t` is seconds since the bridge started, to the millisecond. `params` holds the parameters by name at depth 1, when `params` is true. `pin` is the input link that fired, for a Kismet `SequenceOp`. |
| `{"heartbeat": true}` | Nothing happened for 2 s. |
| `{"dropped": n}` | n events were dropped, because 10,000 were already waiting to be sent. |

- A watch matches its function by name on any object of the function's class or a subclass. A watch on
  `Engine.SequenceOp.Activated` therefore sees every Kismet op that fires.
- `objectContains` keeps only calls on objects whose path contains it, without case.
- mele-mcp's `live_watch` closes the connection when its time or event limit is reached. It hides heartbeats and adds up
  `dropped`.

### 3.7 The editor op

`editor` takes nothing and does not wait for the game thread. It reads atomics and a snapshot the editor publishes once
a frame, so it answers while the world is paused or loading. `live_editor` sends it alone, without the check of §3.3,
and returns the reply as it is. It is for checks and tests, and nothing ships from it.

| Key | What it holds |
|---|---|
| `open`, `freeCam`, `frame`, `speed` | Whether F8 is open and the free cam on; the editor's frame count; the fly speed. |
| `camera` | `{location, rotation, fov}` of the view. |
| `canvas`, `backbuffer` | Their sizes in pixels, `[width, height]`. |
| `lastPick`, `status` | The last click's result and the status line, as text. |
| `edits` | `{count, unsaved, saveProblem}`. |
| `undo` | `{undo, redo}`: the name of the step Ctrl+Z or Ctrl+Y would run (`"move ed_Crate_…"`), or `null`. |
| `session` | `null` or `{file, error, applied, dropped, pending, levels, passFrame}`. `pending` lists `"key: why"`. |
| `levelsJson` | `null` or `{file, problem, dlc, levels, props, lights, npcs, npcLights, characterLights}`: what the bridge read of `levels.json`. |
| `meshesInMemory` | How many the Props window lists, or `null`. |
| `catalog` | `{entries, thumbnails}`. `entries` is `null` until the catalog is read. |
| `autoLight` | Auto light's state. |
| `lights` | Tracked lights: `{path, tag, location, light, npc, edited, unsaved}`. A level light adds `type, rig, collection, metres`. |
| `npcs` | `{name, tag, level, away}`. |
| `effects` | `{path, name, template, level, location, metres, refusal, deleted, hidden, forked, edited, unsaved}`. |
| `selection` | `null` or `{path, class, tag, location, rotation, drawScale, drawScale3D, pawn, static, spawned, undeletable, unmovable, ships, file, light, npc, propLighting, effect}`. A level light adds `levelLight`. |
| `calibration` | `null` or `{ours, canvasProject, errorPixels}`: the editor's projection against `Canvas.Project` (design §3.4). |
| `threads` | Which thread ran `present`, `postRender`, `updateCamera` and `window`. |
| `hooks` | `{present, present1, resizeBuffers, directInput, cursor, window, overlayReady, overlayFailed}`. |
| `counters` | `{presents, overlayFrames, swallowedMessages, zeroedDeviceStates, zeroedDeviceData, blockedCursorCalls}`. |

## 4. levels.json (version 1)

The file says which levels are ours, which vanilla files each streams, and which built props, lights and NPCs the editor
may change (design §11.4, §12.1). It describes the last build, not the install.

`level_build` writes the key of the level it built, last, after its forks. Other keys stay. It writes nothing with its
`package` parameter or with `mod_build`'s `out`. The bridge reads the file on each map change. It reads it again when
its write time changes, checked when F8 opens and once a second while the editor is open.

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
| `version` | 1. The bridge reads a missing version as 1. |
| `dlc` | The DLC folder of the last built level that lies in one. A level whose file lies there and whose name is not a fork is ours too. |
| `levels.<key>` | One level built from a YAML: the package file name without `.pcc`. Each build replaces it whole. Matched without case. |
| `levels.<key>.dlc` | The nearest folder above its package whose name starts with `DLC_`, or `null`. The bridge skips it. |
| `streams` | The vanilla files the level streams, by their vanilla names, also when a fork stands in. The editor's "ships" line reads it. |
| `props` | Tags of the YAML's props that are not removed: the built props the bridge may switch to prop lights (design §11.1). |
| `lights` | Tags of the YAML's `lights:` entries, switched-off ones too: the lights the editor lists. NPC lights sit under their NPC. |
| `npcs.<tag>` | Every NPC of the YAML by her pawn's tag: `name` (the YAML key), `location` (the built pawn's `Location`, written short: `-36739.9`), `yaw` (Unreal units, 65536 = 360°), `lights` (light tag to offset `[forward, right, up]`). |
| `characterLights` | The light collections the build copies into the level: the copy's name in the level, to the file it came from without `.pcc`. |

The bridge treats a missing key, or one of another type, as empty, and skips an unknown key. It compares tags without
case. It skips an NPC without `name`, `location` or a numeric `yaw`, and an NPC light whose offset is not three numbers.
A missing file says "levels.json missing: run mod_build". A broken one says "levels.json can't be read (...): run
mod_build". Either way every actor keeps Phase 3's rules (design §10).

mele-mcp's `catalog_index` leaves our own mods' files out, as it does the catalog's. With `MELE_OWN_DLC` unset, those
are the top `dlc` and every level's `dlc`. With it set, only the folders it lists. A missing or broken file gives none.

## 5. Session files (versions 1 to 3)

`sessions\<map>.json` holds the editor's work on one persistent map (the game's `GWorld`), sideloaded levels included.
It has one entry per actor, with its latest state and no history. The bridge's Save writes it, and its passes drop
entries the installed level shows. `editor_import` marks the entries it applied and drops those no YAML builds (design
§3.7, §10).

```json
{
  "version": 2,
  "map": "BIOA_MYM01",
  "saved": "2026-01-15T14:03:12Z",
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
| `version` | 1, 2 or 3 (§5.4). The bridge reads a missing version as 1. `editor_import` reads it as 0 and refuses, so a file written by hand needs it. |
| `map` | The persistent map's name. |
| `saved` | When the bridge last saved, UTC, `yyyy-MM-ddTHH:mm:ssZ`. |
| `actors` | The entries, in the order they were first saved. |

### 5.2 An entry

| Field | Type | Meaning |
|---|---|---|
| `tag` | string | The actor's `Tag`. A spawn's tag is unique (`ed_<mesh>_<run>_<n>`) and is its key. Empty for a level light. |
| `path` | string | The actor's full path. For an actor in a fork, the path in the vanilla file. For a level light, its component's path (`<file>.TheWorld.PersistentLevel.<collection>.<light>`). |
| `level` | string | The level file the actor belongs to, by its vanilla name. |
| `class` | string | The actor's class name; a level light's component class. |
| `spawned` | bool | The editor spawned it. |
| `mesh` | object or `null` | A spawned prop only: `path` (the vanilla mesh's path), `file` (a level that shows it, a hint, `null` when unknown), `load` (the catalog path for `DynamicLoadObject`, `null` when unknown). |
| `location` | `[x, y, z]` | World location. For a level light, its place in `LightToWorld.WPlane`. |
| `rotation` | `[pitch, yaw, roll]` | Integers in Unreal units (65536 = 360°). Zeros for a level light. |
| `scale` | number | `DrawScale`. 1 for a level light. |
| `scale3d` | `[x, y, z]` | `DrawScale3D`. |
| `deleted` | bool | Hidden by the editor (design §10). A light is switched off with `light.enabled` instead, so `deleted` on a light means a spawned light the editor deleted. |
| `base` | object or `null` | What the YAML holds for the actor, for the three-way merge: a state (below). `null` for a spawn never imported. |
| `light` | object | Lights only: `kind`, `radius`, `brightness`, `color`, `falloff`, `enabled`, and `type` for a level light. |
| `npc` | object | NPC lights only: `tag` (the NPC's pawn tag), `offset` (`[forward, right, up]` from the NPC's pose, turned by her yaw). |
| `owner` | string | Level lights only: the name of the light collection that holds the light. |
| `imported` | object | Written by `editor_import` only: `{ "before": <state> }`, what the installed level may still show. `before` is `null` for a props entry the import added. |
| `installed` | list of states | Written by the bridge only: earlier `imported.before` states an install may still show. |

- Key: a spawn is matched by `tag`, and any other entry by `path`, a fork's path read as the vanilla file's path. A
  later Save replaces the entry with that key. When the bridge saves an edit of a built copy (a level actor with a
  spawn's tag), the copy's entry replaces the spawn's.
- A state (in `base`, `imported.before` and `installed`) holds `location`, `rotation`, `scale` and `scale3d`. It also
  holds `deleted`, `light` and `npc` where the entry has them. A field it lacks takes the actor's own value.
- `base.deleted` says whether the YAML removes the actor (`removed: true`). In `imported.before` it says whether the
  installed level hides it.

The light block:

| Field | Values |
|---|---|
| `kind` | `props` (prop light), `room` (room light), `npc` (an NPC's light), `vanilla` (a level light). Compared exactly. |
| `radius`, `brightness`, `falloff` | Numbers: `Radius`, `Brightness`, `FalloffExponent`. |
| `color` | `[r, g, b]`, integers 0 to 255. The light's alpha is not stored. |
| `enabled` | Whether the light is on. |
| `type` | Level lights only: `B` (baked), `C` (characters only), `U` (special channel) or `D` (dynamic) (design §12.4). |

### 5.3 Rules both tools keep

Two writers share the file. The bridge's Save reads it first and keeps what an import changed in fields the editor did
not change since (design §10). `editor_import` rewrites it only when it has not changed since the read. If the bridge
saved meanwhile, the import merges again, up to 3 tries. With `keepSession` the import leaves the file as it is.

Values count as equal within these limits:

| Value | Limit |
|---|---|
| Location | Within 0.01, after rounding to 2 decimals. |
| Rotation | As an orientation, axes within 0.02°. |
| Scales | Within 0.001. |
| Light | Radius within 0.01, brightness and falloff within 0.001, colour and kind exact. |
| NPC light | The bridge takes its place within 0.05 per axis, `editor_import` its offset within 0.02 per axis. |

Before `editor_import` removes an entry from a session, it adds the entry to `<map>.dropped.json` beside it. That file
reads `{about, dropped: [{at, session, file, why, entry}]}`, and `editor_import` skips `*.dropped.json` when it looks
for sessions. To add an entry back, copy its `entry` into the session's `actors` with `"base": null` and without
`imported` or `installed`, game closed. Then import again.

With no `session` given, `editor_import` takes the level's own `sessions\<level>.json` when it exists, however old. For
a level the game loads into another map, it takes the newest session that matches in one of these ways:

- It has an actor of the level that the editor did not spawn.
- It has an entry whose tag the YAML builds, an NPC's light included.
- It has a light on one of the YAML's NPCs.

Files that can't be parsed are skipped. With no match it refuses and names the sessions that hold spawned meshes.

### 5.4 Versions

| Version | When | What it has |
|---|---|---|
| 1 | Read only; the bridge and `editor_import` never create it | `tag`, `path`, `level`, `class`, `spawned`, `mesh`, `location`, `rotation`, `deleted`. No `base`, no scales. |
| 2 | The default | Version 1's fields and `scale`, `scale3d`, `base`, `imported`, `installed`, plus `light` blocks of kind `props` and `room`. Effects (`class: Emitter`) are plain vanilla entries. |
| 3 | When needed | An entry, its `base`, its `imported.before` or one of its `installed` holds a `light` of kind `npc` or `vanilla`. Adds `npc`, `owner` and `light.type`. |

- The bridge writes 3 when an entry needs it, else 2, at every Save. A pass that drops entries rewrites a version 2 or 3
  file at the version of what is left. A version 1 file stays 1 through a pass.
- `editor_import` never lowers a version. A file it marks becomes at least 2, and 3 when an entry holds an `npc` or
  `vanilla` light.
- On another version, the bridge's Save says
  `"<file> is version N, and this bridge reads versions 1 to 3: left as it is"`, and its passes skip the file.
  `editor_import` says
  `"<file> is a version N session; editor_import reads versions 1 to 3. Deploy mele-mcp and MeleBridge from the same commit."`

## 6. catalog.json (version 1)

`catalog_build` writes the prop catalog for the editor's Props window: `catalog\catalog.json`, its thumbnails in
`catalog\thumbs\`, and `MeleCatalog_Core.pcc` in the dev mod Mele Catalog (`DLC_MOD_MeleCatalog`). The overlay reads the
file the first time it draws the Catalog tab, and again on Reload. The game thread reads it once more, for the `load`
paths of sessions' spawns (design §3.5, §10).

```json
{
  "version": 1,
  "built": "2026-01-15T09:00:00Z",
  "package": "MeleCatalog_Core",
  "categories": ["Crates", "Furniture", "Medical", "Consoles", "Lights", "Industrial", "Debris", "Alien",
                 "Small items", "Signs", "Plants", "Other"],
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
| `category` | One of `categories`. Missing: `Other`. One the list does not name joins it at the end. |
| `mesh` | Required. The vanilla mesh's full path. The bridge uses a copy already in memory at that path, and sessions record it. |
| `source` | A file the game loads with the mesh as an export, without `.pcc`: the smallest one. `editor_import` and `level_build` port the mesh from it. |
| `load` | The path `DynamicLoadObject` loads the mesh by: `MeleCatalog_Core.<Group>.<Name>`. The group is the category in one word (`SmallItems`). A second mesh of one name in a group gets `_2`, `_3`. |
| `thumb` | The thumbnail's path relative to the catalog folder, `thumbs/<Group>_<Name>.png`, or `null`. Characters outside `A-Z a-z 0-9 _ . -` become `_`. The bridge ignores an absolute path or one with `..`. |
| `size` | The bounds box `[x, y, z]`, in Unreal units. |
| `tris` | LOD0 triangles. |
| `placed` | How many StaticMeshComponents of the game's levels use the mesh. |
| `large` | Over 600 units on its longest side. |

- The bridge skips and counts an entry without `name` or `mesh`. A missing number reads as 0.
- A missing file shows "No catalog yet (...): build it with catalog_build in mele-mcp, then Reload."
- `catalog_build` writes the thumbnails into `thumbs.new` and swaps the folders, then writes `catalog.json`. The overlay
  therefore never reads entries whose thumbnails are missing. With `thumbs=false` entries keep the thumbnails already
  rendered, and the others get `null`.

## 7. LE2 and LE3 saves

The `.pcsav` career saves, as `save_list`, `save_read`, `save_head_default` and `save_set` read them. They sit in
`Documents\BioWare\Mass Effect Legendary Edition\Save\ME<n>\<career>\`. Field order comes from
[Trilogy Save Editor](https://github.com/KarlitosVII/trilogy-save-editor), whose format code is the reference; its own
code is not used here. LE2 is format 30 and LE3 format 59.

- Little-endian. A bool is 4 bytes, 0 or 1. A list or map starts with a 4-byte count. A string starts with a 4-byte
  length that counts its terminator: positive for Latin-1, negative for UTF-16 (in characters), 0 for an empty one.
- The last 4 bytes are a checksum of everything before them: CRC-32 with polynomial `04C11DB7`, most significant bit
  first, start and final XOR `FFFFFFFF` (CRC-32/BZIP2, LEC's `Crc32`). Plain zlib CRC-32 does not match.
- Nothing holds a file offset, so a field can change size. Only the checksum is redone.
- A reader refuses a file with a wrong checksum, another format, a bool that is not 0 or 1, or fields that do not end
  exactly on the checksum.

| Part | Fields, in order |
|---|---|
| Header | format, debug name, seconds played, disc, map; LE3: map override; difficulty (1 byte), end state, time (seconds since midnight, day, month, year), location, rotation, loading tip |
| World lists | levels, streaming states, Kismet records, doors; LE3: placeables; pawns |
| Player | female, class; LE3: combat, injured, casual; level, XP, first name, last name string, origin, notoriety (1 byte each), talent points, 3 mapped powers, appearance; LE3: emissive id; powers; LE3: war assets; weapons; LE3: weapon mods; loadout (6 strings); LE3: primary, secondary, weapon groups; hotkeys; LE3: health; credits, medigel, eezo, iridium, palladium, platinum, probes, fuel; LE3: grenades; face code, class name string; LE3: character guid |
| After the player | LE2: ME1 import bonus (2 ints, 5 floats); squad, plot table, journal, codex, ME1 plot table; LE3: player variables; galaxy map, dependent DLC; LE3: treasures, use modules, conversation mode, objective markers, saved objective text |

Difficulty is an index: LE2 Casual, Normal, Veteran, Hardcore, Insanity; LE3 Narrative, Casual, Normal, Hardcore,
Insanity.

Appearance is the combat appearance (1 byte), 13 ints (casual, full body, torso, shoulder, arm, leg, specular, 3 tints,
pattern, pattern colour, helmet), then `bHasMorphHead`. With the flag set a morph follows: hair mesh, accessory meshes,
morph features, offset bones, 4 lists of vertices (12 bytes each, the count for lod 0 first), then scalar, vector and
texture parameters. With the flag off nothing follows, and the game gives the pawn its default head. `save_head_default`
clears the flag and removes the morph.

The plot table's bools are a list of 32-bit words, lowest bit first. LE2's ints and floats are lists indexed by id.
LE3's are id and value pairs, and an id never set has no pair.

`save_set` writes these by name: `level`, `xp`, `talentPoints`, `credits`, `medigel`, `eezo`, `iridium`, `palladium`,
`platinum`, `probes`, `fuel`, and on LE3 `health` and `grenades`. It writes plot values as `bool:<id>`, `int:<id>` or
`float:<id>`. An id past the save's table is refused, except LE3 ints and floats, which gain a pair. Only the value
changes: no plot transition or journal update runs.

An edit refuses while the game runs, unless `allowRunning` is set. It parses the result again and copies the file to
`<backups>\saves\<game>\<career>\<yyyyMMdd-HHmmss-fff>\`. It refuses if the file changed since it was read. It then
moves a `.tmp` over the save, keeps the file's times and reads it back. `.bak` files beside the saves belong to Trilogy
Save Editor, and no save tool reads or writes them.
