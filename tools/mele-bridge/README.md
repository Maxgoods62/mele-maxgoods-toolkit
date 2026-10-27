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

## In-game editor

F8 in the game opens a level editor drawn over the game: free cam, click a prop to select it, drag the red, green and blue
arrows to move it (T) or the ring to turn it (R). Del deletes the selection, Ctrl+S saves. **Props** spawns a prop where
the view points, from one of two tabs:

- **Catalog:** a grid of thumbnails of the curated props mele-mcp's `catalog_build` writes, with a category list and a
  search. A tooltip gives the size, triangles, how often the game's levels place it and the file it comes from.
- **In this level:** the meshes in memory, as a list (Phase 1's).

It is Phase 2 of [docs/07](../../docs/07-ingame-editor.md); undo and keeping edits across a map reload come in later phases.

- **Threads:** the game thread does everything that touches game objects (`Editor.cpp`, after `BioHUD.PostRender` and
  `Camera.UpdateCamera`). The render thread draws [Dear ImGui](https://github.com/ocornut/imgui) and
  [ImGuizmo](https://github.com/CedricGuillemet/ImGuizmo) from a snapshot, in a hooked `IDXGISwapChain::Present`
  (`Overlay.cpp`). The window thread queues mouse and key messages for ImGui (`Input.cpp`).
- **Input:** while the editor is open the game gets no mouse or keys: the window's messages are swallowed, the DirectInput
  mouse reads zero, and the game's `SetCursorPos`/`ClipCursor` calls are blocked. F8 and Alt+F4 always work.
- **Pause:** `WorldInfo.bPlayersOnly`, as photo mode does. Closing gives the old value back.
  - A spawned prop's `DynamicLightEnvironment` only updates when the world ticks: paused, it would render black. After a
    spawn, and when an arrow or ring drag ends, the editor lets the world tick once and takes the pause back at its next
    tick. That release is its own: it isn't logged or counted as the game ending the pause.
- **Save:** one session file per map, `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json`, outside the install. It holds the
  latest state of each actor spawned, moved, turned or deleted: spawned ones by tag, level actors by path. mele-mcp's
  `editor_import` writes it into the level YAML and builds it.
- **State:** the `editor` op (`live_editor` in mele-mcp) returns open, camera, selection, edits, the status line, the
  projection check, the catalog (props read, thumbnails on the GPU) and counters.
- **Tested 2026-10-27** in the freighter: Phase 0's seven steps ([docs/07 §7](../../docs/07-ingame-editor.md#7-phase-0-the-test-build)),
  the arrows within 0.6 px of where the engine projects the prop; Phase 1's five ([§8](../../docs/07-ingame-editor.md#8-phase-1-the-first-usable-editor)),
  a crate spawned and one deleted in game, both shipped by build + install. The Catalog tab is not tested in game yet.

### The prop catalog

mele-mcp's `catalog_build` writes two things, both outside the game install:

- `%LOCALAPPDATA%\MeleBridge\LE1\catalog\catalog.json` and its `thumbs\` (128x128 PNGs, transparent background);
- `MeleCatalog_Core.pcc`, the meshes in a group per category, in a dev mod of its own, **Mele Catalog**
  (`DLC_MOD_MeleCatalog` in the M3 library). `mod_install` installs it with the game closed; M3's AutoTOC lists the file,
  so the game finds it by name.

`catalog.json` (UTF-8, version 1):

```json
{ "version": 1, "built": "2026-10-27T03:00:00Z", "package": "MeleCatalog_Core",
  "categories": ["Crates", "Furniture", "Medical", "Consoles", "Lights", "Industrial", "Debris", "Alien", "Small items", "Signs", "Plants", "Other"],
  "entries": [ { "name": "Crate_XLarge_03", "category": "Crates",
                 "mesh": "biog_apl_phy_crate_xlarge_01_l.Crate_XLarge_03", "source": "BIOA_STA30_02_DSG",
                 "load": "MeleCatalog_Core.Crates.Crate_XLarge_03", "thumb": "thumbs/Crates_Crate_XLarge_03.png",
                 "size": [78, 168, 170], "tris": 1290, "placed": 264, "large": false } ] }
```

| Field | What the bridge does with it |
|---|---|
| `mesh` | The vanilla path, also the path in memory when a level has the mesh: used as is when it is there, and written to the session as `mesh.path`. |
| `load` | When no level has the mesh: `Core.Object.DynamicLoadObject(load, StaticMesh, MayFail)`. That reads the one mesh and its materials from the catalog package, not the rest of the file. |
| `source` | A vanilla file with the mesh as an export, written to the session as `mesh.file`: `editor_import` and `level_build` port the vanilla mesh, never the catalog's copy. |
| `thumb` | Relative to the catalog folder; `null` for none. A path that is absolute or leaves the folder is ignored. |
| `size`, `tris`, `placed`, `large` | Shown in the tooltip. |

- **Reading:** in the background, the first time the Catalog tab shows and again on **Reload**. A missing or unreadable
  file shows in the tab, and the In this level tab still works. An entry without a name or a mesh is skipped; a category
  the list doesn't name is added at its end.
- **Thumbnails:** decoded with WIC (part of Windows) into D3D11 textures on the render thread, only for cells on screen and
  at most four a frame. They are released when the editor closes or the catalog is read again.
- **Loading and garbage collection:** a loaded mesh that no actor shows is collected within about a minute, so the load,
  `Actor.Spawn` and `SetStaticMesh` happen in the same game-thread tick. A failed load (the mod is not installed, or the
  game was not restarted since) is reported on the status line, and so is the time a load took.
- **In this level:** a catalog mesh loaded earlier shows there under its `MeleCatalog_Core` path; a click on it spawns it as
  its catalog entry, so the session still names the vanilla mesh and file. One that no entry of the catalog as last read
  loads (it was rebuilt, or can't be read, since the copy loaded) is refused: spawn the prop from the Catalog tab.

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
