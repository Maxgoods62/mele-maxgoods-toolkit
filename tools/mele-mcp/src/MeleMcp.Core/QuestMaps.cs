using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using static LegendaryExplorerCore.Unreal.BinaryConverters.BioQuestMap;

namespace MeleMcp.Core;

public sealed record QuestGoalInfo(int Index, int Version, int Name, string? NameText, int Description, string? DescriptionText, int Conditional, int State);

public sealed record QuestTaskInfo(int Index, int Version, bool QuestComplete, int Name, string? NameText, int Description, string? DescriptionText,
                                   IReadOnlyList<int> PlotItems, string? PlanetName, string? Waypoint);

public sealed record QuestPlotItemInfo(int Index, int Version, int Name, string? NameText, int IconIndex, int Conditional, int State, int TargetItems);

public sealed record QuestInfo(int Id, int Version, bool IsMission, IReadOnlyList<QuestGoalInfo> Goals, IReadOnlyList<QuestTaskInfo> Tasks, IReadOnlyList<QuestPlotItemInfo> PlotItems);

/// <param name="Variable">bool, int or float: the kind of plot variable <paramref name="Id"/> is.</param>
/// <param name="Id">Plot variable whose change runs the evaluations.</param>
public sealed record TaskEvalInfo(string Variable, int Id, int ListVersion, int Version, int Quest, int Task, int Conditional, int State);

public sealed record QuestGoalSpec(int Name, int Description, int Conditional = -1, int State = -1);

public sealed record QuestTaskSpec(int Name, int Description, bool QuestComplete = false, int[]? PlotItems = null, string? PlanetName = null, string? Waypoint = null);

public sealed record QuestPlotItemSpec(int Name, int IconIndex, int Conditional = -1, int State = -1, int TargetItems = -1);

/// <param name="Id">Plot variable whose change runs the evaluation.</param>
/// <param name="State">Plot bool that must be true for this evaluation to pick its task (-1: none).</param>
public sealed record QuestTaskEvalSpec(int Id, int Task, string Variable = "bool", int Conditional = -1, int State = -1);

public sealed record QuestEdit(string Path, ExportRow Map, bool CreatedMap, QuestInfo? Before, QuestInfo? After, IReadOnlyList<TaskEvalInfo> TaskEvals,
                               IReadOnlyList<string> AutoLoadLines, string BackupPath);

public sealed record QuestMapInfo(ExportRow Map, int QuestCount, int TaskEvalCount, int MinQuestId, int MaxQuestId, IReadOnlyList<QuestInfo> Quests, IReadOnlyList<TaskEvalInfo> TaskEvals);

public sealed record CodexSectionInfo(int Id, int Version, bool IsPrimary, int Title, string? TitleText, int Description, string? DescriptionText, int TextureIndex, int Priority, int CodexSound);

/// <param name="Section">Id of the section the page is listed in.</param>
public sealed record CodexPageInfo(int Id, int Version, int Section, int Title, string? TitleText, int Description, string? DescriptionText, int TextureIndex, int Priority, int CodexSound, string? CodexSoundString);

public sealed record CodexMapInfo(ExportRow Map, int SectionCount, int PageCount, IReadOnlyList<CodexSectionInfo> Sections, IReadOnlyList<CodexPageInfo> Pages);

/// <summary>
/// The journal: the BioQuestMap (quests with goals, tasks and plot items, and the task evaluations run by plot changes), read for
/// every game and written for Game 1. LE2/LE3 also read the BioCodexMap.
/// </summary>
public static class QuestMaps
{
    /// <summary>A <see cref="QuestMapInfo"/>, or a <see cref="CodexMapInfo"/> when <paramref name="export"/> is an LE2/LE3 BioCodexMap.</summary>
    public static object Get(MEGame game, string fileOrPath, string export, int? questId, int? touches, string language, int limit)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        var map = ExportInspector.Resolve(pcc, export);
        if (map.ClassName == "BioCodexMap" && !game.IsGame1()) return Codex(game, map, questId, language, limit);
        if (map.ClassName != "BioQuestMap") throw new ArgumentException($"#{map.UIndex} is a {map.ClassName}, not a BioQuestMap.");

        var bin = ObjectBinary.From<BioQuestMap>(map);
        var strings = game.IsGame1() ? GlobalStrings.Load(game, language) : TalkTables.Load(game, language).Male;
        string? Text(int id) => strings.GetValueOrDefault(id);

        var evals = new[] { ("bool", bin.TaskEvals), ("int", bin.IntTaskEvals), ("float", bin.FloatTaskEvals) }
            .SelectMany(t => t.Item2.SelectMany(list => list.TaskEvals.Select(e =>
                new TaskEvalInfo(t.Item1, list.ID, list.InstanceVersion, e.InstanceVersion, e.Quest, e.Task, e.Conditional, e.State))))
            .Where(e => (questId is null || e.Quest == questId) && (touches is null || e.Id == touches))
            .ToList();

        var quests = bin.Quests
            .Where(q => (questId is null || q.ID == questId) && (touches is null || evals.Any(e => e.Quest == q.ID)))
            .Take(limit)
            .Select(q => Info(q, Text))
            .ToList();

        return new QuestMapInfo(ExportInspector.Row(map), bin.Quests.Count, bin.TaskEvals.Sum(l => l.TaskEvals.Count),
            bin.Quests.Count == 0 ? 0 : bin.Quests.Min(q => q.ID), bin.Quests.Count == 0 ? 0 : bin.Quests.Max(q => q.ID),
            quests, evals.Take(limit * 4).ToList());
    }

    private static CodexMapInfo Codex(MEGame game, ExportEntry map, int? id, string language, int limit)
    {
        var bin = ObjectBinary.From<BioCodexMap>(map);
        var strings = TalkTables.Load(game, language).Male;
        string? Text(int strRef) => strings.GetValueOrDefault(strRef);

        var sections = bin.Sections.Where(s => id is null || s.ID == id)
            .Select(s => new CodexSectionInfo(s.ID, s.InstanceVersion, s.IsPrimary, s.Title, Text(s.Title), s.Description, Text(s.Description), s.TextureIndex, s.Priority, s.CodexSound))
            .ToList();
        var pages = bin.Pages.Where(p => id is null || p.ID == id)
            .Select(p => new CodexPageInfo(p.ID, p.InstanceVersion, p.Section, p.Title, Text(p.Title), p.Description, Text(p.Description), p.TextureIndex, p.Priority, p.CodexSound, p.CodexSoundString))
            .ToList();
        return new CodexMapInfo(ExportInspector.Row(map), bin.Sections.Count, bin.Pages.Count, sections.Take(limit).ToList(), pages.Take(limit).ToList());
    }

    // InstanceVersions of every entry in the base game's QuestMap. The engine reads each struct by its version, so a
    // wrong one misreads the map (as with plot map ints, see PlotMaps.IncrementVersion).
    private const int QuestVersion = 0;
    private const int GoalVersion = 1;
    private const int TaskVersion = 0;
    private const int PlotItemVersion = 2;
    private const int TaskListVersion = 1;
    private const int TaskEvalVersion = 2;

    /// <summary>
    /// Adds or replaces quest <paramref name="questId"/> and all its task evaluations (or removes both). A package without
    /// the map gets a new BioQuestMap export, added to its ObjectReferencer; the result then lists the AutoLoad.ini line.
    /// </summary>
    public static QuestEdit Set(MEGame game, string fileOrPath, string export, int questId, bool isMission,
                                IReadOnlyList<QuestGoalSpec> goals, IReadOnlyList<QuestTaskSpec> tasks, IReadOnlyList<QuestPlotItemSpec> plotItems,
                                IReadOnlyList<QuestTaskEvalSpec> evals, bool remove)
    {
        if (!game.IsGame1()) throw new ArgumentException("quest_set handles Game 1 (LE1).");
        if (!remove) Validate(tasks, plotItems, evals);

        var ((row, created, before, after, evalInfos, path, packageName), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var r = SetIn(pcc, export, questId, isMission, goals, tasks, plotItems, evals, remove);
            return (r.Map, r.CreatedMap, r.Before, r.After, r.TaskEvals, pcc.FilePath, Path.GetFileNameWithoutExtension(pcc.FilePath));
        });

        IReadOnlyList<string> lines = created ? [$"PlotManagerQuestMap1={packageName}.{row.InstancedFullPath}"] : [];
        return new QuestEdit(path, row, created, before, after, evalInfos, lines, backup);
    }

    internal sealed record QuestChange(ExportRow Map, bool CreatedMap, QuestInfo? Before, QuestInfo? After, List<TaskEvalInfo> BeforeEvals, List<TaskEvalInfo> TaskEvals);

    /// <summary>Set inside an open package (validated by the caller).</summary>
    internal static QuestChange SetIn(IMEPackage pcc, string export, int questId, bool isMission,
                                      IReadOnlyList<QuestGoalSpec> goals, IReadOnlyList<QuestTaskSpec> tasks, IReadOnlyList<QuestPlotItemSpec> plotItems,
                                      IReadOnlyList<QuestTaskEvalSpec> evals, bool remove)
    {
        var map = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioQuestMap"
            && (e.InstancedFullPath.Equals(export, StringComparison.OrdinalIgnoreCase) || e.ObjectName.Instanced.Equals(export, StringComparison.OrdinalIgnoreCase)));
        var created = map is null;
        if (map is null)
        {
            if (remove) throw new ArgumentException($"{pcc.FilePath} has no BioQuestMap '{export}'.");
            map = CreateMap(pcc, export);
        }

        var bin = ObjectBinary.From<BioQuestMap>(map);
        var old = bin.Quests.FirstOrDefault(q => q.ID == questId);
        if (remove && old is null) throw new ArgumentException($"No quest {questId} to remove.");
        var oldEvals = EvalsOf(bin, questId);
        bin.Quests.RemoveAll(q => q.ID == questId);
        foreach (var lists in new[] { bin.TaskEvals, bin.IntTaskEvals, bin.FloatTaskEvals })
        {
            foreach (var list in lists) list.TaskEvals.RemoveAll(e => e.Quest == questId);
            lists.RemoveAll(l => l.TaskEvals.Count == 0);
        }

        BioQuest? quest = null;
        if (!remove)
        {
            quest = new BioQuest
            {
                ID = questId,
                InstanceVersion = QuestVersion,
                IsMission = isMission,
                Goals = goals.Select(g => new BioQuestGoal
                    { InstanceVersion = GoalVersion, Name = g.Name, Description = g.Description, Conditional = g.Conditional, State = g.State }).ToList(),
                Tasks = tasks.Select(t => new BioQuestTask
                {
                    InstanceVersion = TaskVersion, QuestCompleteTask = t.QuestComplete, Name = t.Name, Description = t.Description,
                    PlotIndices = [.. t.PlotItems ?? []], PlanetName = new NameReference(t.PlanetName ?? "None"), WaypointRef = t.Waypoint ?? "",
                }).ToList(),
                PlotItems = plotItems.Select(p => new BioQuestPlotItem
                {
                    InstanceVersion = PlotItemVersion, Name = p.Name, IconIndex = p.IconIndex, Conditional = p.Conditional, State = p.State, TargetItems = p.TargetItems,
                }).ToList(),
            };
            bin.Quests.Add(quest);
            bin.Quests.Sort((a, b) => a.ID.CompareTo(b.ID));

            foreach (var e in evals)
            {
                var lists = ListsFor(bin, e.Variable);
                var list = lists.FirstOrDefault(l => l.ID == e.Id);
                if (list is null)
                {
                    list = new BioStateTaskList { ID = e.Id, InstanceVersion = TaskListVersion, TaskEvals = [] };
                    lists.Add(list);
                    lists.Sort((a, b) => a.ID.CompareTo(b.ID));
                }
                list.TaskEvals.Add(new BioTaskEval { InstanceVersion = TaskEvalVersion, Task = e.Task, Conditional = e.Conditional, State = e.State, Quest = questId });
            }
        }
        map.WriteBinary(bin);

        return new QuestChange(ExportInspector.Row(map), created, old is null ? null : Info(old, _ => null), quest is null ? null : Info(quest, _ => null),
            oldEvals, EvalsOf(bin, questId));
    }

    private static List<TaskEvalInfo> EvalsOf(BioQuestMap bin, int questId) =>
        new[] { ("bool", bin.TaskEvals), ("int", bin.IntTaskEvals), ("float", bin.FloatTaskEvals) }
            .SelectMany(t => t.Item2.SelectMany(l => l.TaskEvals.Where(e => e.Quest == questId).Select(e =>
                new TaskEvalInfo(t.Item1, l.ID, l.InstanceVersion, e.InstanceVersion, e.Quest, e.Task, e.Conditional, e.State))))
            .ToList();

    internal static void Validate(IReadOnlyList<QuestTaskSpec> tasks, IReadOnlyList<QuestPlotItemSpec> plotItems, IReadOnlyList<QuestTaskEvalSpec> evals)
    {
        if (tasks.Count == 0) throw new ArgumentException("A quest needs at least one task (the journal shows the task an evaluation picks).");
        for (var i = 0; i < tasks.Count; i++)
        {
            foreach (var item in tasks[i].PlotItems ?? [])
            {
                if (item < 0 || item >= plotItems.Count) throw new ArgumentException($"Task {i} lists plot item {item}, but the quest has {plotItems.Count} plot item(s).");
            }
        }
        foreach (var e in evals)
        {
            if (e.Task < 0 || e.Task >= tasks.Count) throw new ArgumentException($"Evaluation on {e.Variable} {e.Id} picks task {e.Task}, but the quest has {tasks.Count} task(s).");
            if (e.Variable.ToLowerInvariant() is not ("bool" or "int" or "float")) throw new ArgumentException($"Evaluation variable '{e.Variable}' must be bool, int or float.");
        }
    }

    private static List<BioStateTaskList> ListsFor(BioQuestMap bin, string variable) => variable.ToLowerInvariant() switch
    {
        "int" => bin.IntTaskEvals,
        "float" => bin.FloatTaskEvals,
        _ => bin.TaskEvals,
    };

    private static ExportEntry CreateMap(IMEPackage pcc, string name)
    {
        if (name.Contains('.')) throw new ArgumentException($"'{name}' is a path; a new map takes a plain name such as QuestMap.");
        var map = pcc.CreateExport(name, "BioQuestMap", indexed: false);
        map.ObjectFlags |= UnrealFlags.EObjectFlags.Public | UnrealFlags.EObjectFlags.Standalone;
        map.WriteBinary(BioQuestMap.Create());
        foreach (var referencer in pcc.Exports.Where(e => e.ClassName == "ObjectReferencer"))
        {
            var refs = referencer.GetProperty<ArrayProperty<ObjectProperty>>("ReferencedObjects") ?? new ArrayProperty<ObjectProperty>("ReferencedObjects");
            refs.Add(new ObjectProperty(map));
            referencer.WriteProperty(refs);
        }
        return map;
    }

    private static QuestInfo Info(BioQuest q, Func<int, string?> text) => new(q.ID, q.InstanceVersion, q.IsMission,
        q.Goals.Select((g, i) => new QuestGoalInfo(i, g.InstanceVersion, g.Name, text(g.Name), g.Description, text(g.Description), g.Conditional, g.State)).ToList(),
        q.Tasks.Select((t, i) => new QuestTaskInfo(i, t.InstanceVersion, t.QuestCompleteTask, t.Name, text(t.Name), t.Description, text(t.Description),
            t.PlotIndices, t.PlanetName.Instanced is "None" or "" ? null : t.PlanetName.Instanced, string.IsNullOrEmpty(t.WaypointRef) ? null : t.WaypointRef)).ToList(),
        q.PlotItems.Select((p, i) => new QuestPlotItemInfo(i, p.InstanceVersion, p.Name, text(p.Name), p.IconIndex, p.Conditional, p.State, p.TargetItems)).ToList());
}
