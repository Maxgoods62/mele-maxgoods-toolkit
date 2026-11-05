# LE1 character lighting

This page explains why an NPC a mod adds to LE1 can render darker than the vanilla NPC beside it, and which lights the
toolkit gives that NPC. Everything here is LE1 only. Code comments cite sections A.8 and A.9 by number.

| Part | What it does |
|---|---|
| `level_build`, `npcs.<name>.lights` | Builds each listed light from the YAML's `lightTemplate`, else LE1CP's medbay light. Places it at the NPC's pose plus its offset `[forward, right, up]`, turned by the NPC's yaw. Writes the level filter off (A.9). |
| The in-game editor, Add light | Spawns an NPC light with that template's look: Dynamic channel only, no dynamic shadows, level filter off, offset `[60, 0, 80]`, radius 100, brightness 1.2 ([editor design](design/in-game-editor.md) §12.3). |

An NPC gets only the lights its YAML or the editor gives it. `level_build` adds none by itself.

## A.8 A dark NPC in a dark room

The test placed a recoloured clone of the medbay doctor (`BioPawn_3` in `BIONPC_Chakwas.pcc`) next to her in
`BIOA_NOR10_07`. The clone read darker than the doctor, uniform included. These facts explain it:

| Fact | Detail |
|---|---|
| The room is dark for dynamic objects | Static meshes carry baked lighting, and a pawn has only its light environment. Shepard reads dark there too. |
| The doctor has her own light | LE1CP adds `PointLightToggleable_7` in `BIOA_NOR10_07_DSG.pcc`: radius 140, Dynamic channel only, level filter on with `OtherLevelsToAffect` = [`BIOA_NOR10_07`, `BIONPC`]. The medbay Kismet that activates her switches it on. |
| The doctor has lowered ambient | LE1CP sets her `DynamicLightEnvironmentComponent.fAmbientScalar` to 0.2. The class default is 0.3. |
| A clone gets neither | It copies the low ambient and has no light of its own. Raising its `fAmbientScalar` to 0.6 made no visible difference. |

The fix gives the NPC lights of its own. `level_build` does the work on each new copy:

1. It copies the template light into the NPC's level file and adds it to the level's `Actors`.
2. It writes a new tag, `bEnabled` true, and a new `LightGuid` and `LightmapGuid`. Lightmaps, shadow maps and a
   primitive's `IrrelevantLights` know a light by its `LightGuid`, so no two lights may share one.
3. It writes the place to `Location` and to the component's `CachedParentToWorld.WPlane`.

`level_build` never writes `fAmbientScalar`, so a clone keeps its template's value.

A pawn's light environment takes two lights apart (`nNumMultiLights` = 2). These two tested good:

| Light | Where | Radius | Brightness |
|---|---|---|---|
| Fill | 120 units above the NPC's centre | 180 | 1.2 |
| Face | 60 units in front of the face, 15 above eye level | 100 | 1.2 |

A light 81 units to one side lit that side only. The face light's radius keeps it off the doctor's face.

Skin tone matters as much as light. A `SkinTone` of 0.25 / 0.10 / 0.05 reflects about a third of the doctor's 0.69 /
0.31 / 0.18, so the same light gives a face about 3× darker. 0.40 / 0.19 / 0.10 (medium brown) reads well with the face
light. The NPC's `skin` key sets this value, so raise it before adding more light.

## A.9 The level filter and sideloaded levels

LE1CP's Sideloader (`SFXGame.SideloaderFramework`) loads an extra level file into a map, from a config delta (`.m3cd`)
that M3 merges into Coalesced at install. The test moved the clone and both lights into their own level file, whose name
doesn't start with `BIOA_`:

```ini
[BioGame.ini SFXGame.SideloaderFramework]
+Levels=(LevelName="MyNPC_NOR10", LoadIfPrefix=("BIOA_NOR10_07"))
```

No `BIOA_NOR10_07.pcc` exists. The prefix catches the medbay's `_ART`, `_DSG`, `_LAY`, `_PLC`, `_SND`, `_DS1` and
`07wake` files.

The lights loaded at the right place, and the clone was dark again. With `bOnlyAffectSameAndSpecifiedLevels` true (the
level filter, copied from LE1CP's light), no setting reached a pawn in the sideloaded level:

| `OtherLevelsToAffect` | Result |
|---|---|
| [`BIOA_NOR10_07`, `BIONPC`], which works for a pawn in `BIONPC_Chakwas.pcc` | dark |
| the level's name prefix (`MyNPC`) | dark |
| the level's exact file name (`MyNPC_NOR10`) | dark |
| empty, the "same level" rule | dark |

Vanilla lights list area names that are not files, so LE1 doesn't match file names as stock UE3 does. What LE1 compares
is unverified.

With the filter off, the clone in its own level looked as it did in `BIONPC_Chakwas.pcc`. An unfiltered light reaches
everything on its channels within its radius, so keep the radius small and check the neighbours. The vanilla doctor, 115
units away, stood outside the face light and at the edge of the fill light, and looked unchanged.

What the toolkit writes:

| Light | Level filter | `OtherLevelsToAffect` |
|---|---|---|
| NPC light, built | Off | Kept from the template, and ignored with the filter off |
| `lights:` entry | Off | Dropped |
| Level light (static light collection) | Never written | Never written ([editor design](design/in-game-editor.md) §12.4) |
