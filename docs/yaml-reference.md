# YAML reference

This page lists every key the mele-mcp YAML builders read. A level file builds a level package, a conversation file builds
a conversation inside a level, and a plot file builds a plot package.

These builds target LE1 only. `level_build`, `conv_build` and `plot_build` refuse LE2 and LE3, and `mod_build` fails when
it reaches one of them. [games.md](games.md) lists what works on LE2 and LE3. A complete mod with a level and a conversation
is in [examples/README.md](../examples/README.md).

Ids on this page are placeholders, so take your own with `id_alloc`. It reserves the free ids it returns for 30 days, or until
it finds them installed, so give it `owner` (your mod's DLC folder name) and a later call gets the same ids back.

| Term | Meaning |
|---|---|
| Kismet | The engine's visual scripting. A level's scripts sit in its `Main_Sequence` |
| strref | The number of a text string |
| Talk file | A `BioTlkFile` export holding the strings of one language |
| GlobalTlk | A mod package whose talk files the whole game reads |
| M3 | ME3Tweaks Mod Manager |

## Builds

`mod_build` takes `game` (required, `LE1`), `folder` and an optional `out` (see [Building into out](#building-into-out)).
It reads the `*.yaml` and `*.yml` files at the top of `folder`, in name order. A file's top-level keys decide its kind,
and the first row that matches wins:

| A file with | Is | Built by |
|---|---|---|
| `conversation` | a conversation file | `conv_build` |
| any of `npcs`, `usables`, `props`, `streaming`, `characterLights`, `startPoints`, `squad` | a level file | `level_build` |
| any of `transitions`, `consequences`, `quests` | a plot file | `plot_build` |
| none of these | skipped | listed in `skipped` |

A file with nothing but `vanilla` or `lights` is skipped by `mod_build`, though `level_build` builds it. The build order is:

1. Every file is read and its `requires` checked. A syntax error or an older server stops here, with nothing built.
2. Plot files.
3. Level files. An NPC whose `conversation` the level lacks goes into `missingConversations`.
4. Conversation files.
5. The level files that had a missing conversation, again (`levelsAgain`), so their Kismet can start it.

The build stops at the first error and names the file. Files built before the error stay saved, and the error lists each
file they saved and its backup.

`level_build`, `conv_build` and `plot_build` each build one file. They take `game` (required, `LE1`), `file` and an
optional `package` that replaces the file's own. `level_build` with `package` does not write `levels.json`. Paths are
Windows or `/mnt/<drive>/` paths.

These tools write into a level file, keep its comments, then run `level_build`. Each copies the YAML into the backups
folder before it changes it (`backup` in its result names the copy), and a build that fails comes back as `buildError` with the YAML
kept. None checks where the YAML lies, so keep your YAML in a source folder under version control.

| Tool | What it writes |
|---|---|
| `npc_place` | An NPC's `location` and `yaw`, from where Shepard stands in the running game or from given values |
| `live_save` | Where a live actor stands, into the entry with its tag, or into a new `props` entry |
| `editor_import` | What the in-game editor saved, merged field by field. The [editor guide](editor-guide.md) covers it |

## Rules for every file

### Keys and errors

A builder refuses a key it does not know and lists the keys allowed there. In a mapping that holds text, any other key reads
as a language, so a typo there is not refused. Most errors name the YAML line. A build checks that it may write every target
file before it writes any.

`vanilla` and `lights` may stay empty.

### requires

`requires: mele-mcp >= 0.1.0` is an optional top-level key. It names the oldest mele-mcp that builds the file, as `X.Y` or
`X.Y.Z`. The server checks it before any other key. An older server refuses the file.

### Where package points

Paths take `\` or `/`. The DLC folder of a path is its last folder whose name starts with `DLC_`.

| `package` | Example | The file |
|---|---|---|
| A full path | `C:\mods\BIOA_MYM01.pcc` | That file |
| A relative path with a DLC folder | `DLC_MOD_MyMod/CookedPCConsole/BIOA_MYM01.pcc` | Under the mod root: the YAML's folder when it has that DLC folder, else the one mod of M3's library that has it |
| Any other relative path, or a file name | `BIOA_MYM01.pcc` | Beside the YAML, else the copy the game loads |

No mod root, or two library mods with that DLC folder, is an error. Fix it with a full path or with `out`.

M3's library is the `LibraryPath` of `[ModLibrary]` in `C:\ProgramData\ME3TweaksModManager\settings.ini`, else the `mods`
folder beside M3's program. The server finds that program through the registry, so start M3 once. A mod's root there is
`<library>\LE1\<mod name>`.

A missing level or plot package is created when `package` is a full path or has a DLC folder. A file name is never created,
because it could resolve into the game install.

### Building into out

With `mod_build out=<folder>`, that folder is the mod root:

- Each `package` goes under `out` by its DLC folder. A `package` without a DLC folder is refused.
- When `out` lacks the DLC folder and the source one exists, the build first copies that folder whole into `out`. A copy
  that stops half way leaves nothing, so the next build copies again. The copy is a snapshot: later builds into `out` build
  on it as it is, and a note names the source files that are newer than their copy, or were added since and are missing
  from it (a file a build deleted from the copy, such as a fork with no entries left, does not count). Delete the DLC
  folder under `out` to copy it again.
- A GlobalTlk or `AutoLoad.ini` the build changes must lie under `out`, or have a copy there by its DLC folder. Otherwise
  the build is refused.
- `levels.json` is left alone. Backups aside, nothing outside `out` changes.

### globalTlk

All three builders take `globalTlk`, a list of files. Each is looked for beside the package, then beside the YAML, then as
the copy the game loads. Its language comes from its name: `DLC_MOD_MyMod_GlobalTlk_FR.pcc` is FR, and
`DLC_MOD_MyMod_GlobalTlk.pcc` is INT.

### Templates

A template is `{ package: <file>, actor: <name> }`. The package is a full path or a file name the game loads, with `.pcc`
added when it has no extension. The actor is its name in the level (`BioPawn_3`), its full path, or a quoted `#<UIndex>`.

Where a key allows it, a template can leave out `package` or be a plain actor name. It then names an actor of this level,
which the build clones. An import is refused when the level already has an actor of that name, so clone it instead. Lights
and stand-in props handle that clash themselves.

`propTemplate` and a `mesh`'s `package` can also be relative paths beside the YAML or under the mod root.

### Text and languages

Text is a string, the same for every language, or a mapping such as `{ en: "Aye, Commander.", fr: "À vos ordres." }`.

`en` and `int` both mean INT. A talk file takes its own language's text first. Next comes the language it shows (FE shows
FR, GE shows DE, IE shows IT, RA shows RU, PLPC shows PL). Then comes the file's top-level `defaultLanguage` (default INT),
which all three builders take, and last the first text given. Giving one strref two different texts is an error.

Names, wheel text and journal text put a `strref` and languages in one mapping: `{ strref: 61230001, en: "Cargo Guard" }`.

### Values

| Value | Form |
|---|---|
| A location | `[x, y, z]`, Unreal units |
| `yaw` | Degrees: 0 faces +X, 90 faces +Y |
| `rotation` | `[pitch, yaw, roll]`, degrees |
| A light's `color` | `[r, g, b]`, whole numbers from 0 to 255 |
| Skin, hair and eye colours | `[r, g, b]` or `[r, g, b, a]`, decimals allowed |
| An id or strref | A whole number. A leading `$` is ignored |
| `none`, where an id allows it | -1 |
| A flag | `true` or `false`. `yes` and `no` are refused |

### What every build does

- It writes only the values that differ, so a second build changes nothing.
- It copies each file it saves to `%LOCALAPPDATA%\mele-mcp\backups` (or `MELE_BACKUP_DIR`), named
  `<yyyyMMdd-HHmmss-fff>_<file>`, with `-2`, `-3`... after the time when two backups of one name share a millisecond. Forks
  have no backup, because each build makes them again: an error names a fork written over an earlier build's as rewritten.
- It saves its files one after the other, with no rollback. When it fails after saving some, the error names each file
  saved and its backup, and `mod_build`'s error names those of the files built before too. Fix the cause and build again,
  or put the backups back.
- It refuses any file inside the Legendary Edition folder. Build into the mod's copy in M3's library and install it with
  `mod_install`.

## Level files

`package` is required unless the tool's `package` argument is given. Every other key is optional.

| Key | Value | What |
|---|---|---|
| `package` | A path | The level. A missing one is created empty |
| `globalTlk` | A list of files | Where NPC and usable names go. Required when a name gives text |
| `lightTemplate` | A template | The light copied when a light has no template. Default: LE1CP's medbay light, `PointLightToggleable_7` in `BIOA_NOR10_07_DSG.pcc` |
| `postProcess` | A file name or path | The level whose `DefaultPostProcessSettings` this level copies |
| `characterLights` | A list of templates, each with `package` | Light actors copied as they are, such as a map's character lights from its `_DSG` file |
| `startPointTemplate` | A template with `package` | The start point copied for one the level lacks |
| `propTemplate` | A template, a `BioInert` | The actor that shows a static mesh prop, see [props](#props) |

`sideloader`, `streaming`, `startPoints`, `squad`, `npcs`, `usables`, `props`, `lights` and `vanilla` have sections below.
`npcs`, `usables`, `props` and `lights` map entry names to entries. Tags must be unique across them, NPC lights included, in
any case.

### streaming

| Key | What |
|---|---|
| `volume` | Required. A template with `package`: a `BioTriggerStream` of a vanilla map, copied with its place and shape |
| `levels` | Required, at least one. The files the level shows, by name. An extension is dropped |

The volume gets one state, `Default`, so `levels` show while the player is inside it. Its `InChunkName` is the first level.
Without one, pawns that touch the volume stay in stasis, with no AI and no light updates. A changed `volume` replaces the copy
an earlier build made; a volume the build did not write stays.

The level's streaming list only grows. A file that leaves `levels` stays on it, under its own name again when the level
streamed its fork, and that fork is deleted. A YAML with no `streaming` section any more does the same for every file: the
list and the volume stay, and each fork the level streamed goes.

### startPoints

`location` is required and `yaw` is optional. The console command `at <level> <tag>` loads the level at that start point.

The build finds a start point by its tag and copies a missing one from `startPointTemplate`. Each one goes on the level's
navigation list with `bBlocked` true. It has no paths, so AI paths must not start there.

### squad

The level copies the squad prefab of the mission map in `package`: the sequence around its
`BioSeqAct_ShowPartySelectionGUI`. When the level is loaded and its `streaming` levels are visible, the party selection
screen opens. The picked henchmen then spawn and follow Shepard. Without `squad`, the build unlinks the chain, so no party
selection screen opens.

### sideloader

LE1CP's Sideloader loads the level with every file whose name starts with one of the prefixes. The build writes
`ConfigDelta-<level>.m3cd` beside the level, or updates the line naming the level in an `.m3cd` there. M3 merges it at
install. For a level `MyNPC_NOR10.pcc` with `sideloader: [BIOA_NOR10_07]`:

```ini
[BioGame.ini SFXGame.SideloaderFramework]
+Levels=(LevelName="MyNPC_NOR10", LoadIfPrefix=("BIOA_NOR10_07"))
```

Give a sideloaded level a name that does not start with `BIOA_`, so it never matches a Sideloader prefix itself.

Without `sideloader`, the build changes no `.m3cd`. A line an earlier build wrote stays, and LE1CP still loads the level
until you delete it. That is also how a mod keeps a line of its own, with a field the build does not write. When
`ConfigDelta-<level>.m3cd` still names the level, the build says so in its notes.

### npcs

| Key | Value | What |
|---|---|---|
| `tag` | Required | `Tag` and `UniqueTag`: how the build finds the `BioPawn` again |
| `template` | A template, or an actor of this level | The pawn to import with all it references, or to clone. Required when no pawn has the tag |
| `location` | `[x, y, z]` | Where the pawn stands |
| `yaw` | Degrees | Which way it faces |
| `active` | Default true | Shown (`bHidden` false) with its behavior active (`bActive`) |
| `name` | `{ type, strref, <language>: text }` | A copy of its actor type renamed `type`, named by `strref`. Both are required. Text goes to `globalTlk` |
| `skin` | 3 or 4 numbers | `SkinTone` on the pawn's body, face and scalp materials |
| `hair` | 3 or 4 numbers | `HED_Hair_Colour_Vector` on its hair and face materials |
| `eyes` | 3 or 4 numbers | `EYE_Iris_Colour_Vector` on its eye material |
| `conversation` | A conversation name | Kismet: Shepard uses the NPC and the conversation starts. The next build links one the level lacks. A new name, or none, unlinks the old one |
| `idle` | `{ package, pose, weights }` | [idle](#idle) |
| `lights` | A list | [NPC lights](#npc-lights) |

Colours change only the pawn's own materials.

#### idle

| Key | What |
|---|---|
| `package` | Required. A file whose Kismet has an action-station prefab that enters `pose`, such as `BIOA_NOR10_03_DSG.pcc` |
| `pose` | Required. The animation set, such as `HMM_DP_ArmsCrossed`. A wrong pose lists the valid ones |
| `weights` | `{ <fidget>: <weight> }`: the prefab's `Wt_<fidget>` slots. Fidgets left out keep the prefab's weight |

The prefab starts when the level is loaded and visible. Using the NPC leaves the pose, and the end of the conversation starts
it again. Without a linked conversation, using the NPC keeps the pose. A new `pose` replaces the prefab. Without `idle`, the
build unlinks the prefab and it never starts.

#### NPC lights

Each item of `npcs.<name>.lights` is a light that keeps its place relative to the NPC. It is copied from `lightTemplate`.

| Key | Default | What |
|---|---|---|
| `tag` | required | How the build finds the light again |
| `offset` | `[0, 0, 0]` | `[forward, right, up]` from the pawn's `Location`, turned by its yaw |
| `radius`, `brightness` | current value | Size and strength. A new light starts with the template's |
| `color` | the template's | `[r, g, b]`, 0 to 255 |
| `falloff` | the template's | `FalloffExponent`, over 0 |
| `enabled` | true | Off with `false` |

The build always writes `bOnlyAffectSameAndSpecifiedLevels` false. [LE1 character lighting](le1-lighting.md) explains why, and
which values work. A light whose tag moves from `lights` to an NPC takes back its template's channels, shadows, dynamic light
and level list.

### usables

A usable is a placeable such as a terminal. The build makes it targetable.

| Key | Value | What |
|---|---|---|
| `tag` | Required | How the build finds the `BioUseable` again |
| `template` | A template, or an actor of this level | Copied when no usable has the tag |
| `location` | `[x, y, z]` | Its base: the floor, a standing pawn's Z minus 94 |
| `yaw` | Degrees | Which way it faces |
| `name` | `{ strref, <language>: text }` | The name shown when Shepard aims at it. Text goes to `globalTlk` |
| `goTo` | `{ map, startPoint }`, or a list | The map using it loads. `map` is required, and `startPoint` is an optional tag |

A single `goTo` mapping takes no `if`. In a list, every entry but the last has `if: conditional <id> [param <n>]`. The last
has no `if`, and applies when none of the others does:

```yaml
goTo:
  - { if: conditional 812, map: BIOA_MYM02, startPoint: start_mym02 }
  - { map: BIOA_MYM01, startPoint: start_mym01 }
```

Without `goTo`, the build unlinks the chain an earlier build made for the usable, so using it loads no map. Its Kismet
stays in `Main_Sequence`, unlinked.

### props

A prop is an actor copied from a template, or a static mesh shown on a copy of `propTemplate`.

| Key | Value | What |
|---|---|---|
| `tag` | Required | How the build finds the actor, of any class |
| `template` | A template, or an actor of this level | The actor to copy. Not with `mesh` |
| `mesh` | `{ package, export }`, both required | A StaticMesh export. Not with `template`. `pkg_search` with `className` StaticMesh lists them |
| `location` | `[x, y, z]` | Its pivot, usually the floor |
| `yaw` | Degrees | Keeps the actor's pitch and roll. Not with `rotation` |
| `rotation` | `[pitch, yaw, roll]` | Not with `yaw` |
| `scale` | A number | `DrawScale` |
| `scale3d` | `[x, y, z]` | `DrawScale3D` |
| `removed` | true or false | `true` hides the actor without collision, and creates nothing when it has no actor yet. `false` or no key shows it again |

A `StaticMeshActor` copied elsewhere renders black, because its light is baked where it stood. So a template of that class, and
every `mesh`, needs `propTemplate`. That is a `BioInert`, a placeable lit by its light environment, such as
`{ package: BIOA_FRE31_00_LAY.pcc, actor: BioInert_0 }`.

An enabled `lights` entry covers a prop when its falloff at the prop's bounds centre is at least 0.25. A covered prop is lit by
those lights alone. Any other prop is lit by its light environment, brighter than vanilla, with a note. The
[in-game editor design](design/in-game-editor.md) has the reasons.

### lights

These are the level's own lights, which the in-game editor adds. They cast no shadows and are forced dynamic.

| Key | Default | What |
|---|---|---|
| `tag` | required | How the build and the editor find the light again |
| `kind` | `props` | `props` lights props only (the `Gameplay_4` channel). `room` also lights walls, vanilla meshes and pawns |
| `location` | required | `[x, y, z]` |
| `radius` | required | Over 0 |
| `brightness` | required | 0 or more |
| `color` | white | `[r, g, b]`, 0 to 255 |
| `falloff` | 2 | `FalloffExponent`, over 0 |
| `enabled` | true | Off with `false`. `removed` is refused |
| `template` | `lightTemplate`, else LE1CP's medbay light | The light to copy |

A light no entry names any more stays as built, with a note. Switch a light off instead of deleting its entry.

### vanilla

`vanilla` changes actors and lights of vanilla files without touching those files. `<file>` is named without its extension.

```yaml
vanilla:
  BIOA_FRE31_00_LAY.StaticMeshActor_69: { location: [-210.73, -15227.6, -408.58] }
  BIOA_FRE31_00_LAY.StaticLightCollectionActor_131.PointLight_309_LC: { enabled: false }
```

| Key form | Keys |
|---|---|
| `<file>.<actor>` | `location`; `yaw`, which keeps the file's pitch and roll, or `rotation`; `scale` and `scale3d`, not on an emitter; `removed`, where `true` hides it with no collision |
| `<file>.<collection>.<light>` | `location`; `brightness`, 0 or more; `color`; `radius` and `falloff`, over 0; `enabled` |

An actor's file must be in `streaming.levels`. The actor must be a `StaticMeshActor`, a `BioInert` or an `Emitter` that no
Kismet variable of its file holds. Anything else refuses the whole build, and so does `removed` on an actor with
`bIgnoreOwnerHidden`.

A light's file must be in `streaming.levels`, or be a `characterLights` item, which puts the light in the level's copy. Only
point and spot lights that Kismet does not hold may change. A field left out keeps the file's value.

Each streamed file with entries gets a fork beside the level, and the level streams the fork in its place. A fork is a copy of
the file's highest mounted copy outside the mod's DLC folder. It is named `<level>_<file>` without `BIOA_`, as in
`MYM01_FRE31_00_LAY`, with copies of its localized packages as `<fork>_LOC_<lang>`. A fork with no entries left is deleted,
and so is the fork of a file that left `streaming.levels`.

### What a level build writes

A build checks the file, its forks and the level in memory before it writes anything, so a refused build writes nothing.
It then writes the forks, the level, its `.m3cd` and the level's entry of `levels.json`, deletes the forks it no longer
streams, and writes the names last. `levels.json` is backed up as the level is. That file is
`%LOCALAPPDATA%\MeleBridge\LE1\levels.json`, or `MELE_LEVELS_JSON`, and [formats.md](formats.md) describes it. A failure
after the first write names each file written and its backup.

## Conversation files

A conversation file builds an LE1 conversation in a level that exists. The first build copies it from a template.

| Key | Value | What |
|---|---|---|
| `package` | Required | The level |
| `conversation` | Required | Found by name, path or a quoted `#<UIndex>`. A plain name is created from `from` when the level has none |
| `from` | `{ package, conversation, resref, nodeIds }` | The template to copy the first time. All four are required |
| `globalTlk` | A list of files | Optional: the text goes there too |
| `start` | A list of entry nodes | Replaces the starting list. The first entry whose condition passes plays |
| `nodes` | `{ <name>: node }` | The lines |

In `from`, `package` is the template's file, and `conversation` is its name. `resref` is the conversation's own
`m_nResRefID`, and `nodeIds` is the first of its own node ids. Both must be over 0.

The copy takes the template's package with everything it references. The packages become `<base>_D` and `<base>_N`, and the
talk file set `TlkSet_<base>`, where `<base>` is `conversation` without `_dlg`. Every node's plot condition and transition is
cleared. A level that already has the template's package or the new names is refused.

### Nodes

A node named `E<n>` or `R<n>` is entry n or reply n of the conversation, and the YAML changes it. Any other name is a new line.
The first build copies it from `template`, and later builds find it by its strref.

| Key | For | What |
|---|---|---|
| `strref` | `E<n>`, `R<n>` | A new text strref. The voice track and silent lip sync (`FXA_<strref>`, `FXA_<strref>_M`) follow |
| `entry` | new lines | An NPC line, with the strref of its text. Not with `reply` |
| `reply` | new lines | A Shepard line, with the strref of its text. Not with `entry` |
| `template` | new lines | Required on the first build. The node to copy, of the same kind: `E<n>`, `R<n>` or a name defined above |
| `length` | any | Seconds, over 0: the line's `InterpData` length |
| `text` | any | The line's text |
| `if` | any | A condition, see [Conditions](#conditions) |
| `transition` | any | A plot transition id, or `none` |
| `wheel` | replies | The wheel text: `{ strref, category, <language>: text }`. Text needs `strref` |
| `replies` | entries | The wheel, replaced whole: reply names, or `{ to, category, paraphrase, <language>: text }`. `to` is required, and text needs `paraphrase` |
| `then` | replies | The entries that follow. `[]` ends the conversation |

`start`, `replies` and `then` name a node of `nodes`, or any `E<n>` or `R<n>` of the conversation. A choice given by name takes
its text and category from the reply's `wheel`, else from an existing choice that leads to that reply.

`category` is a reply category without its `REPLY_CATEGORY_` prefix, such as `DEFAULT` or `INVESTIGATE`. An unknown one lists
the valid names. The text goes to every talk file of the conversation's set, and to any `globalTlk` files.

### Conditions

| `if` | Passes when |
|---|---|
| `bool <id>`, `bool <id> true` | the plot bool is true |
| `not bool <id>`, `bool <id> false` | the plot bool is false |
| `conditional <id>` | the plot conditional passes |
| `conditional <id> param <n>` | the conditional passes with that parameter |
| `none` | always: the node's condition is removed |

For a line a henchman says, copy a template whose speaker list names the henchmen. Give each such line
`if: conditional 415 param -1`, which passes when that speaker is in the squad. List the lines in priority order, because the
first that passes plays.

## Plot files

A plot file builds a DLC plot package with state transitions, consequences and journal quests.

```yaml
package: DLC_MOD_MyMod/CookedPCConsole/PlotManagerAutoDLC_MyMod.pcc
globalTlk: [DLC_MOD_MyMod_GlobalTlk.pcc]
transitions:
  8422: [int 47 += 2, bool 8421 = true]
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

| Key | Value | What |
|---|---|---|
| `package` | Required | The plot package. A missing one is created with an empty `StateTransitionMap` and `ConsequenceMap` |
| `autoload` | A path, or `none` | The `AutoLoad.ini` to add lines to, looked for beside the YAML, then beside the package. Default: the one in the folder above the package. `none` leaves it alone |
| `globalTlk` | A list of files | Where journal text goes. Required when any text is given |
| `transitions` | `{ <id>: [elements] }` | The `StateTransitionMap`: what a node's transition runs. `[]` removes the event |
| `consequences` | `{ <id>: [elements] }` | The `ConsequenceMap`, in the same form |
| `quests` | `{ <id>: quest }` | Journal quests, see [Quests](#quests) |

Elements use the form `plot_map_get` prints, and a trailing ` [vN]` is ignored:

| Element | What |
|---|---|
| `bool <id> = true` or `= false` | Sets a plot bool |
| `int <id> = <n>`, `int <id> += <n>` | Sets or adds to a plot int |
| `float <id> = <x>`, `float <id> += <x>` | Sets or adds to a plot float |
| `consequence <id>` | Runs a consequence |

Every event and quest in the file replaces the package's. The others are kept and listed in `notes`.

### Quests

| Key | What |
|---|---|
| `mission` | `true` puts the quest under Missions instead of Assignments |
| `title` | Text |
| `goals` | A list of `{ title, description, state, conditional }`. `title` defaults to the quest's. A goal shows while its `state` bool is true or its `conditional` passes. When several pass, the later one shows |
| `tasks` | At least one, as `{ name, description, complete, plotItems, planet, waypoint }`. `name` is required. `complete: true` moves the quest to Completed. `plotItems` lists indexes into the quest's `plotItems`. `planet` and `waypoint` are names |
| `plotItems` | A list of `{ name, icon, state, conditional, targetItems }`. `name` is required, `icon` defaults to 0, and `targetItems` to -1 |
| `evals` | A list of `{ when, task, state, conditional }`. `when` (`bool <id>`, `int <id>` or `float <id>`) and `task` (an index into `tasks`) are required. When the variable changes, the first evaluation that passes shows its task |

Text keys (`title`, `description`, `name`) take a strref whose text a talk file already has, or `{ strref, <language>: text }`.
`description`, `state` and `conditional` default to -1. Text keys, `state` and `conditional` take `none` for -1.

### What a plot build writes

Besides the package and its text, a build adds the `[Packages]` lines the package needs to `AutoLoad.ini`
 (`PlotManagerStateTransitionMap<n>`, `PlotManagerConsequenceMap<n>`, `PlotManagerQuestMap<n>`), numbered after
the file's own. Without an `AutoLoad.ini`, a note gives the lines to add.
