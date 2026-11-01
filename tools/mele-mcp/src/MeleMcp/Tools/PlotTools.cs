using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Plot maps: state transitions and consequences, read for every game and written for LE1.</summary>
[McpServerToolType]
public static class PlotTools
{
    [McpServerTool(Name = "plot_map_get", ReadOnly = true),
     Description("Lists state events of a BioStateEventMap or its subclass BioConsequenceMap (LE1 base game: BIOC_Materials.pcc "
                 + "PlotManagerAuto.StateTransitionMap/ConsequenceMap; an LE1 DLC: PlotManagerAutoDLC_<name>.pcc StateTransitionMap/ConsequenceMap; LE2 base game: Startup_INT.pcc, "
                 + "LE3: SFXGameInfoSP_SF.pcc; an LE2/LE3 DLC: its Startup_<name>_INT.pcc, PlotManagerAutoDLC_<name>.StateTransitionMap): id and what each element sets (bool, int, "
                 + "float, consequence, function), with its InstanceVersion as [vN]. A conversation node's nStateTransition runs the StateTransitionMap event with that id. "
                 + "An LE2/LE3 BioOutcomeMap (OutcomeMap) is a plain int-to-int map: each pair prints as event <key> 'outcome <value>'; what the game does with it is unverified.")]
    public static PlotMapEvents PlotMapGet(
        [Description("Package with the map, e.g. BIOC_Materials.pcc, Startup_INT.pcc (LE2), SFXGameInfoSP_SF.pcc (LE3). File name or full path.")] string package,
        [Description("Map export, e.g. StateTransitionMap or PlotManagerAuto.StateTransitionMap.")] string export = "StateTransitionMap",
        [Description("One event id.")] int? id = null,
        [Description("Lowest id to list.")] int? fromId = null,
        [Description("Highest id to list.")] int? toId = null,
        [Description("Only events that write this plot variable or consequence id.")] int? touches = null,
        [Description("Maximum events returned (Matches still counts all).")] int limit = 50,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => PlotMaps.Get(PackageTools.ParseGame(game), package, export, id, fromId, toId, touches, limit));

    [McpServerTool(Name = "plot_map_new", Destructive = true),
     Description("Creates an LE1 DLC plot package (e.g. PlotManagerAutoDLC_<ModName>.pcc in the mod's CookedPCConsole) with empty "
                 + "StateTransitionMap and ConsequenceMap, as LE1CP's framework file. Returns the AutoLoad.ini [Packages] lines that make "
                 + "the game load them. Refuses an existing file and the game install.")]
    public static NewPlotMapsResult PlotMapNew(
        [Description("Full path of the new .pcc (Windows or /mnt/<drive>).")] string path,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => PlotMaps.NewPackage(PackageTools.ParseGame(game), path));

    [McpServerTool(Name = "plot_event_set", Destructive = true),
     Description("Adds or replaces one state event in an LE1 BioStateEventMap. Elements use plot_map_get's notation: "
                 + "\"bool 8421 = true\", \"int 47 += 2\" (Paragon points; 46 = Renegade), \"float 3 = 0.5\", \"consequence 927\". "
                 + "An empty list removes the event. Use ids from your mod's own block. Backs the package up first and refuses files inside the game install.")]
    public static PlotEventEdit PlotEventSet(
        [Description("Package with the map: your mod's PlotManagerAutoDLC_<name>.pcc. File name or full path.")] string package,
        [Description("State event id (transition id used by conversation nStateTransition).")] int id,
        [Description("Elements, e.g. [\"bool 8421 = true\", \"int 47 += 2\"]. Empty = remove.")] string[] elements,
        [Description("BioStateEventMap export.")] string export = "StateTransitionMap",
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => PlotMaps.SetEvent(PackageTools.ParseGame(game), package, export, id, elements));

    [McpServerTool(Name = "plot_build", Destructive = true),
     Description("Builds an LE1 DLC plot package from a YAML file: state transitions and consequences (elements as plot_map_get prints them), "
                 + "journal quests (title, goals, tasks, plot items, task evaluations) with their text written to the mod's GlobalTlk files, and "
                 + "the missing AutoLoad.ini [Packages] lines. A missing package given as a full path, or under the mod root from the "
                 + "DLC folder on (as mod_build says), is created. Every event and quest in the file replaces the package's, others are "
                 + "kept; running it twice reports 'unchanged'. The format is in mele-maxgoods-toolkit's docs/yaml-reference.md, Plot files. Backs every file up first and refuses "
                 + "files inside the game install.")]
    public static PlotBuildResult PlotBuild(
        [Description("YAML file (Windows or /mnt/<drive> path).")] string file,
        [Description("Plot package, instead of the file's 'package'.")] string? package = null,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => PlotBuilder.Build(PackageTools.ParseGame(game), file, package));
}
