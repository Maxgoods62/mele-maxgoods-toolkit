using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

[McpServerToolType]
public static class KismetTools
{
    private const string PackageDescription =
        "Package file name (resolved to the highest mounted copy) or a full Windows or /mnt/<drive> path.";

    private const string WriteRule =
        " Backs the package up first. Refuses files inside the game install: edit the mod's copy in the M3 library.";

    [McpServerTool(Name = "kismet_read", ReadOnly = true),
     Description("Reads a Kismet sequence as text: ops in flow order (events first, then along output links), each with its settings "
                 + "(EventName, Conv...), linked outputs (Out → Target.In) and variables with their values. With the Kismet Logger ASI "
                 + "installed, each op is marked ✓ (fired this game session, with input pins and counts) or ✗: play, then read where the chain stops.")]
    public static string KismetRead(
        [Description(PackageDescription)] string package,
        [Description("Sequence export (UIndex, path or unique name), e.g. TheWorld.PersistentLevel.Main_Sequence.")] string sequence = "TheWorld.PersistentLevel.Main_Sequence",
        [Description("Mark ops fired or not from the Kismet Logger's log of the current or last session.")] bool trace = true,
        [Description("all, fired or unfired.")] string show = "all",
        [Description("Also print every nested sequence (prefabs can hold hundreds of ops).")] bool recurse = false,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => KismetReader.Read(PackageTools.ParseGame(game), package, sequence, trace, show, recurse));

    [McpServerTool(Name = "kismet_add", Destructive = true),
     Description("Creates a Kismet object (event, action, variable) with its class defaults inside a sequence, e.g. "
                 + "BioSeqEvt_OnPlayerActivate, BioSeqAct_StartConversation, SeqVar_Object. Returns its link slot names for kismet_link; "
                 + "set its properties (Originator, ObjValue, Conv...) with export_set_batch." + WriteRule)]
    public static KismetAddResult KismetAdd(
        [Description(PackageDescription)] string package,
        [Description("Sequence export (UIndex or path), e.g. TheWorld.PersistentLevel.Main_Sequence.")] string sequence,
        [Description("Class name of the new object.")] string className,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => KismetEditor.Add(PackageTools.ParseGame(game), package, sequence, className));

    [McpServerTool(Name = "kismet_link", Destructive = true),
     Description("Links two Kismet objects: output (source output link → target action input), variable (source variable slot → "
                 + "SeqVar_*), event (source event slot → event). The link name must exist on the source (kismet_add lists them)." + WriteRule)]
    public static KismetLinkResult KismetLink(
        [Description(PackageDescription)] string package,
        [Description("Source object (UIndex or path).")] string source,
        [Description("output, variable or event.")] string kind,
        [Description("Link name on the source, e.g. Out, Owner, Instigator.")] string link,
        [Description("Target object (UIndex or path).")] string target,
        [Description("Input link index on the target action (output links only).")] int inputIndex = 0,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => KismetEditor.Link(PackageTools.ParseGame(game), package, source, kind, link, target, inputIndex));

    [McpServerTool(Name = "level_build", Destructive = true),
     Description("Builds an LE1 level from a YAML file. The level itself: vanilla sub-levels it shows (LevelStreamingKismet entries and a "
                 + "BioTriggerStream copied from a vanilla level, whose Default state lists them), the post-process of a vanilla level, light actors "
                 + "copied from another level (e.g. character lights), start points (found by tag, else imported from a template, on the navigation "
                 + "list, blocked for AI paths) for 'at <map> <tag>', and the squad (a vanilla squad prefab: party selection on load, then the picked "
                 + "henchmen spawn and follow Shepard). Its usables: a placeable copied from a template (e.g. a terminal), "
                 + "made targetable, with its own name, whose use runs BioSeqAct_AreaTransition to another map and start point. Its NPCs: the pawn (found by tag, else imported from a template pawn with everything "
                 + "it references, or cloned from an actor of the level), location and yaw, shown and active, own name (a renamed copy of its actor "
                 + "type, text written to the GlobalTlk), skin/hair/eye colors on its own materials, fill lights at offsets that turn with her, and "
                 + "the Main_Sequence Kismet that starts her conversation when Shepard uses her (a conversation the level does not have yet is "
                 + "listed in MissingConversations: build it, then the level again). Its props: any actor copied from a template (prefab "
                 + "archetypes baked in), placed; a StaticMeshActor's mesh goes on a copy of the BioInert 'propTemplate' "
                 + "(live_place and live_save write these); or, instead of a template, a 'mesh' (a StaticMesh export of a file) shown the "
                 + "same way with its own materials (editor_import writes these). A new copy of 'propTemplate' starts turned as the template "
                 + "StaticMeshActor, or not at all for a mesh; then 'yaw' (degrees, pitch and roll kept) or 'rotation: [pitch, yaw, roll]' "
                 + "(degrees; not both), 'scale' (DrawScale) and 'scale3d: [x, y, z]' (DrawScale3D) set it. 'removed: true' hides the prop's "
                 + "actor without collision (none: nothing is created), and a later build without it shows it again. A shown prop a light "
                 + "of the lights section covers (enabled, at least 0.25 of its falloff at the prop's bounds centre) is lit by those lights "
                 + "alone (its light environment off, its mesh on their channel, Gameplay_4); any other by its light environment, as its "
                 + "source is, brighter than vanilla (a note names it). Its lights: tag, location, radius and brightness, 'kind' (props: "
                 + "lights only props; room: walls, vanilla meshes and pawns too), 'color: [r, g, b]' (0-255, white), 'falloff' (2), "
                 + "'enabled' (true; a light is switched off, never removed), 'template' (else 'lightTemplate', else LE1CP's "
                 + "PointLightToggleable_7). Tags are unique across npcs (their lights too), usables, props and lights. Its vanilla actors "
                 + "('vanilla: <file>.<actor>', a StaticMeshActor or BioInert of a file in streaming.levels): 'location', 'yaw' or 'rotation', "
                 + "'scale', 'scale3d', 'removed: true'; a fork of the file (<map>_<file>, a renamed copy of its highest-mounted vanilla copy, "
                 + "next to the level, with byte copies of the file's localized packages as <fork>_LOC_<lang>) gets them, and the level "
                 + "streams it in place of the file (its waits for visible levels too); a file with no entries left streams again and its "
                 + "fork is deleted. Refused, nothing written (any file's refusal stops every fork): any other class (a door, usable, pawn, "
                 + "path node, volume, light, emitter), an actor a Kismet variable holds, 'removed' on an actor drawn whatever its owner "
                 + "(bIgnoreOwnerHidden). Warned: a room shell, cover nearby, a non-uniform scale with no collision cooked "
                 + "for it (a uniform one is made at load). The level's streamed files, shown props and lights go to MeleBridge's levels.json (not with 'package'). Creates a missing "
                 + "level (full path) and the Sideloader ConfigDelta that loads it. Only changed values are written; running it twice changes "
                 + "nothing. See the tool README for the format. Backs every file up first (not forks: each is built again from the vanilla "
                 + "file) and refuses files inside the game install.")]
    public static LevelBuildResult LevelBuild(
        [Description("YAML file (Windows or /mnt/<drive> path).")] string file,
        [Description("Level package, instead of the file's 'package'.")] string? package = null,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => LevelBuilder.Build(PackageTools.ParseGame(game), file, package));
}
