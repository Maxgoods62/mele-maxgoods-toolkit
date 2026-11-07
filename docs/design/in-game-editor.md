# In-game editor: design

This page records how the in-game editor works, why, and where it stops. The editor is LE1 only: `MELEBRIDGE_EDITOR` is
0 in `game/LE2.hpp` and `game/LE3.hpp`.

Keys and tabs are in the [editor guide](../editor-guide.md), file formats in [formats.md](../formats.md), test status in
[games.md](../games.md). Code comments cite this page by section number and label, so those keep their meaning.

Examples use a map `BIOA_MYM01` that streams the vanilla freighter level `BIOA_FRE31_00_LAY` and has an NPC `guard` with
pawn tag `mymod_npc_01`.

| Term | Meaning |
|---|---|
| ours | A level `level_build` built (a `levels.json` key), its actors, and the editor's spawns. |
| vanilla actor | Any other actor of a level file. |
| fork | A renamed copy of a vanilla level file with edits written in (§11). |
| session | Saved work on one map: `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json`. |
| pass | The bridge puts a session's entries back on their actors after a load (§10). |
| light environment | `DynamicLightEnvironmentComponent`: sums the level's lights for one actor while the world ticks. |
| prop light | A point light on `Gameplay_4` only. It lights our props and nothing else (§11.1). |
| room light | A light of ours on the base channels and `Gameplay_4`: walls, vanilla meshes, pawns, props (§11.2). |
| NPC light | A light of an NPC of ours, stored as an offset from its pose (§12.3). |
| level light | A point or spot component of a level's `StaticLightCollectionActor`, or SLCA (§12.4). |
| rig | A map's `characterLights` copies of a vanilla SLCA. |
| kept writer | Writes only the fields asked for, never channels, shadows or level filter (§12.1). |

## 1. What it does

F8 opens the editor over the paused game, with a free camera and a cursor. It spawns props, moves, turns, scales or
hides actors, and edits lights and effects. An edit ships in these steps:

1. Ctrl+S writes each edited actor's latest state to the map's session.
2. On every load a pass puts the session back, with no rebuild.
3. `editor_import` merges the session into the level YAML and marks the entries.
4. `level_build` or `mod_build` writes the level, its forks and `levels.json`.
5. `mod_install` installs the mod through M3, with the game closed.
6. The next pass drops each entry the installed level already shows.

The editor writes nothing into the game install.

## 2. Libraries and LE1 facts

Dear ImGui and ImGuizmo draw the UI ([THIRD_PARTY](../../tools/mele-bridge/THIRD_PARTY.md)). These LE1 facts shape the
design:

- Vanilla props are separate `bStatic` `StaticMeshActor`s with baked lightmaps. LE1 levels have no
  `StaticMeshCollectionActor`.
- LE1 is seekfree: each level file holds its meshes as forced exports, so no prop-only packages exist.
- LE1CP reverts a level file hot-loaded into the running game. So edits stay live and in a session until an install.
- `BioHUD.PostRender` and `Camera.UpdateCamera` reach `ProcessEvent` each frame, also under `WorldInfo.bPlayersOnly`.

## 3. Architecture

```
game thread (ProcessEvent hook)              render thread (Present hook)
  Camera.UpdateCamera: free cam                ImGui windows, ImGuizmo on the selection
  BioHUD.PostRender: editor tick     ----->    reads the snapshot
    commands, picks, passes, snapshot <-----   posts commands
window procedure: input queue for ImGui, swallowed while open
DirectInput8 GetDeviceState, GetDeviceData: zeroed while open
```

- Only the game thread touches game objects (`Editor.cpp`). The render thread (`Overlay.cpp`) reads a snapshot.
- Game-thread work runs under `__try`. An access violation closes the editor, gives the pause back at the next tick and
  logs the frames. F8 does not open the editor while the overlay has failed.
- Sessions, `levels.json`, the catalog and thumbnails live in `%LOCALAPPDATA%\MeleBridge\LE1\`. `imgui.ini` is off,
  because it would land next to the game's executable.

### 3.1 Calling opcode natives

A function with an opcode number has a nonzero `iNative`, and `ProcessEvent` does nothing for it. So `SetLocation`,
`Destroy` and `Trace` do nothing from outside script. The fix saves `iNative`, sets it to 0, calls `ProcessEvent` and
restores it.

- The SDK's generated wrappers are never called: they run `FunctionFlags |= ~0x400`, which sets every flag bit.
- Latent and iterator natives are refused, since both crash from outside script. Another ASI may have set every flag
  bit, so a list of names backs the flag check (`Sleep`, `MoveTo`, `AllActors` and others).

### 3.2 Free camera

- Pause: `WorldInfo.bPlayersOnly`, set at every tick while open, because the game clears it on focus changes. Closing
  gives the old value back.
- Camera: after `Camera.UpdateCamera` returns, the editor overwrites `CameraCache.POV`. The view, culling and LOD read
  that cache.

### 3.3 Overlay and input

- Input: a window-procedure subclass queues input for ImGui and swallows it while open. The game reads the mouse through
  buffered DirectInput, so `GetDeviceState` and `GetDeviceData` return nothing while open.
- Cursor: the game calls `SetCursorPos` and `ClipCursor` every frame, so both are blocked while open. ImGui draws its
  own cursor. The game's own clip comes back on close.
- Overlay failure: the overlay is off for the game run when it can't start, or when a frame throws or faults. The game
  gets its input and cursor back at once, and the game thread closes the editor at its next tick.
- Swap chain: the overlay draws on the first one that presents. A new chain on the game's window takes over once the old
  one has not presented for half a second; one of another D3D11 device turns the overlay off. Each frame checks the
  view against the chain's back buffer.

### 3.4 Picking and the gizmo

- Pick runs in `BioHUD.PostRender`: `Canvas.DeProject` of the mouse, then `Actor.Trace`. Meshes without collision get a
  ray test against primitive bounds, nearest first. It skips boxes that hold the camera, half-extents over 3000, and
  particle systems (§12.2).
- Gizmo: `ImGuizmo::Manipulate`, with matrices from the same frame's `CameraCache.POV`.
- Moves write `Location`, `Rotation` and `DrawScale3D` raw, then call `ForceUpdateComponents`. `SetLocation` refuses
  `bStatic` actors, and blocking ones where they would overlap.
- Each move, turn, scale and light-panel command names the selection it was built for. The overlay's snapshot is a tick
  old, so the game thread drops one whose selection changed before it ran, for example by a pick in the same tick.

### 3.5 Prop catalog

- Index: `catalog_index` (read-only) scans the export tables of every file the game loads, with LE1CP's copy winning. It
  lists each StaticMesh once, with the smallest file that has it.
- Own mods: the index leaves out the catalog's dev mod and our DLC folders (`MELE_OWN_DLC`, else the folders
  `levels.json` names). A built mod's copies would count twice, and a small level could become the file a mesh is copied
  from.
- Default list: `CatalogRules.cs` matches names in order and leaves about 350 meshes in. Search reaches every mesh.
  1. Excluded packages and helper meshes go first.
  2. A package whose name contains `apl` (the `biog_apl_*` prop library and its kin) stays in, with a large badge over
     600 units.
  3. Elsewhere these go out: a mesh over 600 units, a flat one (thinnest side under 2% of a longest side over 50), and
     one with a strong architecture word and no prop noun.
  4. So does a mesh no category names, and a categorised one with a weaker architecture word that vanilla places fewer
     than twice.
- Package: `catalog_build` ports the chosen meshes into `MeleCatalog_Core.pcc`, as `MeleCatalog_Core.<Group>.<Name>`,
  with the category as group. Materials and textures keep their vanilla paths, so a copy in memory is reused. It lives
  in its own dev mod, `DLC_MOD_MeleCatalog`, so a shipped mod never carries it.
- Loading: `Core.Object.DynamicLoadObject` loads one mesh and its materials. Garbage collection purges an unreferenced
  mesh within about a minute, so load, spawn and set happen in one game-thread job.
- Thumbnails: `ThumbnailRenderer.cs` renders 128×128 PNGs offline with a CPU rasterizer, using the material instance's
  colour parameters, because they can tint a mesh.
- Contract: a `catalog.json` entry names the vanilla mesh and a file with it. The session, `editor_import` and
  `level_build` port the vanilla mesh, never the catalog's copy.

### 3.6 Spawning

- Props: `Actor.Spawn` of `Engine.DynamicSMActor_Spawnable` on the player controller, so it lands in the map's level,
  then `SetStaticMesh`. The mesh comes from memory, else from `DynamicLoadObject`.
- A light environment computes only while the world ticks, so a prop spawned while paused would render black. After a
  spawn the editor releases the pause for one world tick (`TickOnce`). The switch of §11.1 then runs. When the game had
  its own pause as the editor opened, no release happens and the status line says so: the world the game paused stays
  paused, and a game unpause during the release would read as the editor's.
- Tags: `ed_<mesh>_<run>_<n>`. `<run>` is a short stamp of the game run, so tags never repeat across runs.

### 3.7 Save and reload

- Save writes one state-based file per persistent map (`GWorld`), never per level file. Spawns key by tag and level
  actors by path, the vanilla path for an actor in a fork. An entry holds the latest state, so an import replays no
  history.
- Reload: a pass puts the entries back on each load and when the loaded levels change (§10).
- Ship: `editor_import` merges the session into the YAML with `YamlEdits` (three-way per field, refusing on conflict by
  default) and runs `level_build`. It marks the entries it applied, and the bridge drops them once the installed level
  shows them. A marked entry never overrides a later YAML edit.
- A vanilla edit ships only as a fork. In a map that isn't ours it stays dev-only: the bridge re-applies it and nothing
  ships it.

## 4. Phases

Phase names label parts of the editor. Code comments and runtime messages use them.

| Phase | Parts | Section |
|---|---|---|
| 0 | Native helper, hook dispatch, overlay, input, free cam, pick, move gizmo. | §7 |
| 1 | The In this level list, spawn, turn, delete, session version 1, first import. | §8 |
| 2 | `catalog_index`, `catalog_build`, the catalog package, the thumbnail grid. | §9 |
| 3 | Re-apply on load, three-way import, full transforms, undo, delete hides. | §10 |
| 4 | Forks, prop lights and the switch, Auto light, the Lights tab, `levels.json`. | §11 |
| 5a | Effects, Kismet set, light kinds, kept writer, NPC lights, session version 3. | §12 |
| 5b | Level lights, the rig's included, live and in forks. Its in-game test is pending. | §12.4 |
| 5c | New effects from a list. Not built. | §12.5 |

| Label in the code | Meaning |
|---|---|
| "Phase 3's rules" | How an editor spawn or an actor of ours is edited (§10). |
| "a Phase 4 bridge" | A MeleBridge with no phase 5 parts (§12.1). |
| "built before Phase 5" | `levels.json` has no entry for this NPC. Run `mod_build`. |
| "not edited in Phase 5" | Sun and sky lights stay out of the editor and the build (§12.4). |

## 5. Decisions

| Decision | Why |
|---|---|
| Everything in `MeleBridge.asi` | One ASI to install, with no second window or process. |
| F8 | `BIOInput.ini` binds F1 to F5 and F9. |
| Delete hides, and a light switches off | Undo writes the old state back to the same actor. The build ships `removed: true` or `enabled: false`. |
| Sun, sky and vanilla light actors stay out | The sun is drawn per pixel, so its edit would likely change the walls (untested). No map of ours streams a light actor. |
| Checks that need ticks run with the game focused | LE1 holds `bPlayersOnly` at every frame while its window has no focus. |

## 6. Limits and open risks

- Unchecked: spawned props or hidden actors in save games, AI navigation around placed props, gamepad input while open.
- A moved vanilla mesh keeps its lightmap, its shadow baked into the floor, and its cover and texture-streaming spheres
  at the old spot. The build warns about cover, and nothing relights the mesh.
- The Normandy's master map is LE1CP's `BIOA_NOR00`, which isn't ours, so vanilla edits there stay dev-only.

## 7. Hooks and input (phase 0)

`Reflection.cpp` holds the native helper (§3.1), which `live_call` uses too. `GameThread.cpp` dispatches the hooks,
`Overlay.cpp` holds the swap-chain hooks and UI, `Input.cpp` the input traps, and `Editor.cpp` the tick.

## 8. The first editor and session version 1 (phase 1)

- The In this level list ("Phase 1's list") shows the StaticMeshes in memory, without engine and UI packages.
- A spawn lands where the view points. `Canvas.DeProject` from the screen centre gives a ray. A trace ahead up to 3000
  units takes a floor (`HitNormal.Z` over 0.7). Anything else traces down from just short of the hit, up to 3000 units.
- A session file version 1 ("Phase 1's") holds location, rotation and deleted only. Its entry has no `base`, so rule 2
  of §10 judges it. Phase 1's import may have put it in the level.
- Save merges into the earlier file through a temporary file. An unreadable file stays as `<map>.json.bad-<ticks>`.
- With no session of its own, a sideloaded level takes the newest session with an actor of that level. Spawned meshes go
  only into the map's own YAML, or the one given `takeSpawned: true`.

## 9. Catalog loading (phase 2)

The rules are in §3.5. A click spawns the mesh and sets it in one job, then releases the pause for one tick. The session
records the vanilla mesh and file (`mesh.path`, `mesh.file`) and the catalog path (`mesh.load`).

## 10. The save loop (phase 3)

A session entry ([formats.md](../formats.md)) holds the latest state. Three blocks hold other states:

| Block | Written by | What it is |
|---|---|---|
| `base` | the bridge (first edit), `editor_import` (each import) | What the YAML holds, for the three-way merge. `null` for a spawn never imported. |
| `imported` | `editor_import` | `{ before: <base before this import> }`: what the installed level may show until the next install. |
| `installed` | the bridge | When Save writes over an imported entry, its `imported.before` and `installed` go here. |

Phase 3's rules cover an editor spawn, an actor of our levels, and every actor while `levels.json` is missing. The gate
of §11.3 decides on any other actor.

- Move, Turn and Scale take any shown actor. R and G refuse a mirrored actor (`DrawScale` or a `DrawScale3D` component
  not positive), because its matrix gives no rotation or scale back. For a pawn, R is one ring about the vertical and G
  is off, since NPCs keep a yaw and no scale.
- Delete refuses pawns and controllers, `bStatic` actors (baked into the level's lighting: change them in the YAML) and
  `bNoDelete` actors.

Bridge (`Editor.cpp`):

| Part | Rule |
|---|---|
| Edit state | An edit stores all an entry holds, plus `bHidden`, `bCollideActors` and `bBlockActors` before its first hide. Every change writes edit and actor, so Save never reads the actor. |
| Base | Taken from the untouched actor before the first write. A command that changes nothing makes no edit. |
| Waiting entries | A command on an actor a waiting entry names is refused, and the pass runs at once. An edit from the stale actor would replace that entry at the next Save. |
| Delete hides | `SetHidden(true)` and `SetCollision(false, false)`. Undo restores the three flags. Move, Turn and Scale refuse a hidden actor. |
| Undo | One step per spawn, delete or drag. A step with equal before and after is dropped, and so is one whose actor is gone. A map change clears both stacks. |
| Save | The file's `base` wins, because an import may have moved it on. A field unchanged since last saved or read takes the file's value, on the actor too. A saved edit is a new state, so its `imported.before` goes into `installed`. |
| Map change | Every edit lets go of its actor, because a new actor in a reused slot would take its moves. Unsaved edits save from their stored state. An edit whose file couldn't be written stays unsaved. |
| Streaming | `ForceUpdateComponents` runs only on actors of a visible level. A level the world doesn't list is updated, as in phase 1. An actor of a level loaded but not visible gets raw writes. When a level unloads, its edits go back to pending and their undo steps go. |

A pass matches pending entries to actors of the persistent level and each `WorldInfo.StreamingLevels[i].LoadedLevel`.

- Matching: a spawn matches by tag. A level actor matches by tag when exactly one loaded actor has it, the tag is real
  (not empty, `None` or the class name), and that actor is in the entry's level and of its class. Otherwise it matches
  by path with the same class. A vanilla actor is also looked for in the map's fork (§11.4).
- Rule 1, an entry with `imported`. The actor has the entry's state: the install is done, drop it. The actor has
  `imported.before` or one of `installed`, or a spawn has no actor with its tag: the install is pending, apply. Anything
  else: the YAML was rebuilt from something else, drop it and log it.
- Rule 2, a version 1 entry with no `base`. The actor has its state: drop. Otherwise apply, with `base` set to the
  actor's state.
- Rule 3, anything else: apply.
- Applying writes the stored state, and the edit starts saved. An entry that shows an actor the build hid gets its
  class's collision back. An actor the game hid stays hidden.
- An unmatched spawn spawns again, with its mesh from memory, else `mesh.load`, else the catalog. A deleted spawn with
  no actor stays as a tombstone edit.
- Rebinding: when a built copy with a spawn's tag shows up in a later pass, the spawned actor is destroyed and the edit
  moves to the copy. An edit still equal to its entry is first judged against the copy by rules 1 and 2. A spawn the
  engine won't destroy is hidden and left out of later passes, so it never passes for the copy.
- When the copy's level unloads, the edit waits for it and spawns nothing. If that level loads again without any actor
  of the tag, the copy is gone and the edit spawns again.
- "Has a state" compares as `editor_import` does: location within 0.01 after rounding to 2 decimals, rotation as an
  orientation (axes within 0.02°), scales within 0.001.
- Timing: a pass runs on the first tick after a load. It runs again when the set of loaded and visible levels changed
  and stayed the same for 0.5 s, editor open or not. It waits while a level is being added or removed, for at most 5 s.
  A pass that faults runs again 5 s later, up to three faults in a row. After that it runs when an edit of an actor its
  entries name asks for it (at most every 5 s), when `levels.json` changes, or when the level set changes.
- A map change commits the new world only once its setup (levels, `levels.json`, session) ran to the end. A setup that
  faults runs again at the next tick, up to three tries in all, and then the editor is off in that world.

mele-mcp (`EditorImport.cs`, `YamlEdits.cs`):

| Part | Rule |
|---|---|
| Three-way merge | Per field, with `base`. Editor ≈ YAML: unchanged. YAML ≈ base: the editor's value. Editor ≈ base: the YAML's value stays. Anything else is a conflict. `removed` merges against `base.deleted`. With no `base`, the editor's value wins. |
| Conflicts | `onConflict` is `refuse` (default: neither applied nor marked), `editor`, or `yaml` (the YAML's value also goes into the state). |
| Writing | A changed pitch, roll or scale of an NPC or usable is noted and left out, and the entry takes the base's back, or the bridge would apply it forever. |
| Marking | Each applied entry gets `base` (the values now in the YAML) and `imported: { before: <previous base> }`. An entry imported again before an install keeps its `before`. |
| Dropping | A spawn that was imported and whose tag left the YAML was removed by hand, and never comes back. A dropped entry goes first to `<map>.dropped.json`, because a YAML reverted alone reads the same, and the entry may be the only copy. |
| Safety | The session is rewritten only if unchanged since it was read. Otherwise the merge runs again, up to 3 tries. |

## 11. Vanilla actors and lights (phase 4)

A fork is a vanilla level file copied with its self `Package` export renamed. No other file imports from a level file. A
map names the file in the `LevelStreamingKismet`'s `PackageName`, the volume's `VisibleChunkNames` and `InChunkName`,
and the squad's `SeqAct_WaitForLevelsVisible.LevelNames`. A vanilla transform often lives only on the archetype, so a
fork writes full effective values.

Code comments call these findings "the spike" or "§11 spike":

| Check | Result |
|---|---|
| The fork loads | Only the fork loads, also from an older save. Forked values draw as written, and component transforms are rebuilt at load. |
| Our prop beside the same vanilla mesh | 25 to 40× brighter on top in the freighter: a light environment sums the level's 463 lights with no occlusion, and a lightmap has occlusion baked in. On the Normandy the level filters starve it, hence darker. |
| Light environment knobs | None fixes it: `fAmbientScalar` scales the sky part only. With the environment off, the prop is black. |
| A private-channel fill light | Environment off, mesh and one `PointLightMovable` on `Gameplay_4` only, no shadows, radius 350, about 210 units above the crate's pivot. Brightness 0.15 gives 1.01 of the vanilla crate, which stays unchanged. This is one measurement and no calibration. |
| Spawning lights | `Spawn(PointLightMovable)` returns None, and works with `Default__PointLightMovable.bNoDelete` false around the call. A spawned light starts at brightness 1, radius 1024, with shadows, on the base channels. |
| A prop's channel change | One reattach leaves the environment's lights on the old channels and the prop black. A second fixes it. |

### 11.1 Prop lighting

A prop we place is lit by its light environment or by prop lights. With prop lights, its environment is off and its mesh
is on `Gameplay_4` alone. No vanilla light in any scanned level uses that channel.

- Falloff: `a = (1 − (d/R)²)^falloff` at distance d, and 0 from radius R out. The bridge and `level_build` share this
  rule.
- Coverage: an enabled light with `Gameplay_4` covers a prop when `a` at its bounds centre is at least 0.25 (d ≤ 0.71·R
  at falloff 2). A built prop counts only its own file's lights, because its build sees no other. In game it counts its
  own level's lights and the editor's spawned ones. A spawn counts them all.
- The switch writes a prop's static mesh components that show a mesh through a dynamic light environment, and those
  environments. A lightmapped mesh is left alone.
  - Covered state: environment `bEnabled` false, mesh channels {`bInitialized`, `Gameplay_4`}.
  - Uncovered state: environment on, and the mesh back on its own channels from before the switch, else its archetype's.
  - The bridge writes only on a difference: environment off first, then channels, with two reattaches. No prop is ever
    black.
- The build switches every props stand-in not removed. An uncovered prop gets a note: "no prop light covers it; lit by
  its light environment, brighter than vanilla: select it in the editor and press L".
- The bridge switches its spawns and the built props `levels.json` lists. Usables, `live_place` copies and vanilla
  meshes keep their environment. It runs on a fresh spawn, after each pass, when levels settle, and when a prop's or an
  ours light's step closes.
- Auto light (on by default) runs only on a fresh spawn. It adds a prop light when none covers the prop, or when the
  prop gets under 0.6 of its target. L adds one whatever the prop gets.
  - Received: `E = Σ brightness · luminance(colour) · a` over enabled prop lights. Target: what a fresh Auto light would
    give. Room lights count for neither side, since they also light the vanilla mesh beside the prop.
  - Place: out from the box along the ray to the camera by half a bounds radius, and half a bounds radius above its top.
    A camera straight above uses its yaw direction.
  - Radius: `max(120, 2.5·r, d/0.65)`, r the bounds radius and d the light's distance to the bounds centre, so `a` ≥
    0.33. A turned prop's world box grows and its sphere doesn't, so 2.5·r alone is too little.
  - Cap: a prop that would need a radius over 1024 gets no light and keeps its environment. A wider light would brighten
    props already tuned against their vanilla neighbour.
  - Seed: brightness and colour come from the last prop light edited on this map, else the nearest one that is on, else
    0.15 white. A source with brightness × luminance under 0.001 is passed over. A map change resets the seed.
- A prop drag that leaves a prop under half its target says so on the status line.
- Link: an Auto light remembers its spawn while neither was ever saved and the light was not moved or set. One undo step
  covers both. Deleting the prop deletes the light, unless another shown prop needs it. A drag takes the light along, to
  where a fresh Auto light would stand. A prop too big from every side loses the light until it fits. A light you
  deleted never follows.

### 11.2 Lights in the editor

The editor lists its spawned lights, the tagged `lights:` entries in `levels.json`, and NPC lights. It refuses a vanilla
light actor, because no map of ours streams one.

- Spawn: `PointLightMovable`, through one helper beside `CallProcessEvent` with no C++ objects in its frame. It clears
  the class default's `bNoDelete` and runs `__try { Spawn } __finally { put the bit back }`. A fault's unwind skips C++
  destructors under `/EHsc`, so a scope guard could miss it. Every field is then written, with no shadows, no level
  filter, `bForceDynamicLight`, one reattach.
- Kinds: a prop light has {`Gameplay_4`}, a room light {`BSP`, `Static`, `Dynamic`, `CompositeDynamic`, `Gameplay_4`}. A
  room light adds the same light to a prop and the vanilla mesh beside it, so a tuned match holds. Kinds are stored by
  name (`props`, `room`).
- On and off is `light.enabled`. `deleted` means only a spawned light the editor deleted. Every light is `bHidden` by
  class default, so nothing reads a light's `bHidden` as deleted, and nothing hides one.
- Delete switches a light off and never destroys it, so undo writes back to the same actor.
- Picking: while the tab is open or Alt is held, a click within 12 px of an icon selects that light. Any other click
  traces, so a light never takes a click meant for a prop.
- Gizmo: a light only moves, whatever tool T, R or G chose.
- Panel: each change sends all settings. Only the field a control changes is held to its range, so an entry built
  outside one keeps its value.

### 11.3 Vanilla actors in the editor

The gate decides on any actor outside Phase 3's rules. The actor may change only when all of these hold:

- Its class is `StaticMeshActor`, `BioInert` or `Emitter`, up the class chain by name.
- It was loaded from a file. Actors spawned at run time have no load flags.
- No loaded Kismet holds it (§12.1).
- It isn't a room shell: the largest half-extent of its primitives' world boxes, particle systems left out, is at most
  3000, measured as the file has the actor. So a scale made in the editor never turns a prop into a shell mid-drag.
- For Delete, no primitive has `bIgnoreOwnerHidden`, which would stay drawn. Pawns and controllers stay refused.
  `bStatic` and `bNoDelete` don't refuse, because hiding destroys nothing.

The ships line comes from `levels.json` and the map's streaming levels: "ships: forked into MYM01_FRE31_00_LAY", "ships:
BIOA_MYM01 streams it (forked on the next build)", or "dev-only: no map of ours streams BIOA_NOR10_07_LAY".

### 11.4 Session entries and levels.json

- Fork names follow one rule: `<map minus BIOA_>_<file minus BIOA_>`. Three tools apply it, and nothing stores the pair.
  Back from a fork, the rest is `BIOA_<rest>`, or `<rest>` when it already starts with `BIO`.
- A vanilla entry names the vanilla file, also when the actor stands in a fork. Matching tries the path, then the same
  path in the map's fork.
- A light entry adds a `light` block (`kind`, `radius`, `brightness`, `color`, `falloff`, `enabled`). Only location
  counts, and rotation and scales are the actor's. Save's rule for unchanged fields applies per field. A spawned light
  rebinds to its built copy by tag.
- `HasLight`: location, `bEnabled` = `enabled && !deleted`, radius (0.01), brightness and falloff (0.001), colour and
  kind (exact). Rotation, scales and `bHidden` don't count.
- `levels.json` is written by `level_build`, and each build replaces only its own key. It describes the last build, and
  the install may differ.
- Ours: a `levels.json` key, or a level whose linker's file lies in the `dlc` folder and whose name is not a fork by the
  rule. Without `levels.json`, every actor keeps Phase 3's rules.

### 11.5 YAML

The fields are in [yaml-reference.md](../yaml-reference.md). `vanilla` keys are `<file>.<actor>`, and the file must be
in `streaming.levels`. A field left out keeps the file's value. A light is switched off with `enabled: false`, never
removed. The build finds actors again by tag, so tags are unique across `npcs` (their lights too), `usables`, `props`
and `lights`.

### 11.6 level_build

Every check runs before the first write: the forks are made in memory, then the level, a new one too, is filled in
memory, so a refusal or a bad entry writes nothing. The writes then go forks, level, `.m3cd`, `levels.json`, deleted
forks, GlobalTlk. Forks come first, because the map never names a file not written yet (`ForkBuilder`). The level's
description comes right after it, so a GlobalTlk that fails leaves it describing the level on disk; `BuildWrites` names
what was written in the error.

- The build copies the highest-mounted copy outside our DLC into memory and renames it. `PackageGUID` is the name's MD5,
  since two packages with one GUID are one package to the engine.
- `removed` writes `bHidden` true and collision false on the actor, and `BlockRigidBody` false on its
  StaticMeshComponent, because physics bodies are made at level start from that field.
- These refuse with the YAML line: a missing actor, a class outside the allow-list, an actor a `SeqVar_Object` or
  `SeqVar_ObjectList` of the file names, and `removed` with a `bIgnoreOwnerHidden` primitive. A refusal in any file
  writes no fork.
- `<file>_LOC_<lang>` packages are copied as `<fork>_LOC_<lang>`. The game loads a level's LOC by its streamed name, and
  without them the fork loses its dialogue.
- These warn: cover within 100 units, a room shell, and a non-uniform scale of a mesh with no cooked collision at that
  scale. The game makes uniform scale's collision at load.

Streaming renames the `LevelStreamingKismet` in place, because a second entry would stream both. Every
`SeqAct_WaitForLevelsVisible`'s `LevelNames` follow: a wait for a name not streamed never finishes, and the squad never
spawns. Entries are only added, so a file that leaves `streaming.levels` stays streamed: an entry naming its fork names
the vanilla file again, and the fork is deleted once the level is saved. Only a name that is `ForkName(map, file)` of a
file loaded outside our DLC counts as a fork, never the mod's own sub-levels. Only `StreamingLevels` counts: an entry off
it (a pair's other name) keeps no fork. A changed `volume` is imported, and the volumes in the shape the build writes
(one `Default` state over listed names) are removed with it.

Lights go through `ApplyLight`. A light with the entry's tag is edited in place, else its template is copied. Every
light gets `Location`, `Tag`, `CachedParentToWorld.WPlane`, the component's `bEnabled`,
`bOnlyAffectSameAndSpecifiedLevels` false, and `Radius` and `Brightness` when given. A new copy gets new `LightGuid` and
`LightmapGuid`. A `lights:` entry also gets the actor's `bEnabled`, `LightColor`, `FalloffExponent`, `CastShadows`
false, `bForceDynamicLight` true and its kind's channels, and drops `OtherLevelsToAffect`. NPC lights are in §12.3. A
light whose channels are a kind's in full, found under an NPC, was a `lights:` entry: it takes its template's channels,
`CastShadows`, `bForceDynamicLight` and, when it has none, `OtherLevelsToAffect` back.

### 11.7 editor_import

- Vanilla: an entry becomes `vanilla.<file>.<actor>`, three-way per field, with the YAML's missing fields read from the
  vanilla file through archetypes.
- It refuses what the build refuses (`ForkBuilder.Refusal`), and a room shell other than an effect, where the build only
  warns. An entry in a level the YAML doesn't stream is skipped as dev-only.
- Lights: a spawned light becomes `lights.<tag>`. An edited one merges three-way per field, with the tolerances of
  §11.4. A deleted one already in the YAML gets `enabled: false`.
- NPC lights merge the same way into `npcs.<name>.lights` (§12.3).
- Fork names map back to their vanilla file by the rule, because the fork is a build output and never a source.

### 11.8 Measured behaviour

- The built light reads 6.7% brighter than the editor's, inside the ±10% band, so a tuned brightness ships as is.
- Auto light's 0.15 is only a start: 0.08 matched in the freighter. Later lights take the last tuned brightness.

## 12. Effects, NPC lights and level lights (phase 5, 5b)

The freighter has no light actors: 5 SLCAs hold its lights, mostly baked. A baked light's edit has no render proxy, so
it reaches only characters and props through their environments, and the walls keep the bake (S2, S3). Phase 4's writer
would rewrite an NPC's or level's channels, shadows and filter, hence a second writer.

### 12.1 Shared parts (5a)

A light's kind comes from its source, read once when the editor tracks it. Channels never decide it.

| Kind | Source |
|---|---|
| `props`, `room` | `lights:` entries and editor spawns. Only these switch between the two. |
| `npc` | Its tag is in its own level's `levels.json` `npcs.*.lights` (§12.3). |
| `vanilla` | A point or spot component of an SLCA, the rig's included (§12.4). |

- Kept writer: for `npc` and `vanilla` lights. It reads the light and writes a field only on a difference: radius beyond
  0.01, brightness and falloff beyond 0.001, colour RGB exact, on/off. A field the entry lacks is never written, and
  colour keeps the alpha, so an archetype's colour stays.
- Hard guard on our writer: it returns and logs once for a light of kind `npc` or `vanilla`, or one not ours. A call
  site the dispatch missed then does nothing. A collection has its own guard (§12.4).
- Session version 3 is written when an entry, `base`, `imported.before` or `installed` holds an `npc` or `vanilla`
  light, else 2. A Phase 4 bridge would apply such an entry through its own writer, so it refuses version 3. Current
  readers take 1, 2 and 3. Dropping entries recomputes the version, and a version 1 file stays 1.
- `levels.json` adds `npcs` and `characterLights` per level. It stays version 1, and readers skip unknown keys.
- The bridge reads it on a map change and when its write time changes, checked on F8, before each re-apply pass (which
  also runs with the editor closed) and every 1000 ms while open, because `npc_place` rebuilds the level while the map
  runs.
- Kismet set: every actor a loaded `SeqVar_Object.ObjValue` or `SeqVar_ObjectList.ObjList` names, rebuilt when first
  needed after a map change or a level turning visible. It reads only loaded levels, and the fork, which reads the whole
  file, has the last word.
- Environment reset (5b): after a B or C light's step closes, each enabled environment whose owner is within the old or
  new radius plus 100 resets (`SetEnabled` false, then true), and the world runs 1.5 s (`TickFor`). Drags don't reset,
  because ticks move NPCs. An environment refreshes at most once a second (`MinTimeBetweenFullUpdates` 1.0) and blends
  over 0.3 s. A prop switched to prop lights stays off. S2 ran with the world running, so whether one tick is enough is
  unproven. NPC lights need no reset (S5).
- Icons: disc ours, diamond NPC, square level, star effect. Hollow when off. Room adds a ring. A level light's type
  letter stands beside it, and the Lights tab has a legend line.
- Which show: ours, the NPCs', the rig's, edited lights and the selected one, always. With Level lights on and the
  Lights tab open or Alt held, other level lights within 30 m (3000 units), baked ones with Baked too, at most 150. An
  edited effect always. With the Effects tab open or Alt held, others within 30 m, at most 50.
- Picking: among icons within 12 px, one drawn under the cursor beats one only near it. Then the one nearest the camera
  wins, so a light in the next room never does.
- Status wording: a line is orange only when it starts with "not " or "the engine refused", so refusals read "not
  <verb>: <reason>".

### 12.2 Effects (5a)

- An effect loops when `EmitterLoops` is 0 in its first level of detail's required module. One that ends shows nothing
  once its 1.5 s ran.
- The bounds fallback skips particle systems, because a dust card's box is about 1000 units across. Effects are picked
  by icon. The room-shell test leaves them out too.
- Delete hides with `SetHidden(true)`, and `bHidden` reads as deleted for an emitter.
- An effect's particles move only in Tick. After its step closes, the editor calls `KillParticlesForced`, then
  `ActivateSystem(false)`, and runs the paused world 1.5 s (`TickFor`). A hidden one is only killed, and no world runs
  (S4).
- The open editor resets `bPlayersOnly` at every tick, so this release is its own. It is skipped, with a message, when
  the game had its own pause when the editor opened, because it couldn't then tell the game's 0 from its own.
- Refusals hold in our levels too (`EffectRefusal`), since the game spawns impacts and shields into the map's level: no
  load flags, one the Kismet set holds, `bAutoActivate` false or no template, and scale, since how particles follow a
  scale is unchecked.
- YAML: a `vanilla:` entry. `scale` on an emitter is an error. The fork also writes `OldPosition`, which only feeds the
  particle system's velocity.

### 12.3 NPC lights (5a)

- The Lights tab has a section per NPC, with no lights too: "Guard (mymod_npc_01)", with Add light. An NPC whose level
  isn't visible shows as away.
- Reference pose: offsets are measured from the pawn's `Location` (its collision centre) and yaw. The pose is the pawn's
  edit in this session, else the built pose from `levels.json`, never the live pawn. A cross and a yaw tick mark it.
- Pose check: before an edit, and before a pass applies an entry, the pawn must stand within 5 units and 2° of its built
  pose, unless the editor moved it. Otherwise it is refused and the entry waits. The game never moves our NPCs (S5), so
  the pawn is the check.
- The panel has no kind control. A note says it keeps its template's channels.
- A built NPC light is a `PointLightToggleable`, not `bMovable`. It still moves live with a raw `Location` and
  `ForceUpdateComponents(true, false)` (S5).
- Offset: with `d = L − P` and `θ = yaw·2π/65536`, forward `f = d.x·cos θ + d.y·sin θ`,
  right `r = −d.x·sin θ + d.y·cos θ`, up `u = d.z`. The build's place is `P + (f·cos θ − r·sin θ, f·sin θ + r·cos θ, u)`.
- Carry: a move or turn of one of our NPCs moves its lights at every drag command, in the same undo step. The step holds
  each light's before and after, so closing it reads no game memory. A carried light without an edit gets none, because
  the build places it from the new pose. One with an edit keeps its offset.
- Add light spawns a `PointLightMovable` with the default template's look: channels {`bInitialized`, `Dynamic`}, no
  shadows, `bForceDynamicLight`, no filter, white with alpha 0, falloff 2. Offset `[60, 0, 80]`, radius 100, brightness
  1.2, tag `<npc tag>_light_<run>_<n>`. No tick runs, because a tick could move NPCs. A pass spawns an entry again with
  the same look.
- Session: `light.kind: npc` and `npc: { tag, offset }`. `location` stays absolute. The live light has the state within
  0.05 on each axis, because offsets written to 2 decimals move a built light by up to 0.007, and a float at the
  Normandy's heights steps by 0.004.
- `editor_import` merges NPC pawns first, then their lights, by tag: offset within 0.02 per axis, written to 2 decimals.
  A spawned light goes to the YAML whose `npcs` has its NPC's tag. An entry whose NPC differs from the item's owner is
  refused.
- `level_build` writes `color`, `falloff` and `enabled` when given, else the template's. The actor's `bEnabled` is
  written when the light is off or already has one. Channels, shadows and `OtherLevelsToAffect` stay the template's. A
  tagged light no entry names stays as built, with a note: switch it off with `enabled: false`.
- [LE1 lighting](../le1-lighting.md) says why an NPC light's level filter is off.

### 12.4 Level lights (5b)

The list holds the point and spot components of each SLCA in the persistent and loaded streaming levels, through
`Components`, never `BIOG_UIWorld`. At run time `LightComponents` is empty. The Lights tab shows "Character lights
(characterLights)" always, "Level lights" within 30 m on its toggle, and type B with "Baked too". A collection of our
levels no `characterLights` item names is refused, because no build writes its lights.

The bridge and the build read the type by one rule, in this order. The panel header and banner name it:

| Type | Rule | What a change reaches |
|---|---|---|
| B baked | `bHasLightEverBeenBuiltIntoLightMap` and `UseDirectLightMap` | Characters and props. The walls keep the bake. |
| D dynamic | `bForceDynamicLight` or the Dynamic channel | Drawn live, so what you see ships. |
| U special channel | Any channel outside BSP, Static and CompositeDynamic | Only meshes on that channel, such as the freighter's doors. |
| C characters only | Anything else | Characters and props, never the walls. |

- Edit target: the selection is (owner SLCA, light component), keyed by canonical path. Location is where `Translation`
  puts it, rotation zeros, scales 1.
- Hard guard: pose, `bHidden`, collision and reattach writes return on an SLCA (`Shielded`), logged once per collection,
  because a reattach would redo all its lights.
- Move: `Translation = M⁻¹·(world − origin)` from the live `CachedParentToWorld`, a full 3×3 inverse, since a scaled
  parent scales `Translation`. Then `SetTranslation` (S1), at every drag command. A level not visible gets a raw write.
- Scaled parents: a row length off 1 by more than 0.001 refuses moves in the bridge (`ParentScaleOf`). The build uses
  0.0001 and refuses with the YAML line. S0 found none in the freighter or its rig.
- Fields go through the kept writer, then `ForceUpdate(false)` (S1), then the reset for B or C. A B or C light gets no
  proxy from the refresh (S2), so the walls are never lit twice.
- Refusals: turn on a spot, turn on a point and scale ("a light only moves"), Delete of one already off.
- Delete switches off. A B light's status adds "switched off for characters and props only: the walls stay lit".
- Ships: a rig light reads "ships: built into BIOA_MYM01 (characterLights copy of BIOA_FRE31_00_DSG)". Others read as
  their collection would.
- Session: an entry per light with `owner` and `light.type`, version 3. A pass matches by path and class, then in the
  map's fork.

YAML, in the `vanilla:` section. A key has two dots:

```yaml
BIOA_FRE31_00_LAY.StaticLightCollectionActor_127.PointLight_35_LC: { brightness: 1.2 }
BIOA_FRE31_00_DSG.StaticLightCollectionActor_68.PointLight_2_LC: { brightness: 0.6 }   # a rig light, by its source file
```

Its file must be in `streaming.levels`, or `<file>.<collection>` must be a `characterLights` item. Errors with a line:
`rotation`, `yaw`, `scale`, `scale3d` or `removed` ("lights move, they don't turn, scale or go: use enabled: false"),
`kind`, a sun or sky component, and a key matching neither rule.

`editor_import` merges these three-way like vanilla actors. An entry whose collection is a rig copy goes to its source
file's key.

`level_build`:

- Fork: on the component export it writes `Brightness`, `LightColor` (alpha kept), `Radius`, `FalloffExponent`,
  `bEnabled`, and `Translation = M⁻¹·(location − origin)` from the collection's binary matrix at the light's index. LE1
  applies it at load (S3), so the binary data stays.
- Never written: `LightGuid`, `LightmapGuid`, `bPrecomputedLightingIsValid`, `bHasLightEverBeenBuiltIntoLightMap`,
  `UseDirectLightMap`, `LightAffectsClassification`, `LightingChannels`, `bForceDynamicLight`, `CastShadows`,
  `bOnlyAffectSameAndSpecifiedLevels`, `OtherLevelsToAffect`. Lightmaps name lights by GUID.
- Rig: each light of the map's copy gets each field from its entry, else the source file's value, so a deleted entry
  restores it.
- The receipt names the light and its type, then old → new values.

### 12.5 New effects (5c, not built)

Effects are forced exports inside levels, so `DynamicLoadObject` by vanilla path should fail. A second catalog file
would port them. `Spawn(Engine.Emitter)` then `SetTemplate` would place one, since `EmitterSpawnable` destroys itself
when a finite system ends.

### 12.6 Spikes S0 to S5

Each settled an engine claim a design choice rests on. All ran with the editor open and paused, except S0 (offline) and
S2.

| Spike | Question | Result |
|---|---|---|
| S0 | Which matrix is a light's parent? Any scaled? | The binary matrix equals `CachedParentToWorld` for all 506 lights there. Row lengths are 1 within 1.2e-7, and no light has a `Translation`. |
| S1 | Does one light move and refresh alone? | `SetTranslation` moved it exactly. `ForceUpdate(false)` refreshes one light, and a raw write shows nothing until then. The collection's `ForceUpdateComponents(true, false)` changed every other light's index, so it is never used. |
| S2 | Do B and C lights reach pawns and spare walls? | With the world running, yes: a baked light 0.4 → 4 raised an NPC's key light by 40%. Walls held a ratio of 1.000. Environments took edits within 2.5 s, and never update while paused. |
| S3 | Does a fork with light components load as written? | Yes, with `Translation` exact and a door light's proxy kept. Walls compare 0.997 to 1.000. |
| S4 | How do effects behave live? | A raw `Location` moves an emitter, and old particles linger. `KillParticlesForced` plus `ActivateSystem(false)` clears the old spot. `SetHidden(true)` hides at once, paused. A raw-moved NPC stayed put through 11 s of release. |
| S5 | Does a spawned Movable light a face like the built Toggleable? Does the game move our NPCs? | Within 1.7%. A built Toggleable moved exactly by a raw `Location`, paused. NPCs kept X, Y and yaw through hundreds of reads. |

### 12.7 Findings

- The LE1 SDK's `FRwVector3` is 12 bytes in C++ and 16 in the engine, so every later field of `UParticleSystem` is 8
  bytes off. The bridge reads those at reflected offsets (`PropertyOffset`), and `static_assert`s pin the rest. Check
  any field after an `FRwVector3` against reflection first.

Out of scope: re-baking lightmaps, turning a baked light dynamic, sun and sky lights, changing channels, shadows or
filters of level or NPC lights, aiming spots, scaling effects, effects Kismet holds, forks of the Normandy's files.
