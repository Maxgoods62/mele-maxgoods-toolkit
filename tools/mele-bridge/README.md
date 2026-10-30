# mele-bridge

`MeleBridge.asi` is an ASI mod for Mass Effect 1 Legendary Edition (LE1). It opens a request/reply pipe into the running
game: it reads and writes any property of one live object, calls UnrealScript functions, runs console commands and
streams events. mele-mcp's `live_*` tools and `game_plot` use it. It also holds the in-game editor that F8 opens.

It does what the LEX Interop ASI can't. LEX Interop is one-way: replies go to a window titled "Legendary Explorer", it
only handles whole classes (`getall`), and commands queued together run in reverse order.

This is MeleBridge 3 (`ASI_VERSION` in `src/SharedVersion.h`), speaking pipe protocol 1. mele-mcp refuses a bridge with
another protocol number ([formats §3.3](../../docs/formats.md#33-the-version-check)), so build the bridge and the server
from the same commit.

## Build and install

No prebuilt `MeleBridge.asi` is published, so you build it from source. The binary would contain code from
ME3Tweaks/LExASIs and LExSDKv2, and neither states a license ([THIRD_PARTY.md](THIRD_PARTY.md)).

```bash
cp build.env.example build.env    # optional: the settings that differ on your machine
./build.sh                        # build, and stage MeleBridge.asi and its .pdb in MELE_STAGE
./build.sh --install              # also copy it to Game\ME1\Binaries\Win64\ASI (the game must be closed)
./build.sh --uninstall            # remove it from there (the game must be closed)
```

`build.sh` runs in WSL and calls Windows programs through interop. It needs:

- Visual Studio 18 (2026) or its Build Tools, with the C++ x64 tools and the "C++ CMake tools for Windows" component.
  The script finds it with `vswhere.exe`. LExASIs' CMake preset uses the "Visual Studio 18 2026" generator, which the
  CMake of older versions does not know.
- Git for Windows (`git.exe`), `rsync` in WSL, and network access on the first build.

Settings come from the environment or from `build.env` (not in git, see `build.env.example`). A variable set in the
environment wins over `build.env`. All are optional:

| Variable | What it is | Default |
|---|---|---|
| `MELE_LE_DIR` | The Legendary Edition folder, as a Windows path. Only `--install` and `--uninstall` use it. | From the registry: BioWare's `Install Dir` for the Legendary Edition, then Steam's uninstall entry (app 1328670) |
| `MELE_VS` | The Visual Studio or Build Tools folder, as a WSL path | Found with `vswhere.exe` |
| `MELE_LEXASIS` | The LExASIs checkout, as a WSL path. Keep it on the Windows disk: CMake and MSBuild do not work well on `\\wsl.localhost` paths. | `%LOCALAPPDATA%\mele-asi-build\MeleBridge\LExASIs` |
| `MELE_STAGE` | Where `MeleBridge.asi` and `MeleBridge.pdb` are copied | `dist/` next to `build.sh` |

How the build works:

- It builds inside its own checkout of [ME3Tweaks/LExASIs](https://github.com/ME3Tweaks/LExASIs), pinned to `aaad03a`
  with its submodules. The first build clones it, and every build checks the pin and repairs a checkout at another
  commit. A repair stops if you changed that checkout's `CMakeLists.txt` yourself: commit or stash the change first.
- The checkout provides the SDK (LExSDKv2), the ASI loader interface (SPI), the `Common` code, spdlog and the
  `asi_target` CMake helper.
- Dear ImGui and ImGuizmo are fetched at pinned commits into `third_party`, next to the LExASIs checkout
  ([THIRD_PARTY.md](THIRD_PARTY.md)). They build as a library of their own, with their default flags.
- `src/` is copied into the checkout as one more project and built for LE1 only, with that tree's flags (`/W4 /WX`).
  A `LEASI_GAME1_WIN64` variable would make that build copy the ASI into the game: the script clears it for the
  configure step, and stops when the configure output says one is still set (one set in the Windows environment
  reaches CMake all the same).
- One build of a checkout runs at a time; a second one waits for it.

`MeleBridge.asi` in the ASI folder is the only file this tool writes inside the game install. It is written only while
`MassEffect1.exe` is closed, and the check counts a failed `tasklist` as "running".
[MelePause](https://github.com/Maxgoods62/mele-pause) keeps the same rule for its own ASI.

The log is `Game\ME1\Binaries\Win64\Logs\MeleBridge.log`, rewritten at each start.

## In-game editor

F8 opens a level editor drawn over the game. You fly a free camera, click a prop to select it, then drag the gizmo of
one of three tools. Del deletes the selection, Ctrl+Z undoes, Ctrl+Y (or Ctrl+Shift+Z) redoes, and Ctrl+S saves. Why it
works the way it does is in the [editor design](../../docs/design/in-game-editor.md), and the files it shares with
mele-mcp are in [formats.md](../../docs/formats.md).

The Props window has four tabs. The first two spawn a prop where the view points:

- Catalog: a grid of thumbnails of the curated props mele-mcp's `catalog_build` writes, with a category list and a
  search. A tooltip gives the size, the triangles, how often the game's levels place the prop and the file it comes
  from.
- In this level: the meshes in memory, as a list (Phase 1's).
- Lights: Prop light and Room light add a light over where the view points, and the list selects one. Below it
  come a section per NPC of ours, with its lights and an Add light button ([NPC lights](#npc-lights)), then the
  map's character lights and, behind the Level lights toggle, the levels' own lights ([Level lights](#level-lights)).
- Effects: the emitters of the loaded levels, nearest first; the list selects one ([Effects](#effects)).

The editor was built in phases ([design §4](../../docs/design/in-game-editor.md#4-phases)). From Phase 3 on:

| Phase | What it added | See |
|---|---|---|
| 3 ([§10](../../docs/design/in-game-editor.md#10-phase-3-the-save-loop)) | Saved edits come back when the map loads again, and ship with their tilt and scale. | [Re-applying a session](#re-applying-a-session) |
| 4 ([§11](../../docs/design/in-game-editor.md#11-phase-4-vanilla-actors-and-lights)) | The props of vanilla levels: moved, turned, scaled or hidden in game, they ship in a fork of their file. Our props lit by prop lights instead of their light environment, and lights edited: L adds a prop light for the selected prop, Auto light (toolbar, on by default) adds one for a fresh spawn that needs it, and Alt+click picks a light by its icon. | [Vanilla actors and forks](#vanilla-actors-and-forks), [Prop lighting and lights](#prop-lighting-and-lights) |
| 5a ([§12](../../docs/design/in-game-editor.md#12-phase-5-effects-npc-lights-and-the-levels-own-lights)) | The levels' effects (moved, turned and hidden, then run for 1.5 s); a gate on actors the loaded Kismet holds; light kinds by source, with a writer that keeps an NPC's or a level's light as built; session version 3; our NPCs' lights, edited as offsets from their NPC, carried by its moves and turns, and added per NPC. | [Effects](#effects), [NPC lights](#npc-lights) |
| 5b ([§12.4](../../docs/design/in-game-editor.md#124-level-lights-5b)) | The lights of the levels' static light collections: moved one by one, set and switched off, the baked ones for characters and props only. Built; its in-game test is pending. | [Level lights](#level-lights) |

| Key | Tool | What the gizmo does |
|---|---|---|
| T | Move | Arrows and planes in world space. Ctrl snaps to 10 units from where the drag started. |
| R | Turn | Three rings in the prop's own space (pitch, yaw and roll). Ctrl snaps to 15 degrees from where the drag started. An NPC gets Phase 1's single ring about the vertical, since the level YAML keeps only a yaw for NPCs. |
| G | Scale | Handles in the prop's own space, one axis or all three (the centre). It changes `DrawScale3D`, never `DrawScale`. Ctrl rounds to 0.1. Off for NPCs, which keep a yaw and no scale. |

- Refusals. R and G are refused on a mirrored actor (a `DrawScale` or `DrawScale3D` component that is not positive):
  its matrix gives no rotation or scale back. Move, turn and scale refuse a hidden actor. Move, turn, scale and delete
  refuse an actor that a session entry still waiting names (or an edit waiting for its actor or its built copy) until
  the re-apply pass, asked for at once, has applied it: an edit made from the actor as it stood would replace the entry
  at the next Save. A vanilla actor that is not a prop is refused too, and gets no gizmo
  ([the gate](#vanilla-actors-and-forks)). The status line says why.
- Delete hides. It calls `SetHidden(true)` and `SetCollision(false, false)` instead of `Destroy`, so undo shows the
  actor again with the flags it had. In our own levels, pawns, controllers, `bStatic` and `bNoDelete` actors are still
  refused; a vanilla prop is not, since hiding destroys nothing. Deleting a vanilla static mesh says that its baked
  shadow stays (it is in the floor's lightmap) and that its physics body stays until the build. A light is switched off
  instead ([lights](#prop-lighting-and-lights)).
- Undo and redo. Each spawn, delete or drag is one step, 200 at most. The game thread opens a drag's step at its first
  command and closes it when the drag ends (or before any other command); a drag that comes back where it started leaves
  none. Undoing writes the whole state back (shown or hidden, location, rotation, scale, a light's settings) and marks
  the edit unsaved. A drag on the light panel is one step too, from the first change to the release. A spawn and the
  prop light Auto light made for it are one step while the light is linked to it, and so are a drag of the prop and
  that light following it ([link](#prop-lighting-and-lights)). A move or turn of one of our NPCs holds the NPC lights it
  carried, and an NPC light's Add is one step ([NPC lights](#npc-lights)). A step whose actor is gone is dropped, with a
  status line. A map change clears both stacks. The toolbar's Undo and Redo name the step (`Undo move ed_…`,
  `Undo spawn ed_… and ed_light_…`, `Undo turn mymod_npc_01 with its light`).
- Threads. The game thread does everything that touches game objects (`Editor.cpp`, after `BioHUD.PostRender` and
  `Camera.UpdateCamera`). The render thread draws [Dear ImGui](https://github.com/ocornut/imgui) and
  [ImGuizmo](https://github.com/CedricGuillemet/ImGuizmo) from a snapshot, in a hooked `IDXGISwapChain::Present`
  (`Overlay.cpp`). The window thread queues mouse and key messages for ImGui (`Input.cpp`).
- Input. While the editor is open the game gets no mouse or keys: the window's messages are swallowed, the DirectInput
  mouse reads zero, and the game's `SetCursorPos` and `ClipCursor` calls are blocked. F8 and Alt+F4 always work.
- Pause. The editor sets `WorldInfo.bPlayersOnly`, as photo mode does, and gives the old value back when it closes.
  - A spawned prop's `DynamicLightEnvironment` only updates when the world ticks: paused, it would render black. After
    a spawn, when a drag ends, after an undo or redo, and after a re-apply applied, spawned or rebound props, the editor
    lets the world tick once and takes the pause back at its next tick. That release is the editor's own: it is not
    logged or counted as the game ending the pause.
  - After an effect's step it lets the world run 1.5 s instead, and takes the pause back once that is over
    ([effects](#effects)). It skips this when the game had its own pause as the editor opened, because it can't tell
    the game ending that pause from its own release over 1.5 s; the status line then says "effects not run".
- Save. One session file per map, `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json`, outside the install. It holds
  the latest state of each actor spawned, moved, turned, scaled or deleted: spawned ones by tag, level actors by path.
  Each edit keeps that state itself, so a save never reads an actor. mele-mcp's `editor_import` writes it into the level
  YAML and builds it ([the session file](#the-session-file)).
  - A field the edit hasn't changed since it was last saved or read takes the file's value, on the actor too, because an
    import may have written the YAML's value into the file while the game ran. That holds for each pose field, each
    field of a light's settings, and `deleted`.
  - An NPC light's place follows its offset from its NPC's pose: a pawn of ours that takes the file's location or yaw
    takes its lights along, as a drag does. An edited light that Save did not write stays unsaved.
  - The status line counts the entries written. A spawn deleted before its first save leaves nothing, is named apart
    and is not counted in `Save (N)`, and a Save of only such spawns rewrites nothing.
  - A map change saves the unsaved edits first. The edits of a map whose file could not be written are kept, and the
    next Save tries again. Until a save writes every map, the toolbar says `not saved`, with the reason on a line of its
    own (the status line fades, and the first pass after a load replaces it).
- Re-apply on load. After a map loads, and again each time the loaded or visible streaming levels change and then stay
  the same for half a second (editor open or not), a pass puts the session's entries back on their actors
  ([Re-applying a session](#re-applying-a-session)).
- State. The `editor` op (`live_editor` in mele-mcp) returns the editor's state
  ([formats §3.7](../../docs/formats.md#37-the-editor-op)):
  - whether the editor is open, the camera, and the selection: its rotation, `DrawScale` and `DrawScale3D`, why delete
    or move refuses it, whether it ships, and the file its level was loaded from while the game keeps the linker. A
    light's settings come as `light`. An NPC light has `npc`: `name`, `tag`, `offset` and the reference `pose`
    (`location` and `yaw`, or null). A switched prop has `propLighting`: `covered`, `received`, `target`, and
    `autoRadius`, the radius a fresh Auto light would need from the view. An effect has `effect`: `template` and
    `loops`.
  - the edits (with `saveProblem`), undo and redo, and the session's re-apply: the file, the entries applied, dropped
    and pending (with why), and the levels the last pass saw;
  - `levelsJson`: the file, its problem, the DLC folder, the levels, and how many props and lights tags, NPCs, NPC
    lights and characterLights copies it lists; and `autoLight`;
  - `lights`: the tracked lights of the visible levels while the editor is open, ours and our NPCs'. Each has its path,
    tag, location, settings with `kind` (`props`, `room`, `npc` or `vanilla`), `npc` (the pawn's tag, or null),
    `edited` and `unsaved`. A level light also has `type` (`B`, `C`, `U`, `D`), `rig`, `collection` and `metres` from
    the view, and its tag is its object name;
  - a selected level light's `levelLight`: `type`, `spot`, `rig`, `unchangeable`, its `base` (`location`, `light`) once
    it has an edit, and a U light's `channels` and `channelActors`. Its `tag` is its collection;
  - `npcs`, the Lights tab's NPC sections: `name`, `tag`, `level`, and `away` (a light's NPC whose level is not
    visible). A light's NPC that `levels.json` no longer lists has a null `name` and `level`;
  - `effects`: the emitters of the visible levels while the editor is open, nearest first. Each has its path, name,
    template, level (a fork's vanilla file), location, `metres` from the view, `refusal` (or null), `deleted`, `hidden`,
    `forked`, `edited` and `unsaved`;
  - the status line, the projection check, the catalog (props read, thumbnails on the GPU) and counters.

  The Diagnostics window shows the same session and `levels.json` summaries.

Tested in game on 2026-10-27, in the freighter: Phase 0's seven steps
([design §7](../../docs/design/in-game-editor.md#7-phase-0-the-test-build)), with the arrows within 0.6 px of where the
engine projects the prop; Phase 1's five ([§8](../../docs/design/in-game-editor.md#8-phase-1-the-first-usable-editor)),
a crate spawned and one deleted in game, both shipped by build and install; Phase 2's catalog, on the Normandy
([§9](../../docs/design/in-game-editor.md#9-phase-2-catalog-and-thumbnails)); and Phase 3's five
([§10](../../docs/design/in-game-editor.md#10-phase-3-the-save-loop)): a tilted, scaled catalog prop kept through a
reload, then imported, built and installed, and a conflict refused. Phases 4 and 5a passed their own tests
([design §4](../../docs/design/in-game-editor.md#4-phases)).

### The session file

Save writes `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json` (UTF-8). Its fields, its versions and the rules
`editor_import` keeps with it are in [formats §5](../../docs/formats.md#5-session-files-versions-1-to-3). The bridge reads
versions 1 to 3. It writes version 3 when an entry holds a light of kind `npc` or `vanilla` (in the entry, its `base`,
`imported.before` or one of `installed`), else version 2. A Phase 4 bridge, which would write such a light with its own
writer (channels and shadows included), refuses a version 3 file instead. A pass that drops entries rewrites the
version for what is left; a version 1 file stays 1. A file of another version is left as it is, and one that can't be
parsed is moved aside (`<map>.json.bad-<tick>`) at the next Save.

What the bridge puts in an entry, beyond the format:

- A light's kind is its source, read once when the editor first tracks the light, then stored: `props` or `room` for
  ours (by their channels), `npc` for a light `levels.json` names among its level's `npcs` lights, and `vanilla` for a
  level light (Phase 5b). A light's `rotation` and scales are the actor's; only its location counts.
- An NPC light's `npc.offset` is `[forward, right, up]` from its NPC's reference pose, turned by the NPC's yaw:
  `forward = dx cos t + dy sin t`, `right = -dx sin t + dy cos t`, `up = dz`, with `t = yaw · 2π / 65536`. Its
  `location` stays absolute.
- `base` is what the level YAML holds, for `editor_import`'s three-way merge. Save keeps the file's `base` when the
  entry has one (an import moves it on), with its `deleted` (whether the YAML removes the actor) when an import gave it.
  A base the editor takes from a shown actor has `deleted: false`, since the build that made it did not remove it. One
  that a version 1 entry takes from a hidden actor has no `deleted`, since the game may have hidden it.
- Save leaves `imported` out: a saved edit is a new state. When Save writes over an imported entry, the entry's
  `imported.before` (and its earlier `installed` states) go into `installed`. The next import's `before` is the base,
  and the install may still show any of these, so `editor_import` leaves them as they are.
- A spawn deleted before it was ever saved is not written. On a light, `deleted` only marks a spawned light the editor
  deleted; a light of a level or a YAML is switched off with `light.enabled` instead.

### Re-applying a session

A pass reads the actors of the loaded levels (the persistent level and each streaming level's `LoadedLevel`, through
`ULevelBase::Actors`) and matches each waiting entry.

- Matching. A spawn matches by its tag (a built copy has it). A level actor matches by tag when exactly one loaded actor
  has it, it is a real tag (not empty, `None` or the class name), and that actor is in the entry's level and of its
  class. Otherwise it matches by path, with the same class.
- Deciding. An entry with `imported` is dropped when the actor already has its state (the install is done). It is
  applied when the actor still has `imported.before` or one of `installed` (or, for a spawn, when no actor has its tag
  yet), and dropped otherwise, because the YAML has been built from something else since then. A version 1 entry is
  dropped when the actor already has its state (Phase 1's import put it in the level), else applied with the actor's
  state before applying as its base (for a spawn spawned again, its built copy's, once rebound). Anything else is
  applied. Dropped entries are removed from the file.
- Comparing. States compare as `editor_import` compares them: location to 0.01, rotation as an orientation, scales to
  0.001. A light compares its location, its component's `bEnabled` against `enabled && !deleted`, the radius to 0.01,
  the brightness and falloff to 0.001, and the colour and kind exactly. Its rotation, scales and `bHidden` don't count,
  since every light is `bHidden` by class default. An NPC light's location matches within 0.05 on each axis: the build
  places it from 2-decimal offsets, and a float at the Normandy's heights steps by 0.004. So an entry is dropped as
  installed only when the game shows it.
- Applying. The stored state is written (deleted means hidden), and the edit starts saved. An entry that shows an actor
  the build hid (the YAML state it was judged by says removed, and the actor is hidden) shows it again with its class's
  collision; an actor the game hid stays hidden. A spawn whose tag no loaded actor has is spawned again, with its mesh
  from memory, else from `mesh.load`, else from the catalog entry with that vanilla mesh. A light is spawned again as a
  new `PointLightMovable` with its settings. A deleted spawn is kept without an actor. When a built copy with its tag
  shows up later, the spawn is destroyed and the edit moves to the copy. The edit is first judged as its entry would
  have been, when it is still that entry: installed or built from other YAML, the edit takes the copy as built and the
  entry leaves the file.
- Streaming. An actor of a level that is loaded but not visible gets the raw writes only, with no component update; when
  the level turns visible, those actors' components are updated. No pass runs while a level is still being added to
  the world or taken from it (`bShouldBeVisible` ≠ `bIsVisible`), unless that lasts 5 s. When a level unloads, its edits
  wait for it again (with their state) and their undo steps go.
- Waiting. An entry whose actor isn't loaded waits for a later pass, with the reason; Diagnostics and the `editor` state
  list them. A pass that faults is not tried again until the loaded levels change. The entries it had applied or dropped
  leave the list then.
- NPC lights. Their entries come after every other entry, so a pawn's entry sets the pose they are placed from. One
  waits, as does an edit of one waiting for its actor, while its NPC fails the pose check ([NPC lights](#npc-lights)). A
  new `levels.json`, read again while the editor is open, asks for a pass. Applied, the light stands at its NPC's
  reference pose plus its offset (unsaved when that moved it from the file's `location`); spawned again, it gets the NPC
  light look instead of the class defaults. A pawn of ours the pass wrote takes its lights along, as a drag does.
- Forks. An entry of a vanilla actor names its vanilla file. Once our map streams a fork of that file, the actor is found
  there by the rule below (its path, then the same path in the fork), and the tag the same way.

### Vanilla actors and forks

A vanilla actor moved, turned, scaled or hidden in one of our maps ships in a fork: a renamed copy of its level file,
built by `level_build` from the YAML's `vanilla:` section, that the map streams instead of the vanilla file
([design §11](../../docs/design/in-game-editor.md#11-phase-4-vanilla-actors-and-lights)).

- Fork names follow one rule: `<map minus BIOA_>_<file minus BIOA_>`, so `BIOA_MYM01` streams `BIOA_FRE31_00_LAY` as
  `MYM01_FRE31_00_LAY`. Nothing stores the pair: `level_build`, `editor_import` and the bridge each apply the rule. Back
  from a fork, `<map minus BIOA_>_<rest>` is `BIOA_<rest>` (`<rest>` when it already starts with `BIO`).
- Sessions name the vanilla file. An edit of an actor in a fork is saved with its vanilla `path` and `level`
  (`BIOA_FRE31_00_LAY.TheWorld.PersistentLevel.StaticMeshActor_69`), keyed the same way, and matched in either file.
  Matching never reads `levels.json`.
- The gate. An editor spawn and an actor of one of our levels keep the rules above. Any other actor is a vanilla one,
  and move, turn, scale and delete take it only when:
  - its class is `StaticMeshActor`, `BioInert` or `Emitter` (up the class chain, by name; an emitter passes the effect
    gate first, [effects](#effects)). Anything else says why: "a BioDoor: doors carry Kismet and door volumes", "a
    PathNode: the AI's paths", "a PointLight: a vanilla light actor; no map of ours streams one, so it can't ship", and
    so on. Pawns and controllers stay refused;
  - it was loaded from a level file (a linker, or the load flags its export gave it), not spawned at run time;
  - no loaded Kismet holds it: "Kismet holds it (Sequence_1791.SeqVar_Object_0): the story moves, hides or switches it".
    The Kismet set is every object that a `SeqVar_Object`'s `ObjValue` or a `SeqVar_ObjectList`'s `ObjList` names in the
    loaded levels, read when first needed after a map change or a level turned visible. A variable of a level that is
    not loaded is missed; `level_build`'s fork, which reads the whole file, has the last word;
  - no part's bounds reach more than 3000 units from their centre: "a room shell (110 m): hiding it opens the room" (the
    size is the box's, across). The bounds are measured as the level file has the actor, before the editor's own edits
    (as `editor_import` measures it), so a scale or a turn made in the editor never makes a prop a room shell. A
    particle system's box is where its particles fly, not a size, so it is left out;
  - for delete, no part of it ignores its owner's `bHidden` (`bIgnoreOwnerHidden`): hidden, it would stay drawn.

  `bStatic` and `bNoDelete` don't refuse a vanilla prop, since hiding destroys nothing.
- Ships or not. The Selection window says whether an edit of the vanilla actor ships: `ships: forked into
  MYM01_FRE31_00_LAY` (it stands in a fork), `ships: BIOA_MYM01 streams it (forked on the next build)` (the running
  map's `levels.json` streams list has the file), or `dev-only: no map of ours streams BIOA_NOR10_07_LAY` (the bridge
  re-applies it, but no build ships it; the Normandy's own files are one case). A map of ours not yet in `levels.json`
  that streams the file says `ships if <map>'s YAML streams it`. An actor the gate refuses shows only why, with no ships
  line.
- A level the bridge can't place is not a `levels.json` key, not a fork and not a file a key streams, and its linker is
  detached, so its file is unknown. A level of ours whose own YAML was not built since Phase 4, while other YAMLs were,
  then reads as vanilla, because each build writes only its own key. Its refusals and its `dev-only` line add
  `<level> is not in levels.json and its file can't be read: if it is ours, run mod_build`.
- Without `levels.json`, every actor keeps the rules above (no class gate, and `bStatic` refused for delete), and
  opening the editor says `levels.json missing: run mod_build`.

`levels.json` (`%LOCALAPPDATA%\MeleBridge\LE1\levels.json`, UTF-8) tells the bridge which levels are ours, which vanilla
files each streams, and which built props, lights and NPCs it may change. Its fields are in
[formats §4](../../docs/formats.md#4-levelsjson-version-1). `level_build` writes it, and each build replaces its own
level's key. The bridge reads it on each map change, and again whenever its write time changes: it checks when F8 opens
the editor and once a second while it is open, because `npc_place` builds, and so rewrites the file, while the map runs.
A new read tracks the lights, props and effects again.

Its `levels` keys are our level files, built from a YAML's `package` (sideloaded ones too, never a fork), and their
actors keep the rules above. A level that is not a key is ours too when the file its linker names lies in the `dlc`
folder and its name is not a fork by the rule. That works only while the game keeps the level's linker attached; the
selection's `file` in the `editor` state shows it.

### Prop lighting and lights

A prop's light environment lights it 25 to 40 times brighter than the vanilla meshes beside it (in the freighter). A
prop light fixes that ([design §11.1](../../docs/design/in-game-editor.md#111-prop-lighting)): a point light on the
`Gameplay_4` channel only, which no vanilla light uses, so it lights our props and nothing else.

- The switch. A prop a light covers has its light environment off and its meshes on `Gameplay_4` alone; any other keeps
  its light environment and its own channels. A light covers a prop when it is on, its channels include `Gameplay_4`
  (both kinds do), and `(1 - (d/R)^2)^falloff` at the prop's bounds centre is at least 0.25. `level_build` uses the
  same rule.
  - The bridge switches its own spawns and the built props that `levels.json` lists (`props`); usables, `live_place`
    copies and vanilla meshes keep their light environment.
  - The switch runs on a fresh spawn; when a prop's move, turn, scale, delete, undo or redo ends; when a light is added,
    moved, set or switched off (every switched prop then); mid-drag when a prop's following light is deleted or comes
    back ([link](#prop-lighting-and-lights)); and after each re-apply pass and each change of the loaded levels once
    they settle.
  - A prop already right is left alone. One being switched gets `SetEnabled(false)` on its light environment, its
    channels, two reattaches (one leaves the environment's lights on the old channels), and `SetEnabled(true)` when
    uncovered. A prop of a level that is loaded but not visible waits for its level, since its bounds come with its
    components.
- Auto light (toolbar, on by default) and L (on a selected prop) add a prop light for a prop.
  - Auto light only runs on a fresh spawn, and only when no light covers the prop or the prop gets under 0.6 of its
    target from prop lights: `E = Σ brightness · luminance(colour) · falloff` over the prop lights that are on, against
    `T`, what a fresh Auto light would give it. Room lights count for neither, since they light the vanilla mesh beside
    it too.
  - The light goes outside the prop's box toward the camera (half the bounds radius out), half the bounds radius above
    its top, with falloff 2. Its radius is 2.5 bounds radii, 120 at least, and at least its distance to the bounds centre
    over 0.65, so it always covers its prop (a turned or tilted prop's box is wider than its sphere, and its light stands
    farther out).
  - Its brightness and colour are those of the last prop light set in the panel on this map, else the nearest prop
    light's, else 0.15 white. One set to brightness 0 or black is passed over, since its light would turn the prop black.
    L selects the new light.
- Too big for one prop light. A prop whose light would need a radius over 1024 from where the view is gets none, from
  Auto light or L, and keeps its light environment; the status line says why and suggests a room light, which lights it
  with the walls around it. A prop light lights every prop within its radius, so a wider one would add to the props
  around it, each tuned against the vanilla mesh beside it (two huge catalog props got 2421 and 1991 in the in-game
  test). 1024 is the engine's default point light radius, three times the widest the freighter's crates needed, and
  about 310 of the catalog's 354 meshes fit under it from every side (330 from some side). The rest are over about 450
  units across: rock piles, drills, market stalls, cargo containers and the catalog's large ones. Such a prop's target is
  nothing, so neither the selection nor a drag offers L for it.
- Link. An Auto light remembers its spawn while neither was ever saved and the light was not moved or set (Save, a move
  of the light or a panel change ends it). Meanwhile:
  - one Ctrl+Z undoes both, and deleting the prop deletes the light in the same step, unless another shown prop needs
    it: the light covers that prop and no other light does, so without it that prop would go back to its light
    environment (the status line names it). A prop another light covers too doesn't need it;
  - a move, turn or scale of the prop takes the light along, at every frame of the drag. It goes where a fresh Auto light
    would for the prop's new bounds, toward the camera as it is now, with the radius that gives, and keeps its
    brightness, colour, falloff and kind. It follows the camera now, and not the light's old side, because `T` is
    measured from there: the light meets it by construction, so the prop stays covered, the drag warning stays quiet,
    and it lights the faces you look at. It moves at every frame, and not at the release, so the prop stays lit while it
    moves and the drag's step holds the light from its first frame;
  - the light is in the drag's step. Ctrl+Z and Ctrl+Y put both back, also after the link ended, since they only put
    back what the drag did. A light another shown prop needs stays where it is (the status line says so), as for a
    delete;
  - when the view needs a radius over 1024, the light keeps the side it stood on when the drag began, else takes the side
    that needs the least (the box's narrower one), so where the camera stands never deletes it;
  - while the drag leaves the prop too big for one prop light from every side, the light is deleted and the prop goes
    back to its light environment at once. The light comes back once the prop fits again, in the same drag or a later
    one, unless another light covers the prop when that drag begins (one you added since stays your choice). Ctrl+Z
    brings both back. A light you deleted does not follow. Save writes the light where it ended.
- Selection. A switched prop shows `prop light 40% of target: L adds one`, and `no prop light covers it` when its light
  environment lights it. A prop too big for one prop light shows the radius it would need instead, no L, and, while no
  light covers it, a room-light hint. A switched-off light that would cover it is named, as the build's note names it
  (switching it on in its panel lights the prop). After a drag that leaves a prop under half its target, the status line
  says so. L always answers on the status line, "L: select a prop first" included.
- Lights the editor lists. Its own lights (`PointLightMovable`, tagged `ed_light_<run>_<n>`) and the YAMLs' `lights:`
  entries (`levels.json`'s `lights` tags, `PointLightToggleable` when built) are the Lights tab's rows. Our NPCs' lights
  (`levels.json` `npcs`) are listed in their NPC's section ([NPC lights](#npc-lights)) and tracked apart, so the prop
  switch never walks them. A light actor of a vanilla level stays refused ("a vanilla light actor; no map of ours streams
  one, so it can't ship"), and one of our levels that no `levels.json` entry names says "no levels.json entry names this
  light (run mod_build)".
  - Each tracked light shows an icon in its colour, its shape set by its source: a disc for ours, a diamond for an NPC's,
    a square for a level's (a star is an effect; the Lights tab has a legend line). The icon is hollow when the light is
    off and has a second ring for a room light; the selected light shows its radius as three rings.
  - Icons are drawn far to near, lights and effects together, so where two overlap the nearer is on top. While the
    Lights tab is open, a click within 12 px of a light's icon selects the one nearest the camera among the icons drawn
    under the cursor, or with none under it, among those within 12 px. So a light in the next room never wins over the
    one in front, and a click on the icon on top picks it. With Alt held this covers lights and effects alike. Any other
    click traces as usual, so a light never takes a click meant for a prop.
- Two writers. A light's settings are written by the writer its stored kind takes. Ours (`props`, `room`) get every
  field written, as below. An NPC's or a level's (`npc`, `vanilla`) go through the kept writer: it reads the light first
  and writes radius (beyond 0.01), brightness and falloff (beyond 0.001), colour RGB (its alpha stays) and on/off only
  where they differ, and never its channels, shadows or level filter, so an untouched colour from a prefab archetype
  stays the archetype's. As a hard guard, our writer leaves alone (and logs once) any light of kind `npc` or `vanilla`.
  It also leaves alone any light that is neither an editor spawn, nor a `lights:` entry of `levels.json`, nor a light of
  our levels (not an NPC's) with a `props` or `room` edit: the light the Lights tab lets you pick while `levels.json` is
  missing or no longer names it, which Phase 4 wrote too.
- Editing a light. The gizmo only moves it (T, R and G don't apply). The Selection window's panel sets the kind
  (Prop light: `Gameplay_4`; Room light: BSP, Static, Dynamic, CompositeDynamic and `Gameplay_4`, so it lights
  walls, pawns and props alike), enabled, radius (16 to 4096), brightness (0 to 10), colour and falloff (1 to 8). Every
  field is written to the component (shadows off, `bForceDynamicLight` on), then one reattach. Only a field you change is
  held to its range: a `lights:` entry built outside one (radius 6000) keeps its value when another control moves. While
  a panel control is held, as during a gizmo drag, Del, Ctrl+Z/Y, Ctrl+S, L and T/R/G wait for the release.
- Delete switches a light off and never destroys it. A light of the YAML gets `enabled` false, as a light step (Delete is
  greyed out while it is off already). A spawned light gets `deleted`, as a spawned prop does; deleted before its first
  save, it leaves nothing.
- Spawning a light clears `bNoDelete` on `PointLightMovable`'s default object for the `Spawn` call only (`Spawn` refuses
  a `bNoDelete` class), in a `__finally` that puts the bit back whatever happens. A pass spawns a session's lights again
  the same way.

### NPC lights

Our NPCs' lights (`npcs.<name>.lights` in a level YAML, `PointLightToggleable` when built) are edited as offsets from
their NPC ([design §12.3](../../docs/design/in-game-editor.md#123-npc-lights-5a)): `[forward, right, up]` from the pawn's
`Location` (its collision centre), turned by its yaw, as `level_build` places them.

- Listed. A light is an NPC light when `levels.json` names its tag among its own level's `npcs` lights. The Lights tab has
  a section per NPC of the visible levels, lights or not: `Guard (mymod_npc_01)` (the YAML key with a capital, and the
  pawn's tag) with an Add light button, then a row per light: `mymod_npc_01_facelight · NPC · radius 100 ·
  brightness 1.2`, `(off)`, `(edited)` or `(unsaved)`.
  - A light shown while its NPC's level is not visible (on the Normandy: an NPC light in `BIOA_NOR00`, seen from another
    deck) keeps its NPC's section, without Add light: `<Name> (<tag>) (<level> is not visible here: its lights wait for
    it)`.
  - A light whose NPC `levels.json` no longer lists keeps a section under its NPC's tag, without Add light.
  - A light no `levels.json` entry names stays refused: "not edited: no levels.json entry names this light (run
    mod_build)".
- Reference pose. Offsets are measured from the pawn's own edit in this session when it has one, else from the built
  pose `levels.json` holds, never from the live pawn. While one of an NPC's lights is selected, an amber cross and yaw
  tick mark that pose.
- Pose check. Before an edit of an NPC's light (move, panel, delete, Add light), and before a pass applies an entry of
  one, the NPC's pawn must stand within 5 units and 2 degrees of its built pose, unless the editor moved or turned it
  (the game never moves our NPCs). Otherwise the light is refused and its entry waits: "not edited: Guard stands 37.2
  from where levels.json puts them (the last build): install the last build (mod_install, game closed): a reload shows
  the installed level, not npc_place's build" (or "is turned 12.0°").
  - `levels.json` is read again on F8 and once a second while the editor is open, so a fresh `npc_place`, which moves
    the pawn live, doesn't trip the check while the map stays loaded. `npc_place` builds but doesn't install: a reload
    puts the pawn back where the install has it, and the check then refuses its lights until `mod_install`.
  - An NPC `levels.json` doesn't list: "levels.json has no NPC mymod_npc_01 (built before Phase 5): run mod_build". A
    pawn that is not loaded: "Guard's pawn (mymod_npc_01) is not in the loaded levels".
- Panel. `NPC light: Guard`, then the offset `[forward, right, up]` (2 decimals, as the YAML has it), "It keeps its
  template's channels (Dynamic: characters and props on their light environment, not the walls).", enabled, radius
  (16 to 4096), brightness (0 to 10), colour and falloff (1 to 8). There is no kind control. The panel is greyed while
  the pose check fails. The kept writer writes the fields ([two writers](#prop-lighting-and-lights)).
- Moving one. The gizmo only moves it, by raw `Location` and `ForceUpdateComponents(true, false)`. A built
  `PointLightToggleable` moves too: it is not `bMovable`, but it moves live this way, paused, with no tick. Its new
  offset is `R(-yaw) · (L - P)` from the reference pose. R says "not turned: a light only moves (a point light shines
  all ways)"; G says "not scaled: a light only moves (its radius is in the panel)".
- The NPC carries its lights. A move or turn of one of our NPCs moves its NPC lights to its new pose plus their offsets,
  at every frame of the drag, in the same undo step (`turn mymod_npc_01 with its light`). A light without an edit gets
  none, since its offset didn't change and the build places it from the NPC's new pose. One with an edit keeps its
  offset, and its `location` follows, so Save writes the right place. The step holds where each light stood before and
  after, the after computed from the pawn's stored pose, so closing it reads no game memory.
- Add light (an NPC section's button, or the tab's NPC light with one of our pawns selected) spawns a
  `PointLightMovable` at offset `[60, 0, 80]` (the face light of the NPC the editor was tested with), radius 100,
  brightness 1.2, tagged `<npc tag>_light_<run>_<n>`. It has the look of the build's default NPC light template:
  channels {`bInitialized`, `Dynamic`}, no dynamic shadows, `bForceDynamicLight`, no level filter, white with alpha 0,
  falloff 2.
  - The new light is selected, and its Add is one step. It needs no tick: an NPC light reaches its NPC at once, paused.
    It lands in the persistent level (`BIOA_NOR00` on the Normandy).
  - The first Add of a game run says that a YAML with its own `lightTemplate` may build it with another look.
  - Refused: "not added: only our NPCs (npcs: in a level YAML) take NPC lights; BioPawn_12 is the game's", and "not
    added: levels.json has no NPC mymod_npc_01 (built before Phase 5): run mod_build".
- Delete switches it off. A light of the YAML gets `enabled` false; one already off says "mymod_npc_01_facelight is
  switched off already (a light of the YAML is switched off, never removed)". A spawned one deleted before its first
  save leaves nothing, and the status line adds "to keep it switched off, untick Enabled instead".
- Session. `light.kind` is `npc`, with `npc: { tag, offset }`, in `base` too (`levels.json`'s offset for a built light),
  and the file is version 3. `location` stays absolute: when written, it is always the reference pose plus the turned
  offset.

### Effects

The emitters (`Emitter`) of the loaded levels, ours and vanilla ones, are moved, turned and hidden like props, and ship
like them: in our levels through the YAML, in a vanilla level through its fork
([design §12.2](../../docs/design/in-game-editor.md#122-effects-5a)).

- The Effects tab (Props window, next to Lights) has one row per effect, nearest first: `Emitter_12 ·
  FX_ME1_dust_card_METR · BIOA_FRE31_00_LAY · 4.2 m` (the template's name, the level file or a fork's vanilla file, and
  the distance from the view). A deleted one says `(deleted)`, one hidden in a fork `(hidden: removed in the YAML)`, one
  hidden otherwise `(hidden)`, and one with an edit `(edited)` or `(unsaved)`. One the editor leaves alone is greyed,
  with its reason. A click on a row selects it.
- Icons. An effect's icon is an amber star (grey when refused, hollow while hidden). While the Effects tab is open or Alt
  is held, the untouched effects within 30 m of the camera get one, nearest first, at most 50; one with an edit always
  has one. With the tab open, a click within 12 px of an icon selects as a light's does (the nearest the camera among
  those under the cursor, else among those within 12 px); with Alt held, lights and effects alike. A click elsewhere
  never picks an effect: `PickByBounds` skips particle systems (a dust card's box is about 1000 units across), so a
  click on a crate behind one selects the crate.
- Refusals, in any level, ours included. One with no load flags: "not edited: made at run time by the game (an impact,
  a shield, a footstep), not part of the map". One a loaded Kismet holds: "not edited: Kismet holds it
  (<sequence>.SeqVar_Object_N): the story moves, hides or switches it". One with no template, or with `bAutoActivate`
  false: "not edited: off until Kismet or a matinee starts it". G: "not scaled: an effect moves and turns (how its
  particles follow a scale is unchecked)".
- Selection. The Selection window shows the template, whether it loops (`loops`) or plays once (`ends`), and the ships
  line as for a vanilla prop.
- Seeing the result. Particles only move while the world ticks, and old ones stay where they were. After an effect's
  step closes (a move, a turn, a delete, an undo or redo, a pass that applied one), the editor calls its component's
  `KillParticlesForced`, then `ActivateSystem(false)` unless it is hidden. When one was started again, it lets the
  paused world run for 1.5 s (`TickFor`; the status line adds "effects run 1.5 s"). A delete needs none: `SetHidden` and
  the kill show at once, paused. Like the one-tick release, that release is the editor's own: the pause is taken back
  once the deadline passes, and it is not counted as the game's. It is skipped when the game had its own pause when the
  editor opened (the status line adds "effects not run: the game had paused the world when F8 opened"). This is live
  only: nothing of it ships.

### Level lights

The point and spot lights of each `StaticLightCollectionActor` in the loaded levels, walked through its `Components`,
are edited one by one ([design §12.4](../../docs/design/in-game-editor.md#124-level-lights-5b), Phase 5b). Sun and sky
lights are never listed. The selection is the light's component, and its collection is never written: as a hard guard,
`Put`, `SetDeleted`, `WriteFlags` and the reattach return on a collection (logged once per collection).

- Types, read from the live fields by `level_build`'s rule:
  - B, baked: `bHasLightEverBeenBuiltIntoLightMap` and `UseDirectLightMap`. The walls keep its light, and a change
    reaches characters and props only.
  - D, dynamic: `bForceDynamicLight` or the Dynamic channel.
  - U, special channel: any channel but BSP, Static and CompositeDynamic. It lights only the meshes on that channel.
  - C, every other light: characters and props through their light environments, never the walls.
- Lights tab. After the NPC sections, Character lights (characterLights) always lists the lights of the map's
  `characterLights` copies. Level lights (off by default) lists the other lights within 30 m of the view, and
  Baked too adds the B ones; nearest first, at most 150 untouched. A row reads `PointLight_35_LC · U ·
  StaticLightCollectionActor_127 · radius 300 · brightness 0.6 · 4.2 m`. A light with an edit is always listed and drawn.
- Icons. A square in the light's colour, with its type letter beside it (white with a dark outline). Untouched level
  lights are drawn while Level lights is on and the Lights tab is open or Alt is held; the rig's lights, edited ones and
  the selected one always. Picking works as for a light icon (the nearest the camera among those under the cursor).
- Move. Only the move gizmo works. Each drag frame writes the `Translation` that puts the light there, through its
  collection's `CachedParentToWorld` (a full 3×3 inverse) and `PointLightComponent.SetTranslation`, which updates that one
  light (raw in a level that is loaded but not visible). A collection whose matrix scales the light (a row length off 1
  by more than 0.001) refuses with "not moved: its light collection scales it"; the panel still works.
- Panel. The type's header and banner ("Baked light (B)" … "Dynamic light (D)"), with no kind radio. A U light's banner
  names its channels and the actors of the visible levels on them ("BioDoor_0 … BioDoor_7 (8)"). A B light's banner
  adds what the change leaves against its base (off, moved, brighter). Fields go through the kept writer, then
  `ActorComponent.ForceUpdate(false)`, which refreshes that one light.
- Characters. A B or C light has no render proxy, and light environments take a change only while the world ticks. When
  a B or C light's step closes (a drag or panel release, a delete, an undo or redo, a pass), every enabled
  `DynamicLightEnvironmentComponent` whose owner stands within the light's radius (before or after) plus 100 is reset
  (`SetEnabled` false, then true), and the paused world runs 1.5 s (`TickFor`; the status line adds "characters lit
  again: N light environments reset, the world runs 1.5 s"). This is skipped while the game had its own pause when the
  editor opened ("characters not updated: the game had paused the world when F8 opened"). A prop the switch put on prop
  lights has its environment off, and is left so.
- Refusals. R on a spot light: "not turned: a spot light's aim is not edited (its cone stays as built)". R on a point
  light, and G: Phase 4's texts. Delete of a light already off: "PointLight_0_LC is switched off already (Enabled in the
  panel switches it on)". A collection of our levels that no `characterLights` item names: "… is a light collection of
  BIOA_MYM01 that no characterLights item names: no build writes its lights".
- Delete switches the light off, as a light step (Ctrl+Z switches it on). A B light's adds "switched off for characters
  and props only: the walls stay lit".
- Ships. The light of a `characterLights` copy says "ships: built into BIOA_MYM01 (characterLights copy of
  BIOA_FRE31_00_DSG)". Any other ships as its collection would: in a fork of its file, or dev-only.
- Session. One entry per light, by its canonical path (a fork's light under its vanilla file), with `owner` and
  `light.type`, version 3. A pass matches it by path (then the same path in the map's fork), with the same class, judges
  it by the light's place and fields, and applies it with `SetTranslation` and the kept writer.

### The prop catalog

mele-mcp's `catalog_build` writes two things, both outside the game install:

- `%LOCALAPPDATA%\MeleBridge\LE1\catalog\catalog.json` and its `thumbs\` folder (128x128 PNGs, transparent background);
- `MeleCatalog_Core.pcc`, the meshes in a group per category, in a dev mod of its own, Mele Catalog
  (`DLC_MOD_MeleCatalog` in the ME3Tweaks Mod Manager library). `mod_install` installs it with the game closed. M3's
  AutoTOC lists the file, so the game finds it by name.

The fields of `catalog.json` (UTF-8, version 1) are in [formats §6](../../docs/formats.md#6-catalogjson-version-1). The
bridge uses them this way:

- `mesh`, the vanilla path, is also the path in memory when a level has the mesh. The bridge uses that copy when it is
  there, and writes the path to the session as `mesh.path`.
- When no level has the mesh, the bridge loads `load` with `Core.Object.DynamicLoadObject(load, StaticMesh, MayFail)`.
  That reads the one mesh and its materials from the catalog package, not the rest of the file.
- `source`, a vanilla file with the mesh as an export, goes to the session as `mesh.file`: `editor_import` and
  `level_build` port the vanilla mesh, never the catalog's copy.
- `thumb` is relative to the catalog folder, or `null` for none. A path that is absolute or leaves the folder is ignored.
- `size`, `tris`, `placed` and `large` are shown in the tooltip.

How the catalog is read and used:

- Reading. It is read in the background, the first time the Catalog tab shows and again on Reload. A missing or
  unreadable file shows in the tab, and the In this level tab still works. An entry without a name or a mesh is skipped;
  a category the list doesn't name is added at its end.
- Thumbnails are decoded with WIC (part of Windows) into D3D11 textures on the render thread, only for cells on screen
  and at most four a frame. They are released when the editor closes or the catalog is read again.
- Loading and garbage collection. A loaded mesh that no actor shows is collected within about a minute, so the load,
  `Actor.Spawn` and `SetStaticMesh` happen in the same game-thread tick. A failed load (the mod is not installed, or the
  game was not restarted since) is reported on the status line, and so is the time a load took.
- In this level. A catalog mesh loaded earlier shows there under its `MeleCatalog_Core` path, and a click on it spawns it
  as its catalog entry, so the session still names the vanilla mesh and file. A mesh that no entry of the catalog as last
  read loads (the catalog was rebuilt, or can't be read, since the copy loaded) is refused: spawn the prop from the
  Catalog tab.

## Pipe protocol

`\\.\pipe\MeleBridge_LE1` takes newline-delimited JSON: one request line in, one reply line out. The transport, the ops
(`ping`, `console`, `find`, `get`, `set`, `call`, `describe`, `subscribe`, `editor`, `status`, `keep_ticking`), their
arguments and replies, the value types, the event stream and the version check are in
[formats §3](../../docs/formats.md#3-pipe-protocol).

`status` (MeleBridge 3) is one cheap look for unattended tests: the map and its world, the player, streaming levels still
on their way, the loading movie, the squad screen and the switch below.

Test switch: keep ticking. LE1 pauses its world and Kismet when its window loses focus (`BioWorldInfo.Tick` sets
`m_bPausedByFocusLoss` and `bPlayersOnly`), so a test driven from another window stalls. `keep_ticking` (MeleBridge 3,
mele-mcp's `live_keep_ticking`) stops that pause while it is on. Around each `BioWorldInfo.Tick` that would run
`m_fNoBrowserWheelTimer` out, it raises that timer (`CanPause()` is then false) and puts it back afterwards, with requests
held until that Tick is over. It clears `bPlayersOnly` only with `m_bPausedByFocusLoss`, so menus, the squad screen, photo
mode and F8 still pause. It is off by default and again at every game start, so normal play still pauses on alt-tab. It is
for tests only.

A `subscribe` stream sees only calls that go through `ProcessEvent`. Script-to-script calls and natives called from
script never show up.

## How it works

- Game thread. Requests run on the game thread, inside a `UObject::ProcessEvent` hook, before the original call. That is
  also where LEX Interop runs its commands.
  - A thread-local flag stops a request from starting inside another one, because requests call `ProcessEvent`
    themselves.
  - A request that hasn't started when its timeout ends never runs.
- Hook race. The loader makes a hook live before it hands back the original function. A call that arrives in that gap
  waits for the pointer instead of calling address 0. That race is the likely cause of a crash seen once at boot in
  another ASI.
- Crash guard. An access violation inside a request (a stale path, a bad guess) fails that request instead of crashing
  the game: the work runs inside `__try`.
- Values. The SDK maps memory layouts (property offsets, bit masks, enum names), but not the engine's
  `ImportText`/`ExportText` virtuals, so `Reflection.cpp` converts values itself. Strings and arrays live on the game's
  heap (`GMalloc`).

## Findings

- Flags corrupted by generated wrappers. The SDK's generated function wrappers run `FunctionFlags |= ~0x400`, which
  leaves every flag bit set. LEX Interop calls `ConsoleCommand` through one, so that function's flags read `0xFFFFFFFF`.
  `call` skips its latent/iterator check when the flags look like that.
- No console output in LE1. `ConsoleCommand` returns an empty string, `getall` writes to a global log device instead of
  the `Ar` it is given, and DebugLogger's `Logf` hooks never call the original function. So `live_console` reads the
  output from the DebugLogger log: it sends the command, then a `getall` (logged as an error, which makes the logger
  flush), and returns the lines in between.
- Encrypted executable. `MassEffect1.exe` is encrypted on disk, so it can't be disassembled statically. Its code exists
  only in memory.
- Plot table. It is a single object, `SFXGame.Default__BioWorldInfo.BioGlobalVars0`. Its `GetBool`/`SetBool` (and the
  Int and Float versions) work through `call`. `game_plot` uses them.
- SDK layouts. Some LExSDKv2 classes don't match the engine's memory layout. The SDK's `FRwVector3` is 12 bytes in C++
  and 16 in the engine, so `FBioRwBox` compiles to 0x28 bytes instead of 0x30. Every later field of `UParticleSystem` is
  then 8 bytes off, and so are `UParticleSystemComponent::Template` and `bAutoActivate`. MeleBridge reads those fields
  at the offsets the engine's reflection gives (`PropertyOffset` in `src/Reflection.cpp`) and pins the other fields it
  reads with `static_assert` (`src/Editor.cpp`). Before you read a field that comes after an `FRwVector3`, or after a
  struct that holds one, check its offset against reflection
  ([design §12.7](../../docs/design/in-game-editor.md#127-findings)).

## License

GPL-3.0 ([`LICENSE`](../../LICENSE)). Third-party code and what it is used for: [`THIRD_PARTY.md`](THIRD_PARTY.md).
