# In-game editor: design

The in-game editor is a level editor for Mass Effect 1 Legendary Edition (LE1) that runs inside the game. `MeleBridge.asi`
draws it over the game when you press F8: the world pauses, a free camera flies, and you spawn, move, turn, scale and hide
props, edit lights and move or hide particle effects. Save writes a session file outside the game install. mele-mcp's
`editor_import` merges that file into a level YAML, and `level_build` ships it in the mod. This document says how each part
works, why it was built that way, and what it can't do. Section numbers are stable: code comments cite them (`§10`, `§12.4`),
so a section is never renumbered. The user guide (keys, tabs, status lines) is the bridge's
[README](../../tools/mele-bridge/README.md). The files and the pipe protocol the two tools share are in
[formats.md](../formats.md).

**Example names.** The examples come from the test mod the editor was built and tested with. Its map `BIOA_CLD01` streams
the vanilla freighter level `BIOA_FRE31_00_LAY`, and has an NPC `dorn` (tag `claude_npc_02`). Its level `ClaudeNPC_NOR10`,
sideloaded into the Normandy's medbay, has an NPC `reyes` (tag `claude_npc_01`) with two lights. Use your own map, level and
tag names in their place. Every other name (`BIOA_NOR10_07_DSG`, `StaticLightCollectionActor_127`, `PointLight_35_LC`) is the
game's or LE1CP's.

**Terms.**

| Term | Meaning |
|---|---|
| ours | A level `level_build` built from one of the mod's YAML files (a `levels.json` key), its actors, and the editor's own spawns. |
| vanilla actor | Any other actor of a level file. |
| fork | A renamed copy of a vanilla level file, with the edits written in, that a map of ours streams instead of the file (§11). |
| session | The editor's saved work on one map: `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json` ([formats.md](../formats.md) §5). |
| pass | A re-apply pass: the bridge puts a session's entries back on their actors after a load (§10). |
| prop light | A point light on the `Gameplay_4` channel only. It lights our props and nothing else (§11.1). |
| room light | A point light of ours on the base channels and `Gameplay_4`: walls, vanilla meshes, pawns and props (§11.2). |
| NPC light | A light that goes with an NPC of ours, stored as an offset from her pose (§12.3). |
| level light | A point or spot light component of a level's `StaticLightCollectionActor` (an SLCA, §12.4). |
| kept writer | The light writer that changes only the fields asked for and never channels, shadows or level filter (§12.1). |
| Phase N | The build step that added a part (§4). Code comments name them: "Phase 3's rules", "a Phase 4 bridge". |
| S0–S5 | The checks that settled Phase 5's engine questions (§12.6). |

## 1. What it does

- **F8** opens the editor: a toolbar, a free mouse cursor, the world paused. F8 again closes it.
- **Free cam:** hold the right mouse button to look; WASD to fly, Q/E down and up, Shift faster, the wheel sets the speed.
- **Props:** a grid of thumbnails (the catalog) or the meshes in memory. A click spawns the prop where the view points.
- **Select and edit:** click a prop. **T** moves it (arrows), **R** turns it (rings), **G** scales it (handles). Ctrl snaps.
  Del hides it, Ctrl+Z and Ctrl+Y undo and redo, Ctrl+S saves.
- **Lights:** add prop lights and room lights, edit our NPCs' lights, edit the lights of the levels' light collections.
- **Effects:** move, turn and hide the particle effects the levels place.
- **Vanilla props** of a file a map of ours streams: moved, turned, scaled or hidden, then shipped in a fork of that file.

The loop from an edit to a shipped mod:

| Step | Where | What happens |
|---|---|---|
| Edit | game, F8 | The edit changes the live actor and is kept in memory, with an undo step. |
| Save | game, Ctrl+S | The latest state of each edited actor goes into the map's session file. |
| Reload | game | A pass puts the session back on every load, so the work survives without a rebuild. |
| Import | `editor_import` | The session is merged into the level YAML, three-way per field; its entries are marked imported. |
| Build | `level_build` (or `mod_build`) | The level, its forks and `levels.json` are written in the mod. |
| Install | `mod_install`, game closed | The mod reaches the game. |
| Load | game | The pass drops each entry the installed level already shows. |

The game install is never written. Edits reach the game only through the mod.

## 2. Prior art and libraries

| Tool | What it is | What the editor takes from it |
|---|---|---|
| LEX Live Level Editor + LEXInterop ASI | A WPF window that remote-controls an ASI: pick an actor from a list, type numbers. No free cam, picking, spawning or drag. | The `iNative` trick for opcode natives (§3.1); the `BioHUD.PostRender` hook. |
| [SuperAdjustmentStudio](https://github.com/Reyuu/SuperAdjustmentStudio) | An LE ASI with a Dear ImGui overlay, a photo-mode free cam, a DirectInput8 mouse trap, click to select, spawning. No gizmo drag, thumbnails or level save. | The DirectInput8 trap's approach; the `bNoDelete` flip for spawning lights. No code copied. |
| [BmEditor](https://github.com/etkramer/BmEditor) | UE3's 2011 editor source, modified for another game. | Reading only: `ProcessEvent`, `Canvas.Project`, light components, light environments. |
| [BLMapEditor](https://github.com/juso40/BLMapEditor) | A Borderlands 2 in-game map editor: per-map JSON replayed at load. | The save model (§3.7). |
| [Dear ImGui](https://github.com/ocornut/imgui) v1.93.0, [ImGuizmo](https://github.com/CedricGuillemet/ImGuizmo) | The UI and the move/turn/scale gizmo. | Compiled in (MIT). |

LE1 facts that shape the design:

- Vanilla props are separate `StaticMeshActor`s, `bStatic`, with baked lightmaps, named `<file>.<actor>`. LE1 levels have no
  `StaticMeshCollectionActor`.
- LE1 is seekfree: a mesh lives as a forced export inside each level file that uses it. There are no prop-only packages.
- A level file hot-loaded into the running game is reverted by LE1CP within seconds. A rebuilt level reaches the game only
  through an install with the game closed. So the editor applies its edits live and keeps them in a session.
- `BioHUD.PostRender`, `Camera.UpdateCamera` and `PlayerController.PlayerTick` reach `ProcessEvent` once per frame and keep
  firing while `WorldInfo.bPlayersOnly` is set.

## 3. Architecture

```
 game thread (ProcessEvent hook)                 render thread (IDXGISwapChain::Present hook)
 ─────────────────────────────────              ─────────────────────────────────────────────
 Camera.UpdateCamera  ── post ──► free cam:      ImGui frame:
                                  write POV        toolbar, Props, Lights, Effects, Selection, Diagnostics
 BioHUD.PostRender    ── post ──► editor tick:     ImGuizmo on the selection
                                  run commands     clicks → Pick, drags → Move / Rotate / Scale
                                  pick, passes                  │
                                  publish snapshot ─────────────┘ (camera, viewport, selection, lists)
                                         ▲ commands
 window procedure (game's main thread): mouse buttons, wheel, keys → input queue for ImGui; swallowed while open
 DirectInput8 GetDeviceState/GetDeviceData: zeroed while the editor is open (the game reads the mouse there)
```

- **One rule:** only the game thread touches game objects (`Editor.cpp`). The render thread (`Overlay.cpp`) reads a snapshot
  and posts commands. The window procedure (`Input.cpp`) queues input.
- **Fault guard:** the game-thread work runs under `__try`. An access violation closes the editor, gives the world its own
  pause back and logs the faulting frames. The undo step under way is closed from the edits' stored state, never from game
  memory.
- **Files stay outside the install:** sessions, `levels.json`, the catalog and its thumbnails live in
  `%LOCALAPPDATA%\MeleBridge\LE1\`. ImGui's `imgui.ini` is off: it would land next to the game's executable.

### 3.1 Calling opcode natives

UE3's `ProcessEvent` does nothing for a function whose `iNative` (its opcode number) is not 0. That is why `SetLocation`,
`SetRotation`, `Destroy` and `Trace` did nothing when called from outside script. The fix every UE3 SDK uses: save `iNative`,
set it to 0, call `ProcessEvent`, restore it. `ProcessEvent` then calls the native with normal parameters.

- The helper (`CallProcessEvent`, `Reflection.cpp`) restores `iNative` and `FunctionFlags` exactly, in a `__finally`.
- The SDK's generated function wrappers run `FunctionFlags |= ~0x400`, which leaves every flag bit set. They are never called.
- Latent and iterator natives are refused. Flags can't be trusted (another ASI may have set every bit), so a list of names
  backs the flag check: `Sleep`, `FinishAnim`, `MoveTo`, `AllActors`, `TraceActors` and the like.

### 3.2 Free camera

- **Pause:** `WorldInfo.bPlayersOnly`, as photo mode does, set again at every tick while the editor is open (the game clears
  it on focus changes). Closing gives the old value back.
- **Camera:** after `Camera.UpdateCamera` returns on the player's camera, the editor overwrites `CameraCache.POV` (location,
  rotation, FOV). The rendered view, culling and LOD read that cache, so they follow.
- **Not the game's fly cams:** in the FreeCam camera style mouse look can't be gated, and `DebugCameraController` replaces
  the player's controller, which stops `BioHUD` and the editor's hook with it.

### 3.3 Overlay and input

- **Overlay:** Dear ImGui and ImGuizmo, drawn from hooked `IDXGISwapChain::Present`, `Present1` and `ResizeBuffers`. Their
  addresses come from a dummy D3D11 device's vtable; the hooks go through the ASI loader's `InstallHook`, which chains with
  other hooks.
- **Input:** a window-procedure subclass queues mouse buttons, the wheel, keys and characters for ImGui and swallows them
  while the editor is open. The game also reads the mouse through buffered DirectInput (`GetDeviceData`), so both
  `GetDeviceState` and `GetDeviceData` return nothing while the editor is open. A trap on `GetDeviceState` alone, as most
  overlays have, would let the camera turn behind the editor.
- **Cursor:** the game calls `SetCursorPos` and `ClipCursor` at every frame. Those calls are blocked while the editor is
  open, or the cursor would snap to the centre. ImGui draws its own cursor from `GetCursorPos`.
- **Threads:** `BioHUD.PostRender`, `Camera.UpdateCamera` and the window procedure run on one thread (UE3 pumps messages on
  its game thread). `Present` runs on its own.

### 3.4 Picking and the gizmo

- **Pick:** in `BioHUD.PostRender`, while `Canvas.SceneView` is valid: the mouse position scaled to the canvas →
  `Canvas.DeProject` → `Actor.Trace` (through the §3.1 helper) → the hit actor. For meshes without collision, a ray test
  against the visible primitives' bounds, nearest first. It skips boxes that hold the camera, room-sized boxes and particle
  systems (§12.2).
- **Gizmo:** `ImGuizmo::Manipulate` with matrices rebuilt from the same frame's `CameraCache.POV`:
  - view = translate(−location) · inverse(rotation) · axis swap (UE3: X forward, Y right, Z up → x right, y up, z depth);
  - projection: left-handed perspective with a horizontal FOV, `m00 = 1/tan(FOV/2)`, `m11 = m00 · width/height`, near 10;
  - UE3's row-vector `FMatrix` layout is ImGuizmo's.
- **Moves:** the game thread writes `Location` (and `Rotation`, `DrawScale3D`) raw, then calls `ForceUpdateComponents`.
  `SetLocation` is not used: for an actor that blocks others it goes through `FarMoveActor`, which refuses.
- **Calibration:** the editor projects the selection itself and compares with `Canvas.Project` of the same point. Measured:
  0.1–0.2 px, 0.6 px at worst while flying.

### 3.5 Prop catalog

- **Index:** `catalog_index` (mele-mcp, read-only) scans the export tables of every file the game loads (LE1CP's copy of a
  file wins) and lists each StaticMesh once by path: size, triangles, how often levels place it, and the smallest file that
  has it. LE1 has 5,226 unique StaticMeshes; a scan takes about 3 s on 16 threads. Copies of a mesh in several files are
  identical.
- **Default list:** name rules (`CatalogRules.cs`) sort meshes into Crates, Furniture, Medical, Consoles, Lights,
  Industrial, Debris, Alien, Small items, Signs, Plants and Other. The `biog_apl_*` prop library is always in. Elsewhere a
  mesh over 600 units, a flat one, one with an architecture word and no prop noun, a helper, or one of an effects, UI or sky
  package is left out. About 350 remain. Search reaches every mesh.
- **Package:** `catalog_build` ports the chosen meshes into `MeleCatalog_Core.pcc`, each under a group named after its
  category, so its path is `MeleCatalog_Core.<Group>.<Name>` (the shape of vanilla `BIOG_*_R` files). Materials and
  textures stay at their vanilla paths, so a copy already in memory is reused. The package lives in a dev mod of its own,
  **Mele Catalog** (`DLC_MOD_MeleCatalog`), so a shipped mod never carries it. M3's AutoTOC lists the file, so the game finds
  it by name.
- **Loading:** `Core.Object.DynamicLoadObject` by the catalog path loads one mesh and its materials, not the whole file, with
  no measurable hitch. Unreferenced, a loaded mesh is purged by garbage collection within about a minute, so load, spawn and
  set the mesh happen in one game-thread job.
- **Thumbnails:** rendered offline, 128×128 PNGs: a CPU rasterizer over LOD0 with the diffuse texture picked by name, the
  material instance's colour parameters and opacity mask, and a Lambert light. About 6 ms a mesh. The overlay decodes them
  with WIC (part of Windows), at most four a frame, only for cells on screen.
- **Contract:** `catalog.json` ([formats.md](../formats.md) §6). An entry names the vanilla mesh and a vanilla file with it,
  so the session, `editor_import` and `level_build` port the vanilla mesh, never the catalog's copy.

### 3.6 Spawning

- **Props:** `Actor.Spawn` of `Engine.DynamicSMActor_Spawnable` on the player controller (so it lands in the map's level),
  then `DynamicSMActor.SetStaticMesh`. Its own `DynamicLightEnvironment` lights it where it stands. The mesh comes from
  memory when a level has it, else `DynamicLoadObject` of its catalog path.
- **Lit while paused:** a light environment only computes while the world ticks, so a prop spawned in the paused editor
  would render black. After a spawn the editor releases the pause for one world tick (`TickOnce`) and takes it back at its
  next tick. Phase 4 then switches the prop to prop lights when one covers it (§11.1).
- **Tags:** `ed_<mesh>_<run>_<n>`, where `<run>` is a short stamp of the game run, so tags never repeat across runs.
- **Lights:** `PointLightMovable` inherits `bNoDelete` from `Light`, which `Spawn` refuses. The class default's bit is
  cleared for the call and put back in a `__finally` (§11.2).

### 3.7 Save and reload

- **Save (Ctrl+S)** writes one state-based session file per **persistent map** (the game's `GWorld`), not per level file: a
  level the game loads into another map (sideloaded, as on the Normandy) is saved in that map's file. One entry per actor:
  spawns by tag, level actors by path (the vanilla path for an actor in a fork). The entry holds the actor's latest state,
  so `editor_import` never replays history.
- **Reload, no rebuild:** on each load, and when the loaded levels change, a pass puts the session's entries back (§10).
- **Ship:** `editor_import` merges the session into the level YAML with `YamlEdits` (three-way per field, refusing on
  conflict by default) and runs `level_build`. It marks the entries it applied (`base` and `imported`); the bridge drops them
  once the installed level shows them. A marked entry never overrides a later YAML edit.
- **YAML:** props take `rotation: [pitch, yaw, roll]`, `scale`, `scale3d`, `mesh: {package, export}` and `removed: true`;
  levels take `lights:` and `vanilla:` sections, NPCs a `lights` list. Vanilla edits ship only as a fork; in maps that are not
  ours they stay dev-only (the bridge re-applies them, nothing ships them).
- **Undo:** an in-memory stack in the bridge. An undo is a new state of the entry, never a history to replay.

## 4. Phases

| Phase | What it added | Status |
|---|---|---|
| **0. Test build** (§7) | Native helper, hook dispatch, overlay and input, free cam, click to pick, move gizmo, diagnostics. | Done, tested in game. |
| **1. First usable editor** (§8) | Spawn meshes in memory, turn, delete, the session file and `editor_import`. | Done, tested: a placed prop survives build and install. |
| **2. Catalog and thumbnails** (§9) | `catalog_index`, `catalog_build`, the catalog package and the thumbnail grid. | Done, tested: any curated prop spawns on the Normandy. |
| **3. Save loop** (§10) | Re-apply on load, three-way import, full transforms, undo/redo, the turn and scale gizmos, delete hides. | Done, tested: edit, save, reload, import, build, install, and a conflict refused. |
| **4. Vanilla actors and lights** (§11) | Forks, prop lights and the switch, Auto light, the Lights tab, `levels.json`. | Done, tested: a vanilla crate moved in a fork ships; a placed prop reads as bright as the vanilla mesh beside it. |
| **5a. Effects and NPC lights** (§12) | Effects, the Kismet set, light kinds by source, the kept writer, NPC lights, session version 3. | Done, tested: an effect moved and one hidden, a light added to an NPC and another's changed, all shipped. |
| **5b. Level lights** (§12.4) | The lights of the levels' static light collections, in game and in forks. | Built; its in-game test is pending. |
| **5c. New effects** (§12.5) | Spawning effects from a list. | Not built. |

## 5. Decisions

| Decision | Why |
|---|---|
| GPL-3.0 | mele-mcp links LegendaryExplorerCore (GPL-3.0); one license for the toolkit. |
| Everything in `MeleBridge.asi` | One ASI to install; no second window or process. |
| Dear ImGui + ImGuizmo, not Canvas or Scaleform | Only an overlay shows PNG thumbnails and gives a ready-made gizmo. |
| Our own camera, not the game's fly cams | The FreeCam style can't gate mouse look; `DebugCameraController` stops `BioHUD`. |
| F8 opens the editor | F1–F5 and F9 are bound in `BIOInput.ini` (F9 is quick load). |
| Session file outside the install; the YAML stays the source of truth | Nothing writes the game install; a hot-loaded level is reverted by LE1CP. |
| The catalog in its own dev mod, loaded on demand | The shipped mod stays clean; `DynamicLoadObject` loads one mesh by name, so no global package or rooting. Built levels port the vanilla mesh. |
| Marking imported entries, not retiring them | An entry stays until the installed level shows it, so an import before an install loses nothing (§10). |
| Prop lights on `Gameplay_4`, switched on only when one covers the prop | A placed prop's light environment reads 25–40× too bright in the freighter; a prop with its environment off and no light would be black (§11.1). |
| Delete hides, never destroys; a light is switched off | Undo writes the old state back to the same actor; the build ships `removed: true` or `enabled: false`. |
| Light kinds come from the light's source, never its channels | Channels can't tell an NPC's or a level's light from a room light (§12.1). |
| NPC lights stored as offsets from the NPC's built pose; edited only while the pawn stands there | The build places them from the pose, so a move carries them; the game never moves our NPCs (S5), so the pawn is the check (§12.3). |
| Effects killed, restarted and run 1.5 s after each step | Without the kill old particles stay where the effect was (S4); the editor's pause freezes them, so it releases its own pause (§12.2). |
| Session version 3 only with an `npc` or `vanilla` light | A Phase 4 bridge would write such a light with its own writer, channels and shadows included; it refuses a version 3 file instead. |
| SDK fields the engine lays out elsewhere read at reflected offsets, the rest pinned by `static_assert` | The SDK's `FRwVector3` is 12 bytes, the engine's 16 (§12.7). |
| Level lights moved by `SetTranslation`, refreshed by `ForceUpdate(false)`, shipped as the fork's `Translation` and fields | One light moves and refreshes alone (S1); the build's writes load as written (S3). |
| Baked level lights editable, for characters only | Their edits reach characters and props through light environments, never the walls (S2, S3). |
| After a baked or character level light's step: reset the light environments in reach, run the world 1.5 s | Pawn environments take a change only while the world ticks, refresh fully at most once a second and blend over 0.3 s (S2); whether one tick would do is untested. |
| Sun, sky and vanilla light actors left out | The sun is drawn per pixel and its edit would likely change the walls (not tested); no map of ours streams a vanilla light actor, so none could ship. |
| In-game checks that need ticks run with the game window focused | LE1 holds `bPlayersOnly` at every frame while its window has no focus. |

## 6. Limits and open risks

- **Unchecked:** whether spawned props or hidden vanilla actors end up in save games (test with a normal save, not a
  quicksave); AI navigation around placed props; gamepad input and the game's own menus while the overlay is open.
- **What a moved vanilla mesh keeps:** its own lightmap (lit as at its old spot), its shadow baked into the floor, its cover
  nodes and texture-streaming spheres at the old spot. The build warns about cover; nothing relights the mesh.
- **Live look vs built look:** a placed prop is a `DynamicSMActor_Spawnable` lit by a `PointLightMovable`; the build makes a
  `BioInert` copy lit by a `PointLightToggleable`. The built one read 6.7% brighter in the test, inside the ±10% band (§11.8).
- **The Normandy:** its master map is LE1CP's `BIOA_NOR00`, not ours, so vanilla edits there stay dev-only.
- **No test harness for the bridge's session code:** it is checked in game, and offline by running its functions against the
  real `editor_import`.

## 7. Phase 0: the test build

**Goal:** prove the risky parts in one build before writing the editor: opcode natives, overlay and input, the camera
override, picking, and the gizmo's alignment.

| Part | File | What it does |
|---|---|---|
| Native helper | `Reflection.cpp` | `live_call` runs opcode natives with `iNative` 0, flags restored exactly; latent and iterator natives refused by flag and by name (§3.1). |
| Hook dispatch | `GameThread.cpp` | `ProcessEvent` calls editor code after `BioHUD.PostRender` and after `Camera.UpdateCamera`. |
| Overlay | `Overlay.cpp` | `Present`/`Present1`/`ResizeBuffers` hooks, Dear ImGui + ImGuizmo, toolbar, diagnostics. |
| Input | `Input.cpp` | Window-procedure subclass with an input queue, the DirectInput8 trap, `SetCursorPos`/`ClipCursor` blocked while open. |
| Editor | `Editor.cpp` | Game-thread tick: pause, free cam, pick, move, snapshot, the projection check. |

**What it proved** (in the freighter, 1920×1080):

| Check | Result |
|---|---|
| Input capture | Mouse and keys no longer reach Shepard while the editor is open. |
| Free cam | Right-mouse look, WASD/QE fly, wheel speed; Shepard stays put. |
| Picking | Every click resolved by `Actor.Trace`, on vanilla meshes, a placed crate and an NPC. |
| Gizmo | The prop follows the arrow; the projection check agrees within 0.6 px. |
| Threads | `PostRender`, `UpdateCamera` and the window procedure on one thread; `Present` on its own. |
| Robustness | No crash through alt-tab, the Steam overlay and closing. |
| Opcode natives | `Trace` returns a real hit (component, normal, physical material); `SetLocation` runs and returns the engine's own `false` for a blocking actor; `Sleep` is refused as latent. |

**Findings:**

- **The game reads the mouse through buffered DirectInput:** `GetDeviceData` was zeroed thousands of times, `GetDeviceState`
  never.
- **The game moves the cursor at every frame** (`SetCursorPos`, `ClipCursor`): without the block, the cursor snaps to the
  centre.
- **`bPlayersOnly` is not only the editor's:** the game sets and clears it on focus changes. The editor sets it again at each
  tick while open and logs when the game took it.

## 8. Phase 1: the first usable editor

**Goal:** a prop placed in game survives build and install.

| Part | File | What it does |
|---|---|---|
| Props list | `Overlay.cpp`, `Editor.cpp` | The StaticMeshes in memory (engine and UI packages left out), with a search box. A click spawns one where the view points: `Canvas.DeProject` from the screen centre, a trace ahead, then down to the floor. |
| Spawn | `Editor.cpp` | `DynamicSMActor_Spawnable` + `SetStaticMesh` (§3.6), tagged `ed_<mesh>_<run>_<n>`, a tag no actor, edit or session entry has. |
| Turn | `Overlay.cpp` | R: a ring about the vertical. Ctrl snaps (10 units, 15°). |
| Delete | `Editor.cpp` | Del. Refused for pawns, controllers, `bStatic` and `bNoDelete` actors (Phase 3 made it a hide, §10). |
| Save | `Editor.cpp` | Ctrl+S: the map's session file, merged with the earlier one, written to a temporary file and moved over it. An unreadable file is kept as `<map>.json.bad-<tick>`. |
| Import | `EditorImport.cs` | `editor_import`: a spawned mesh becomes a `props` entry with `mesh`, a moved actor gets its location and yaw, a deleted prop `removed: true` (the build hides it, without collision), then `level_build`. |

**Findings:**

- **`mesh.file` is a hint, not the mesh's file:** the bridge names a level that shows the mesh, and that level may only import
  it. `editor_import` then takes the next file that has the mesh as an export. The bridge names another level before the
  persistent map, which may have the mesh only from an earlier build.
- **"New" is decided before an edit is appended:** the first Save wrote a file of nameless entries, one per drag frame,
  because a lookup's "not found" was compared after the list grew. Save now also refuses an empty map name.
- **A spawn deleted before its first save leaves nothing:** no YAML can have it.
- **Sideloaded levels:** on the Normandy the session is the persistent map's (`BIOA_NOR00`), while the YAML builds a
  sideloaded file. With no session of the level's own, `editor_import` takes the newest session with an actor of the level
  ([formats.md](../formats.md) §5.3), and puts spawned meshes only into
  the map's own YAML or the one given `takeSpawned: true`.
- **Edits are detached on a world change:** a new actor can reuse an old one's pointer and slot.

## 9. Phase 2: catalog and thumbnails

**Goal:** the menu shows thumbnails, and any curated prop spawns on the Normandy, where most props are not in memory.

**What the research and the spike found:**

| Question | Answer |
|---|---|
| How many props? | 17,326 StaticMesh exports, 5,226 unique; 145 in the `biog_apl_*` library; about 350 after the rules (§3.5). |
| Which copy of a mesh? | Copies are identical (same size, triangles and dependencies). The smallest file the game loads is taken. |
| A package the game loads by name? | An empty LE1 package from LEC, each mesh ported with `EntryExporter.ExportExportToPackage`, then moved under a group that is not a forced export. M3's AutoTOC lists every DLC file by bare name. |
| Loading one mesh from the ASI | `DynamicLoadObject("MeleCatalog_Core.<Group>.<Mesh>")` returns the mesh: one mesh and its own materials and textures, not the whole file, as fast as a `FindObject` that finds nothing. A wrong path returns null. Imports to other packages resolve. |
| Garbage collection | An unreferenced loaded mesh is purged within about a minute. Once an actor shows it, it stays. So load, spawn and set the mesh happen in one job. |
| Spawned while paused | Black: the actor never ticks, so its light environment never computes. One released world tick lights it (§3.6). |
| Thumbnail colour | A material instance can tint a mesh (a cargo container reads dark brown in game, green and orange from its texture alone). Thumbnails apply the instance's colour parameters. |

**Design:** `catalog_index` and `catalog_build` (§3.5) write `catalog.json`, its thumbnails and the catalog package. The
bridge's Props window gets a **Catalog** tab (category list, search, thumbnail grid, tooltip with size, triangles, how often
placed and the source file) next to Phase 1's **In this level** list. A click takes the mesh from memory when a level has it,
else from `DynamicLoadObject`; spawns and sets the mesh in the same job; then releases the pause for one tick. The session
records the vanilla mesh and file (`mesh.path`, `mesh.file`) and the catalog path (`mesh.load`).

**Finding:** placed props read wrong next to vanilla meshes: darker on the Normandy, much brighter in the freighter. Vanilla
meshes carry baked lighting; a placed prop is lit only by its light environment. That is a lighting task, solved in Phase 4
(§11.1).

## 10. Phase 3: the save loop

**Goal:** edit, Save, reload keeps the edit; import, build, install ships it, tilt and scale included.

**Session entry, version 2.** The full format is in [formats.md](../formats.md) §5. In short: an entry has `tag`, `path`,
`level`, `class`, `spawned`, `mesh` (spawns only), the state (`location`, `rotation` in Unreal units, `scale` = `DrawScale`,
`scale3d` = `DrawScale3D`, `deleted`) and three more states:

| Block | Written by | What it is |
|---|---|---|
| `base` | the bridge (first edit), `editor_import` (each import) | What the YAML holds for the actor, for the three-way merge: its state before the editor first changed it, or what an import wrote. `base.deleted` says whether the YAML removes it. `null` for a spawn never imported. |
| `imported` | `editor_import` only | `{ "before": <the base before this import> }`: what the installed level may still show until the next install. |
| `installed` | the bridge only | When Save writes a new state over an imported entry, its `imported.before` (and its own `installed`) go here: the install may still show any of them. |

A version 1 entry (the first editor builds) has no `base` and no scales. Both tools read it; neither writes one.

**Phase 3's rules.** Before Phase 4 every actor was edited under these rules, and an editor spawn, an actor of one of our
levels, and every actor while `levels.json` is missing still are:

- Move, Turn and Scale take any actor that is shown. R and G refuse a mirrored actor (a `DrawScale` or `DrawScale3D`
  component that is not positive): its matrix gives no rotation or scale back.
- Delete refuses pawns and controllers, `bStatic` actors (baked into the level's lighting: change them in the YAML) and
  `bNoDelete` actors.

Any other actor is a vanilla one, which Phase 4's gate decides on (§11.3).

**Bridge (`Editor.cpp`):**

| Part | What it does |
|---|---|
| Edit state | An edit stores everything a session entry has, plus the actor's `bHidden`, `bCollideActors` and `bBlockActors` before its first hide, for undo. Every change (move, turn, scale, hide, show, undo, redo, re-apply) writes the edit's fields as well as the actor, so a Save never reads the actor. |
| Base | Taken from the untouched actor before the first write. A command that changes nothing (a click on a handle without motion) makes no edit. No edit is made of an actor a waiting session entry names: the command is refused with a status line, and the pass runs at once. An edit made from the stale actor would replace that entry at the next Save. |
| Full transforms | Move sends a location, Rotate a rotation, Scale a `DrawScale3D`. `DrawScale` is never changed. Writes are raw, then `ForceUpdateComponents`. |
| Delete hides | `SetHidden(true)` and `SetCollision(false, false)` through the §3.1 helper, then `ForceUpdateComponents`. Undo restores the three flags as they were. Move, Turn and Scale refuse a hidden actor. |
| Undo and redo | One step per spawn, delete or drag. A drag's step opens at its first command and closes when the drag ends, before any other command, on close, on a map change, and on a fault. A step whose before equals its after is dropped. A step whose actor is gone is dropped with a status line. At most 200 steps; a new step clears redo; a map change clears both. |
| Save | As Phase 1, plus: the file's `base` wins over the edit's (an import may have moved it on). A field the edit has not changed since it was last saved or read takes the file entry's value, on the actor too: an import may have written the YAML's value into it. `imported` is not written (a saved edit is a new state); its `before` goes into `installed`. |
| Map change | Every edit lets go of its actor, the unsaved ones are saved from their stored state, the edits of each map written are cleared, both undo stacks are cleared, and the new map's session is read into a pending list. An edit whose file could not be written stays, unsaved, and the next Save tries again. |
| Streaming | `ForceUpdateComponents` only on actors of a visible level; an actor of a level loaded but not visible gets the raw writes only, and its components are updated when the level turns visible. When a level unloads, its edits go back to pending with their state, and their undo steps go. |

**The re-apply pass.** A pass matches pending entries to actors in the loaded levels: the persistent level (the
`WorldInfo`'s `Outer`) and each `WorldInfo.StreamingLevels[i].LoadedLevel`, walking their `Actors` list.

- **Matching:** a spawn by its tag (a built copy has it). A level actor by tag when exactly one loaded actor has it, the tag is
  real (not empty, `None` or the class name), and that actor is in the entry's level and of its class; else by path, with the
  same class. A vanilla actor is also looked for in the running map's fork of its file (§11.4).
- **Rule 1, an entry with `imported`:** the actor already has the entry's state → the install is done, the entry is dropped.
  The actor has `imported.before` or one of `installed` (or, for a spawn, no actor has its tag) → the install is pending,
  apply. Anything else → the YAML was rebuilt from something else since, drop the entry (logged).
- **Rule 2, a version 1 entry (no `base`):** the actor already has its state → drop. Else apply, with `base` = the actor's
  state before applying.
- **Rule 3, anything else:** apply.
- **Applying** writes the stored state (deleted: hidden); the edit starts saved. An entry that shows an actor the build hid
  (the state it was judged by says deleted, and the actor is hidden) shows it again with its class's collision; an actor the
  game hid stays hidden. An unmatched spawn is spawned again with its mesh from memory, else `mesh.load`, else the catalog
  entry with that vanilla mesh. A deleted spawn with no actor stays as a tombstone edit.
- **Rebinding:** when a built copy with a spawn's tag shows up in a later pass, the spawned actor is destroyed and the edit
  moves to the copy. An edit still equal to its session entry is first judged against the copy by rules 1 and 2.
- **"Has a state"** compares as `editor_import` does: location within 0.01 after rounding to 2 decimals, rotation as an
  orientation (axes within 0.02°), scales within 0.001.
- **When a pass runs:** on the first tick after a load, then whenever the set of loaded and visible levels changed and stayed
  the same for 0.5 s, editor open or not, while something is pending or a spawn can still be rebound. Not while a level is
  still being added or removed (`bShouldBeVisible` ≠ `bIsVisible`), unless that lasts 5 s. A pass that faults is not tried
  again until the level set changes. Dropped entries are removed from the file (same temporary-file write as Save).

**Overlay (`Overlay.cpp`):**

| Part | What it does |
|---|---|
| Tools | T move (world space), R three rings in local space, G scale (local). For a pawn, R is a single ring about the vertical and G is off: NPCs keep a yaw and no scale. |
| Matrices | The model matrix's rows carry `DrawScale · DrawScale3D[i]`. Rotate: the normalised rows → UE3's `FMatrix::Rotator`. Scale: `DrawScale3D[i]` = row length / `DrawScale` for the dragged axis, rounded to 4 decimals. |
| Snap | Ctrl: move 10 units and turn 15° from where the drag started; scale rounds the sent `DrawScale3D` to 0.1. |
| Commands | Move, Rotate and Scale are coalesced per frame; `EndDrag` closes the step. Del, Ctrl+Z, Ctrl+Y and Ctrl+S wait while the gizmo is in use. |

**mele-mcp (`EditorImport.cs`, `YamlEdits.cs`, `LevelBuilder.cs`):**

| Part | What it does |
|---|---|
| YAML | Props take `rotation: [pitch, yaw, roll]` (degrees), `scale` and `scale3d`; `yaw` and `rotation` together are an error. NPCs and usables keep `yaw` only. |
| Three-way merge | Per field, with `base`: editor ≈ YAML → unchanged; YAML ≈ base → the editor's value; editor ≈ base → the YAML's value stays; else a conflict. `removed` merges the same way against `base.deleted`. Without `base` (version 1, a spawn never imported): two-way. |
| Conflicts | `onConflict`: `refuse` (default: the entry is neither applied nor marked; the result lists base, YAML and editor values per field), `editor`, or `yaml` (the YAML's value is also written into the entry's state). |
| Writing | `yaw` while pitch and roll stay ≈ 0 and the entry has no `rotation`; else `rotation`. Scales only when the merge says the editor changed them. A changed pitch, roll or scale of an NPC or usable is noted, not written, and the session entry takes the base's back, or the bridge would apply it forever. |
| Marking | Each entry the import applied gets `base` = the values now in the YAML and `imported: { before: <its previous base> }`. The bridge drops it after the install (rule 1). |
| Dropping | A spawn that was imported and whose tag the YAML no longer has was removed by hand: it is never added back. Every entry an import drops goes to `<map>.dropped.json` first, with when, the YAML file and why: a YAML reverted alone reads as removed by hand, and the entry may be the only copy of the spawn. |
| Safety | `keepSession: true` leaves the session untouched (for trial runs). The session is rewritten only if it did not change since it was read; if the bridge saved meanwhile, the merge runs again, up to 3 times. Undoing an import means restoring the session file together with the YAML. |

**Tested:** a catalog prop spawned, moved, tilted and stretched; another deleted, undone and redone; kept through a reload,
imported, built and installed; and a conflict refused when the YAML and the editor both moved the prop.

## 11. Phase 4: vanilla actors and lights

**Goal:** a vanilla crate moved in our freighter ships; a prop we place reads as bright as the vanilla mesh next to it.

**What the research found:**

| Question | Answer |
|---|---|
| Can a level file be renamed? | Yes: copy it, rename its self `Package` export (a forced export named as the file), save with LEC. Only that name, the edited actors and the shader cache's offsets differ from vanilla. No other file imports from a level file. |
| What names the file in our map? | Four places: the `LevelStreamingKismet`'s `PackageName`, the volume's `VisibleChunkNames` and `InChunkName`, and the squad's `SeqAct_WaitForLevelsVisible.LevelNames`. Streaming entries are only ever added, so a renamed level is renamed in place. |
| Where does a vanilla actor's transform live? | On the actor, often only through its archetype: most meshes of the freighter file take `Rotation` from a prefab archetype. A fork writes the full effective `Location`, `Rotation`, `DrawScale` and `DrawScale3D`. |
| Which vanilla actors may change? | Static meshes, `BioInert`s and (Phase 5) emitters that no Kismet holds. Doors and usables are Kismet-held; path nodes, cover, volumes and light collections must not move. |
| Lights at run time | The freighter's own lights are components of 5 `StaticLightCollectionActor`s, baked. Only `SpotLightMovable` can be spawned as is: `PointLightMovable` and every `*Toggleable` inherit `bNoDelete`. |
| The Normandy | No fork: its master map is LE1CP's. Vanilla edits there stay dev-only. |

**What the spike found** (a hand-made fork installed, then live checks):

| Check | Result |
|---|---|
| The fork loads | Only the fork loads, not the vanilla file, even from a save made before the fork existed. A name not starting with `BIOA_` works in the chunk names. |
| Forked values | Moved, hidden, scaled and turned actors have the fork's values in memory and are drawn so: component transforms are rebuilt from the actor at load. |
| A removed fork | M3 replaces the whole DLC folder on install, so a fork removed from the mod leaves the game too. |
| Our prop next to the same vanilla mesh | 25–40× brighter (top face, linear luminance). A light environment sums the level's 463 lights with no occlusion; a lightmap has the occlusion baked in. On the Normandy the level filters starve it instead, hence darker there. |
| Light environment knobs | `fAmbientScalar` scales the sky part only; at 0 the prop is still 22× too bright. Shadows, channels, `LightDistance` and quality modes don't change the key light. With the environment off the prop is black. |
| A private-channel fill light | Light environment off, mesh on `Gameplay_4` only, one `PointLightMovable` on `Gameplay_4` only, no shadows, radius 350: brightness 0.05 / 0.15 / 0.4 → 0.43 / 1.01 / 2.96 of the vanilla crate, which stays unchanged. Faces turned from the light stay dark. |
| Spawning lights | `Spawn(PointLightMovable)` returns None, and works with `Default__PointLightMovable.bNoDelete` false around the call. A spawned light starts at brightness 1, radius 1024, casting shadows, on BSP, Static, Dynamic and CompositeDynamic. |
| Live light edits | Raw `Radius`, `Brightness`, `LightColor`, `LightingChannels` + `ForceUpdateComponents` show at once. A dynamic light lights lightmapped vanilla meshes too, per pixel, when the channels match. |
| A prop's channel change | The light environment takes the owner's channels when it attaches: one reattach left its lights on the old channels and the prop black; a second fixed it. |

### 11.1 Prop lighting

A prop we place is lit either by its light environment or by **prop lights**: its light environment off and its mesh on
`Gameplay_4` alone. No vanilla light uses that channel, so a prop light lights props and nothing else. Vanilla actors keep
their lightmaps; pawns keep their environment.

- **Covered:** a light covers a prop when it is enabled, its channels include `Gameplay_4` (both kinds do), and its falloff
  at the prop's bounds centre, `a = (1 − (d/R)²)^falloff`, is at least 0.25 (d ≤ 0.71·R at falloff 2). The bridge and
  `level_build` use this one rule. A built prop counts only the lights of its own file (its build sees no other); a spawn
  counts them all.
- **The switch:** covered → light environment `bEnabled` false, mesh channels {`bInitialized`, `Gameplay_4`}. Not covered →
  light environment on, the mesh archetype's channels. Written only when they differ, with two reattaches. So no prop is
  ever black.
  - The build switches every props stand-in that is not removed. An uncovered prop gets a note: "no prop light covers it;
    lit by its light environment, brighter than vanilla: select it in the editor and press L". When a switched-off
    `lights:` entry would cover it, the note names that light.
  - The bridge switches its own spawns and the built props `levels.json` lists (`props`); usables, `live_place` copies and
    vanilla meshes keep their environment. It runs on a fresh spawn, after each pass, when a prop's step closes, when a light
    is added, moved, set or switched off (for the props it covered before and after), and mid-drag when a linked light goes
    or comes back. A prop of a level loaded but not visible waits for its level.
- **Auto light** (toolbar, on by default) and **L** (on a selected prop) add a prop light for a prop. Auto light runs only on
  a fresh spawn, never in a pass. It adds one when no light covers the prop, or when the prop gets less than 0.6 of its target
  from prop lights:
  - received `E = Σ brightness · luminance(colour) · a` over enabled prop lights; target `T` = what a fresh Auto light would
    give it. Room lights count for neither side: they light the vanilla mesh beside the prop too;
  - placement: outside the bounds box's near face toward the camera, half a bounds radius out, half a bounds radius above
    its top. Radius `max(120, 2.5·r, d/0.65)`, r the bounds radius and d the light's distance to the bounds centre, so the
    light covers its prop (a ≥ 0.33). A turned prop's world box grows and its sphere does not, so 2.5·r alone is not enough;
  - cap: a prop whose light would need a radius over 1024 from where the camera is gets none, from Auto light or L. It keeps
    its light environment, and the status line suggests a room light. A prop light lights every prop within its radius, so a
    wider one would brighten props already tuned against their own vanilla neighbour. 1024 is the engine's default point
    light radius; most catalog meshes fit under it, and those that don't are over about 450 units across;
  - brightness and colour: the last prop light edited on this map, else the nearest prop light, else 0.15 white (one
    measurement from the spike, not a calibration). A source with brightness × luminance under 0.001 is passed over.
- **Selection** shows `prop light 40% of target: L adds one`, `no prop light covers it`, or for a prop too big the radius it
  would need. After a prop drag that leaves it under half its target, the status line says so.
- **Link:** an Auto light remembers its spawn while neither was ever saved and the light was not moved or set. Meanwhile one
  undo step covers both; deleting the prop deletes the light unless another shown prop needs it; and a move, turn or scale of
  the prop takes the light along at every frame of the drag, where a fresh Auto light would stand for the prop's new bounds,
  toward the camera as it is now. When the view would need a radius over 1024, the light keeps the side it stood on, else
  takes the box's narrower side. Only while the prop is too big from every side is the light deleted (the prop goes back to
  its environment), and it comes back when the prop fits again. A light you deleted never follows.
- `live_place` copies are never switched: they keep their light environment, as the build of a prop with no prop light does.

### 11.2 Lights in the editor

The editor lists the lights it spawned and the lights of the YAMLs' `lights:` sections (the tags `levels.json` lists). A
vanilla light actor is refused ("no map of ours streams one, so it can't ship"); NPC lights and level lights came in Phase 5
(§12.3, §12.4).

- **Spawn:** `PointLightMovable`, through one helper beside `CallProcessEvent` with no C++ objects in its frame: save the class
  default's `bNoDelete` bit, clear it, `__try { Spawn } __finally { put the bit back }`. A fault is caught by the caller's
  `__except`, whose unwind skips C++ destructors under `/EHsc`, so a scope guard could miss it. Every field is then written,
  so nothing depends on class defaults: `Radius`, `Brightness`, `LightColor`, `FalloffExponent` 2, `CastShadows` false,
  `bOnlyAffectSameAndSpecifiedLevels` false, `bForceDynamicLight` true, `LightingChannels` from the kind, one reattach.
- **Kinds:** **Prop light** {`Gameplay_4`} and **Room light** {`BSP`, `Static`, `Dynamic`, `CompositeDynamic`, `Gameplay_4`}.
  A room light adds the same light to a prop and to the vanilla mesh beside it, so a match tuned with prop lights holds.
  Stored by name (`props`, `room`), never as bits.
- **On and off is one field,** `light.enabled`. `deleted` on a light means only a spawned light the editor deleted. Every
  light is `bHidden` by class default, so nothing reads a light's `bHidden` as deleted, and nothing hides one.
- **Delete** switches a light off and never destroys it: undo writes the old state back to the same actor. A light of the
  YAML: `enabled` false. A spawned light: `deleted`; deleted before its first save it leaves nothing.
- **UI:** a **Lights** tab: Prop light, Room light, the list of lights. Each light shows an icon in its colour (hollow when
  off), the selected one its radius as rings. While the tab is open or Alt is held, a click within 12 px of an icon selects
  that light; any other click traces, so a light never takes a click meant for a prop. Gizmo: move only. Panel: radius
  (16–4096, logarithmic), brightness (0–10), colour, falloff (1–8), enabled, kind. Only the field a control changes is held
  to its range, so a `lights:` entry built outside one keeps its value.
- **Undo:** a panel drag is one step, like a gizmo drag; keys wait while a control is held.

### 11.3 Vanilla actors in the editor

- **The gate** decides on any actor not under Phase 3's rules (§10). It may be edited only when its class is
  `StaticMeshActor`, `BioInert` or (Phase 5) `Emitter`, up the class chain by name; it was loaded from a file (actors the game
  spawns have no linker or load flags); no loaded Kismet holds it (§12.1); no primitive's bounds reach more than 3000 units
  from their centre ("a room shell: hiding it opens the room", measured as the file has the actor, before the editor's own
  edits); and, for Delete, no primitive has `bIgnoreOwnerHidden` (it would stay drawn). Anything else says why: "a BioDoor:
  doors carry Kismet and door volumes", and so on. Pawns and controllers stay refused for Delete. `bStatic` and `bNoDelete`
  don't refuse: hiding destroys nothing.
- **Delete** hides a static actor as in §10. The status line adds that its baked shadow stays (it is in the floor's lightmap)
  and its physics body stays until the build.
- **Ships or not,** from `levels.json` and the running map's streaming levels: "ships: forked into CLD01_FRE31_00_LAY" (it
  stands in a fork), "ships: BIOA_CLD01 streams it (forked on the next build)", or "dev-only: no map of ours streams
  BIOA_NOR10_07_LAY". Shown only for an actor the gate lets through.

### 11.4 Session entries and levels.json

- **Fork names follow one rule,** `ForkName(map, file)` = `<map minus BIOA_>_<file minus BIOA_>`, so `BIOA_CLD01` streams
  `BIOA_FRE31_00_LAY` as `CLD01_FRE31_00_LAY`. `level_build`, `editor_import` and the bridge each apply it; nothing stores
  the pair. Back from a fork, `<map minus BIOA_>_<rest>` is `BIOA_<rest>` (`<rest>` when it already starts with `BIO`).
- **A vanilla entry names the vanilla file:** its key, `path` and `level` are the vanilla ones, also when the actor stands in
  a fork. Matching tries the path, then the same path in the running map's fork. Matching never reads `levels.json`.
- **A light entry** adds a `light` block (`kind`, `radius`, `brightness`, `color`, `falloff`, `enabled`) in the state and in
  `base`, `imported.before` and `installed`. It keeps the actor's rotation and scales, but only its location counts. A
  spawned light is spawned again on load by the same helper, and rebound to its built copy (`PointLightToggleable`) by tag.
- **Rules 1–3 decide as before;** a light "has a state" when its location, `bEnabled` = `enabled && !deleted`, radius
  (0.01), brightness and falloff (0.001), colour and kind (exact) match. Rotation, scales and `bHidden` don't count.
- **`levels.json`** ([formats.md](../formats.md) §4): written by `level_build`, read by the bridge on each map change. It
  says which levels are ours, which vanilla files each streams (for "ships"), which built props the bridge may switch, and
  which tagged lights it may list. Each build replaces only its own level's key. It describes the last build, not the
  install.
- **Ours:** a level is ours when it is a `levels.json` key, or when its linker's file lies in the `dlc` folder and its name is
  not a fork by the rule. Without `levels.json` every actor keeps Phase 3's rules and the editor says "levels.json missing:
  run mod_build".

### 11.5 YAML

```yaml
# Vanilla actors the map's streamed files change: shipped in a fork.
vanilla:
  BIOA_FRE31_00_LAY.StaticMeshActor_69:  { location: [-210.73, -15227.6, -408.58] }  # Crate_XLarge_02; vanilla location (-360.73, -15227.6, -408.58)
  BIOA_FRE31_00_LAY.StaticMeshActor_104: { removed: true }                          # Cargo_Container_01
  BIOA_FRE31_00_LAY.StaticMeshActor_93:  { rotation: [0, -45, 0] }                  # Cargo_Container_01; vanilla rotation (0, -90, 0)

# Lights of the level. kind props: lights only props (Gameplay_4); room: walls, vanilla meshes, pawns and props.
lights:
  ed_light_1:
    tag: ed_light_1
    kind: props
    location: [-300, -15300, -200]
    radius: 350
    brightness: 0.15
    color: [255, 255, 255]     # optional; falloff and enabled too
```

- `vanilla` keys are `<file>.<actor>`; the file must be in `streaming.levels`. Fields: `location`, `rotation` or `yaw`,
  `scale`, `scale3d`, `removed`. A field left out keeps the file's value.
- `lights` entries: `tag`, `kind` (default `props`), `location`, `radius`, `brightness`, `color` (default white), `falloff`
  (default 2), `enabled` (default true: a light is switched off, never removed), `template` (default the file's
  `lightTemplate`, else LE1CP's `PointLightToggleable_7` in `BIOA_NOR10_07_DSG.pcc`).
- Tags are unique across `npcs` (their lights too), `usables`, `props` and `lights`.

### 11.6 level_build

- **Forks** (`ForkBuilder`, before the map is written). For each file of `streaming.levels` with `vanilla` entries: the
  highest-mounted copy outside our own DLC, copied in memory; its self `Package` export renamed to `ForkName`, its
  `PackageGUID` made from the name's MD5; each entry applied with full effective values. `removed` writes `bHidden` true,
  `bCollideActors` and `bBlockActors` false on the actor, and `BlockRigidBody` false on its StaticMeshComponent (physics
  bodies are made at level start from it).
  - Refused with the line: a missing actor, a class outside the allow-list, an actor a `SeqVar_Object` or
    `SeqVar_ObjectList` of the file names, `removed` on an actor with a `bIgnoreOwnerHidden` primitive. Every file's fork is
    made in memory first: a refusal in any of them writes no fork.
  - The file's localized packages (`<file>_LOC_<lang>`) are copied as `<fork>_LOC_<lang>`: the game loads a level's LOC by
    the name it streams, and without them the fork loses the file's dialogue and sounds.
  - Warned: a cover prefab or cover link within 100 units; a room shell; a total scale that became non-uniform for a mesh
    with collision cooked only at its vanilla scale. A uniform scale is not warned: the game makes its collision at load.
  - Written only when its hash differs, before the map. The receipt lists each actor changed, vanilla → fork values.
- **Streaming:** the level list is mapped through the forks. The `LevelStreamingKismet` naming the other name of the pair is
  renamed in place, then every `SeqAct_WaitForLevelsVisible`'s `LevelNames` (a wait for a name no longer streamed never
  finishes, and the squad never spawns). A file with no `vanilla` entries left streams its vanilla name again; its fork
  file is deleted only after the map is saved.
- **Props:** the switch of §11.1, on every stand-in.
- **Lights:** `ApplyLight`, shared with NPC lights. A light of the level with the entry's tag is edited in place; else its
  template is copied. Always written: `Location`, `Tag`, `CachedParentToWorld.WPlane`, `bEnabled` on the component and the
  actor, `Radius`, `Brightness`, `bOnlyAffectSameAndSpecifiedLevels` false, and new `LightGuid`/`LightmapGuid` for a new copy.
  A `lights:` entry also writes `LightColor`, `FalloffExponent`, `CastShadows` false, `bForceDynamicLight` true, its kind's
  channels in full, and drops the template's `OtherLevelsToAffect`. NPC lights leave those to their template. A later copy of
  a template clones the first only when the level's actor of that name really is a copy (same class and subobjects); else the
  template is imported again under a free name. The receipt lists each value changed, old → new.
- **`levels.json`** last, after any fork deletion.

### 11.7 editor_import

- **Vanilla:** an entry becomes `vanilla.<file>.<actor>`, three-way per field like props, with the YAML's missing fields
  read from the vanilla file (effective values). A new entry gets a comment: the actor's mesh and the vanilla values it
  changes. A delete → `removed: true`; an undone delete loses `removed`, and the whole entry when that was all it had.
  Refused with the reason: the build's own refusals (`ForkBuilder.Refusal`). In a level the YAML doesn't stream:
  skipped, dev-only. A marked vanilla entry whose line was removed by hand is never added back.
- **Lights:** a spawned light → a new `lights.<tag>` with `color` and `falloff`. An edited one → three-way per field
  (location and each light field, the §11.4 tolerances). A deleted spawned light already in the YAML → `enabled: false`.
- Fork names in a session map back to their vanilla file by the rule: the fork is a build output, never a source.

### 11.8 What the tests showed

- **The built light reads 6.7% brighter than the editor's:** the same crate, place and brightness gave 1.038 of the vanilla
  crate live and 1.107 built. Inside the ±10% band, so a brightness tuned in the editor ships as is. Both the light class
  and the prop class differ between the two, so the test does not say which adds it.
- **Auto light's 0.15 is a start, not a match:** it gave 1.71 in the freighter; 0.08 matched. Auto light and L take the
  last tuned light's brightness, so later lights start right.
- **"Dropped as installed" keeps only newer edits:** after an install the next import meets only what changed since.
- **The three-way base kept a hand edit:** a brightness changed in the YAML after the session's base stayed through an
  import, a load, a move and a Save.
- **A uniform scale's collision follows the scale:** the game makes it at load (a crate scaled to 0.57 blocked at its scaled
  face).
- **A reverted fork leaves one unused name** in the map's name table. LEC's export data holds raw name indices, so it is not
  removed; it is harmless.

**Out of scope:** relighting a moved vanilla mesh; its texture-streaming spheres and cover nodes (warned, not fixed); scaling
every prop light of a level at once.

## 12. Phase 5: effects, NPC lights and the level's own lights

**Goal:** in F8, move and hide the placed particle effects, edit and add our NPCs' lights, and edit the lights of a level's
static light collections (door, character and baked ones; the baked ones for characters only). Ship all of it through
`editor_import` and `level_build` as Phase 4 ships vanilla meshes. Built in halves, so a failed check would block only one:

- **5a:** the shared parts, effects and NPC lights. Done and tested in game.
- **5b:** the lights of static light collections (SLCA lights), the copied character lights included. Gated by S0–S3, which
  all passed. Built; its in-game test is pending.
- **5c (optional):** new effects from a list. Not built.

**What the research found:**

| Question | Answer |
|---|---|
| What lights does the freighter have? | No light actors. 5 `StaticLightCollectionActor`s in `BIOA_FRE31_00_LAY` hold 492 light components: 381 point, 108 spot, 3 directional. Of the point and spot lights: 463 baked, 14 door lights on channel `Unnamed_6` (never baked, drawn per pixel), 12 off and never baked. A map's `characterLights` copy adds the freighter's 19-light character rig (`StaticLightCollectionActor_68` of `BIOA_FRE31_00_DSG`). |
| What does an edit of a baked light do? | It has no render proxy but sits in the world's light list, so it reaches only characters and props, through their light environments. The walls keep the bake (S2, S3). |
| The sun | A shadow-mapped light drawn per pixel, with a level filter. Its edit would likely change the walls: left out. |
| How is an SLCA light reached? | By its component path, `BIOA_FRE31_00_LAY.TheWorld.PersistentLevel.StaticLightCollectionActor_127.PointLight_35_LC`. At run time `LightComponents` is empty and the lights sit in `Components`. Its place is `LightToWorld.WPlane`. No Kismet variable can name a component. |
| What may a fork write on one? | Brightness, colour, radius, falloff, on/off and place. Never the GUIDs, the built and precomputed flags, `UseDirectLightMap`, channels, `bForceDynamicLight`, `CastShadows` or the level filter: lightmaps name lights by `LightmapGuid`. |
| How does a fork move one? | At load UE3 reads one matrix per light from the collection's binary data and makes it the light's `CachedParentToWorld`; the light's place is its `Translation` through that matrix. So the binary matrix is the parent, and a scaled parent would scale a `Translation`. LE1 does the same (S0, S3). |
| How does the editor move one live? | `PointLightComponent.SetTranslation` moves one light; `ActorComponent.ForceUpdate(false)` refreshes one light's fields. `ForceUpdateComponents` on the collection reattaches all of its lights: never used (S1). |
| Why a writer of their own? | Phase 4's writer writes the channels from the kind, no shadows, `bForceDynamicLight` and no level filter, and read the kind back from the channels, so an NPC's or a level's light read as a room light and would have been rewritten. |
| Emitters | Levels place only `Engine.Emitter`: the freighter has 88 dust cards, location only, none Kismet-held, no collision. The editor's pause freezes particles, and old particles may stay after a move (S4). The game spawns 25 or more emitters into the map's own level at run time (impacts, shields); load flags tell them apart. |

### 12.1 Shared parts (5a)

- **Light sources and kinds.** A light's kind comes from where it is, read once when the editor tracks it, and stored on its
  edit:
  - `props` / `room`: ours (`lights:` entries and editor spawns), told apart by channels as in Phase 4;
  - `npc`: its tag is in its level's `levels.json` `npcs.*.lights` (§12.3);
  - `vanilla`: a point or spot component of an SLCA, the rig's included (§12.4).

  Stored kinds compare exactly, so an `npc` entry matches its light read back with its source. NPC and level lights are
  tracked apart from ours, so the prop switch never walks them.
- **The kept writer.** For `npc` and `vanilla` lights. It reads the light first and writes a field only when the wanted value
  differs from the live one (radius beyond 0.01, brightness and falloff beyond 0.001, colour RGB exact, on/off), and never a
  field the entry lacks. Colour keeps the light's alpha. So an untouched colour from a prefab archetype is never written. It
  never writes channels, shadows or the level filter.
- **Hard guard.** Our writer returns without writing (and logs once) for a light of kind `npc` or `vanilla`, and for a light
  that is not ours (neither an editor spawn, a `lights:` entry of `levels.json`, nor a light of our levels with an ours-kind
  edit). A call site the dispatch missed then does nothing.
- **Session version 3.** Written when an entry (or its `base`, `imported.before` or `installed`) holds an `npc` or `vanilla`
  light; else 2. A Phase 4 bridge would apply such an entry through its own writer and rewrite its channels and shadows; it
  refuses a version 3 file and skips its entries, and a Phase 4 import refuses it. Every Phase 5 reader accepts 1, 2 and 3.
  Effect entries are plain vanilla entries (`class: Emitter`) and stay version 2.
- **`levels.json`** gains, per level, every NPC of the level's YAML (with no lights too: `name`, built `location` and `yaw`,
  light offsets) and the `characterLights` copies (collection name → source file). It stays version 1: a Phase 4 bridge
  skips unknown keys. The bridge reads it on a map change and whenever its write time changes (checked on F8 and once a
  second while the editor is open), because `npc_place` rebuilds the level and moves the pawn live.
- **The Kismet set.** On a map change and when a level turns visible: every actor a loaded `SeqVar_Object.ObjValue` or
  `SeqVar_ObjectList.ObjList` names. It gates effects and the vanilla mesh gate (§11.3), with one wording: "not edited: Kismet
  holds it (<sequence>.SeqVar_Object_N): the story moves, hides or switches it". It reads only the loaded levels; the build's
  fork, which reads the whole file, has the last word.
- **Light-environment reset (5b).** After a baked or character level light's step closes (a gizmo or panel release, an undo
  or redo, a pass), each enabled `DynamicLightEnvironmentComponent` whose owner stands within the light's old or new radius
  plus 100 is reset (`SetEnabled` false, then true), and the paused world runs 1.5 s (`TickFor`). Not during a drag: a reset
  needs world ticks, and ticks move NPCs. An environment refreshes fully at most once a second
  (`MinTimeBetweenFullUpdates` 1.0) and blends over 0.3 s (`EnvironmentTransitionTime`). S2 ran with the world running, so
  whether a reset and one tick are enough is unproven: 5b runs the world 1.5 s until its in-game test shows otherwise. NPC
  lights need no reset: a `bForceDynamicLight` light on the Dynamic channel reaches a face at once, paused (S5).
- **Icons and picking.** Shapes by source: disc = ours, diamond = NPC light, square = level light, star = effect; filled in
  the light's colour, hollow when off; a level light's type letter (B, C, U, D) beside its icon; one legend line in the Lights
  tab. A click picks, among the icons within 12 px, the one nearest the camera (one drawn under the cursor first), so a light
  in the next room never wins. With a tab open only that tab's set is picked; with Alt held, lights and effects both.
  Untouched level lights and effects are drawn within 30 m of the camera, nearest first, at most 150 lights and 50 effects;
  one with an edit is always listed and drawn.
- **Status wording.** The overlay colours a status line orange only when it starts with "not " or "the engine refused", so
  every refusal reads "not <verb>: <reason>".

### 12.2 Effects (5a)

- **Editor.** An **Effects** tab next to Lights: the loaded levels' emitters, nearest first, one row each (`Emitter_12 ·
  FX_ME1_dust_card_METR · BIOA_FRE31_00_LAY · 4.2 m`). Refused ones are greyed with their reason. Icons while the tab is open
  or Alt is held. The bounds fallback of picking skips particle systems (a dust card's box is about 1000 units across), so a
  click on a crate behind a dust card selects the crate. Gizmo: move and turn. The Selection window shows the template,
  "loops" or "ends", and the ships line.
- **Writes.** Move and turn as props: raw `Location`/`Rotation`, then `ForceUpdateComponents`. Delete hides with
  `SetHidden(true)` (no collision to clear); `bHidden` reads as deleted for an emitter, since its class default is false.
- **Seeing the result.** After an effect step closes (move, turn, undo, redo, a pass that applied one), the editor calls the
  component's `KillParticlesForced`, then `ActivateSystem(false)` unless it is hidden, and lets the paused world run 1.5 s
  (`TickFor`): "effects run 1.5 s". Without the kill, old particles stay where the effect was (S4). The release is the
  editor's own: an outside write to `bPlayersOnly` lasts one frame, since the open editor sets it again at every tick. It is
  skipped when the game had its own pause when the editor opened. Nothing of it ships.
- **Refusals:** an emitter with no load flags ("made at run time by the game (an impact, a shield, a footstep), not part of
  the map"), in our levels too; one the Kismet set holds; one with `bAutoActivate` false or no template ("off until Kismet or
  a matinee starts it"); and scale ("not scaled: an effect moves and turns (how its particles follow a scale is
  unchecked)"). The room-shell test leaves particle systems out, so a wide effect never reads as a room shell.
- **YAML and ship:** a Phase 4 `vanilla:` entry with `location`, `rotation` or `yaw`, and `removed`; `scale` or `scale3d` on
  an emitter is an error with its line. `Emitter` is in the fork's allow-list; the fork also writes the component's
  `OldPosition` (it only feeds the particle system's velocity). Nothing of an emitter is baked.

### 12.3 NPC lights (5a)

- **Listed.** A light whose tag is in its own level's `levels.json` `npcs.*.lights`; its owner is the pawn of that level with
  the NPC's tag. The Lights tab gets a section per NPC of the visible levels, with no lights too ("Dorn (claude_npc_02)", the
  YAML key with a capital and the tag), with an **Add light** button. A light of our levels no `levels.json` entry names is
  refused: "no levels.json entry names this light (run mod_build)".
- **Reference pose.** Offsets are measured from the NPC's reference pose: the pawn's own edit in this session when it has
  one, else the built pose from `levels.json`; never the live pawn. A cross and a yaw tick mark it while one of the NPC's
  lights is selected.
- **Pose check.** Before an edit of an NPC's light, and before a pass applies an entry of one, the pawn (unless the editor
  moved or turned it) must stand within 5 units and 2° of its built pose. Otherwise the light is refused and its entry waits:
  "not edited: Dorn stands 37.2 from where levels.json puts them (the last build): install the last build (mod_install, game
  closed): a reload shows the installed level, not npc_place's build". The game never moves or turns our NPCs (S5), so the
  pawn is the check.
- **Panel.** "NPC light: Dorn": radius, brightness, colour, falloff, enabled; no kind control. The offset shows as
  `[forward, right, up]`. A note says it keeps its template's channels (Dynamic: characters and props on their light
  environment, not the walls).
- **Moving one.** A built NPC light is a `PointLightToggleable` (not `bMovable`); it still moves live with a raw `Location`
  and `ForceUpdateComponents(true, false)`, paused, no tick (S5). Its new offset is `R(−yaw)·(L − P)`: forward
  `f = d.x·cos θ + d.y·sin θ`, right `r = −d.x·sin θ + d.y·cos θ`, up `u = d.z`, with `d = L − P`, `P` and θ = yaw·2π/65536 from
  the reference pose. That is the build's formula inverted; the round trip holds to 0.002.
- **The NPC carries its lights.** A move or turn of one of our NPCs moves its NPC lights to the new pose plus their offsets,
  at every frame of the drag, in the same undo step. The step holds where each light stood before and after, the after
  computed from the stored pose, so closing it reads no game memory. A carried light without an edit gets none (its offset
  didn't change, and the build places it from the NPC's new pose); one with an edit keeps its offset and its `location`
  follows. A pass that applies an NPC's edit moves its lights the same way.
- **Add light** spawns a `PointLightMovable` (the §11.2 helper) with the default NPC light template's look: channels
  {`bInitialized`, `Dynamic`}, no dynamic shadows, `bForceDynamicLight`, no level filter, white with alpha 0, falloff 2. Offset
  `[60, 0, 80]`, radius 100, brightness 1.2, tag `<npc tag>_light_<run>_<n>`. A pass spawns an `npc` entry again with the same
  look, never with the class defaults. A Movable with this look lights a face within 2% of the built Toggleable (S5). It lands
  in the persistent level (`BIOA_NOR00` on the Normandy). A YAML with its own `lightTemplate` may build another look; the
  status line says so once a run.
- **Delete** switches it off. Refused: Add on a pawn that is not ours; Add on an NPC `levels.json` doesn't list ("built
  before Phase 5: run mod_build"); turn and scale ("a light only moves").
- **Session:** `light.kind: npc` and `npc: { tag, offset }`, with `base.npc.offset` = the YAML's. `location` stays absolute:
  always the reference pose plus the turned offset. A state is had when the live light stands within 0.05 of its `location`
  on each axis and its fields match: 2-decimal offsets move a built light by up to 0.007 per axis, and a float at the
  Normandy's heights steps by 0.004. So an entry is dropped as installed only when the game shows it.
- **YAML** (`npcs.<name>.lights`, flow items):

  ```yaml
  npcs:
    dorn:
      tag: claude_npc_02
      # Lights that go with this NPC: offset [forward, right, up] from the NPC's centre (the pawn's Location), turned by the NPC's yaw.
      # Optional: color (default white), falloff (default 2), enabled (default true). A light is switched off, never removed.
      lights:
        - { tag: claude_npc_02_light_ff9xa_1, offset: [60, 40, 80], radius: 100, brightness: 2, color: [255, 255, 255], falloff: 2 }
  ```

- **`editor_import`** merges NPC pawns first, then their lights: each item found by tag, three-way per field (offset within
  0.02 on each axis, written to 2 decimals; radius 0.01; brightness 0.001; colour, falloff and enabled exact). A field the
  item leaves out is the template's. A spawned NPC light goes to the YAML whose `npcs` has its NPC's tag, whatever
  `takeSpawned` says. An entry whose NPC differs from the item's owner is refused.
- **`level_build`** writes `color`, `falloff` and `enabled` when given, the template's values when not, and `bEnabled` on
  the actor too. Channels, shadows and `OtherLevelsToAffect` stay the template's. A tagged light no entry names stays as
  built, with a note: switch a light off with `enabled: false` instead of deleting its line.
- **`npc_place`** reads the map's session and warns when it holds an unimported edit of the NPC or its lights.

### 12.4 Level lights (5b)

- **What is listed.** The point and spot components of each SLCA in the map's levels (the persistent level and the streaming
  levels' loaded levels, never `BIOG_UIWorld`), walked through `Components`. Groups in the Lights tab:
  - **Character lights (characterLights):** the rig copies of the map, always shown;
  - **Level lights** (toggle, off by default): the other SLCA lights within 30 m; **Baked too** adds type B.

  Never listed: sun and sky components, vanilla light actors, `BIOG_UIWorld`'s lights, prefab archetypes, light-environment
  lights. A collection of our levels that no `characterLights` item names is refused: no build writes its lights.
- **Light types,** read from live fields by the bridge and from the export by the build, in this order:

  | Type | Rule | Freighter `_LAY` / rig | What a change reaches | Panel header |
  |---|---|---|---|---|
  | **B** baked | built into a lightmap (`bHasLightEverBeenBuiltIntoLightMap`) and `UseDirectLightMap` | 463 / 1 | Characters and props through their light environments; the walls keep the bake. | "Baked light (B)" |
  | **D** dynamic | else `bForceDynamicLight` or the Dynamic channel | 0 / 0 | Drawn live: what you see ships. | "Dynamic light (D)" |
  | **U** special channel | else any channel outside BSP, Static and CompositeDynamic (`Unnamed_*`, `Gameplay_*`) | 17 / 1 | Only meshes on that channel (the freighter's doors on `Unnamed_6`). | "Special-channel light (U)" |
  | **C** characters only | anything else | 9 / 15 | Characters and props, never the walls. | "Character light (C)" |

  "Light type" is the word in the panel, the receipt and the YAML comments; `kind` stays the session's (`vanilla`). A B
  light's banner says the walls keep its baked light, and what the change leaves against its base (off, moved, brighter).
- **The edit target.** The selection becomes (actor, component): the actor is the owner SLCA, the component the light. The
  edit's path and key are the component's canonical path. Location is `LightToWorld.WPlane`; rotation is written as zeros
  and scales as 1. Every function that reaches an edit's actor takes a component branch or returns early (prop lighting and
  spawns never involve level lights).
- **Hard guard:** `Put`, `SetDeleted`, `WriteFlags` and the reattach return (logged once per collection) on a
  `StaticLightCollectionActor`: no pose, `bHidden`, collision or reattach on one, ever. A reattach of a collection would redo
  all of its lights.
- **Moving one.** The editor writes `Translation = M⁻¹·(world − origin)`, M and origin from the light's live
  `CachedParentToWorld` (a full 3×3 inverse, since a scaled parent scales `Translation`), then calls `SetTranslation`, which
  moves that one light (S1). On a drag it does so every frame. A light whose parent matrix scales it (a row length off 1 by
  more than 0.001) refuses moves: "not moved: its light collection scales it"; the panel still works. S0 found none in the
  freighter or its rig.
- **Fields** go through the kept writer, then `ForceUpdate(false)`, which refreshes that one light (S1), then the
  light-environment reset (§12.1) for a B or C light. A B or C light has no proxy and gets none from the refresh (S2), so the
  walls are never lit twice.
- **Refusals:** turn on a spot ("not turned: a spot light's aim is not edited (its cone stays as built)"), turn on a point
  and scale (Phase 4's texts), Delete of one already off. A light of a file no map of ours streams is editable, dev-only.
- **Delete** switches the light off (a light step). A B light's status line adds "switched off for characters and props
  only: the walls stay lit".
- **Ships:** a `characterLights` copy's light: "ships: built into BIOA_CLD01 (characterLights copy of BIOA_FRE31_00_DSG)";
  any other as its collection would (in a fork of its file, or dev-only).
- **Session:** an entry per light by its canonical path, with `owner` (the collection's name) and `light.type`, version 3. A
  pass matches it by path (then the same path in the map's fork), judges it by place and fields, and applies it with
  `SetTranslation` and the kept writer.
- **YAML** (`vanilla:`, Phase 4's section):

  ```yaml
  vanilla:
    # A light of a light collection: <file>.<collection>.<light>. Fields: location, brightness, color, radius, falloff, enabled.
    BIOA_FRE31_00_LAY.StaticLightCollectionActor_127.PointLight_35_LC: { location: [552.55, -11923.24, -222.2], brightness: 1.2 }   # U door light; vanilla location (352.55, -11923.24, -222.2), brightness 0.6
    BIOA_FRE31_00_LAY.StaticLightCollectionActor_131.PointLight_309_LC: { enabled: false }   # B baked: off for characters and props only
    # A light of a characterLights copy: keyed by its source file, written into this map.
    BIOA_FRE31_00_DSG.StaticLightCollectionActor_68.PointLight_2_LC: { brightness: 0.6 }   # C character light; brightness 1
  ```

  A two-dot key's file must be in `streaming.levels`, or `<file>.<collection>` must be a `characterLights` item. Errors with
  their line: `rotation`, `scale` or `removed` on a light ("lights move, they don't turn, scale or go: use enabled: false");
  `kind` on a light (a Room light lights the walls instead); a sun or sky component; a key matching neither rule.
- **`editor_import`** sends `vanilla` light entries to the vanilla merge: `location` and the light fields, three-way, with
  the YAML's missing fields read from the file through archetypes. An entry of the map's own level whose collection is a
  `characterLights` copy goes to the key of its source file. A new entry's comment names the light type and the vanilla
  values it changes.
- **`level_build`:**
  - Fork: on the component export `TheWorld.PersistentLevel.<collection>.<light>`, write `Brightness`, `LightColor` (RGB,
    alpha kept), `Radius`, `FalloffExponent` and `bEnabled`. A move writes `Translation` = M⁻¹·(location − origin), M and
    origin from the collection's binary matrix at the light's index (the load-time parent), a full 3×3 inverse. LE1 applies
    a written `Translation` at load on top of that matrix (S3), so the binary data is never touched. A light whose parent
    matrix scales it is refused with its line. A field the file leaves to the class default is added as a new property.
  - Never written on a level light: `LightGuid`, `LightmapGuid`, `bPrecomputedLightingIsValid`,
    `bHasLightEverBeenBuiltIntoLightMap`, `UseDirectLightMap`, `LightAffectsClassification`, `LightingChannels`,
    `bForceDynamicLight`, `CastShadows`, `bOnlyAffectSameAndSpecifiedLevels`, `OtherLevelsToAffect`.
  - The map's `characterLights` copy: every light of it gets each editable field from its entry, else the source file's
    value, so a deleted entry gives the source value back.
  - Receipt, per light: "PointLight_309_LC (B baked): switched off, characters and props only (the walls stay lit)",
    "PointLight_35_LC (U door light): brightness 0.6 → 1.2, location (…) → (…)".
- **Undo:** Phase 4 light steps (a panel drag, a gizmo drag, an on/off) on the component target, with the reset after each.
- **The pending in-game test** checks, in the freighter: the groups and type letters; a U door light brightened and moved
  (the lit patch follows); a B light brightened (walls unchanged, the nearby NPC's key light up) and switched off; a rig C
  light dimmed; Save, reload, import, build, install; the fork's level light keeping its `LightmapGuid` and channels; the
  walls within 0.5% of the same shot before the build; and a revert.

### 12.5 New effects (5c, not built)

- **List:** an index of the export tables (741 unique `ParticleSystem`s); the default list would be the looping ambient
  systems, without cinematic packages.
- **Loading:** systems are forced exports inside levels, with no effect-only packages, so `DynamicLoadObject` by the vanilla
  path should fail. A second catalog file would port them as `MeleCatalog_Core` does meshes. Whether a ported system brings
  the shaders its materials need is unchecked.
- **Spawn:** `Spawn(Engine.Emitter)` on the player controller, then `Emitter.SetTemplate`, in one job. Never
  `EmitterSpawnable`: it destroys itself when a finite system ends.

### 12.6 Spikes S0–S5

Each spike settled the engine claims a design choice rested on. The ones run in game were run with the editor open and
paused, as it runs, except S2 (below).

| Spike | Question | Result |
|---|---|---|
| **S0** (offline) | Which matrix is a light's parent; are any parents scaled? | For all 506 point and spot lights of the freighter's collections and its rig, the binary matrix equals `CachedParentToWorld` bit for bit; every row length is 1 within 1.2e-7; no light has a `Translation`. No move is refused. |
| **S1** | Does one light move and refresh alone? | `SetTranslation` moved a door light exactly +200 and back, and a spot through its rotated parent; the lit patch moved. `ForceUpdate(false)` and `SetEnabled` false then true each refresh one light; a raw field write shows nothing until refreshed. The other lights of the collection kept their `SceneInfo` and `LightListIndex`. The collection's `ForceUpdateComponents(true, false)` changed every other light's index: never used. |
| **S2** | Do baked and character lights reach pawns and not walls? Do pawn environments reset? | Pawns yes, walls no, with the world running: a baked light 0.4 → 4 raised the nearby NPC's key light by 40% and Shepard's by 63%; off, moved and a rig light's change moved them too, and came back exactly when undone. Walls: box ratio 1.000. The lights never got a proxy. The environments took every edit by themselves within 2.5 s, without moving, and a reset changed nothing more (within 0.01). Whether a reset with the editor's pause and one tick is enough is unchecked, so 5b runs the world 1.5 s. An environment folds near lights into its key light and far ones into its sky term; checks read both. Environments never update while the world is paused, and LE1 pauses the world while its window has no focus: the first run, with another window focused, measured nothing. |
| **S3** | Does a fork with light components load as written? | Yes: a baked light off, a door light at 1.2 and exactly +200 (its proxy kept), the rig light at 0.6. Walls, fork against vanilla from the same camera: 0.997–1.000 away from the characters. |
| **S4** | How do effects behave live? | A raw `Location` + `ForceUpdateComponents` moved an emitter, but old particles lingered (its bounds spanned both spots); with `KillParticlesForced` + `ActivateSystem(false)` the old spot was empty. `SetHidden(true)` hid it at once, paused. An NPC moved by a raw `Location` stayed put through 11 s of release: the game writes back no pose. |
| **S5** | Does a spawned Movable light a face as the built Toggleable? Does the game move our NPCs? | Face luminance within 1.7% (band ±10%); a built Toggleable moved +10.000 exactly by a raw `Location`, paused; both NPCs kept X, Y and yaw exactly through hundreds of reads and their conversations. |

A repeatable game camera for screenshots: place Shepard (`live_place`), set the player controller's `Rotation`, check it
before and after `game_screenshot`, and shoot unfocused right after a load. A running world drifts the camera, and a drawn
weapon switches to the closer combat camera.

### 12.7 Findings

1. **The LE1 SDK's C++ layout of the particle classes is wrong.** Every click on an effect's icon closed the editor (an
   access violation on the game thread). The fault filter now unwinds from the exception context (`RtlLookupFunctionEntry`,
   `RtlVirtualUnwind`) and logs the faulting address and up to 24 `MeleBridge.asi+offset` frames; `llvm-symbolizer` on the
   PDB put the fault in a read of `UParticleSystem::Emitters` at +0x88, where the engine has it at +0x90. Cause: the SDK's
   `FRwVector3` is 12 bytes in C++ and 16 in the engine, so `FBioRwBox` compiles to 0x28 instead of 0x30 and every later
   field of `UParticleSystem` is 8 bytes off; `UParticleSystemComponent::Template` and `bAutoActivate` are off too. Fix:
   those are read at the offsets the engine's reflection gives (`PropertyOffset`, `Reflection.cpp`), and `static_assert`s pin
   every other field Phase 5 reads (`AEmitter::ParticleSystemComponent` 0x284, `USeqVar_Object::ObjValue` 0xAC,
   `USeqVar_ObjectList::ObjList` 0xB4, `UParticleEmitter::LODLevels` 0x70, `UParticleLODLevel::RequiredModule` 0xE8,
   `UParticleModuleRequired::EmitterLoops` 0xC4). **Any SDK class with an `FRwVector3` (or a struct holding one) before a
   field you read has the same trap: check its offset against reflection before trusting it.**
2. `YamlEdits` keeps a line's trailing comment in its column when a value's length changes, so comments aligned over
   several lines stay so. A comment's own text is never rewritten.

**Out of scope:** re-baking lightmaps (truly dimming or removing a baked light on the walls); turning a baked light dynamic
(it may light the walls twice); sun and sky lights; vanilla light actors; changing channels, shadows or level filters of
level or NPC lights (a Room light covers "light the walls"); aiming spot lights; linking a lamp mesh to its light; forks of
the Normandy's files; scaling effects, switching them on or off, or their particle parameters; effects held or started by
Kismet or a matinee; an NPC light preview for a YAML with its own `lightTemplate`; new effects (§12.5).

## Sources

- LEX Live Level Editor: `tools/mele-mcp/external/LegendaryExplorer/LegendaryExplorer/LegendaryExplorer/Tools/LiveLevelEditor/`
  (the LegendaryExplorer submodule).
- [ME3Tweaks/LExASIs](https://github.com/ME3Tweaks/LExASIs) (LEXInterop, StreamingLevelsHUD, LExSDKv2).
- [SuperAdjustmentStudio](https://github.com/Reyuu/SuperAdjustmentStudio): `src/render/mouse.cpp`, `src/game/freecam.cpp`.
- [BmEditor](https://github.com/etkramer/BmEditor) (UE3 source, read only): `UnLight.cpp`, `PointLightComponent.cpp`,
  `SpotLightComponent.cpp`, `UnActorComponent.cpp`, `DynamicLightEnvironmentComponent.cpp`, `UnParticleComponents.cpp`.
- [BLMapEditor](https://github.com/juso40/BLMapEditor).
- [ImGuizmo](https://github.com/CedricGuillemet/ImGuizmo), [Dear ImGui](https://github.com/ocornut/imgui) v1.93.0.
- [LE1 lighting notes](../le1-lighting.md): why an NPC light's level filter is off.
