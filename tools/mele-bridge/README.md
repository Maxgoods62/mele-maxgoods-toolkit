# MeleBridge

MeleBridge is an ASI mod that opens a request and reply pipe into a running Mass Effect Legendary Edition game. This
page covers what it does on LE1, LE2 and LE3, how to build and install it, and how mele-mcp talks to it.

An ASI is a DLL that the game's ASI loader starts with the game. Through the pipe, a client can read and write any
property of one live object, call UnrealScript functions, run console commands and stream events.

It fills gaps in the LEX Interop ASI. LEX Interop sends its replies to a window titled "Legendary Explorer", it only
handles whole classes (`getall`), and commands queued together run in reverse order. MeleBridge answers each request
on the same connection and works on one object at a time.

This is MeleBridge 5 (`ASI_VERSION` in `src/SharedVersion.h`), speaking pipe protocol 1.

## What it does on each game

All three builds come from the same sources. `src/game/LE<n>.hpp` holds what differs.

| | LE1 | LE2 | LE3 |
|---|---|---|---|
| ASI file | `MeleBridge.asi` | `LE2MeleBridge.asi` | `LE3MeleBridge.asi` |
| Pipe | `\\.\pipe\MeleBridge_LE1` | `\\.\pipe\MeleBridge_LE2` | `\\.\pipe\MeleBridge_LE3` |
| Read, write, call, console, events | yes | yes | yes |
| `status` and `keep_ticking` | yes | yes | yes |
| `status`: squad screen | yes | yes | `null` |
| `status`: `loadingMovie.inLoad` | yes | `null` | `null` |
| In-game editor and the `editor` op | yes | the op refuses | the op refuses |

`MELEBRIDGE_EDITOR` is 1 only in `game/LE1.hpp`. Without it, `Editor.cpp`, `Overlay.cpp` and `Input.cpp` compile to
nothing, and no ImGui ends up in the ASI.

[games.md](../../docs/games.md) says what has run in game on each title.

## Build and install

`build.sh` runs in WSL and calls Windows programs through interop. No prebuilt `MeleBridge.asi` is published, because
the binary would hold LExASIs and LExSDKv2 code, and neither states a license ([THIRD_PARTY.md](THIRD_PARTY.md)).

```bash
cp build.env.example build.env     # optional: the settings that differ on your machine
./build.sh                         # build LE1's ASI, and stage it and its .pdb in MELE_STAGE
./build.sh --install               # also copy it to Game\ME1\Binaries\Win64\ASI
./build.sh --uninstall             # delete it from that folder, without building
./build.sh --game LE2              # build and stage LE2MeleBridge.asi
./build.sh --game LE2 --install    # also copy it to Game\ME2\Binaries\Win64\ASI
./build.sh --game LE3 --uninstall  # delete LE3MeleBridge.asi from Game\ME3\Binaries\Win64\ASI
```

| Flag | What it does | Default |
|---|---|---|
| `--game LE1\|LE2\|LE3` | Picks the game. Case does not matter. | `LE1` |
| `--install` | Builds and stages, then copies the ASI into the game's `ASI` folder. | off |
| `--uninstall` | Deletes the ASI from that folder. It does not build. | off |

Close the game before you install or uninstall. `--install` still builds while the game runs, then refuses the copy.
`--uninstall` refuses at once. A failed `tasklist` counts as a running game.

The chosen game's ASI is the only file this tool writes inside the game install. Once installed, it runs at every
start of that game. Remove it with `--uninstall`. The game needs the ASI loader, which M3 installs. `build.sh` does
not create the `ASI` folder, so `--install` stops with a `cp` error when the folder is missing.

The log is `Game\ME<n>\Binaries\Win64\Logs\MeleBridge.log` on every game, rewritten at each start.

### What the build needs

- Visual Studio 18 (2026) or its Build Tools, with the C++ x64 tools and the "C++ CMake tools for Windows" component.
  The script finds it with `vswhere.exe`. LExASIs' CMake preset uses a generator that older versions do not know.
- Git for Windows (`git.exe`), `rsync` in WSL, and network access on the first build.
- The latest Microsoft Visual C++ Redistributable, x64, on the game PC. The ASI links the C++ runtime dynamically.

### Settings

Settings come from the environment or from `build.env`, which is not in git. An environment variable wins over the
file. All of them are optional.

| Variable | What it is | Default |
|---|---|---|
| `MELE_LE_DIR` | The Legendary Edition folder, as a Windows path. Only `--install` and `--uninstall` read it. | The registry: BioWare's `Install Dir`, then Steam's uninstall entry for app 1328670 |
| `MELE_VS` | The Visual Studio or Build Tools folder, as a WSL path | Found with `vswhere.exe` |
| `MELE_LEXASIS` | The LExASIs checkout, as a WSL path. Keep it on the Windows disk, because CMake and MSBuild do not work well on `\\wsl.localhost` paths. | `%LOCALAPPDATA%\mele-asi-build\MeleBridge\LExASIs` |
| `MELE_STAGE` | Where the `.asi` and `.pdb` are copied | `dist/` next to `build.sh` |

### How the build works

- It builds inside its own checkout of [ME3Tweaks/LExASIs](https://github.com/ME3Tweaks/LExASIs), pinned to `aaad03a`
  with its submodules. The first build clones it, and every build checks the pin. A repair stops if you changed that
  checkout's `CMakeLists.txt` yourself, so commit or stash your change first.
- That checkout provides the SDK (LExSDKv2), the ASI loader interface (SPI), the `Common` code, spdlog and the
  `asi_target` CMake helper.
- Dear ImGui and ImGuizmo are fetched at pinned commits into `third_party`, next to the checkout.
- `src/` is copied into the checkout as one more project (`rsync --delete`), and built for the one game `--game`
  names.
- A `LEASI_GAME*_WIN64` variable would make the build copy the ASI into the game. The script removes it for the
  configure step, and stops if one set on the Windows side still reaches CMake.

## In-game editor

The LE1 build also holds a level editor, drawn over the game, that F8 opens. The LE2 and LE3 builds leave it out.

- [Editor guide](../../docs/editor-guide.md): keys, tabs, lights, saving and shipping.
- [Editor design](../../docs/design/in-game-editor.md): how it works and why.
- [formats.md](../../docs/formats.md): the session, `levels.json` and catalog files it shares with mele-mcp.

## How mele-mcp reaches it

mele-mcp opens the game's pipe as a local client and sends one JSON line per request. These tools and scripts use it:

| What | Games | Note |
|---|---|---|
| `live_find`, `live_get`, `live_set`, `live_call`, `live_describe`, `live_watch`, `live_status`, `live_keep_ticking` | LE1, LE2, LE3 | |
| `live_console` | LE1, LE2, LE3 | Its output comes from the DebugLogger ASI's log. |
| `live_place`, `live_remove` | Accept all three | [games.md](../../docs/games.md) says where they have run. |
| `live_editor`, `live_save` | LE1 only | |
| `game_plot` | LE1 only | Uses the bridge when the pipe is there, else LEX Interop and DebugLogger. |
| `game_cycle`, `game_close` | LE2, LE3 | LE1 uses LEX Interop for these. |
| `tools/mele-mcp/scripts/scenario.py` | LE1 only | [scenarios.md](../../docs/scenarios.md) |

`LE<n> is not running` means the game process is absent. `Pipe MeleBridge_LE<n> not found` means the ASI is not
installed, or the game is still starting.

mele-mcp pings before its first request to a game process. It refuses a bridge with another protocol number, so build
the bridge and the server from the same commit.

## Pipe ops

The pipe takes newline-delimited JSON: one request line in, one reply line out. The arguments, replies, value types,
event stream and version check are in section 3 of [formats.md](../../docs/formats.md).

```json
{"op": "ping"}
{"bridge":"5","map":"BIOA_NOR00","objects":512345,"ok":true,"protocol":1}
```

| Op | What it does | Waits for the game thread |
|---|---|---|
| `ping` | Returns the bridge version, pipe protocol, map name and object count. | yes |
| `console` | Runs a console command. | yes |
| `find` | Lists objects by class, name or path. | yes |
| `get` | Reads one property, or every property of an object. | yes |
| `set` | Writes one property and reads it back. | yes |
| `call` | Calls a function, opcode natives included. Latent and iterator functions are refused. | yes |
| `describe` | Lists a class's chain, properties and functions. | yes |
| `subscribe` | Turns the connection into an event stream for the functions named. | yes |
| `status` | One look at the game for tests: map, world, player, streaming, loading movie, squad screen. | yes |
| `keep_ticking` | Reads or sets a test switch that stops the focus-loss pause. | only to turn it on |
| `editor` | Returns the in-game editor's state. LE1 only. | no |

A request that waits for the game thread times out on a load screen, or in a menu that runs no script. The default
wait is 5 seconds.

### Keep ticking

Each game pauses its world and Kismet when its window loses focus, so a test driven from another window stalls.
`keep_ticking` stops that pause while it is on. It is off at every game start and is meant for tests only.

- LE1 raises `m_fNoBrowserWheelTimer` around each `BioWorldInfo.Tick` that would run it out.
- LE2 and LE3 have no such timer. The switch holds `m_bAllowBrowserWheel` off for the length of each `Tick`. It
  refuses to write when the flags' offsets from reflection differ from the SDK's.
- On all three, requests wait until that `Tick` is over, so a request never sees the changed value.
- The switch only ends the pause that sets `m_bPausedByFocusLoss`. Menus, the squad screen, photo mode and F8 still
  pause.

## How it works

- Requests run on the game thread, inside a `UObject::ProcessEvent` hook, before the original call. A thread-local
  flag stops one request from starting inside another, because requests call `ProcessEvent` themselves.
- A request that has not started when its timeout ends never runs. One that has started runs to the end. If it is
  still running 1.5 s after the timeout, the reply says so (`"running": true`) and the result that comes later is
  dropped.
- An access violation, in-page error, integer division by zero or integer overflow inside a request fails that request
  and leaves the game running. The work runs inside `__try`. A stack overflow still ends the game.
- The loader makes a hook live before it hands back the original function. A call that arrives in that gap waits for
  the pointer and does not call address 0.
- `Reflection.cpp` converts values itself, using the layouts the engine's reflection reports. Strings and arrays live
  on the game's heap (`GMalloc`).

## Findings

These facts matter if you write your own ASI or drive the pipe by hand.

- A `subscribe` stream sees only calls that go through `ProcessEvent`. Script-to-script calls and natives called from
  script never show up. Nor do calls made while a request runs (a `call`'s own function, `status`'s reads) or the F8
  editor's: the bridge would otherwise stream its own reads as the game's.
- A `stringref` is its TLK id as a plain int, read and written like an `int` (`12345`, not `$12345`).
- The SDK's generated function wrappers run `FunctionFlags |= ~0x400`, which leaves every flag bit set. LEX Interop
  calls `ConsoleCommand` through one, so that function's flags read `0xFFFFFFFF`. So `call` also checks a list of
  latent and iterator names, and restores `iNative` and the flags after its own calls.
- `ConsoleCommand` returns an empty string, and with DebugLogger installed the printed lines go only to its log. So
  `live_console` sends the command, then a flush command that logs an error, and returns the log lines in between.
- The games' executables keep their code encrypted on disk, so they can't be disassembled statically. mele-mcp's
  `exe_dump` reads the code from a running game.
- LE1's plot table is one `BioGlobalVariableTable`, a subobject of `BioWorldInfo`'s defaults. Its `GetBool`, `SetBool`
  and the Int and Float versions work through `call`, which is how `game_plot` uses it.
- Some LExSDKv2 classes do not match the engine's memory layout on LE1. The SDK's `FRwVector3` is 12 bytes in C++ and
  16 in the engine, so later fields of `UParticleSystem` are 8 bytes off, and `UParticleSystemComponent::Template` and
  `bAutoActivate` are off too. MeleBridge reads those at the offsets reflection gives (`PropertyOffset` in
  `src/Reflection.cpp`). Check a field's offset against reflection before you read it past an `FRwVector3`.

## License

GPL-3.0, like the rest of this repo (`LICENSE` at its root). Third-party code and how it is used:
[THIRD_PARTY.md](THIRD_PARTY.md).
