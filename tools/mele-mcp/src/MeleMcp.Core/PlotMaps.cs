using System.Globalization;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using static LegendaryExplorerCore.Unreal.BinaryConverters.BioStateEventMap;

namespace MeleMcp.Core;

/// <param name="Version">The event's InstanceVersion; each element prints its own as [vN].</param>
public sealed record PlotEvent(int Id, int Version, IReadOnlyList<string> Elements);

public sealed record PlotMapEvents(ExportRow Map, int EventCount, int MinId, int MaxId, int Matches, IReadOnlyList<PlotEvent> Events);

public sealed record NewPlotMapsResult(string Path, string PackageName, IReadOnlyList<ExportRow> Exports, IReadOnlyList<string> AutoLoadLines);

public sealed record PlotEventEdit(string Path, ExportRow Map, int Id, IReadOnlyList<string>? Before, IReadOnlyList<string>? After, string BackupPath);

/// <summary>
/// Plot maps (BioStateEventMap: StateTransitionMap, ConsequenceMap; in LE2/LE3 ConsequenceMap's class is BioConsequenceMap, a subclass
/// with the same binary, and OutcomeMap a BioOutcomeMap). A conversation node's nStateTransition and Kismet's
/// BioSeqAct_PMExecuteTransition run a state event of the StateTransitionMap by id. Writing is Game 1 only.
/// </summary>
public static partial class PlotMaps
{
    [GeneratedRegex(@"^(?<type>bool|int|float)\s+(?<id>\d+)\s*(?<op>\+?=)\s*(?<value>\S+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VariableElement();

    [GeneratedRegex(@"^consequence\s+(?<id>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConsequenceElement();

    [GeneratedRegex(@"\s*\[v\d+\]$")]
    private static partial Regex VersionSuffix();

    /// <summary>
    /// InstanceVersion of int and float elements. The game's maps store int elements as version 1 (the version that
    /// serializes Increment); LEC always writes Increment, so a version 0 int makes the game read 4 bytes short and
    /// desync the rest of the map (boot crash "QueueIORequest: Invalid filename or read offset"). The game ships no
    /// float element; LEC gives it the same layout as int.
    /// </summary>
    private const int IncrementVersion = 1;

    /// <summary>
    /// Writes a DLC plot package: empty BioStateEventMap exports (StateTransitionMap, ConsequenceMap) and an ObjectReferencer,
    /// as LE1CP's PlotManagerAutoDLC_MOD_Framework. The game loads the maps named in the DLC's AutoLoad.ini [Packages].
    /// </summary>
    public static NewPlotMapsResult NewPackage(MEGame game, string path)
    {
        if (!game.IsGame1()) throw new ArgumentException("plot_map_new handles Game 1 (LE1).");
        LecHost.EnsureInitialized();
        var full = Path.GetFullPath(GamePaths.ToWindowsPath(path));
        if (!full.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Package files end in .pcc.");
        if (File.Exists(full)) throw new ArgumentException($"{full} already exists.");
        PackageWriter.EnsureWritable(game, full);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            PackageWriter.Create(full, game);
            using var pcc = MEPackageHandler.OpenMEPackage(full, forceLoadFromDisk: true);
            var maps = new[] { "ConsequenceMap", "StateTransitionMap" }.Select(name =>
            {
                var map = pcc.CreateExport(name, "BioStateEventMap", indexed: false);
                map.ObjectFlags |= UnrealFlags.EObjectFlags.Public | UnrealFlags.EObjectFlags.Standalone;
                map.WriteBinary(BioStateEventMap.Create());
                return map;
            }).ToList();
            var referencer = pcc.CreateExport("CombinedStartupReferencer", "ObjectReferencer", indexed: false);
            referencer.WriteProperty(new ArrayProperty<ObjectProperty>(maps.Select(m => new ObjectProperty(m)), "ReferencedObjects"));
            PackageWriter.Save(pcc, backup: false);

            var name = Path.GetFileNameWithoutExtension(full);
            return new NewPlotMapsResult(full, name, pcc.Exports.Select(ExportInspector.Row).ToList(),
                [$"PlotManagerStateTransitionMap1={name}.StateTransitionMap", $"PlotManagerConsequenceMap1={name}.ConsequenceMap"]);
        }
        catch (Exception)
        {
            // The file did not exist (refused above), so whatever is there now is this call's: an empty package without its
            // maps would block every rerun (this refuses an existing file, and plot_build finds no map in it).
            try
            {
                File.Delete(full);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    /// <summary>
    /// Adds or replaces state event <paramref name="id"/> with elements written as plot_map_get prints them:
    /// "bool 8421 = true", "int 47 += 2", "float 3 = 0.5", "consequence 927" (a trailing [vN] is ignored: the
    /// version follows the element type). No elements removes the event.
    /// </summary>
    public static PlotEventEdit SetEvent(MEGame game, string fileOrPath, string export, int id, IReadOnlyList<string> elements)
    {
        if (!game.IsGame1()) throw new ArgumentException("plot_event_set handles Game 1 (LE1).");
        var parsed = elements.Select(Parse).ToList();

        var ((row, before, after, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var map = ExportInspector.Resolve(pcc, export);
            if (map.ClassName != "BioStateEventMap") throw new ArgumentException($"#{map.UIndex} is a {map.ClassName}, not a BioStateEventMap.");

            var bin = ObjectBinary.From<BioStateEventMap>(map);
            var (before, after) = SetEventIn(bin, id, parsed);
            if (before is null && after is null) throw new ArgumentException($"No event {id} to remove.");
            map.WriteBinary(bin);
            return (ExportInspector.Row(map), before, after, pcc.FilePath);
        });

        return new PlotEventEdit(path, row, id, before, after, backup);
    }

    /// <summary>Replaces event <paramref name="id"/> in a loaded map (no elements removes it); the caller writes the binary.</summary>
    internal static (List<string>? Before, List<string>? After) SetEventIn(BioStateEventMap bin, int id, List<BioStateEventElement> parsed)
    {
        var before = bin.StateEvents.FirstOrDefault(e => e.ID == id)?.Elements.Select(Describe).ToList();
        bin.StateEvents.RemoveAll(e => e.ID == id);
        if (parsed.Count == 0) return (before, null);
        bin.StateEvents.Add(new BioStateEvent { ID = id, InstanceVersion = 0, Elements = parsed });
        bin.StateEvents.Sort((a, b) => a.ID.CompareTo(b.ID));
        return (before, parsed.Select(Describe).ToList());
    }

    internal static BioStateEventElement Parse(string text)
    {
        var inv = CultureInfo.InvariantCulture;
        var t = VersionSuffix().Replace(text.Trim(), "");
        if (ConsequenceElement().Match(t) is { Success: true } c)
        {
            return new BioStateEventElementConsequence { Type = BioStateEventElementType.Consequence, Consequence = int.Parse(c.Groups["id"].Value, inv) };
        }
        var m = VariableElement().Match(t);
        if (!m.Success) throw new ArgumentException($"Cannot read element '{text}'. Use: bool <id> = true|false, int <id> = <n>, int <id> += <n>, float <id> = <x>, consequence <id>.");

        var target = int.Parse(m.Groups["id"].Value, inv);
        var increment = m.Groups["op"].Value == "+=";
        var value = m.Groups["value"].Value;
        switch (m.Groups["type"].Value.ToLowerInvariant())
        {
            case "bool":
                if (increment) throw new ArgumentException($"'{text}': a bool is set with =, not +=.");
                return new BioStateEventElementBool { Type = BioStateEventElementType.Bool, GlobalBool = target, NewState = bool.Parse(value) };
            case "int":
                return new BioStateEventElementInt { Type = BioStateEventElementType.Int, InstanceVersion = IncrementVersion, GlobalInt = target, NewValue = int.Parse(value, inv), Increment = increment };
            default:
                return new BioStateEventElementFloat { Type = BioStateEventElementType.Float, InstanceVersion = IncrementVersion, GlobalFloat = target, NewValue = NumberText.Float(value), Increment = increment };
        }
    }

    public static PlotMapEvents Get(MEGame game, string fileOrPath, string export, int? id, int? fromId, int? toId, int? touches, int limit)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        var map = ExportInspector.Resolve(pcc, export);
        if (map.ClassName == "BioOutcomeMap" && !game.IsGame1()) return Outcomes(map, id, fromId, toId, touches, limit);
        if (map.ClassName is not ("BioStateEventMap" or "BioConsequenceMap")) throw new ArgumentException($"#{map.UIndex} is a {map.ClassName}, not a BioStateEventMap.");

        var events = ObjectBinary.From<BioStateEventMap>(map).StateEvents;
        var matches = events
            .Where(e => (id is null || e.ID == id) && (fromId is null || e.ID >= fromId) && (toId is null || e.ID <= toId)
                        && (touches is null || e.Elements.Any(x => Target(x) == touches)))
            .ToList();
        return new PlotMapEvents(ExportInspector.Row(map), events.Count,
            events.Count == 0 ? 0 : events.Min(e => e.ID), events.Count == 0 ? 0 : events.Max(e => e.ID), matches.Count,
            matches.Take(limit).Select(e => new PlotEvent(e.ID, e.InstanceVersion, e.Elements.Select(Describe).ToList())).ToList());
    }

    /// <summary>
    /// BioOutcomeMap is no state event map: its SDK class is a native int-to-int map (OutcomeMapping), stored as a count and key, value
    /// pairs. Every LE3 file has it empty, LE2's only full one is the base game's (57 pairs). What the game does with it is unverified.
    /// </summary>
    private static PlotMapEvents Outcomes(ExportEntry map, int? id, int? fromId, int? toId, int? touches, int limit)
    {
        var data = map.GetBinaryData();
        var count = data.Length >= 4 ? BitConverter.ToInt32(data, 0) : -1;
        if (count < 0 || data.Length != 4 + 8L * count) throw new InvalidDataException($"#{map.UIndex}: {data.Length} bytes do not fit a count and {count} int pairs.");

        var pairs = Enumerable.Range(0, count).Select(i => (Key: BitConverter.ToInt32(data, 4 + 8 * i), Value: BitConverter.ToInt32(data, 8 + 8 * i))).ToList();
        var matches = pairs
            .Where(p => (id is null || p.Key == id) && (fromId is null || p.Key >= fromId) && (toId is null || p.Key <= toId)
                        && (touches is null || p.Value == touches))
            .ToList();
        return new PlotMapEvents(ExportInspector.Row(map), pairs.Count,
            pairs.Count == 0 ? 0 : pairs.Min(p => p.Key), pairs.Count == 0 ? 0 : pairs.Max(p => p.Key), matches.Count,
            matches.Take(limit).Select(p => new PlotEvent(p.Key, 0, [$"outcome {p.Value}"])).ToList());
    }

    /// <summary>The plot variable or consequence an element writes, for the touches filter.</summary>
    private static int? Target(BioStateEventElement e) => e switch
    {
        BioStateEventElementBool b => b.GlobalBool,
        BioStateEventElementSubstate s => s.GlobalBool,
        BioStateEventElementInt i => i.GlobalInt,
        BioStateEventElementFloat f => f.GlobalFloat,
        BioStateEventElementConsequence c => c.Consequence,
        _ => null,
    };

    private static string Describe(BioStateEventElement e) => $"{DescribeValue(e)} [v{e.InstanceVersion}]";

    private static string DescribeValue(BioStateEventElement e)
    {
        var inv = CultureInfo.InvariantCulture;
        string Param(bool useParam) => useParam ? " (from param)" : "";
        return e switch
        {
            BioStateEventElementBool b => $"bool {b.GlobalBool} = {b.NewState}{Param(b.UseParam)}",
            BioStateEventElementInt i => $"int {i.GlobalInt} {(i.Increment ? "+=" : "=")} {i.NewValue}{Param(i.UseParam)}",
            BioStateEventElementFloat f => $"float {f.GlobalFloat} {(f.Increment ? "+=" : "=")} {f.NewValue.ToString(inv)}{Param(f.UseParam)}",
            BioStateEventElementConsequence c => $"consequence {c.Consequence}",
            BioStateEventElementFunction fn => $"function {fn.PackageName.Instanced}.{fn.ClassName.Instanced}.{fn.FunctionName.Instanced}({fn.Parameter})",
            BioStateEventElementLocalBool lb => $"local bool {lb.ObjectTag.Instanced}.{lb.FunctionName.Instanced} = {lb.NewValue}{Param(lb.UseParam)}",
            BioStateEventElementLocalInt li => $"local int {li.ObjectTag.Instanced}.{li.FunctionName.Instanced} = {li.NewValue}{Param(li.UseParam)}",
            BioStateEventElementLocalFloat lf => $"local float {lf.ObjectTag.Instanced}.{lf.FunctionName.Instanced} = {lf.NewValue.ToString(inv)}{Param(lf.UseParam)}",
            BioStateEventElementSubstate s => $"substate bool {s.GlobalBool} = {s.NewState}{Param(s.UseParam)} parent {s.ParentIndex} ({(s.ParentTypeOr ? "or" : "and")}) siblings [{string.Join(", ", s.SiblingIndices)}]",
            _ => e.Type.ToString(),
        };
    }
}
