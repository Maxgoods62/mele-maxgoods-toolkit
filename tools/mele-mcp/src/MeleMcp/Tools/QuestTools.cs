using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Game 1 journal: BioQuestMap quests and task evaluations.</summary>
[McpServerToolType]
public static class QuestTools
{
    [McpServerTool(Name = "quest_map_get", ReadOnly = true),
     Description("Reads an LE1 BioQuestMap (journal): quests with goals, tasks and plot items (strrefs with their global text, conditionals, states, "
                 + "InstanceVersions), and the task evaluations a plot bool/int/float change runs (quest, task, conditional, state). "
                 + "Base game: BIOC_Materials.pcc PlotManagerAuto.QuestMap; a DLC: PlotManagerAutoDLC_<name>.pcc QuestMap.")]
    public static QuestMapInfo QuestMapGet(
        [Description("Package with the map, e.g. BIOC_Materials.pcc. File name or full path.")] string package,
        [Description("BioQuestMap export, e.g. QuestMap or PlotManagerAuto.QuestMap.")] string export = "QuestMap",
        [Description("One quest id.")] int? questId = null,
        [Description("Only task evaluations run by this plot variable id, and their quests.")] int? touches = null,
        [Description("Language for the text: INT, FR, DE...")] string language = "FR",
        [Description("Maximum quests returned.")] int limit = 20,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => QuestMaps.Get(PackageTools.ParseGame(game), package, export, questId, touches, language, limit));

    [McpServerTool(Name = "quest_set", Destructive = true),
     Description("Adds, replaces or removes one LE1 journal quest in a BioQuestMap, with all its task evaluations, using the base game's "
                 + "InstanceVersions. Journal model: a goal is the quest's title and description, shown while its state bool is true (or its "
                 + "conditional passes); a task is the current step, and a questComplete task moves the quest to Completed; when plot variable Id "
                 + "changes, the first of its evaluations whose state bool is true (-1: always) shows its task. Strings must be in the GlobalTlk. "
                 + "A package without the map gets one (add the returned AutoLoad.ini line). Backs the package up first and refuses files inside the game install.")]
    public static QuestEdit QuestSet(
        [Description("Package for the map: your mod's PlotManagerAutoDLC_<name>.pcc. File name or full path.")] string package,
        [Description("Quest id, unique among all quest maps (check with id_alloc kind quest).")] int questId,
        [Description("Goals: name (title strref), description (strref), state (bool shown while true, -1), conditional (-1).")] QuestGoalSpec[]? goals = null,
        [Description("Tasks: name, description (strrefs), questComplete, plotItems (indexes), planetName, waypoint.")] QuestTaskSpec[]? tasks = null,
        [Description("Evaluations: id (plot variable), task (index), variable (bool/int/float), state (bool that must be true, -1), conditional (-1). Order matters: the first match wins.")] QuestTaskEvalSpec[]? evals = null,
        [Description("Plot items: name, iconIndex, state, conditional, targetItems.")] QuestPlotItemSpec[]? plotItems = null,
        [Description("Missions tab instead of Assignments.")] bool isMission = false,
        [Description("Remove the quest and its evaluations instead.")] bool remove = false,
        [Description("BioQuestMap export (created with this name if missing).")] string export = "QuestMap",
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => QuestMaps.Set(PackageTools.ParseGame(game), package, export, questId, isMission,
            goals ?? [], tasks ?? [], plotItems ?? [], evals ?? [], remove));
}
