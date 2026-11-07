using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using YamlDotNet.RepresentationModel;
using static LegendaryExplorerCore.Unreal.BinaryConverters.BioStateEventMap;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <param name="Action">added, replaced, unchanged, removed or absent (an empty list for an event the map does not have).</param>
public sealed record BuiltPlotEvent(string Map, int Id, string Action, IReadOnlyList<string>? Elements);

/// <param name="Action">added, replaced or unchanged.</param>
public sealed record BuiltQuest(int Id, string Action, int Goals, int Tasks, int PlotItems, int TaskEvals);

public sealed record PlotBuildResult(string Path, bool CreatedPackage, IReadOnlyList<BuiltPlotEvent> Events, IReadOnlyList<BuiltQuest> Quests,
    int StringCount, IReadOnlyList<string> TlkFiles, string? AutoLoad, IReadOnlyList<string> AutoLoadAdded, IReadOnlyList<string> Notes,
    IReadOnlyList<string> BackupPaths);

/// <summary>
/// Builds a Game 1 DLC plot package from a YAML file: state transitions and consequences, journal quests (goals, tasks,
/// plot items, task evaluations) with their text in the mod's GlobalTlk files, and the AutoLoad.ini lines that load the
/// maps. Every event and quest in the file replaces the package's; the ones not in the file are kept.
/// </summary>
public static partial class PlotBuilder
{
    [GeneratedRegex(@"^(?<type>bool|int|float)\s+(?<id>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlotVariable();

    [GeneratedRegex(@"^(?<key>\D+?)(?<n>\d*)$")]
    private static partial Regex AutoLoadKey();

    // The exports plot_map_new writes, and the AutoLoad.ini [Packages] key that makes the game load each.
    private static readonly (string Export, string Key)[] Maps =
    [
        ("StateTransitionMap", "PlotManagerStateTransitionMap"),
        ("ConsequenceMap", "PlotManagerConsequenceMap"),
        ("QuestMap", "PlotManagerQuestMap"),
    ];

    private sealed record EventSpec(int Id, List<BioStateEventElement> Elements);

    private sealed record QuestSpec(int Id, bool IsMission, List<QuestGoalSpec> Goals, List<QuestTaskSpec> Tasks, List<QuestPlotItemSpec> PlotItems,
                                    List<QuestTaskEvalSpec> Evals);

    private sealed class Spec
    {
        public string? Package;
        public string? AutoLoad;
        public string DefaultLanguage = "INT";
        public List<string> GlobalTlk = [];
        public List<EventSpec>? Transitions;
        public List<EventSpec>? Consequences;
        public List<QuestSpec>? Quests;
        public readonly Dictionary<int, Dictionary<string, string>> Strings = [];
    }

    /// <param name="outRoot">mod_build out=: the mod root to build into instead (see <see cref="YamlFiles.Target"/>).</param>
    /// <param name="writes">
    /// The files written so far, each with its backup: the package, each GlobalTlk, the AutoLoad.ini are saved one after the
    /// other with no rollback, so a failure after the first becomes a <see cref="PartialBuildException"/> naming them. mod_build
    /// passes one for all its files; null: this build's own.
    /// </param>
    public static PlotBuildResult Build(MEGame game, string yamlPath, string? packageOverride, string? outRoot = null, BuildWrites? writes = null)
    {
        if (!game.IsGame1()) throw new ArgumentException("plot_build handles Game 1 (LE1) plots.");
        LecHost.EnsureInitialized();
        var file = Path.GetFullPath(GamePaths.ToWindowsPath(yamlPath));
        if (!File.Exists(file)) throw new FileNotFoundException($"No file {file}.");
        var spec = Parse(File.ReadAllText(file));
        var dir = Path.GetDirectoryName(file)!;

        // A missing package is created from a full path or one under the mod root, never from a bare name: it could resolve into the game install.
        var notes = new List<string>();
        var (package, create) = Target(game, packageOverride ?? spec.Package ?? throw new ArgumentException("The file has no 'package'."), dir, outRoot, notes);
        var globals = spec.GlobalTlk.Select(g => InOut(Locate(game, g, Path.GetDirectoryName(package)!, dir), outRoot, "globalTlk")).ToList();
        if (spec.Strings.Count > 0 && globals.Count == 0) throw new ArgumentException("The file gives journal text but no 'globalTlk' to write it to (the journal reads only the GlobalTlk).");
        var autoload = FindAutoLoad(spec.AutoLoad, package, dir, notes) is { } ini ? InOut(ini, outRoot, "autoload") : null;
        foreach (var target in globals.Prepend(package).Concat(autoload is null ? [] : [autoload])) PackageWriter.EnsureWritable(game, target);

        var written = writes ?? new BuildWrites();
        return written.Guard(() =>
        {
            // This build's backups for its result; written may hold earlier builds' files too (mod_build).
            var backups = new List<string>();
            void Wrote(string path, string? backup)
            {
                if (backup is not null) backups.Add(backup);
                written.Wrote(path, backup);
            }

            if (create)
            {
                PlotMaps.NewPackage(game, package);
                Wrote(package, null);
                notes.Add($"Created {Path.GetFileName(package)}.");
            }

            var ((events, quests, loaded), backup) = PackageWriter.Edit(game, package, pcc =>
            {
                var events = new List<BuiltPlotEvent>();
                var loaded = new List<(string Key, string Export)>();
                foreach (var (list, (export, key)) in new[] { spec.Transitions, spec.Consequences }.Zip(Maps))
                {
                    if (list is null) continue;
                    var map = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioStateEventMap" && e.InstancedFullPath.Equals(export, StringComparison.OrdinalIgnoreCase))
                              ?? throw new ArgumentException($"{Path.GetFileName(pcc.FilePath)} has no BioStateEventMap '{export}' (plot_map_new writes one).");
                    var bin = ObjectBinary.From<BioStateEventMap>(map);
                    foreach (var e in list)
                    {
                        var (before, after) = PlotMaps.SetEventIn(bin, e.Id, e.Elements);
                        var action = (before, after) switch
                        {
                            (null, null) => "absent",
                            (null, _) => "added",
                            (_, null) => "removed",
                            ({ } b, { } a) => b.SequenceEqual(a) ? "unchanged" : "replaced",
                        };
                        events.Add(new BuiltPlotEvent(export, e.Id, action, after));
                    }
                    map.WriteBinary(bin);
                    var kept = bin.StateEvents.Select(e => e.ID).Except(list.Select(e => e.Id)).ToList();
                    if (kept.Count > 0) notes.Add($"{export}: kept events not in the file: {string.Join(", ", kept)}.");
                    if (bin.StateEvents.Count > 0) loaded.Add((key, map.InstancedFullPath));
                }

                var quests = new List<BuiltQuest>();
                if (spec.Quests is { } questSpecs)
                {
                    var (export, key) = Maps[2];
                    ExportRow? row = null;
                    foreach (var q in questSpecs)
                    {
                        var r = QuestMaps.SetIn(pcc, export, q.Id, q.IsMission, q.Goals, q.Tasks, q.PlotItems, q.Evals, remove: false);
                        row = r.Map;
                        var action = r.Before is null ? "added"
                                     : JsonSerializer.Serialize(r.Before) + JsonSerializer.Serialize(r.BeforeEvals) == JsonSerializer.Serialize(r.After) + JsonSerializer.Serialize(r.TaskEvals)
                                         ? "unchanged" : "replaced";
                        if (r.CreatedMap) notes.Add($"Created the BioQuestMap '{export}'.");
                        quests.Add(new BuiltQuest(q.Id, action, q.Goals.Count, q.Tasks.Count, q.PlotItems.Count, r.TaskEvals.Count));
                    }
                    if (row is not null)
                    {
                        var kept = ObjectBinary.From<BioQuestMap>(pcc.GetUExport(row.UIndex)).Quests.Select(q => q.ID).Except(questSpecs.Select(q => q.Id)).ToList();
                        if (kept.Count > 0) notes.Add($"{export}: kept quests not in the file: {string.Join(", ", kept)}.");
                        loaded.Add((key, row.InstancedFullPath));
                    }
                }
                return (events, quests, loaded);
            });
            Wrote(package, backup);

            var tlkFiles = new List<string>();
            if (spec.Strings.Count > 0)
            {
                foreach (var globalPath in globals)
                {
                    var language = LanguageOfFile(Path.GetFileNameWithoutExtension(globalPath));
                    var edit = TalkFiles.Set(game, globalPath, StringsFor(spec.Strings, language, spec.DefaultLanguage), null);
                    Wrote(edit.Path, edit.BackupPath);
                    tlkFiles.Add($"{Path.GetFileName(globalPath)} ({language})");
                }
            }

            var packageName = Path.GetFileNameWithoutExtension(package);
            var wanted = loaded.Select(l => (l.Key, $"{packageName}.{l.Export}")).ToList();
            List<string> added = [];
            if (autoload is not null) added = EnsureAutoLoad(autoload, wanted, Wrote);
            else if (wanted.Count > 0) notes.Add($"Add to the DLC's AutoLoad.ini [Packages]: {string.Join("; ", wanted.Select(w => $"{w.Key}1={w.Item2}"))}.");

            return new PlotBuildResult(package, create, events, quests, spec.Strings.Count, tlkFiles, autoload, added, notes, backups);
        });
    }

    /// <summary>The file's 'autoload' (relative to the YAML's folder or the package's; none: leave it alone), else the DLC folder's AutoLoad.ini.</summary>
    private static string? FindAutoLoad(string? given, string package, string dir, List<string> notes)
    {
        var packageDir = Path.GetDirectoryName(package)!;
        if (given is not null)
        {
            if (given.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            var candidate = GamePaths.ToWindowsPath(given);
            return new[] { dir, packageDir }.Select(d => Path.GetFullPath(Path.Combine(d, candidate))).FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException($"No AutoLoad.ini at '{given}' (looked in {dir} and {packageDir}).");
        }
        // DLC_MOD_<name>\AutoLoad.ini, with the package in DLC_MOD_<name>\CookedPCConsole.
        var beside = Path.GetFullPath(Path.Combine(packageDir, "..", "AutoLoad.ini"));
        if (File.Exists(beside)) return beside;
        notes.Add($"No AutoLoad.ini in {Path.GetDirectoryName(packageDir)}.");
        return null;
    }

    /// <summary>
    /// Adds the [Packages] lines missing for <paramref name="wanted"/> (key without its number, package.export), numbered
    /// after the file's own. A line edit that keeps everything else (LEC's AutoloadIni drops ConsequenceMap entries).
    /// <paramref name="wrote"/> gets the file and its backup once it is saved.
    /// </summary>
    private static List<string> EnsureAutoLoad(string ini, List<(string Key, string Value)> wanted, Action<string, string?> wrote)
    {
        var bytes = File.ReadAllBytes(ini);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(newline).ToList();

        var header = lines.FindIndex(l => l.Trim().Equals("[Packages]", StringComparison.OrdinalIgnoreCase));
        if (header < 0)
        {
            lines.InsertRange(0, ["[Packages]", ""]);
            header = 0;
        }
        var end = lines.FindIndex(header + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        while (end > header + 1 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;

        var added = new List<string>();
        foreach (var (key, value) in wanted)
        {
            var same = lines.Skip(header + 1).Take(end - header - 1)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .Select(p => (Key: AutoLoadKey().Match(p[0].Trim()), Value: p[1].Trim()))
                .Where(p => p.Key.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (same.Any(p => p.Value.Equals(value, StringComparison.OrdinalIgnoreCase))) continue;
            var n = same.Select(p => int.TryParse(p.Key.Groups["n"].Value, CultureInfo.InvariantCulture, out var i) ? i : 0).DefaultIfEmpty(0).Max() + 1;
            var line = $"{key}{n}={value}";
            lines.Insert(end++, line);
            added.Add(line);
        }
        if (added.Count == 0) return added;

        var backup = PackageWriter.Backup(ini);
        File.WriteAllText(ini, string.Join(newline, lines), new UTF8Encoding(bom));
        wrote(ini, backup);
        return added;
    }

    // ---- YAML ----

    private static Spec Parse(string yaml)
    {
        var spec = new Spec();
        foreach (var (key, value) in TopLevel(Root(yaml), "package", "autoload", "defaultLanguage", "globalTlk", "transitions", "consequences", "quests"))
        {
            switch (key)
            {
                case "package": spec.Package = Scalar(value); break;
                case "autoload": spec.AutoLoad = Scalar(value); break;
                case "defaultLanguage": spec.DefaultLanguage = Language(Scalar(value)); break;
                case "globalTlk": spec.GlobalTlk = Sequence(value).Select(Scalar).ToList(); break;
                case "transitions": spec.Transitions = Events(value); break;
                case "consequences": spec.Consequences = Events(value); break;
                case "quests":
                    if (value is not YamlMappingNode quests) throw Fail(value, "'quests' maps quest ids to quests.");
                    spec.Quests = quests.Children.Select(c => QuestOf(c.Key, c.Value, spec.Strings)).ToList();
                    break;
            }
        }
        foreach (var ids in new[] { spec.Transitions?.Select(e => e.Id), spec.Consequences?.Select(e => e.Id), spec.Quests?.Select(q => q.Id) })
        {
            var twice = ids?.GroupBy(id => id).FirstOrDefault(g => g.Count() > 1);
            if (twice is not null) throw new ArgumentException($"Id {twice.Key} is defined twice.");
        }
        return spec;
    }

    /// <summary>{8421: [bool 8421 = true, int 47 += 2]}; an empty list removes the event.</summary>
    private static List<EventSpec> Events(YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "plot events map ids to lists of elements: 8421: [bool 8421 = true].");
        return m.Children.Select(c => new EventSpec(Int(c.Key), Sequence(c.Value).Select(Element).ToList())).ToList();
    }

    private static BioStateEventElement Element(YamlNode value)
    {
        var text = Scalar(value);
        try
        {
            return PlotMaps.Parse(text);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            throw Fail(value, e.Message);
        }
    }

    private static QuestSpec QuestOf(YamlNode idNode, YamlNode body, Dictionary<int, Dictionary<string, string>> strings)
    {
        var id = Int(idNode);
        if (body is not YamlMappingNode m) throw Fail(body, $"quest {id} must be a mapping.");
        var entries = Entries(m, "mission", "title", "goals", "tasks", "plotItems", "evals").ToDictionary(e => e.Key, e => e.Value);
        var title = entries.TryGetValue("title", out var t) ? StrOf(t, strings) : (int?)null;

        var goals = entries.TryGetValue("goals", out var g) ? Sequence(g).Select(n => GoalOf(n, title, strings)).ToList() : [];
        var tasks = entries.TryGetValue("tasks", out var k) ? Sequence(k).Select(n => TaskOf(n, strings)).ToList() : [];
        var items = entries.TryGetValue("plotItems", out var p) ? Sequence(p).Select(n => PlotItemOf(n, strings)).ToList() : [];
        var evals = entries.TryGetValue("evals", out var e) ? Sequence(e).Select(EvalOf).ToList() : [];
        try
        {
            QuestMaps.Validate(tasks, items, evals);
        }
        catch (ArgumentException x)
        {
            throw Fail(idNode, $"quest {id}: {x.Message}");
        }
        return new QuestSpec(id, entries.TryGetValue("mission", out var mission) && Bool(mission), goals, tasks, items, evals);
    }

    private static QuestGoalSpec GoalOf(YamlNode value, int? questTitle, Dictionary<int, Dictionary<string, string>> strings)
    {
        int? title = questTitle;
        int description = -1, state = -1, conditional = -1;
        foreach (var (key, v) in Entries(Mapping(value, "a goal"), "title", "description", "state", "conditional"))
        {
            switch (key)
            {
                case "title": title = StrOf(v, strings); break;
                case "description": description = StrOf(v, strings); break;
                case "state": state = Id(v); break;
                case "conditional": conditional = Id(v); break;
            }
        }
        return new QuestGoalSpec(title ?? throw Fail(value, "the goal needs a 'title' (or give the quest one)."), description, conditional, state);
    }

    private static QuestTaskSpec TaskOf(YamlNode value, Dictionary<int, Dictionary<string, string>> strings)
    {
        int? name = null;
        var description = -1;
        var complete = false;
        int[]? items = null;
        string? planet = null, waypoint = null;
        foreach (var (key, v) in Entries(Mapping(value, "a task"), "name", "description", "complete", "plotItems", "planet", "waypoint"))
        {
            switch (key)
            {
                case "name": name = StrOf(v, strings); break;
                case "description": description = StrOf(v, strings); break;
                case "complete": complete = Bool(v); break;
                case "plotItems": items = Sequence(v).Select(Int).ToArray(); break;
                case "planet": planet = Scalar(v); break;
                case "waypoint": waypoint = Scalar(v); break;
            }
        }
        return new QuestTaskSpec(name ?? throw Fail(value, "the task needs a 'name'."), description, complete, items, planet, waypoint);
    }

    private static QuestPlotItemSpec PlotItemOf(YamlNode value, Dictionary<int, Dictionary<string, string>> strings)
    {
        int? name = null;
        int icon = 0, state = -1, conditional = -1, target = -1;
        foreach (var (key, v) in Entries(Mapping(value, "a plot item"), "name", "icon", "state", "conditional", "targetItems"))
        {
            switch (key)
            {
                case "name": name = StrOf(v, strings); break;
                case "icon": icon = Int(v); break;
                case "state": state = Id(v); break;
                case "conditional": conditional = Id(v); break;
                case "targetItems": target = Int(v); break;
            }
        }
        return new QuestPlotItemSpec(name ?? throw Fail(value, "the plot item needs a 'name'."), icon, conditional, state, target);
    }

    /// <summary>{when: bool 8421, task: 0, state: 8430}: when bool 8421 changes and bool 8430 is true, the journal shows task 0.</summary>
    private static QuestTaskEvalSpec EvalOf(YamlNode value)
    {
        (string Variable, int Id)? when = null;
        int? task = null;
        int state = -1, conditional = -1;
        foreach (var (key, v) in Entries(Mapping(value, "an evaluation"), "when", "task", "state", "conditional"))
        {
            switch (key)
            {
                case "when":
                    var match = PlotVariable().Match(Scalar(v).Trim());
                    if (!match.Success) throw Fail(v, "write 'when: bool <id>' (or int/float <id>): the plot variable whose change runs the evaluation.");
                    when = (match.Groups["type"].Value.ToLowerInvariant(), int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture));
                    break;
                case "task": task = Int(v); break;
                case "state": state = Id(v); break;
                case "conditional": conditional = Id(v); break;
            }
        }
        if (when is null) throw Fail(value, "the evaluation needs 'when: bool <id>'.");
        return new QuestTaskEvalSpec(when.Value.Id, task ?? throw Fail(value, "the evaluation needs 'task: <index>'."), when.Value.Variable, conditional, state);
    }

    /// <summary>A strref whose text is already in a TLK, or {strref: id, fr: "...", en: "..."} to write it.</summary>
    private static int StrOf(YamlNode value, Dictionary<int, Dictionary<string, string>> strings)
    {
        if (value is YamlScalarNode) return Id(value);
        int? strRef = null;
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Mapping(value, "a text").Children)
        {
            var key = Scalar(k);
            if (key == "strref") strRef = Int(v);
            else text[Language(key)] = Scalar(v);
        }
        if (strRef is null) throw Fail(value, "text needs 'strref' (the GlobalTlk id to write it to).");
        if (text.Count > 0) Collect(strings, strRef.Value, text, (int)value.Start.Line);
        return strRef.Value;
    }

    /// <summary>A plot id, conditional or strref; none is -1.</summary>
    private static int Id(YamlNode value) => Scalar(value).Equals("none", StringComparison.OrdinalIgnoreCase) ? -1 : Int(value);

    private static YamlMappingNode Mapping(YamlNode value, string what) => value as YamlMappingNode ?? throw Fail(value, $"{what} is a mapping of keys.");
}
