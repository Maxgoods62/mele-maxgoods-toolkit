# In-game editor guide

This page shows how to use the level editor that MeleBridge draws over the game, from opening it to shipping your
edits in a mod. The editor is LE1 only: the LE2 and LE3 builds of MeleBridge leave it out. Every tool that takes `game`
requires it, so each tool this page names is called with `game` set to `LE1`.

The reasons behind its behaviour are in the [design note](design/in-game-editor.md). The file fields are in
[formats.md](formats.md). What has been tested in game is in [games.md](games.md).

## Words used here

| Word | Meaning |
|---|---|
| ours | A level built from one of your mod's YAML files, its actors, and anything you spawn in the editor. |
| vanilla actor | Any other actor that comes from a level file. |
| session | Your saved work on one map: `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json`. |
| fork | A renamed copy of a vanilla level file with your edits written in. Your map streams it instead of the file. |
| pass | The step that puts a session's entries back on their actors after a load. |
| light environment | The engine component that lights one prop or character from the lights around it. |

The examples use a map `BIOA_MYM01` that streams the vanilla file `BIOA_FRE31_00_LAY`. It has an NPC `guard` whose
pawn tag is `mymod_npc_01`.

## What you need

| To do this | You need |
|---|---|
| Open the editor, fly, select, spawn from In this level | `MeleBridge.asi` in LE1's ASI folder, from `build.sh --install` ([MeleBridge](../tools/mele-bridge/README.md)) |
| Spawn from the Catalog tab | `catalog_build`, then `mod_install` of Mele Catalog, then a game restart |
| Tell your levels from vanilla ones, edit your NPCs' lights | `levels.json`, which `level_build` and `mod_build` write |
| Ship your edits | mele-mcp and M3 ([getting started](getting-started.md)) |

Without `levels.json` the editor still opens and says `levels.json missing: run mod_build`. Every actor then follows
the rules for your own levels, and the Selection window says `ships: unknown`.

## Open and close

Press F8 while you are in a map with the game window in front. The world pauses and a mouse cursor appears. Press F8
again, or click Close (F8), to go back to the game. A map change also closes the editor.

While the editor is open, the game gets no mouse or keys. F8 and Alt+F4 still work. The editor gives the old pause
state back when it closes. If F8 does nothing, read `Game\ME1\Binaries\Win64\Logs\MeleBridge.log`.

| Window | Where | What it holds |
|---|---|---|
| Toolbar | Top centre | Free cam, Props, Auto light, the three tools, Delete, Undo, Redo, Save, Diagnostics, Close, key hints, status line |
| Props | Toolbar's Props box | The tabs Catalog, In this level, Lights and Effects |
| Selection | Top right, while something is selected | Path, class, tag, whether edits ship, pose, prop light share, light panel, Deselect |
| Diagnostics | Bottom left, on by default | Hooks, camera, last click, catalog, session and `levels.json` state |

The status line under the toolbar's hints shows the last message for 8 seconds. Refusals are orange and other messages
are green. Each message also goes to the log. `live_editor` in mele-mcp returns the editor's state as JSON, and
section 3.7 of [formats.md](formats.md) lists its fields.

A yellow plus and a cyan ring at the selection's origin are a projection check. They should overlap.

## Fly the camera

Tick Free cam on the toolbar. The camera starts where the game camera is. Free cam is off again the next time you open
the editor.

| Input | What it does |
|---|---|
| Hold right mouse | Look around |
| W, S | Forward, back |
| A, D | Left, right |
| Q, E | Down, up |
| Shift | Four times faster |
| Ctrl | Four times slower |
| Mouse wheel | Fly speed, by 1.25 per notch, from 50 to 20000 units per second. It starts at 600. |

Keys go by the letter on the key. On an AZERTY keyboard, W, A, S and D are not side by side.

## All other keys

| Input | What it does |
|---|---|
| Left click | Selects what is under the cursor. A click on nothing clears the selection. |
| Alt + left click | Selects the light or effect icon nearest the camera |
| T, R, G | Picks the Move, Turn or Scale tool |
| Ctrl during a drag | Snaps: 10 units, 15 degrees, or scale by 0.1 |
| Del | Deletes the selection |
| Ctrl+Z | Undo |
| Ctrl+Y or Ctrl+Shift+Z | Redo |
| Ctrl+S | Save |
| L | Adds a prop light for the selected prop |

T, R, G, Del, L and the Ctrl shortcuts wait while you type in a search box, hold the right mouse button, drag a gizmo
or hold a control on the light panel.

## Select something

A left click traces a line from the cursor into the world. When the trace hits nothing, the editor takes the
nearest visible mesh whose bounds the line enters. It skips room-sized bounds and bounds that hold the camera. The
Selection window then shows the actor, and Deselect clears it.

Lights and effects are selected through their icons or their rows in the tabs. A plain click never picks an effect, so
a dust effect's wide box can't take a click meant for a crate behind it.

| Icon | What it is |
|---|---|
| Disc | One of your lights. A second ring marks a room light. |
| Diamond | An NPC's light |
| Square, with a letter | A level light, with its type (see [level lights](#level-lights)) |
| Star | An effect: amber, or grey when the editor refuses it |

A light's icon has its colour and is hollow when off. An effect's star is hollow while hidden. The selected light
shows its radius as three rings.

Your lights, NPC lights, `characterLights` copies and anything you edited always show an icon. Other level lights show
with Level lights ticked and either the Lights tab open or Alt held. Other effects show with the Effects tab open or
Alt held, within 30 m of the camera, at most 50.

With the Lights tab open, a click within 12 pixels of a light's icon selects it. The Effects tab does the same for
effects, and Alt does it for both. The nearest icon to the camera wins among those under the cursor.

## Place a prop

Both spawn tabs place the prop where the view points. It lands on the surface ahead when that surface is a floor, else
on the floor below a point just short of it. It faces the camera and is selected, with the tag `ed_<mesh>_<run>_<n>`.

### Catalog tab

The Catalog tab shows thumbnails of a curated prop list. It needs a one-time setup:

1. Call `catalog_build`. Its default list is `catalog_index`'s curated meshes, and its `meshes` parameter adds more.
2. With the game closed, call `mod_install` with `game` set to `LE1` and `mod` set to `Mele Catalog`.
3. Start the game, tick Props, and open the tab.

`catalog_build` writes `catalog.json` and `thumbs\` to `%LOCALAPPDATA%\MeleBridge\LE1\catalog`, and
`MeleCatalog_Core.pcc` to `mods\LE1\Mele Catalog` in M3's library. After another `catalog_build`, click Reload in the
tab. New meshes also need `mod_install` and a restart.

Pick a category, or search by name or mesh path. A cell's tooltip gives the size, triangles, how often the game's
levels place the prop, and its source file. Click a cell to spawn it.

Mele Catalog is a dev mod. Your shipped level holds a copy of the vanilla mesh, so players never need it.

### In this level tab

This tab lists the meshes in memory and needs no setup. Search it, click a row to spawn, and click Refresh for a new
list. A catalog mesh loaded earlier appears under its `MeleCatalog_Core` path and spawns as its catalog entry. If the
catalog no longer has that entry, the row is refused, so spawn the prop from the Catalog tab.

## Move, turn and scale

Select an actor, pick a tool, and drag the gizmo.

| Key | Tool | Gizmo | With Ctrl |
|---|---|---|---|
| T | Move | Arrows and planes in world space | Snaps to 10 units from where the drag started |
| R | Turn | Three rings in the actor's own space. An NPC gets one ring, about the vertical. | Snaps to 15 degrees from where the drag started |
| G | Scale | Handles in the actor's own space, one axis or all three (the centre). It changes `DrawScale3D` only. | Rounds to 0.1 |

Lights only move, whichever tool is picked. Effects move and turn. NPCs move and turn their yaw.

The tools refuse these cases, and the status line says why:

- Move, Turn and Scale refuse a hidden actor.
- Turn and Scale refuse a mirrored actor, one with a zero or negative `DrawScale` or `DrawScale3D` component.
- All three refuse an actor whose session entry is still waiting. Try again in a moment. If the re-apply pass failed,
  the status line says when it runs again.
- All three refuse a vanilla actor outside the rules in [vanilla actors](#vanilla-actors).

The editor lets the paused world tick once after a spawn, a drag, an undo or a redo. A prop's light environment only
updates during a tick, and a new prop would render black without it. It skips this when the game had its own pause as
F8 opened, and the status line says so.

## Delete, undo and redo

Delete hides the actor and turns off its collision, so undo can bring it back with the flags it had. It never destroys
anything.

| You delete | What happens |
|---|---|
| A prop you spawned | It is hidden. If it was never saved, it leaves nothing in the session. |
| An actor of your levels | It is hidden. Pawns, controllers, `bStatic` and `bNoDelete` actors are refused. |
| A vanilla prop | It is hidden, after the checks below. Its baked shadow stays, and so does its physics body until the build. |
| A light from your YAML | It is switched off (`enabled: false`). A light of the YAML is never removed. |
| A light you spawned | It is hidden, like a spawned prop. |
| An effect | It is hidden. |

Each spawn, delete or drag is one undo step, up to 200 steps. A drag on the light panel is one step, from the first
change to the release. The toolbar's Undo and Redo tooltips name the step, such as `Undo move ed_crate_x_1 (Ctrl+Z)`.

A map change clears both stacks. A level that unloads takes its steps with it. A step whose actor is gone is dropped,
and the status line says so. An undo or a redo marks the edit unsaved.

## Vanilla actors

You can edit a vanilla actor only when these checks pass. The Selection window and the status line give the reason
when one fails.

| Check | Example message |
|---|---|
| Its class, or a parent class, is `StaticMeshActor`, `BioInert` or `Emitter` | `a BioDoor: doors carry Kismet and door volumes`, `a PathNode: the AI's paths` |
| It was loaded from a level file | `spawned at run time: no level file has it` |
| No loaded Kismet holds it | `Kismet holds it (Sequence_1791.SeqVar_Object_0): the story moves, hides or switches it` |
| No part reaches more than 3000 units from its centre | `a room shell (110 m): hiding it opens the room` |
| To delete: no part ignores its owner's hidden flag | `a part of it ignores its owner's bHidden (bIgnoreOwnerHidden): hidden, it would stay drawn` |

The Kismet check reads loaded levels only, so `level_build` has the last word when it builds the fork.

The Selection window also says whether an edit of a vanilla actor reaches your mod:

| Line | Meaning |
|---|---|
| `ships: forked into MYM01_FRE31_00_LAY` | The actor already stands in a fork. |
| `ships: BIOA_MYM01 streams it (forked on the next build)` | Your map's `levels.json` entry streams this file. |
| `ships if BIOA_MYM01's YAML streams it: ...` | The map is yours, and `levels.json` does not list it yet. Run `mod_build`. |
| `dev-only: no map of ours streams BIOA_NOR10_07_LAY` | The bridge re-applies the edit on your machine. No build ships it. |

A fork's name follows one rule: `<map minus BIOA_>_<file minus BIOA_>`. `BIOA_MYM01` streams `BIOA_FRE31_00_LAY` as
`MYM01_FRE31_00_LAY`. Sessions always name the vanilla file, so an edit matches in either file.

## Lights

A placed prop's own light environment lights it far brighter than the vanilla meshes beside it. A prop light fixes
that: it is a point light on the `Gameplay_4` channel only, which lights your props and nothing else. A room light
lights walls, vanilla meshes, pawns and props alike.

A prop that a light covers has its light environment off and its meshes on `Gameplay_4`. A light covers a prop when it
is on and `(1 - (d/R)^2)^falloff` is at least 0.25 at the prop's bounds centre. `level_build` uses the same rule. This
switch applies to your spawns and to the built props `levels.json` lists. Usables, `live_place` copies and vanilla
meshes keep their light environment.

### Add a light

| Control | What it adds | Where | Starting values |
|---|---|---|---|
| Prop light (Lights tab) | A prop light | 150 units over where the view points | Radius 350. Brightness and colour from the last prop light you set on this map, else the nearest prop light, else 0.15 white. |
| Room light (Lights tab) | A room light | 250 units over where the view points | Radius 600, brightness 0.5, white |
| L | A prop light for the selected prop | Outside its box, toward the camera, above its top | Falloff 2. The radius fits the prop. |
| Auto light (toolbar, on by default) | The same, for a fresh spawn | The same | It runs when no light covers the spawn, or the spawn gets under 60% of its target. |
| Add light, or NPC light with an NPC selected | An NPC light | Offset `[60, 0, 80]` from the NPC | Radius 100, brightness 1.2 |

A prop that would need a radius over 1024 from where the view stands gets no light from L or Auto light. It keeps its
light environment, and the status line suggests a room light.

An Auto light stays linked to its prop until a Save, a move of the light or a panel change. While linked, one Ctrl+Z
undoes both, and a drag of the prop takes the light along. Deleting the prop deletes the light too, unless another
shown prop needs it.

The Selection window shows how a prop stands:

| Line | Meaning |
|---|---|
| `prop light 40% of target: L adds one` | Prop lights give it under 60% of its target. Room lights do not count. |
| `no prop light covers it: ...` | Its light environment lights it. |
| `too big for one prop light: ...` | L adds none. A room light is the way to light it. |

### The light panel

Selecting a light opens a panel in the Selection window. The gizmo only moves the light.

| Control | Range | Note |
|---|---|---|
| Prop light, Room light | | Your own lights only |
| Enabled | | Unticked means switched off |
| Radius | 16 to 4096 | |
| Brightness | 0 to 10 | |
| Colour | | RGB, 0 to 255 |
| Falloff | 1 to 8 | 2 is the usual value for a point light |

Only the field you change is held to its range. A YAML light built with a radius of 6000 keeps it when another control
moves.

### NPC lights

Your NPCs' lights (`npcs.<name>.lights` in a level YAML) are stored as offsets from their NPC. An offset is
`[forward, right, up]` from the pawn's centre, turned by its yaw.

The Lights tab has a section per NPC of the visible levels, such as `Guard (mymod_npc_01)`, with an Add light button
and a row per light. An amber cross and a yaw tick mark the NPC's reference pose while one of its lights is selected.

- Move a light to change its offset. The panel has no kind control, because the light keeps its template's channels.
- Move or turn the NPC, and its lights follow in the same undo step.
- A light of your NPC is switched off by Delete. A spawned one deleted before its first save leaves nothing, so untick
  Enabled instead if you want it kept and off.
- A YAML with its own `lightTemplate` may build an added light with another look. The first Add says so.

The editor checks the pawn before it edits an NPC's lights. Unless you moved the NPC in this session, the pawn must
stand within 5 units and 2 degrees of the pose `levels.json` holds. Otherwise the message reads:

```
Guard stands 37.2 from where levels.json puts them (the last build): install the last build (mod_install, game closed):
a reload shows the installed level, not npc_place's build
```

This happens after `npc_place` and a reload, since `npc_place` builds but does not install. Run `mod_install` with the
game closed.

### Level lights

The editor can also edit the point and spot lights inside each `StaticLightCollectionActor`, one at a time. It never
lists sun or sky lights. Check [games.md](games.md) for this feature's test status before you rely on it.

The Lights tab always lists the map's `characterLights` copies under Character lights. Tick Level lights for the other
lights within 30 m of the view, and Baked too to add the baked ones. Lists run nearest first, with at most 150
untouched lights. Edited lights are always listed.

| Type | Meaning | What a change reaches |
|---|---|---|
| B | Baked | Characters and props only. The walls keep their baked light. |
| C | Every other light | Characters and props through their light environments, never the walls |
| U | Special channel | Only the meshes on that channel. The panel names them. |
| D | Dynamic | What you see is what ships. |

Only the move tool works. Turn says a spot light's aim is not edited, and a collection whose matrix scales its lights
refuses the move. The panel has no kind control. Its banner gives the type and, on a baked light, what the change
leaves on the walls.

Delete switches a level light off, and Ctrl+Z switches it on. After a step on a B or C light, the editor resets the
light environments in reach and lets the world run 1.5 seconds, because they only update while the world ticks. It
skips this when the game had its own pause as F8 opened, and the status line says so.

A `characterLights` copy says `ships: built into BIOA_MYM01 (characterLights copy of BIOA_FRE31_00_DSG)`. Any other
level light ships in a fork of its file, or stays dev-only.

## Effects

The Effects tab lists the emitters of the visible levels, nearest first. You can move, turn and hide them. An effect
of a vanilla file ships like a vanilla prop, so read its ships line in the Selection window. A row reads
`Emitter_12 · FX_ME1_dust_card_METR · BIOA_FRE31_00_LAY · 4.2 m`, with a mark such as `(hidden)` or `(unsaved)`.

The editor refuses some effects in any level, and greys their rows with the reason:

- `made at run time by the game (an impact, a shield, a footstep), not part of the map`
- `Kismet holds it (<sequence>.SeqVar_Object_N): the story moves, hides or switches it`
- `off until Kismet or a matinee starts it`

The Selection window shows the template and whether it loops. One marked `ends` plays once, so after a move it shows
for the 1.5 second run and then nothing until the game starts it.

After a step, the editor restarts the effect and lets the paused world run 1.5 seconds, with `effects run 1.5 s` on
the status line. It skips this when the game had its own pause as F8 opened. This restart only happens live, and
nothing of it ships.

## Save your work

Press Ctrl+S, or click Save. The button reads `Save (N)` when N edits are unsaved.

Save writes one session file per map, and the game install is never touched. A level the game loads into another map
(sideloaded) is saved in that map's file. An entry holds the latest state of one actor: spawned ones by tag, level
actors by path.

- A map change saves your unsaved edits first.
- If a write fails, the toolbar shows `not saved` with the reason on its own line. The edits stay, and the next Save
  tries again.
- A file that can't be parsed moves aside to `<map>.json.bad-<n>` at the next Save.
- `editor_import` may write into the file while the game runs. A field you have not changed since then takes the
  file's value.

After a map loads, and whenever the streaming levels change and settle for half a second, a pass puts your entries
back on their actors. It runs with the editor open or closed, so saved work survives a reload with no rebuild. An
entry whose actor is not loaded waits for a later pass. Diagnostics shows the counts of applied, dropped and waiting
entries, and `live_editor` lists every waiting entry.

## Bring a session into your YAML

`editor_import` merges a session into a level YAML, keeping its comments, then builds the level. The game need not
run. See [getting started](getting-started.md) for how to call tools.

```bash
python3 -I tools/mele-mcp/scripts/mcp_call.py editor_import '{"game": "LE1", "file": "<level YAML>"}'
```

| Parameter | Default | What it does |
|---|---|---|
| `game` | required | `LE1` |
| `file` | required | The level YAML, as a Windows or `/mnt/<drive>` path |
| `session` | Found for you | The session file. The default is `<package name>.json`. For a sideloaded level, it is the newest session with an actor of that level. |
| `build` | `true` | Runs `level_build` after writing the file |
| `takeSpawned` | See below | Makes spawned meshes into `props` entries of this file |
| `keepSession` | `false` | Leaves the session file untouched, for trying options |
| `onConflict` | `refuse` | `refuse`, `editor` or `yaml` |

`takeSpawned` defaults to true when the session is of the map this file builds. For a sideloaded level, pass `true` on
the one file that should hold the props, with `session` set to the file of the map that loads it.

| In the session | In the YAML |
|---|---|
| A prop moved, turned or scaled | Location, `yaw` while upright or else `rotation: [pitch, yaw, roll]`, `scale`, `scale3d` |
| An NPC or usable moved or turned | Location and yaw. A pitch, roll or scale is only noted. |
| A prop deleted | `removed: true`. NPCs and usables are only noted. |
| A mesh spawned | A new `props` entry named by its tag, with `mesh` |
| A light spawned | A new `lights` entry named by its tag |
| A light of a `lights` entry | Merged per field. A deleted one gets `enabled: false`. |
| An NPC light | An item of `npcs.<name>.lights`, in the file that has that NPC |
| A vanilla actor of a file the YAML streams | `vanilla.<file>.<actor>` |
| A level light | `vanilla.<file>.<collection>.<light>` |
| A vanilla actor of a file the YAML does not stream | Skipped. It stays dev-only. |

The merge works per field against the entry's base, which is what the YAML held when the editor first changed the
actor. A value changed only in the YAML stays. A value changed on both sides is a conflict. With `refuse`, that actor
is left out and listed with the base, YAML and editor values. Import again with `editor` or `yaml` to pick a side.

Each actor in the result has an action: `updated`, `added`, `removed`, `unchanged`, `conflict`, `refused`, `skipped`
or `failed`. Read the `notes` too. Importing the same session again changes nothing. If the build fails, the import
stays done: the result keeps its actors and `marked` count, and `buildError` says why. Fix the cause, then run
`level_build`.

`editor_import` copies the YAML into the backups folder before it writes it (`backup` in the result names the copy),
but it has no game-install guard on that file, so keep the YAML in git and outside the game folder. To undo an import,
restore the YAML and the session file together. A YAML reverted alone reads as edits by hand. Entries that an import
drops go to `<session>.dropped.json` beside the session.

## Ship the result

1. Save in the editor.
2. Run `editor_import`, which also builds the level. Run `mod_build` as well if you changed other files.
3. Close the game and run `mod_install`, which installs through M3.
4. Start the game and load the map.

An imported entry stays in the session until the installed level shows its state, then the pass drops it. So an import
before an install loses nothing. An entry whose actor no longer matches what was imported is dropped too, because the
YAML has been built from something else since.

## Limits

- The editor is LE1 only, and F8 needs the game window in front.
- Mouse and keyboard are held back from the game. A gamepad is not, so leave it alone while the editor is open.
- You can't spawn a new effect, and you can't scale one. A spot light's aim is not edited.
- A vanilla light actor can't ship, because no map of yours streams one. Sun and sky lights are not listed.
- A moved vanilla mesh keeps its own lightmap, its shadow baked in the floor, and its cover nodes at the old spot. The
  build warns about cover, and nothing relights the mesh.
- The Normandy's master map is LE1CP's `BIOA_NOR00`, so vanilla edits there stay dev-only.
- A placed prop is a `DynamicSMActor_Spawnable` lit by a `PointLightMovable`. The build makes a `BioInert` copy lit by
  a `PointLightToggleable`, so the built look can differ a little from the live one.
- The design note lists what is still unchecked, such as placed props in save games and AI paths around them.

## Common messages

| Message | What to do |
|---|---|
| `levels.json missing: run mod_build` | Build your mod once, then press F8 again. |
| `No catalog yet (...)` | Run `catalog_build`, then click Reload. |
| `not spawned: ... install the Mele Catalog mod (mod_install, game closed) and restart the game` | Do what it says. A mesh that is already in memory needs no package. |
| `no levels.json entry names this light (run mod_build)` | The level was built before this light had an entry. Run `mod_build`. |
| `levels.json has no NPC mymod_npc_01 (built before Phase 5): run mod_build` | An older build wrote that level. Run `mod_build`. |
| `<level> is not in levels.json and its file can't be read: if it is ours, run mod_build` | The editor can't tell whose level it is. If it is yours, build it. |
| `effects not run: the game had paused the world when F8 opened` | Close the editor, unpause the game, and open it again. |
| `lighting not updated: the game had paused the world when F8 opened (it updates once the world runs)` | The same. |
| `the session was not re-applied: ... (tried again in 5 s)` | Wait. After three failures in a row it waits until you edit one of its actors or the loaded levels change. `MeleBridge.log` has the cause. |
