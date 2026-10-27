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

F8 in the game opens a level editor drawn over the game: free cam, click a prop to select it, then drag the gizmo of one of
three tools. Del deletes the selection, Ctrl+Z undoes, Ctrl+Y (or Ctrl+Shift+Z) redoes, Ctrl+S saves. **Props** spawns a
prop where the view points, from one of two tabs:

- **Catalog:** a grid of thumbnails of the curated props mele-mcp's `catalog_build` writes, with a category list and a
  search. A tooltip gives the size, triangles, how often the game's levels place it and the file it comes from.
- **In this level:** the meshes in memory, as a list (Phase 1's).
- **Lights:** Prop light and Room light add a light over where the view points; the list selects one.

It is Phase 3 of [docs/07](../../docs/07-ingame-editor.md#10-phase-3-the-save-loop): saved edits come back when the map
loads again, and ship with their tilt and scale. Phase 4's first half ([§11](../../docs/07-ingame-editor.md#11-phase-4-vanilla-actors-and-lights))
adds the props of vanilla levels: moved, turned, scaled or hidden in game, they ship in a fork of their file. See
[Vanilla actors and forks](#vanilla-actors-and-forks). Its second half lights our props with prop lights instead of their
light environment, and edits lights: **L** adds a prop light for the selected prop, **Auto light** (toolbar, on by default)
adds one for a fresh spawn that needs it, and Alt+click picks a light by its icon. See
[Prop lighting and lights](#prop-lighting-and-lights).

| Key | Tool | What the gizmo does |
|---|---|---|
| T | Move | Arrows and planes in world space. Ctrl snaps to 10 units from where the drag started. |
| R | Turn | Three rings in the prop's own space (pitch, yaw and roll). Ctrl snaps to 15 degrees from where the drag started. An npc gets Phase 1's single ring about the vertical: the level YAML keeps a yaw for npcs. |
| G | Scale | Handles in the prop's own space, one axis or all three (the centre); it changes `DrawScale3D`, never `DrawScale`. Ctrl rounds to 0.1. Off for npcs (they keep a yaw and no scale). |

- **Refusals:** R and G are refused on a mirrored actor (a `DrawScale` or `DrawScale3D` component that is not positive):
  its matrix gives no rotation or scale back. Move, turn and scale refuse a hidden actor. Move, turn, scale and delete
  refuse an actor that a session entry still waiting names (or an edit waiting for its actor or its built copy) until the
  re-apply pass, asked for at once, has applied it: an edit made from the actor as it stood would replace the entry at the
  next Save. A vanilla actor that is not a prop is refused too, and gets no gizmo ([the gate](#vanilla-actors-and-forks)).
  The status line says why.
- **Delete hides:** `SetHidden(true)` and `SetCollision(false, false)`, not `Destroy`: undo shows it again with the flags it
  had. In our own levels, pawns, controllers, `bStatic` and `bNoDelete` actors are still refused; a vanilla prop is not
  (hiding destroys nothing). Deleting a vanilla static mesh says that its baked shadow stays (it is in the floor's lightmap)
  and its physics body stays until the build. A light is switched off instead ([lights](#prop-lighting-and-lights)).
- **Undo and redo:** one step per spawn, delete or drag, 200 at most. The game thread opens a drag's step at its first
  command and closes it when the drag ends (or before any other command); a drag that comes back where it started leaves
  none. Undoing writes the whole state back (shown or hidden, location, rotation, scale, a light's settings) and marks the
  edit unsaved. A drag on the light panel is one step too, from the first change to the release. A spawn and the prop light
  Auto light made for it are one step while the light is linked to it, and so are a drag of the prop and that light
  following it ([link](#prop-lighting-and-lights)). A step whose actor is gone is dropped, with a status line. A map change
  clears both stacks. The toolbar's Undo and Redo name the step (`Undo move ed_…`, `Undo spawn ed_… and ed_light_…`).

- **Threads:** the game thread does everything that touches game objects (`Editor.cpp`, after `BioHUD.PostRender` and
  `Camera.UpdateCamera`). The render thread draws [Dear ImGui](https://github.com/ocornut/imgui) and
  [ImGuizmo](https://github.com/CedricGuillemet/ImGuizmo) from a snapshot, in a hooked `IDXGISwapChain::Present`
  (`Overlay.cpp`). The window thread queues mouse and key messages for ImGui (`Input.cpp`).
- **Input:** while the editor is open the game gets no mouse or keys: the window's messages are swallowed, the DirectInput
  mouse reads zero, and the game's `SetCursorPos`/`ClipCursor` calls are blocked. F8 and Alt+F4 always work.
- **Pause:** `WorldInfo.bPlayersOnly`, as photo mode does. Closing gives the old value back.
  - A spawned prop's `DynamicLightEnvironment` only updates when the world ticks: paused, it would render black. After a
    spawn, when a drag ends, after an undo or redo, and after a re-apply applied, spawned or rebound props, the editor lets
    the world tick once and takes the pause back at its next tick. That release is its own: it isn't logged or counted as the game
    ending the pause.
- **Save:** one session file per map, `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json`, outside the install. It holds the
  latest state of each actor spawned, moved, turned, scaled or deleted: spawned ones by tag, level actors by path. Each
  edit keeps that state itself, so a save never reads an actor. mele-mcp's `editor_import` writes it into the level YAML
  and builds it. A field the edit hasn't changed since it was last saved or read takes the file's value (an import may have
  written the YAML's into it while the game ran), on the actor too: each pose field, each field of a light's settings, and
  `deleted`. The status line counts the entries written; a spawn
  deleted before its first save leaves nothing, is named apart and is not counted in `Save (N)`, and a Save of only such
  spawns rewrites nothing. A map change saves the unsaved edits first; those of a map whose file could not be written are
  kept, and the next Save tries again. Until a save writes every map, the toolbar says `not saved`, with why on a line of
  its own (the status line fades, and the first pass after a load replaces it).
- **Re-apply on load:** after a map loads, and again each time the loaded or visible streaming levels change and then stay
  the same for half a second (editor open or not), a pass puts the session's entries back on their actors. See
  [Re-applying a session](#re-applying-a-session).
- **State:** the `editor` op (`live_editor` in mele-mcp) returns open, camera, selection (rotation, `DrawScale`,
  `DrawScale3D`, why delete or move refuses it, whether it ships, the file its level was loaded from while the game keeps
  the linker, a light's settings as `light`, a switched prop's `propLighting`: `covered`, `received`, `target`, and
  `autoRadius`, the radius a fresh Auto light would need from the view), edits (with
  `saveProblem`), undo and redo, the session's re-apply (file, applied, dropped, pending with why, the levels the last pass
  saw), `levelsJson` (file, problem, DLC folder, levels, how many props and lights tags it lists), `autoLight`, `lights`
  (the listed lights of the visible levels while the editor is open: path, tag, location, settings), the status line, the
  projection check, the catalog (props read, thumbnails on the GPU) and counters. Diagnostics shows the same session and
  levels.json summaries.
- **Tested 2026-10-27** in the freighter: Phase 0's seven steps ([docs/07 §7](../../docs/07-ingame-editor.md#7-phase-0-the-test-build)),
  the arrows within 0.6 px of where the engine projects the prop; Phase 1's five ([§8](../../docs/07-ingame-editor.md#8-phase-1-the-first-usable-editor)),
  a crate spawned and one deleted in game, both shipped by build + install; Phase 2's catalog on the Normandy
  ([§9](../../docs/07-ingame-editor.md#9-phase-2-catalog-and-thumbnails)); Phase 3's five ([§10](../../docs/07-ingame-editor.md#10-phase-3-the-save-loop)):
  a tilted, scaled catalog prop kept through a reload, then imported, built and installed, and a conflict refused.

### The session file

Version 2 (UTF-8). The bridge reads versions 1 and 2 and writes 2; a file of another version is left as it is, and one
that can't be parsed is moved aside (`<map>.json.bad-<tick>`) at the next Save.

```json
{ "version": 2, "map": "BIOA_CLD01", "saved": "2026-10-27T04:00:00Z",
  "actors": [ { "tag": "ed_Crate_XLarge_03_fdsdl_1", "path": "BIOA_CLD01.TheWorld.PersistentLevel.DynamicSMActor_Spawnable_3",
                "level": "BIOA_CLD01", "class": "DynamicSMActor_Spawnable", "spawned": true,
                "mesh": { "path": "biog_apl_phy_crate_xlarge_01_l.Crate_XLarge_03", "file": "BIOA_STA30_02_DSG",
                          "load": "MeleCatalog_Core.Crates.Crate_XLarge_03" },
                "location": [1200.5, -340, 96], "rotation": [0, 16384, 2730], "scale": 1, "scale3d": [1, 1, 1.5],
                "deleted": false, "base": null },
              { "tag": "ed_light_t3k9q2_1", "path": "BIOA_CLD01.TheWorld.PersistentLevel.PointLightMovable_0",
                "level": "BIOA_CLD01", "class": "PointLightMovable", "spawned": true, "mesh": null,
                "location": [1180, -300, 330], "rotation": [0, 0, 0], "scale": 1, "scale3d": [1, 1, 1],
                "light": { "kind": "props", "radius": 350, "brightness": 0.15, "color": [255, 255, 255], "falloff": 2, "enabled": true },
                "deleted": false, "base": null } ] }
```

| Field | What it holds |
|---|---|
| `location`, `rotation`, `scale`, `scale3d` | The actor's state: rotation in Unreal units (65536 = 360°), `scale` is `DrawScale`, `scale3d` is `DrawScale3D`. |
| `deleted` | Hidden by Delete. A spawn deleted before it was ever saved is not written. On a light, only a spawned light the editor deleted (switched off). |
| `light` | Lights only: `kind` (`props` or `room`), `radius`, `brightness`, `color` (0-255), `falloff` (`FalloffExponent`) and `enabled`, its on/off. The same block in `base`, `imported.before` and `installed`. A light's `rotation` and scales are the actor's: only its location counts. |
| `mesh` | Spawned props only (`null` otherwise, lights too): the vanilla mesh and a vanilla file with it, and `load`, the catalog package's copy (`null` for a mesh from the In this level list). |
| `base` | What the level YAML holds, for `editor_import`'s three-way merge: the actor before the editor first changed it, `null` for a spawn never imported. Save keeps the file's `base` when the entry has one (an import moves it on), with its `deleted` (whether the YAML removes the actor) when an import gave it. A base the editor takes from a shown actor has `deleted: false` (the build that made it did not remove it); one of a version 1 entry taken from a hidden actor has none (the game may have hidden it). |
| `imported` | Written by `editor_import` only: `{ "before": <the base before the import, plus deleted> | null }`. Save leaves it out: a saved edit is a new state. |
| `installed` | Written by the bridge only: when Save writes over an imported entry, its `imported.before` (and its own `installed`). The next import's `before` is the base, and the install may still show any of these; `editor_import` leaves them as they are. |

### Re-applying a session

A pass reads the actors of the loaded levels (the persistent level and each streaming level's `LoadedLevel`, through
`ULevelBase::Actors`) and matches each waiting entry:

- **Matching:** a spawn by its tag (a built copy has it); a level actor by tag when exactly one loaded actor has it, it is
  a real tag (not empty, `None` or the class name), and that actor is in the entry's level and of its class; else by path
  with the same class.
- **Deciding:** an entry with `imported` is dropped when the actor already has its state (the install is done), applied
  when the actor still has `imported.before` or one of `installed` (or, for a spawn, no actor has its tag yet), and
  dropped otherwise (the YAML was built from something else since). A version 1 entry is dropped when the actor already has its state (Phase 1's
  import put it in the level), else applied with the actor's state before applying as its base (a spawn spawned again:
  its built copy's, once rebound). Anything else is applied. Dropped entries are removed from the file. States compare as
  `editor_import` compares them: location to 0.01, rotation as an orientation, scales to 0.001. A light compares its
  location, its component's `bEnabled` against `enabled && !deleted`, the radius to 0.01, the brightness and falloff to
  0.001, and the colour and kind exactly; its rotation, scales and `bHidden` don't count (every light is `bHidden` by class
  default).
- **Applying:** the stored state is written (deleted: hidden); the edit starts saved. An entry that shows an actor the
  build hid (the YAML state it was judged by says removed, and the actor is hidden) shows it again with its class's
  collision; one the game hid stays hidden. A spawn no loaded actor has the tag of is spawned again, with its mesh from
  memory, else from `mesh.load`, else from the catalog entry with that vanilla mesh; a light as a new `PointLightMovable`
  with its settings; a deleted one is kept without an actor. When a built copy with its tag shows up later, the spawn is destroyed and the edit moves to the copy, judged
  first as its entry would have been when it is still that entry: installed or built from other YAML, the edit takes the
  copy as built and the entry leaves the file.
- **Streaming:** an actor of a level loaded but not visible gets the raw writes only, no component update; when the level
  turns visible, those actors' components are updated. No pass runs while a level is still being added to the world or
  taken from it (`bShouldBeVisible` ≠ `bIsVisible`), unless that lasts 5 s. When a level unloads, its edits wait for it
  again (with their state) and their undo steps go.
- **Waiting:** an entry whose actor isn't loaded waits for a later pass, with why (Diagnostics and the `editor` state list
  them). A pass that faults is not tried again until the loaded levels change; the entries it had applied or dropped leave
  the list then.
- **Forks:** an entry of a vanilla actor names its vanilla file; once our map streams a fork of that file, the actor is
  found there by the rule below (its path, then the same path in the fork), with the tag the same way.

### Vanilla actors and forks

A vanilla actor moved, turned, scaled or hidden in one of our maps ships in a **fork**: a renamed copy of its level file,
built by `level_build` from the YAML's `vanilla:` section, that the map streams instead
([docs/07 §11](../../docs/07-ingame-editor.md#11-phase-4-vanilla-actors-and-lights)).

- **Fork names follow one rule:** `<map minus BIOA_>_<file minus BIOA_>`, so `BIOA_CLD01` streams `BIOA_FRE31_00_LAY` as
  `CLD01_FRE31_00_LAY`. Nothing stores the pair: `level_build`, `editor_import` and the bridge each apply the rule. Back
  from a fork, `<map minus BIOA_>_<rest>` is `BIOA_<rest>` (`<rest>` when it already starts with `BIO`).
- **Sessions name the vanilla file:** an edit of an actor in a fork is saved with its vanilla `path` and `level`
  (`BIOA_FRE31_00_LAY.TheWorld.PersistentLevel.StaticMeshActor_69`), keyed the same way, and matched in either file.
  Matching never reads `levels.json`.
- **The gate:** an editor spawn and an actor of one of our levels keep the rules above. Any other actor is a vanilla one, and
  move, turn, scale and delete take it only when:
  - its class is `StaticMeshActor` or `BioInert` (up the class chain, by name); anything else says why: "a BioDoor: doors
    carry Kismet and door volumes", "a PathNode: the AI's paths", and so on. Pawns and controllers stay refused;
  - it was loaded from a level file (a linker, or the load flags its export gave it), not spawned at run time;
  - no part's bounds reach more than 3000 units from their centre: "a room shell (110 m): hiding it opens the room" (the
    size is the box's, across). The bounds are measured as the level file has the actor, before the editor's own edits
    (as `editor_import` measures it), so a scale or a turn made in the editor never makes a prop a room shell;
  - for delete, no part of it ignores its owner's `bHidden` (`bIgnoreOwnerHidden`): hidden, it would stay drawn.

  `bStatic` and `bNoDelete` don't refuse a vanilla prop: hiding destroys nothing.
- **Ships or not:** the Selection window says whether an edit of the vanilla actor ships: `ships: forked into
  CLD01_FRE31_00_LAY` (it stands in a fork), `ships: BIOA_CLD01 streams it (forked on the next build)` (the running map's
  `levels.json` streams list the file), or `dev-only: no map of ours streams BIOA_NOR10_07_LAY` (the bridge re-applies it,
  no build ships it: the Normandy's own files, for one). A map of ours not in `levels.json` yet that streams the file says
  `ships if <map>'s YAML streams it`. An actor the gate refuses shows only why, no ships line.
- **A level the bridge can't place:** not a `levels.json` key, not a fork, not a file a key streams, and its linker
  detached (its file unknown). A level of ours that only a `level_build` of another YAML has run since Phase 4 (each build
  writes only its own key) then reads as vanilla; its refusals and its `dev-only` line add `<level> is not in levels.json
  and its file can't be read: if it is ours, run mod_build`.
- **Without `levels.json`** every actor keeps the rules above (no class gate, `bStatic` refused for delete), and opening the
  editor says `levels.json missing: run mod_build`.

`levels.json` (`%LOCALAPPDATA%\MeleBridge\LE1\levels.json`, UTF-8) is written by `level_build` (each build replaces its own
level's key) and read by the bridge on each map change:

```json
{ "version": 1, "dlc": "DLC_MOD_ClaudeChakwas",
  "levels": { "BIOA_CLD01": { "streams": ["BIOA_FRE31_00_LAY"], "props": ["ed_Crate_XLarge_03_fdsdl_1"], "lights": [] },
              "ClaudeNPC_NOR10": { "streams": [], "props": [], "lights": [] } } }
```

| Field | What the bridge does with it |
|---|---|
| `levels` | Its keys are our level files (built from a YAML's `package`, sideloaded ones too; never a fork): their actors keep the rules above. A level not rebuilt since is ours too when the file its linker names lies in the `dlc` folder and its name is not a fork by the rule (only while the game keeps the level's linker attached: the selection's `file` in the `editor` state shows it). |
| `streams` | The vanilla files the level streams, for `ships`. |
| `dlc` | The DLC folder our level files lie in. |
| `props` | The tags of the level's built props: the bridge switches them between prop lights and their light environment, as it does its own spawns. |
| `lights` | The tags of the level's `lights:` entries: the editor lists them, and they may be selected, moved, set and switched off. |

A missing or broken file reads as no file; a missing key, or one of another type, as empty; an unknown key is skipped. A
version other than 1 is not read.

### Prop lighting and lights

A prop's light environment lights it 25-40 times brighter than the vanilla meshes beside it (the freighter). A prop light
fixes that ([docs/07 §11](../../docs/07-ingame-editor.md#11-phase-4-vanilla-actors-and-lights)): a point light on the
`Gameplay_4` channel only, which no vanilla light uses, so it lights our props and nothing else.

- **The switch:** a prop a light covers has its light environment off and its meshes on `Gameplay_4` alone; any other keeps
  its light environment and its own channels. A light covers a prop when it is on, its channels include `Gameplay_4` (both
  kinds do), and `(1 - (d/R)^2)^falloff` at the prop's bounds centre is at least 0.25 (`level_build` uses the same rule).
  The bridge switches its own spawns and the built props `levels.json` lists (`props`); usables, `live_place` copies and
  vanilla meshes keep their light environment. It runs on a fresh spawn, when a prop's move, turn, scale, delete, undo or
  redo ends, when a light is added, moved, set or switched off (every switched prop then), mid-drag when a prop's
  following light is deleted or comes back ([link](#prop-lighting-and-lights)), and after each re-apply pass and each
  change of the loaded levels once they settle. A prop already right is left alone; one being switched gets
  `SetEnabled(false)` on its light environment, its channels, two reattaches (one leaves the environment's lights on the old
  channels) and `SetEnabled(true)` when uncovered. A prop of a level loaded but not visible waits for its level: its
  bounds come with its components.
- **Auto light** (toolbar, on by default) and **L** (on a selected prop) add a prop light for a prop. Auto light only runs
  on a fresh spawn, and only when no light covers the prop or it gets under 0.6 of its target from prop lights: `E = Σ
  brightness · luminance(colour) · falloff` over the prop lights that are on, against `T`, what a fresh Auto light would
  give it (room lights count for neither: they light the vanilla mesh beside it too). The light goes outside the prop's box
  toward the camera (half the bounds radius out), half the bounds radius above its top, falloff 2. Its radius is 2.5 bounds
  radii, 120 at least, and at least its distance to the bounds centre over 0.65, so it always covers its prop (a turned or
  tilted prop's box is wider than its sphere, and its light stands farther out). Its brightness and colour are the last
  prop light set in the panel on this map, else the nearest prop light, else 0.15 white; one set to brightness 0 or black
  is passed over (its light would turn the prop black). L selects the new light.
- **Too big for one prop light:** a prop whose light would need a radius over 1024 from where the view is gets none, from
  Auto light or L, and keeps its light environment; the status line says why and suggests a room light, which lights it
  with the walls around it. A prop light lights every prop within its radius, so a wider one would add to the props
  around it, each tuned against the vanilla mesh beside it (two huge catalog props got 2421 and 1991 in the in-game test).
  1024 is the engine's default point light radius, three times the widest the freighter's crates needed, and about 310
  of the catalog's 354 meshes fit under it from every side (330 from some side); the rest are over about 450 units across:
  rock piles, drills, market stalls, cargo containers and the catalog's large ones. Such a prop's target is nothing, so
  neither the selection nor a drag offers L for it.
- **Link:** an Auto light remembers its spawn while neither was ever saved and the light was not moved or set (Save, a
  move of the light or a panel change ends it). Meanwhile:
  - one Ctrl+Z undoes both, and deleting the prop deletes the light in the same step unless another shown prop needs it:
    the light covers that prop and no other light does, so without it the prop would go back to its light environment
    (the status line names it). A prop another light covers too doesn't need it;
  - a move, turn or scale of the prop takes the light along, at every frame of the drag: it goes where a fresh Auto light
    would for the prop's new bounds, toward the camera as it is now, with the radius that gives, and keeps its brightness,
    colour, falloff and kind. The camera now and not the light's old side, because `T` is measured from there: the light
    meets it by construction, so the prop stays covered and the drag warning stays quiet, and it lights the faces you
    look at. Every frame and not at the release, so the prop stays lit while it moves and the drag's step holds the light
    from its first frame. The light is in the drag's step: Ctrl+Z and Ctrl+Y put both back, also after the link ended, since
    they only put back what the drag did. A light another shown prop needs stays where it is (the status line says so),
    as for a delete. When the view needs a radius over 1024, the light keeps the side it stood on when the drag began,
    else takes the side that needs the least (the box's narrower one), so where the camera stands never deletes it.
    While the drag leaves the prop too big for one prop light from every side, the light is deleted and the prop goes
    back to its light environment at once. It comes back once the prop fits again, in the same drag or a later one
    (unless another light covers the prop when that drag begins: one you added since stays your choice), and Ctrl+Z
    brings both back. A light you deleted does not follow. Save writes the light where it ended.
- **Selection:** a switched prop shows `prop light 40% of target: L adds one`, and `no prop light covers it` when its light
  environment lights it; a prop too big for one prop light shows the radius it would need instead, no L, and while no
  light covers it a room-light hint. After a drag that leaves a prop under half its target, the status line says so.
- **Lights the editor lists:** its own (`PointLightMovable`, tagged `ed_light_<run>_<n>`) and the YAMLs' `lights:` entries
  (`levels.json`'s `lights` tags, `PointLightToggleable` when built). Vanilla lights (baked) and NPC lights (edited in the
  YAML) are neither listed nor picked. Each shows an icon in its colour (hollow when off, a second ring for a room light);
  the selected one shows its radius as three rings. While the Lights tab is open or Alt is held, a click within 12 px of an
  icon selects that light, nearest first; any other click traces as usual, so a light never takes a click meant for a prop.
- **Editing a light:** the gizmo only moves it (T, R and G don't apply). The Selection window's panel sets the kind (**Prop
  light**: `Gameplay_4`; **Room light**: BSP, Static, Dynamic, CompositeDynamic and `Gameplay_4`, so it lights walls, pawns
  and props alike), enabled, radius (16-4096), brightness (0-10), colour and falloff (1-8). Every field is written to the
  component (shadows off, `bForceDynamicLight` on), then one reattach. Only a field you change is held to its range: a
  `lights:` entry built outside one (radius 6000) keeps its value when another control moves. While a panel control is
  held, as during a gizmo drag, Del, Ctrl+Z/Y, Ctrl+S, L and T/R/G wait for the release.
- **Delete** switches a light off and never destroys it. A light of the YAML: `enabled` false, a light step (Delete is
  greyed out while it is off already). A spawned light: `deleted`, as a spawned prop; deleted before its first save it
  leaves nothing.
- **Spawning** a light clears `bNoDelete` on `PointLightMovable`'s default object for the `Spawn` call only (`Spawn`
  refuses a `bNoDelete` class), in a `__finally` that puts the bit back whatever happens; a pass spawns a session's lights
  again the same way.

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
