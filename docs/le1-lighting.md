# LE1 character lighting

An NPC a mod adds to Mass Effect 1 Legendary Edition (LE1) can read much darker than the vanilla NPC beside her: some rooms
are dark for every dynamic object, and the vanilla NPC has a light of her own. This page says why, and how the toolkit lights
its NPCs: small point lights on the Dynamic channel, kept at an offset from the NPC, with the level filter off, because LE1's
level filter never matches a pawn in a sideloaded level. Both findings come from one test, a cloned NPC placed in the
Normandy's medbay. Section numbers A.8 and A.9 are stable: code comments cite them (`§A.8–A.9`).

**Where the toolkit uses this:**

| Part | What it does |
|---|---|
| `level_build`, `npcs.<name>.lights` | Builds each NPC light from a template, by default LE1CP's medbay light (`PointLightToggleable_7` in `BIOA_NOR10_07_DSG.pcc`, A.8), unless the YAML sets `lightTemplate`. Places it at the NPC's pose plus its offset `[forward, right, up]`, turned by her yaw. Always writes `bOnlyAffectSameAndSpecifiedLevels` false (A.9). |
| The in-game editor, Add light | Spawns an NPC light with the template's look: Dynamic channel only, no dynamic shadows, no level filter, offset `[60, 0, 80]`, radius 100, brightness 1.2 ([editor design](design/in-game-editor.md) §12.3). |

## A.8 A dark NPC in a dark room

**The test:** a clone of the vanilla medbay doctor (`BIONPC_Chakwas.pcc`, `BioPawn_3`), recoloured, placed next to her in
the medbay (`BIOA_NOR10_07`). The clone read darker than the doctor, uniform included.

**What the room does:**

| Fact | Detail |
|---|---|
| The medbay is dark for dynamic objects | Shepard reads dark there too. Static meshes carry baked lighting; a pawn is lit by its light environment alone. |
| LE1CP adds a light for the doctor | `PointLightToggleable_7` in `BIOA_NOR10_07_DSG.pcc`: radius 140, on the Dynamic channel only, `bOnlyAffectSameAndSpecifiedLevels` true with `OtherLevelsToAffect` = [`BIOA_NOR10_07`, `BIONPC`]. The medbay Kismet that activates her switches it on. |
| LE1CP lowers her ambient | Her `DynamicLightEnvironmentComponent.fAmbientScalar` is 0.2; the class default is 0.3 (`Engine.Default__DynamicLightEnvironmentComponent`). |
| A clone gets neither | It copies the low ambient and has no light of its own. |
| Ambient is not the fix | Raising the clone's `fAmbientScalar` to 0.6 made no visible difference. |

**The fix: lights of her own.**

1. Copy LE1CP's light into the NPC's level file (`export_import`: 2 exports and 4 imports) and add it to the level's
   `Actors`.
2. Give it its own tag, `bEnabled` true, and a new `LightGuid` and `LightmapGuid`. Lightmaps, shadow maps and a primitive's
   `IrrelevantLights` know a light by its `LightGuid`, so no two lights may share one.
3. Move it: write the place to `Location` and to the component's `CachedParentToWorld.WPlane`.
4. Put the clone's `fAmbientScalar` back to 0.2.

**Placement, tested good with two lights.** A pawn's light environment takes two lights apart (`nNumMultiLights` = 2):

| Light | Where | Radius | Brightness |
|---|---|---|---|
| Fill | 120 units above the NPC's centre | 180 | 1.2 |
| Face | 60 units in front of her face, 15 above eye level (a copy of the fill light) | 100 | 1.2 |

- A light 81 units to one side lit that side only.
- The face light's radius keeps it off the doctor's face.

**Skin tone matters as much as light.** The per-pawn face material's `SkinTone` (a `VectorParameterValues` entry) of
0.25 / 0.10 / 0.05 reflects about a third of the doctor's 0.69 / 0.31 / 0.18, so the same light gives a face about 3× darker.
0.40 / 0.19 / 0.10 (medium brown) reads well with the face light. Raise skin values before adding more light.

## A.9 The level filter and sideloaded levels

**The test:** the clone and both lights moved into a level file of their own, made by `level_new` (a name that doesn't start
with `BIOA_`, so it never matches a Sideloader prefix itself). The Sideloader (`SFXGame.SideloaderFramework`) loads it into
the medbay, from a config delta (`.m3cd`) next to the level, which M3 merges into Coalesced at install:

```ini
[BioGame.ini SFXGame.SideloaderFramework]
+Levels=(LevelName="MyNPC_NOR10", LoadIfPrefix=("BIOA_NOR10_07"))
```

The medbay has no `BIOA_NOR10_07.pcc`; the prefix catches its `_ART`, `_DSG`, `_LAY`, `_PLC`, `_SND`, `_DS1` and `07wake`
files.

**What happened:** the lights loaded at the right place, and the clone was dark again. With
`bOnlyAffectSameAndSpecifiedLevels` true (copied from LE1CP's light), no setting reached a pawn in the sideloaded level:

| `OtherLevelsToAffect` | Result |
|---|---|
| [`BIOA_NOR10_07`, `BIONPC`] (LE1CP's; it worked while the pawn was in `BIONPC_Chakwas.pcc`) | dark |
| the level's name prefix (`MyNPC`) | dark |
| the level's exact file name (`MyNPC_NOR10`) | dark |
| empty: the "same level" rule (light and pawn in one file) | dark |

Vanilla lights list area names that are not files (`BIOA_NOR10_07`, `BIOA_NOR10_08`), so LE1 does not match file names the way
stock UE3 does. **Unverified:** what LE1 compares.

**The fix:** `bOnlyAffectSameAndSpecifiedLevels` false on our lights. The filter kept a light off other levels' objects;
without it a light reaches everything on its channels within its radius. So:

- keep the radius small (180 for the fill light, 100 for the face light);
- check the neighbours: the vanilla doctor, 115 units away, stood outside the face light and at the edge of the fill light,
  and looked unchanged.

With the filter off, the clone in its own level looked as she did in `BIONPC_Chakwas.pcc` (tested good).

**What the toolkit writes:** `level_build` writes `bOnlyAffectSameAndSpecifiedLevels` false on every light it builds, NPC
lights and `lights:` entries alike. A `lights:` entry also drops its template's `OtherLevelsToAffect`; an NPC light keeps it
(with the filter off, the list is ignored). A level's own lights, in its static light collections, keep their filter: the
build never writes it ([editor design](design/in-game-editor.md) §12.4).
