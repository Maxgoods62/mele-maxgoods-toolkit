# mele-bridge

`MeleBridge.asi`, our own ASI for Mass Effect 1 Legendary Edition: a request/reply pipe into the running game. It reads and
writes any property of one live object, calls UnrealScript functions, runs console commands and streams events. mele-mcp's
`live_*` tools and `game_plot` use it.

It does what the LEX Interop ASI can't. LEX Interop is one-way: replies go to a window titled "Legendary Explorer", it
only handles whole classes (`getall`), and commands queued together run in reverse order.

## Build and install

```bash
./build.sh              # build, stage in D:\MELE\tools\mele-bridge
./build.sh --install    # also copy it to Game\ME1\Binaries\Win64\ASI (the game must be closed)
./build.sh --uninstall  # remove it from there (the game must be closed)
```

- **Toolchain:** Visual Studio Build Tools 2026 (MSVC, CMake and Ninja ship with it). Nothing else to install.
- **Where it builds:** inside a checkout of [ME3Tweaks/LExASIs](https://github.com/ME3Tweaks/LExASIs) at
  `D:\MELE\asi-build\LExASIs`, pinned to `aaad03a`. The script clones it if missing.
  - The checkout provides the SDK (LExSDKv2), the ASI loader interface (SPI), spdlog and the `asi_target` CMake helper.
  - `src/` is copied in as one more project and built LE1-only, with that tree's flags (`/W4 /WX`).
- **Install rule:** `MeleBridge.asi` in the ASI folder is the only file this repo may write inside the game install. It is
  written only while `MassEffect1.exe` is closed. The game check counts a failed `tasklist` as "running".
- **Log:** `Binaries\Win64\Logs\MeleBridge.log`, rewritten at each start.

## In-game editor (test build)

F8 in the game opens a level editor drawn over the game: free cam, click a prop to select it, drag the red, green and blue
arrows to move it. It is Phase 0 of [docs/07](../../docs/07-ingame-editor.md): a test build that checks the hooks, the input
capture and the arrows line up with the game. Props, Save and anything kept after a map reload come in later phases.

- **Threads:** the game thread does everything that touches game objects (`Editor.cpp`, after `BioHUD.PostRender` and
  `Camera.UpdateCamera`). The render thread draws [Dear ImGui](https://github.com/ocornut/imgui) and
  [ImGuizmo](https://github.com/CedricGuillemet/ImGuizmo) from a snapshot, in a hooked `IDXGISwapChain::Present`
  (`Overlay.cpp`). The window thread queues mouse and key messages for ImGui (`Input.cpp`).
- **Input:** while the editor is open the game gets no mouse or keys: the window's messages are swallowed, the DirectInput
  mouse reads zero, and the game's `SetCursorPos`/`ClipCursor` calls are blocked. F8 and Alt+F4 always work.
- **Pause:** `WorldInfo.bPlayersOnly`, as photo mode does. Closing gives the old value back.
- **State:** the `editor` op (`live_editor` in mele-mcp) returns open, camera, selection, the projection check and counters.
- **Tested 2026-10-27** in the freighter: all seven steps of [docs/07 §7](../../docs/07-ingame-editor.md#7-phase-0-the-test-build)
  pass; the arrows sit within 0.6 px of where the engine projects the prop.

## Protocol

`\\.\pipe\MeleBridge_LE1` takes newline-delimited JSON. Send one request line, get one reply line. Many clients can connect
at once, and remote clients are refused.

- **Replies:** `{"ok":true,...}` or `{"ok":false,"error":"..."}`.
- **Optional fields:** `id` is echoed back. `timeoutMs` (default 5000) is how long to wait for the game thread.

| op | fields | reply |
|---|---|---|
| `ping` | | `bridge`, `map`, `objects` |
| `find` | `class` (subclasses too, any package), `name`, `path` (substrings), `defaults`, `limit` | `total`, `objects[{path, class}]` |
| `get` | `object` (path), `property` (`Location`, `Location.X`, `Arr[2]`, `S.Arr[0].M`; omit for all), `depth` | `value` |
| `set` | `object`, `property`, `value` (JSON) | `value` (new) |
| `call` | `object`, `function`, `args` (JSON list, declaration order) | `return`, `out{}` |
| `describe` | `object` or `class`, `functions` | `class`, `chain`, `properties[{name,type,owner}]`, `functions[{name,owner,signature}]` |
| `console` | `command` | nothing: LE1 returns no output (see below) |
| `subscribe` | `functions` (paths, `Class.Function` matches any package), `objectContains`, `params` | `functions`, then the connection streams events |

### Values

| Type | As JSON |
|---|---|
| Bool, int, float | JSON values |
| Byte with an enum | The enum name (a number works too) |
| Name, string | Strings |
| Object | Its path, or `null` |
| Struct | An object of its members |
| Array | A list, at most 64 items. To set one, it must keep its length, or set elements one at a time with `Arr[i]` |
| Delegate | `Object.Function` (read only) |

### Events

After `subscribe`, each watched `ProcessEvent` call arrives as one line:
`{"t","function","object","class","params"}`, plus `pin` when the object is a Kismet op.

- **Heartbeat:** when nothing happens, `{"heartbeat":true}` comes every 2 s.
- **Overflow:** past 10000 waiting lines, a line `{"dropped":n}` reports what was lost.
- **Overrides match:** `ProcessEvent` receives the most derived override, so a watch matches by function name on any
  object of the declaring class or a subclass. For example, `SequenceOp.Activated` catches `SeqAct_Log.Activated`.
- **What never shows up:** script-to-script calls, and natives called from script. Neither goes through `ProcessEvent`.

## How it works

- **Game thread:** requests run on the game thread, inside a `UObject::ProcessEvent` hook, before the original call. That's
  also where LEX Interop runs its commands.
  - A thread-local flag stops a request from starting inside another one, because requests call `ProcessEvent` themselves.
  - A request that hasn't started when its timeout ends never runs.
- **Hook race:** the loader makes a hook live before it hands back the original function. A call that arrives in that gap
  waits for the pointer instead of calling address 0. That race is the likely cause of a crash seen once at boot in another
  ASI.
- **Crash guard:** an access violation inside a request (a stale path, a bad guess) fails that request instead of crashing
  the game: `__try` around the work.
- **Values:** the SDK maps memory layouts (property offsets, bit masks, enum names), but not the engine's
  `ImportText`/`ExportText` virtuals, so `Reflection.cpp` converts values itself. Strings and arrays live on the game's
  heap (`GMalloc`).

## Findings

- **Flags corrupted by generated wrappers.** The SDK's generated function wrappers run `FunctionFlags |= ~0x400`, which
  leaves every flag bit set. LEX Interop calls `ConsoleCommand` through one, so that function's flags read `0xFFFFFFFF`.
  `call` skips its latent/iterator check when the flags look like that.
- **No console output in LE1.**
  - `ConsoleCommand` returns an empty string.
  - `getall` writes to a global log device, not to the `Ar` it's given.
  - DebugLogger's `Logf` hooks never call the original function.

  So `live_console` reads the output from the DebugLogger log. It sends the command, then a `getall` (logged as an error,
  which makes the logger flush), and returns the lines in between.
- **Encrypted executable.** `MassEffect1.exe` is encrypted on disk, so it can't be disassembled statically. Its code exists
  only in memory.
- **Plot table.** It's a single object: `SFXGame.Default__BioWorldInfo.BioGlobalVars0`. Its `GetBool`/`SetBool` (and the
  Int and Float versions) work through `call`. `game_plot` uses them.

## License

GPL-3.0 ([`LICENSE`](../../LICENSE)). Third-party code and what it is used for: [`THIRD_PARTY.md`](THIRD_PARTY.md).
