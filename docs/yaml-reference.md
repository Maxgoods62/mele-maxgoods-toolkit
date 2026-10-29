# YAML reference

mele-mcp builds a mod from YAML files. A level file builds a level package (`level_build`), a conversation file builds a
conversation inside a level (`conv_build`), and a plot file builds a DLC plot package (`plot_build`). `mod_build` builds
every file of a folder in the right order. This page lists every key the builders read, what each one does, and what a
build writes.

The examples use a mod called "My Mod" with the DLC folder `DLC_MOD_MyMod`, the level `BIOA_MYM01`, an NPC `guard`
tagged `mymod_npc_01` and ids from `61230000`. Ids in the examples are placeholders: take your own with `id_alloc`. A
complete mod that builds is in [examples/hello-npc](../examples/hello-npc/).

## Builds

### mod_build

| Argument | Default | What |
|---|---|---|
| `folder` | required | The folder with the mod's YAML files. A Windows path or a `/mnt/<drive>/` path |
| `out` | none | A mod root to build into instead ([Building into out](#building-into-out)) |
| `game` | `LE1` | Only LE1 |

`mod_build` reads the `*.yaml` and `*.yml` files at the top of `folder` (not its subfolders), in name order, and sorts
them by their top-level keys:

| A file with | Is | Built by |
|---|---|---|
| `conversation` | a conversation file | `conv_build` |
| any of `npcs`, `usables`, `props`, `streaming`, `characterLights`, `startPoints`, `squad` | a level file | `level_build` |
| any of `transitions`, `consequences`, `quests` | a plot file | `plot_build` |
| none of these | skipped | listed in `skipped`, with the reason |

`mod_build` finds a level file by these keys only, so a file with nothing but `vanilla` or `lights` is skipped.
`level_build` builds such a file.

The build order:

1. The `requires` line of every file. An older server stops here and builds nothing.
2. Plot files.
3. Level files. An NPC whose `conversation` the level does not have yet is listed in `missingConversations`.
4. Conversation files.
5. The level files that had a missing conversation, again, so their Kismet can link it.

The build stops at the first error and names the file. Files built before the error stay saved. The result has
`folder`, `out`, `plots`, `levels`, `conversations`, `levelsAgain` and `skipped`, with each file's own build result.

### level_build, conv_build and plot_build

Each builds one file. They take the same arguments:

| Argument | What |
|---|---|
| `file` | The YAML file. A Windows path or a `/mnt/<drive>/` path |
| `package` | Optional. A package to build instead of the file's `package` |
| `game` | `LE1` |

`level_build` with `package` does not write `levels.json`.

### Tools that edit level files

These tools write into a level file, keep its comments, then run `level_build` on it.

| Tool | What it writes |
|---|---|
| `npc_place` | An NPC's `location` and `yaw`, from where Shepard stands in the running game (or from `x`, `y`, `z`). `npc` names the entry of `npcs` (optional when there is one), `turn` adds degrees to the yaw, `build` and `live` (both default true) build the level and move the NPC and her lights in the game |
| `live_save` | Where a live actor stands, into the `npcs`, `usables`, `props` or `lights` entry with its tag; with none, a new `props` entry |
| `editor_import` | What the in-game editor saved (F8, Save): props, NPC and usable poses, lights, NPC lights and `vanilla` entries, merged field by field. The full rules are in the [mele-mcp README](../tools/mele-mcp/README.md) |

## Rules for every file

### Keys and errors

A builder refuses a key it does not know and lists the keys allowed there. Errors name the line of the YAML file. A
build checks every file it will write before it writes any.

`vanilla` and `lights` may be left empty (`vanilla:` alone, `~` or `null`), as when their last entry is deleted.

### requires

```yaml
requires: mele-mcp >= 0.1.0
```

Optional, at the top level of any file. It names the oldest mele-mcp that builds the file, as `X.Y` or `X.Y.Z`. The
server checks it before it reads any other key, and an older server refuses the file:

```text
line 1: the file requires mele-mcp >= 9.1.0, and this server is 0.1.0. Update mele-mcp to 9.1.0 or later, then reconnect it.
```

### Where package points

Paths take `\` or `/`. The DLC folder of a path is its last folder whose name starts with `DLC_`.

| `package` | Example | The file |
|---|---|---|
| A full path | `C:\mods\My Mod\DLC_MOD_MyMod\CookedPCConsole\BIOA_MYM01.pcc` | That file |
| A path from the DLC folder on | `DLC_MOD_MyMod/CookedPCConsole/BIOA_MYM01.pcc` | Under the mod root: the YAML's folder when it has that DLC folder, else the one mod of M3's library that has it |
| Any other relative path, or a file name | `BIOA_MYM01.pcc` | Beside the YAML, else the copy the game loads |

M3's library is the `LibraryPath` of `[ModLibrary]` in M3's `settings.ini`, else the `mods` folder beside
`ME3TweaksModManager.exe`. A mod's root there is `<library>\LE1\<mod name>`.

A level or plot package that does not exist yet is created when `package` is a full path or a path from the DLC folder
on. A file name is never created: it could resolve into the game install. A conversation goes into a level that exists.

Errors:

- no mod root: "No package '...': neither the YAML's folder (...) nor a mod of M3's library has its DLC folder. Make the
  DLC folder beside the YAML, give a full path, or build with mod_build out=."
- two library mods with the DLC folder: "2 mods of M3's library have DLC_MOD_MyMod (...): give a full path, or
  mod_build out=."

### Building into out

With `mod_build out=<folder>`, that folder is the mod root:

- Each `package` goes under `out` by its DLC folder: `DLC_MOD_MyMod/CookedPCConsole/BIOA_MYM01.pcc` and
  `C:\mods\My Mod\DLC_MOD_MyMod\CookedPCConsole\BIOA_MYM01.pcc` both become
  `<out>\DLC_MOD_MyMod\CookedPCConsole\BIOA_MYM01.pcc`. A `package` without a DLC folder is refused.
- When `out` does not have the DLC folder yet and the build would write into one without `out`, that folder is first
  copied into `out` whole. The note reads "out=: copied A to B; the build changes the copy."
- A GlobalTlk or `AutoLoad.ini` the build changes must lie under `out`, or have a copy there by its DLC folder.
  Otherwise the build is refused.
- `levels.json` is left alone.

The build changes nothing outside `out` except its backups.

### Files a build also changes

| Key | Found |
|---|---|
| `globalTlk` (all three builders) | Beside the package, else beside the YAML, else the copy the game loads. Each file's language comes from its name: `DLC_MOD_MyMod_GlobalTlk_FR.pcc` is FR, `DLC_MOD_MyMod_GlobalTlk.pcc` is INT |
| `autoload` (plot files) | See [plot files](#plot-files) |

### Templates and other source files

A template is `{ package: <file>, actor: <name> }`. The package is a full path or a file name the game loads (the highest
mounted copy, mods included, so LE1CP's copy wins where it has one); `.pcc` is added when the name has no extension. The
actor is its name in the level (`BioPawn_3`), its full path, or `#<UIndex>`. Where a key allows it, a template can also
be a plain actor name: an actor of the level being built, to clone.

`propTemplate` and the `package` of a `mesh` can also be relative paths beside the YAML or under the mod root (under
`out` first, with `out`).

### Text and languages

Text is a string, the same for every language, or a mapping of languages:

```yaml
text: "Aye, Commander."                                            # every language
```

```yaml
text: { en: "Aye, Commander.", fr: "À vos ordres, commandant." }   # per language
```

`en` and `int` both mean INT. A talk file takes its own language's text, else the language it shows (FE shows FR, GE
shows DE, IE shows IT, RA shows RU, PLPC shows PL), else `defaultLanguage` (default INT), else the first text given.
Giving one strref two different texts is an error.

Names, wheel text and journal text take a `strref` and language keys in one mapping:
`{ strref: 61230001, en: "Cargo Guard" }`.

### Numbers

| Value | Form |
|---|---|
| A location | `[x, y, z]`, Unreal units |
| `yaw` | Degrees: 0 faces +X, 90 faces +Y |
| `rotation` | `[pitch, yaw, roll]`, degrees |
| A light's `color` | `[r, g, b]`, whole numbers from 0 to 255 |
| Skin, hair and eye colours | `[r, g, b]` or `[r, g, b, a]` |
| An id or strref | A whole number; a leading `$` is ignored |
| `none`, where an id allows it | -1 |

### What every build does

- It writes only the values that differ, so a second build changes nothing.
- It copies each file to `%LOCALAPPDATA%\mele-mcp\backups` (or `MELE_BACKUP_DIR`) before it saves it, named
  `<yyyyMMdd-HHmmss-fff>_<file>`. A fork is not backed up: each build makes it again from the vanilla file.
- It refuses any file inside the Legendary Edition folder. A file name can resolve to the installed copy of a mod or of
  the base game, so build into the mod's copy in M3's library, then install it with `mod_install`.

## Level files

```yaml
requires: mele-mcp >= 0.1.0
package: DLC_MOD_MyMod/CookedPCConsole/BIOA_MYM01.pcc
streaming:
  volume: { package: BIOA_FRE31.pcc, actor: BioTriggerStream_0 }
  levels: [BIOA_FRE31_00_LAY]
postProcess: BIOA_FRE31.pcc
startPointTemplate: { package: BIOA_FRE31.pcc, actor: BioStartPoint_0 }
startPoints:
  start_mym01: { location: [-2703.0488, -16297.884, -284.53085], yaw: 0 }
npcs:
  guard:
    tag: mymod_npc_01
    template: { package: BIONPC_Pressly.pcc, actor: BioPawn_3 }
    location: [-1647.74, -16092.3, -358.2]
    yaw: 200.16
    conversation: MyModGuard_dlg
```

### Top-level keys

`package` is required, unless the tool's `package` argument is given. Every other key is optional.

| Key | Value | What |
|---|---|---|
| `package` | A path ([Where package points](#where-package-points)) | The level. Created (an empty level) when missing |
| `defaultLanguage` | A language, default INT | The text for talk files whose language a text does not give |
| `globalTlk` | A list of files | Where NPC and usable names go. Required when a name gives text |
| `sideloader` | A list of file name prefixes | LE1CP's Sideloader loads the level with every file whose name starts with one ([sideloader](#sideloader)) |
| `lightTemplate` | A template | The light copied for an NPC light or a `lights` entry without its own template. Default: LE1CP's medbay light, `PointLightToggleable_7` in `BIOA_NOR10_07_DSG.pcc` |
| `streaming` | `{ volume, levels }` | Vanilla files the level shows ([streaming](#streaming)) |
| `postProcess` | A file name | The level whose `DefaultPostProcessSettings` the level copies |
| `characterLights` | A list of templates, each with `package` | Light actors copied as they are, such as a map's character lights from its `_DSG` file |
| `startPointTemplate` | A template with `package` | The start point copied for a start point the level does not have |
| `startPoints` | `{ <tag>: { location, yaw } }` | Start points, found by tag. `location` is required. The console command `at <level> <tag>` loads the level there |
| `squad` | `{ package }` | A mission map whose squad prefab the level copies ([squad](#squad)) |
| `npcs` | `{ <name>: NPC }` | [NPCs](#npcs) |
| `usables` | `{ <name>: usable }` | [Usables](#usables) |
| `propTemplate` | A template, a BioInert | The actor a static mesh prop is shown on ([props](#props)) |
| `props` | `{ <name>: prop }` | [Props](#props) |
| `lights` | `{ <name>: light }` | [The level's lights](#lights) |
| `vanilla` | `{ <file>.<actor>: change }` | [Changes to vanilla actors and lights](#vanilla) |

The names under `npcs`, `usables`, `props` and `lights` label the entries in the result. Tags must be unique across
`npcs` (their lights too), `usables`, `props` and `lights`.

### streaming

| Key | What |
|---|---|
| `volume` | Required. A template with `package`: a `BioTriggerStream` of a vanilla map, copied with its place and shape |
| `levels` | Required. The files the level shows, by name (an extension is dropped) |

The files go into the level's streaming list. The volume gets one state, `Default`, whose visible chunks are `levels`:
they show while the player is inside it, including when spawned there. Its `InChunkName` is the first level; without
one, pawns that touch the volume stay in stasis (no AI, no light updates). The level's `BioWorldInfo` gets
`bPathsRebuilt` and `bHasPathNodes`, as vanilla persistent levels have.

### startPoints

A start point is found by its tag. A missing one is copied from `startPointTemplate`, with its navigation links cut. It
goes on the level's navigation list with `bBlocked` true: it has no paths, so AI paths must not start there.

### squad

The level copies the squad prefab of `package` (the sequence around its `BioSeqAct_ShowPartySelectionGUI`) and adds
Kismet to `Main_Sequence`: when the level is loaded and its `streaming` levels are visible, the screen goes black, the
party selection screen opens, then the picked henchmen spawn and follow Shepard, and the screen comes back.

### sideloader

The build writes `ConfigDelta-<level>.m3cd` beside the level, or updates the line that names the level in an `.m3cd`
there. For a level `MyNPC_NOR10.pcc` with `sideloader: [BIOA_NOR10_07]`:

```ini
[BioGame.ini SFXGame.SideloaderFramework]
+Levels=(LevelName="MyNPC_NOR10", LoadIfPrefix=("BIOA_NOR10_07"))
```

M3 merges it into Coalesced at install. Give a sideloaded level a name that does not start with `BIOA_`, so it never
matches a Sideloader prefix itself ([LE1 character lighting](le1-lighting.md#a9-the-level-filter-and-sideloaded-levels)).

### npcs

| Key | Value | What |
|---|---|---|
| `tag` | Required | `Tag` and `UniqueTag`: how the build finds the pawn again |
| `template` | A template, or an actor of this level | The pawn to import (with everything it references) or clone when no pawn has the tag |
| `location` | `[x, y, z]` | Where the pawn stands |
| `yaw` | Degrees | Which way it faces |
| `active` | Default true | Shown (`bHidden` false) and its behavior active (`bActive`) |
| `name` | `{ type, strref, <language>: text }` | A name of its own: a copy of its actor type, renamed `type`, whose name is `strref`. Both are required. Text goes to `globalTlk` |
| `skin` | 3 or 4 numbers | `SkinTone` on the pawn's own materials |
| `hair` | 3 or 4 numbers | `HED_Hair_Colour_Vector` on the pawn's own materials |
| `eyes` | 3 or 4 numbers | `EYE_Iris_Colour_Vector` on the pawn's own materials |
| `conversation` | A conversation name | Kismet in `Main_Sequence`: Shepard uses the NPC, the conversation starts. A conversation the level does not have yet is noted and listed in `missingConversations`; the next build links it |
| `idle` | `{ package, pose, weights }` | An ambient pose ([idle](#idle)) |
| `lights` | A list | Lights that follow the NPC ([NPC lights](#npc-lights)) |

#### idle

| Key | What |
|---|---|
| `package` | Required. A file whose Kismet has an action-station prefab that enters `pose`, e.g. `BIOA_NOR10_03_DSG.pcc` |
| `pose` | Required. The animation set, e.g. `HMM_DP_ArmsCrossed`. A wrong pose lists the poses the file has |
| `weights` | `{ <fidget>: <weight> }`, e.g. `{ Talk: 0, Point: 0 }`: the prefab's `Wt_<fidget>` slots. Fidgets left out keep the prefab's weight |

The build copies the prefab into `Main_Sequence` and starts it when the level is loaded and visible. With a
conversation, using the NPC leaves the action station, and the end of the conversation starts the prefab again. The
prefab's `Teleport`, `Snap To Pose`, `Coll/PhysOFF` and `SnapToFloor` inputs get a false bool, so the NPC stays where
`location` puts her. A new `pose` replaces the prefab.

#### NPC lights

Each item of `npcs.<name>.lights` is a light that keeps its place relative to the NPC:

| Key | Default | What |
|---|---|---|
| `tag` | required | How the build finds the light again |
| `offset` | `[0, 0, 0]` | `[forward, right, up]` from the pawn's `Location`, turned by its yaw |
| `radius`, `brightness` | the template's | Its size and strength |
| `color` | the template's | `[r, g, b]`, 0 to 255 |
| `falloff` | the template's | `FalloffExponent`, over 0 |
| `enabled` | true | Off with `false` |

The light is copied from `lightTemplate` with its channels. The build always writes `bOnlyAffectSameAndSpecifiedLevels`
false: the level filter never matches a pawn in a sideloaded level. Why NPCs need lights of their own, and what values
tested well: [LE1 character lighting](le1-lighting.md).

### usables

A usable is a placeable, such as a terminal. With `goTo`, using it takes Shepard to another map.

| Key | Value | What |
|---|---|---|
| `tag` | Required | How the build finds the `BioUseable` again |
| `template` | A template, or an actor of this level | Copied when no usable has the tag |
| `location` | `[x, y, z]` | Its base: the floor (a standing pawn's Z minus 94) |
| `yaw` | Degrees | Which way it faces |
| `name` | `{ strref, <language>: text }` | The name shown when Shepard aims at it. Text goes to `globalTlk` |
| `goTo` | `{ map, startPoint }`, or a list | Where using it takes Shepard. `map` is required (an extension is dropped), `startPoint` is a start point tag |

The build makes the usable targetable (its behavior's `m_bTargetable` and `m_bTargetableOverride`) and adds Kismet to
`Main_Sequence`: `BioSeqEvt_ArtPlaceableUsed` → `BioSeqAct_AreaTransition`.

A `goTo` list picks the map by plot. Every entry but the last has `if: conditional <id> [param <n>]`; the last has no
`if` and applies when none of the others does:

```yaml
goTo:
  - { if: conditional 812, map: BIOA_MYM02, startPoint: start_mym02 }
  - { map: BIOA_MYM01, startPoint: start_mym01 }
```

The build chains one `BioSeqAct_PMCheckConditional` per `if`. A rebuild of the same shape writes only what differs;
another shape replaces the checks and transitions.

### props

A prop is an actor copied from a template, or a static mesh shown on a copy of `propTemplate`.

| Key | Value | What |
|---|---|---|
| `tag` | Required | How the build finds the actor again (any class) |
| `template` | A template, or an actor of this level | The actor to copy. Not with `mesh` |
| `mesh` | `{ package, export }` | A StaticMesh export of that file, shown on a copy of `propTemplate`. Not with `template`. `pkg_search` with `className` StaticMesh lists a file's meshes |
| `location` | `[x, y, z]` | Its pivot: for most meshes, the floor |
| `yaw` | Degrees | Keeps the actor's pitch and roll. Not with `rotation` |
| `rotation` | `[pitch, yaw, roll]` | Not with `yaw` |
| `scale` | A number | `DrawScale` |
| `scale3d` | `[x, y, z]` | `DrawScale3D` |
| `removed` | true or false | `true` hides the actor without collision; with no actor yet, nothing is created. `false` or left out shows it again |

A StaticMeshActor copied to another place renders black, because its light is baked where it stood. So a template that is
a StaticMeshActor, and every `mesh`, goes on a copy of `propTemplate` instead (a BioInert, e.g.
`{ package: BIOA_FRE31_00_LAY.pcc, actor: BioInert_0 }`), which its light environment lights. Such a prop needs
`propTemplate`. An imported template is first flattened: what it inherits from prefab archetypes is written into it, so
the import does not bring the whole prefab.

A prop that an enabled `lights` entry covers is lit by those lights alone; any other is lit by its light environment,
brighter than vanilla, with a note. The rules are in [the in-game editor design](design/in-game-editor.md#111-prop-lighting).

### lights

The level's own lights, which the in-game editor adds.

| Key | Default | What |
|---|---|---|
| `tag` | required | How the build and the editor find the light again |
| `kind` | `props` | `props` lights props only (the `Gameplay_4` channel); `room` lights walls, vanilla meshes, pawns and props |
| `location` | required | `[x, y, z]` |
| `radius` | required | Over 0 |
| `brightness` | required | 0 or more |
| `color` | white | `[r, g, b]`, 0 to 255 |
| `falloff` | 2 | `FalloffExponent`, over 0 |
| `enabled` | true | A light is switched off with `false`. `removed` is refused |
| `template` | `lightTemplate`, else LE1CP's medbay light | The light to copy |

A light no entry names any more stays as built, with a note. Switch a light off instead of deleting its entry.

### vanilla

`vanilla` changes actors and lights of the vanilla files the level streams, without touching those files. Two kinds of
key:

```yaml
vanilla:
  BIOA_FRE31_00_LAY.StaticMeshActor_69: { location: [-210.73, -15227.6, -408.58] }
  BIOA_FRE31_00_LAY.StaticMeshActor_104: { removed: true }
  BIOA_FRE31_00_LAY.StaticLightCollectionActor_131.PointLight_309_LC: { enabled: false }
```

An actor, `<file>.<actor>`:

| Key | What |
|---|---|
| `location` | `[x, y, z]` |
| `yaw` | Keeps the file's pitch and roll. Not with `rotation` |
| `rotation` | `[pitch, yaw, roll]`. Not with `yaw` |
| `scale`, `scale3d` | `DrawScale`, `DrawScale3D`. Not on an `Emitter_*` (an effect) |
| `removed` | `true` hides it, with no collision |

The file must be in `streaming.levels`, and the actor must be a StaticMeshActor, a BioInert or an Emitter that no Kismet
variable of its file holds. Anything else refuses the whole build, and so does `removed` on an actor drawn whatever its
owner (`bIgnoreOwnerHidden`); nothing is written.

Each file with entries gets a fork beside the level: a copy of the highest-mounted copy of the file outside the mod's
DLC folder, named `<level>_<file>` without `BIOA_` (`MYM01_FRE31_00_LAY`), with byte copies of its localized packages as
`<fork>_LOC_<lang>`. The level streams the fork in place of the file. A file with no entries left is streamed again,
and its fork is deleted.

A light of a light collection, `<file>.<collection>.<light>`:

| Key | What |
|---|---|
| `location` | `[x, y, z]` |
| `brightness` | 0 or more |
| `color` | `[r, g, b]`, 0 to 255 |
| `radius` | Over 0 |
| `falloff` | Over 0 |
| `enabled` | true or false |

The file must be in `streaming.levels` (the light goes into its fork) or be a `characterLights` item (the light goes into
the level's copy). `rotation`, `yaw`, `scale`, `scale3d`, `removed` and `kind` are refused, and so are sun and sky lights
(`DirectionalLight*`, `SkyLight*`). A field left out keeps the file's value. The details, such as what a baked light
still changes, are in [the in-game editor design](design/in-game-editor.md#124-level-lights-5b).

### What a level build writes

- The level package, created when missing.
- Its `Main_Sequence` Kismet for conversations, usables, the squad and idle poses.
- Forks of streamed files, for `vanilla` entries.
- `ConfigDelta-<level>.m3cd`, with `sideloader`.
- NPC and usable names in the `globalTlk` files.
- Its entry of MeleBridge's `levels.json` (`%LOCALAPPDATA%\MeleBridge\LE1\levels.json`, or `MELE_LEVELS_JSON`), written
  last. Not with the tool's `package` argument, and not with `mod_build out=`. The format is in
  [formats](formats.md#4-levelsjson-version-1).

## Conversation files

A conversation file builds an LE1 conversation in a level that exists. The first build copies it from a template.

```yaml
requires: mele-mcp >= 0.1.0
package: DLC_MOD_MyMod/CookedPCConsole/BIOA_MYM01.pcc
conversation: MyModGuard_dlg
from:
  package: BIOA_NOR10_11_DSG_LOC_INT.pcc
  conversation: nor20_req_officer_dlg
  resref: 612301
  nodeIds: 61230100
start: [E16]
nodes:
  E16:
    strref: 61230010
    text: "Commander. This deck is closed to the crew. Not to you, of course."
    replies: [R1, posting, R5]
  posting:
    reply: 61230020
    template: R7
    text: "How long have you served on this ship?"
    wheel: { strref: 61230021, category: INVESTIGATE, en: "Your posting" }
    then: [posting_answer]
```

### Top-level keys

| Key | Value | What |
|---|---|---|
| `package` | Required | The level ([Where package points](#where-package-points)). It must exist: "a conversation goes into an existing level (level_build or level_new creates one)" |
| `conversation` | Required | The conversation, found by name or path. Created from `from` when the level has none |
| `from` | `{ package, conversation, resref, nodeIds }` | The template to copy the first time. All four are required |
| `defaultLanguage` | Default INT | The text for talk files whose language a text does not give |
| `globalTlk` | A list of files | Optional: the text goes there too |
| `start` | A list of node names | Replaces the starting list. The first entry whose condition passes plays |
| `nodes` | `{ <name>: node }` | The lines |

`from`:

| Key | What |
|---|---|
| `package` | The file with the template: a file name the game loads, or a path |
| `conversation` | The template's name |
| `resref` | Over 0. The conversation's own `m_nResRefID` |
| `nodeIds` | Over 0. The first of its own node ids |

The copy takes the template's package (`<template>_D`: the conversation and its talk file set) with everything it
references (the `_N` node data, lip sync sets, sounds). The build renames the packages `<base>_D` and `<base>_N`, the
conversation `conversation` and its talk file set `TlkSet_<base>` (base: `conversation` without `_dlg`). It sets
`resref` and node ids from `nodeIds`, and clears every node's plot condition and transition. It refuses a level that
already has the template's package or the new names. The template's nodes stay in the file; those the YAML does not
reach never play.

### Nodes

A node named `E<n>` or `R<n>` is a node of the conversation (entry n or reply n), which the YAML changes. Any other name
is a new line: the first build copies it from `template`, and later builds find it by its strref.

| Key | For | What |
|---|---|---|
| `strref` | `E<n>`, `R<n>` | New text strref. The VO track and lip sync line (`FXA_<strref>`, `FXA_<strref>_M`, silent) follow |
| `entry` | new lines | An NPC line, with the strref of its text |
| `reply` | new lines | A Shepard line, with the strref of its text |
| `template` | new lines | Required. The node to copy (cameras, gestures, timing, lip sync): `E<n>`, `R<n>` or a name defined above, of the same kind |
| `length` | any | Seconds, over 0: the line's `InterpData` length |
| `text` | any | The line's text ([Text and languages](#text-and-languages)) |
| `if` | any | A condition ([Conditions](#conditions)) |
| `transition` | any | A plot transition id, or `none` |
| `wheel` | replies | The wheel text: `{ strref, category, <language>: text }` |
| `replies` | entries | The wheel, replaced as a whole: a list of reply names, or `{ to, category, paraphrase, <language>: text }` per choice |
| `then` | replies | The entries that follow. `[]` ends the conversation |

`start`, `replies`, `then` and `template` name a node of `nodes` (for `template`, one defined above), or any `E<n>` or
`R<n>` of the conversation. A choice given by name takes its wheel text and category from the reply's `wheel`, else
from an existing choice that leads to that reply. `category` is a reply category without its `REPLY_CATEGORY_` prefix, such as `DEFAULT` or
`INVESTIGATE`; an unknown one lists the valid names.

The text goes to every talk file of the conversation's set, and to the `globalTlk` files when there are any. A talk
file whose strings already match is left as it is.

### Conditions

| `if` | Passes when |
|---|---|
| `bool <id>` | the plot bool is true |
| `not bool <id>`, `bool <id> false` | the plot bool is false |
| `conditional <id>` | the plot conditional passes |
| `conditional <id> param <n>` | the conditional passes with that parameter |
| `none` | always: the node's condition is removed |

For a line a henchman says, copy a template whose speaker list names the henchmen. Give each henchman's line
`if: conditional 415 param -1`, which passes when that speaker is in the squad, and list the lines in priority order in
the node before: the first that passes plays. A kept or copied node keeps its speaker.

## Plot files

A plot file builds a DLC plot package: state transitions, consequences and journal quests.

```yaml
requires: mele-mcp >= 0.1.0
package: DLC_MOD_MyMod/CookedPCConsole/PlotManagerAutoDLC_MyMod.pcc
globalTlk: [DLC_MOD_MyMod_GlobalTlk.pcc]
transitions:
  8421: [bool 8421 = true]
  8422: [int 47 += 2, bool 8422 = true]
quests:
  8425:
    title: { strref: 61230040, en: "The guard's records" }
    goals:
      - { state: 8421, description: { strref: 61230041, en: "Ask the guard about the records." } }
    tasks:
      - name: { strref: 61230044, en: "Talk to the guard" }
      - { name: 61230046, complete: true }
    evals:
      - { when: bool 8421, task: 0 }
```

### Top-level keys

| Key | Value | What |
|---|---|---|
| `package` | Required | The plot package ([Where package points](#where-package-points)). Created when missing, with an empty `StateTransitionMap` and `ConsequenceMap` |
| `autoload` | A path, or `none` | The `AutoLoad.ini` to add lines to. A relative path is looked for beside the YAML, then beside the package. Default: `AutoLoad.ini` in the folder above the package (the DLC folder, for a package in `CookedPCConsole`); without one, a note. `none` leaves it alone |
| `defaultLanguage` | Default INT | The text for files whose language a text does not give |
| `globalTlk` | A list of files | Where journal text goes. Required when any text is given: the journal reads only the GlobalTlk |
| `transitions` | `{ <id>: [elements] }` | The `StateTransitionMap`: what a conversation node's transition runs. `[]` removes the event |
| `consequences` | `{ <id>: [elements] }` | The `ConsequenceMap`, in the same form |
| `quests` | `{ <id>: quest }` | Journal quests ([Quests](#quests)) |

Elements, as `plot_map_get` prints them (a trailing ` [vN]` is ignored):

| Element | What |
|---|---|
| `bool <id> = true`, `bool <id> = false` | Sets a plot bool |
| `int <id> = <n>`, `int <id> += <n>` | Sets or adds to a plot int |
| `float <id> = <x>`, `float <id> += <x>` | Sets or adds to a plot float |
| `consequence <id>` | Runs a consequence |

Every event and quest in the file replaces the package's. The others are kept and listed in `notes`.

### Quests

| Key | What |
|---|---|
| `mission` | `true` puts the quest under Missions instead of Assignments |
| `title` | A strref, or `{ strref, <language>: text }` |
| `goals` | A list of `{ title, description, state, conditional }`. A goal shows while its `state` bool is true; the last true goal wins. `title` defaults to the quest's |
| `tasks` | A list of `{ name, description, complete, plotItems, planet, waypoint }`, at least one. `name` is required. `complete: true` moves the quest to Completed. `plotItems` lists indexes into the quest's `plotItems` |
| `plotItems` | A list of `{ name, icon, state, conditional, targetItems }`. `name` is required |
| `evals` | A list of `{ when, task, state, conditional }`. `when` (`bool <id>`, `int <id>` or `float <id>`) and `task` (an index into `tasks`) are required. When the variable changes, the first evaluation that passes shows its task |

Text keys (`title`, `description`, `name`) take a strref whose text a talk file already has, or
`{ strref, <language>: text }`. Ids, states and conditionals take `none` for -1.

### What a plot build writes

- The plot package, created when missing. The result lists each event and quest as added, replaced, unchanged, removed
  or absent.
- Journal text in the `globalTlk` files.
- The `[Packages]` lines the package needs in `AutoLoad.ini` (`PlotManagerStateTransitionMap<n>`,
  `PlotManagerConsequenceMap<n>`, `PlotManagerQuestMap<n>`), numbered after the file's own. The rest of the file, its BOM
  and its line endings are kept.

## See also

- [examples/hello-npc](../examples/hello-npc/): a level and a conversation that build
- [mele-mcp README](../tools/mele-mcp/README.md): every tool
- [In-game editor design](design/in-game-editor.md): prop lighting, vanilla actors and level lights in depth
- [Shared formats](formats.md): `levels.json` and the editor's session files
- [LE1 character lighting](le1-lighting.md): why NPCs get lights of their own
