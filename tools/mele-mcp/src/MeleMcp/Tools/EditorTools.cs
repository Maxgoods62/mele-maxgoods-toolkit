using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>The in-game editor's saved work (MeleBridge, F8; docs/design/in-game-editor.md) brought into the mod's files. Offline: the game need not run.</summary>
[McpServerToolType]
public static class EditorTools
{
    [McpServerTool(Name = "editor_import", Destructive = true),
     Description("Merges what the in-game editor saved (F8, Save: MeleBridge's session file of the map, version 1 to 3) into a level_build YAML file, "
                 + "keeping its comments, then builds the level. Actors are matched by tag with the npcs, usables, props and lights entries. A props entry "
                 + "gets its location, rotation ('yaw' while upright, else 'rotation: [pitch, yaw, roll]' in place of 'yaw'), 'scale' and 'scale3d' "
                 + "when the editor changed them; npcs and usables a location and a yaw (a pitch, roll or scale is noted, not written). Field by field "
                 + "against the session's base (what the YAML held when the editor first changed the actor): a value changed only in the YAML is "
                 + "kept, one changed on both sides is a conflict (onConflict). A version 1 entry, or a spawn never imported, has no base: the "
                 + "editor's values win. A deleted prop gets 'removed: true' (the build hides it; npcs and usables are only noted), merged "
                 + "the same way. A mesh spawned in the editor gets a props entry named by its tag, with 'mesh' (and 'propTemplate' when the "
                 + "file has none), once: one an import already put in a YAML file is not added to a file without its tag; on the map's own "
                 + "file, one unchanged since is dropped from the session and kept in <session>.dropped.json beside it (the notes say how "
                 + "to add it again). "
                 + "A light spawned in the editor gets a lights entry named by its tag (kind, location, radius, brightness, color, falloff; "
                 + "'enabled: false' when switched off), taken as a spawned mesh is; a light of a lights entry is merged field by field the "
                 + "same way (location, kind, radius, brightness, color, falloff, enabled); a deleted one gets 'enabled: false' (a light is "
                 + "switched off, never removed), and one deleted before any import leaves nothing. An NPC light (its entry's npc block: "
                 + "its NPC's tag and its offset [forward, right, up]) goes to the file whose npcs has its NPC, whatever takeSpawned says (on "
                 + "the Normandy the editor spawns it in BIOA_NOR00), after the NPCs' own entries: its item of npcs.<name>.lights is "
                 + "merged field by field the same way (offset within 0.02, written to 2 decimals; radius, brightness, color, falloff, "
                 + "enabled; a key left out is the template's: white, 2, true); one spawned in the editor gets a new item with every field "
                 + "('enabled: false' when off), and the list with its comment when the NPC has none. An item of another NPC than the "
                 + "entry's is refused; one removed by hand is never added back (dropped from the session when unchanged since). "
                 + "A vanilla actor of a file the YAML streams (streaming.levels; an actor of a fork counts as its vanilla file's) gets "
                 + "'vanilla.<file>.<actor>', merged the same way, a value the entry leaves out being the file's; a new one holds the fields "
                 + "the editor changed (against the file's values too when the session's base is not the file's: an entry deleted by hand "
                 + "since), with the mesh (an effect: 'Emitter <template>') and their vanilla values in a comment on its key line, and a deleted one 'removed: true'; shown "
                 + "again, it loses 'removed', and the whole entry when that was all it had. A light of a light collection (a 'vanilla' "
                 + "light block) gets 'vanilla.<file>.<collection>.<light>' (one of the map's characterLights copy: keyed by its source "
                 + "file), merged per field as a lights entry is (deleted: 'enabled: false'), a new one with its light type and the vanilla "
                 + "values it changes in its comment. "
                 + "Refused with the reason: what level_build would refuse (a class other than StaticMeshActor, BioInert or Emitter, an actor "
                 + "Kismet holds, a delete of one drawn whatever its owner) and a room shell (not an effect). One imported before whose entry is gone was removed "
                 + "by hand: dropped from the session when unchanged since, else skipped. A vanilla actor of a file the YAML doesn't stream, "
                 + "or of another map's session, stays dev-only (skipped; MeleBridge re-applies it). The entries applied are marked in the session file "
                 + "(base = what the YAML holds now, 'imported'), so MeleBridge re-applies them until the install shows them and then drops "
                 + "them; importing again changes nothing. To undo an import, restore the session file together with the YAML: a YAML "
                 + "reverted alone reads as edits by hand (an imported spawn leaves the session, a moved actor takes the YAML's place back); "
                 + "try options with keepSession=true. Offline: the game need not run. Install the level (game closed: mod_install) to see "
                 + "it after a reload.")]
    public static EditorImportResult EditorImport(
        [Description("Level YAML file (level_build format; Windows or /mnt/<drive> path).")] string file,
        [Description("Session file (Windows or /mnt/<drive> path); default %LOCALAPPDATA%\\MeleBridge\\LE1\\sessions\\<the YAML's package name>.json (the "
                     + "sessions folder beside MELE_LEVELS_JSON when set), or for a level the game loads into another map (sideloaded, e.g. on the "
                     + "Normandy) the newest session with an actor of it, or with an entry whose tag the file has (a mesh imported into it before, an "
                     + "NPC light) or a light of one of its NPCs. The first import of meshes spawned on that map needs it, "
                     + "with takeSpawned=true.")] string? session = null,
        [Description("Build the level after writing the file.")] bool build = true,
        [Description("Meshes spawned in the editor become props entries of this file. Default: when the session is of the map this file builds; "
                     + "for a sideloaded level pass true on the one file that should have them.")] bool? takeSpawned = null,
        [Description("Leave the session file untouched (default: mark the entries this import applied as imported, with the YAML's values as their base).")] bool keepSession = false,
        [Description("When the editor and the YAML both changed a field since the base: refuse (default: the actor is not applied and is listed with "
                     + "the base, YAML and editor values), editor (the editor's value) or yaml (the YAML's value stays, and goes into the session entry too).")] string onConflict = "refuse",
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => Core.EditorImport.Run(PackageTools.ParseGame(game), file, session, build, takeSpawned, keepSession, onConflict));
}
