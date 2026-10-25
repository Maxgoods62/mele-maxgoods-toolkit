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
}
