using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>The in-game editor's saved work (MeleBridge, F8; docs/07-ingame-editor.md) brought into the mod's files. Offline: the game need not run.</summary>
[McpServerToolType]
public static class EditorTools
{
    [McpServerTool(Name = "editor_import", Destructive = true),
     Description("Merges what the in-game editor saved (F8, Save: MeleBridge's session file of the map) into a level_build YAML file, keeping "
                 + "its comments, then builds the level. Actors are matched by tag with the npcs, usables and props entries: a moved one gets "
                 + "its location and yaw (pitch and roll are not kept), a deleted prop gets 'removed: true' (the build hides it; npcs and usables "
                 + "are only noted), a mesh "
                 + "spawned in the editor gets a props entry named by its tag, with 'mesh' (and 'propTemplate' when the file has none). "
                 + "Vanilla actors are skipped: moving or deleting them ships later. The entries applied are dropped from the session file, so "
                 + "a later import can't put an old position back over a newer YAML edit; importing again changes nothing. "
                 + "Offline: the game need not run. Install the level (game closed: mod_install) to see it after a reload.")]
    public static EditorImportResult EditorImport(
        [Description("Level YAML file (level_build format; Windows or /mnt/<drive> path).")] string file,
        [Description("Session file (Windows or /mnt/<drive> path); default %LOCALAPPDATA%\\MeleBridge\\LE1\\sessions\\<the YAML's package name>.json, or for "
                     + "a level the game loads into another map (sideloaded, e.g. on the Normandy) the newest session with an actor of it.")] string? session = null,
        [Description("Build the level after writing the file.")] bool build = true,
        [Description("Meshes spawned in the editor become props entries of this file. Default: when the session is of the map this file builds; "
                     + "for a sideloaded level pass true on the one file that should have them.")] bool? takeSpawned = null,
        [Description("Leave the session file as it is (default: drop the entries this import applied).")] bool keepSession = false,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => Core.EditorImport.Run(PackageTools.ParseGame(game), file, session, build, takeSpawned, keepSession));
}
