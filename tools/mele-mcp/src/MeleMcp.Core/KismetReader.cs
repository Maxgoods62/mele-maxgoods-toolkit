using System.Text;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core;

/// <summary>
/// A Kismet sequence as readable text: its ops in flow order (events first, then along their output links), each with its
/// settings, linked outputs and variables. With the Kismet Logger ASI's trace of the current or last game session, every op is
/// marked fired (✓, with the input pins that got an impulse) or not (✗), so a broken chain shows where it stops.
/// </summary>
public static class KismetReader
{
    /// <summary>Properties every sequence object has for the editor or the links, left out of the settings line.</summary>
    private static readonly HashSet<string> Plumbing = new(StringComparer.OrdinalIgnoreCase)
    {
        "InputLinks", "OutputLinks", "VariableLinks", "EventLinks", "ObjPosX", "ObjPosY", "DrawWidth", "DrawHeight", "MaxWidth",
        "ObjComment", "m_aObjComment", "ParentSequence", "SequenceObjects", "ObjInstanceVersion", "ObjName", "ObjColor",
        "bDrawFirst", "bDrawLast", "bOutputObjCommentToScreen", "bSuppressAutoComment", "DefaultViewX", "DefaultViewY", "DefaultViewZoom",
    };

    /// <param name="show">all, fired or unfired (the last two need the trace).</param>
    /// <param name="recurse">Also prints every nested sequence after this one.</param>
    public static string Read(MEGame game, string fileOrPath, string sequence, bool trace, string show, bool recurse)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        var root = ExportInspector.Resolve(pcc, sequence);
        if (!root.IsA("Sequence")) throw new ArgumentException($"#{root.UIndex} is a {root.ClassName}, not a Sequence.");
        var filter = show.Trim().ToLowerInvariant();
        if (filter is not ("all" or "fired" or "unfired")) throw new ArgumentException("show is all, fired or unfired.");

        var log = trace ? KismetLog.TryReadAll(game) : null;
        if (filter != "all" && log is null) throw new InvalidOperationException("show fired/unfired needs the Kismet Logger ASI's log (none found).");
        var fired = log is { } l
            ? l.Steps.GroupBy(s => $"{s.Level}.{s.Object}", StringComparer.OrdinalIgnoreCase)
                     .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase)
            : null;

        var sb = new StringBuilder();
        if (log is { } session)
        {
            var stem = Path.GetFileNameWithoutExtension(pcc.FilePath);
            var here = session.Steps.Count(s => s.Level.Equals(stem, StringComparison.OrdinalIgnoreCase));
            sb.AppendLine($"trace: {session.Path}, {session.Steps.Count} activations this session, {here} in {stem}"
                          + (here == 0 ? " (not played since the game started?)" : ""));
        }
        else if (trace)
        {
            sb.AppendLine("trace: none (install the Kismet Logger ASI to see what fired)");
        }

        var sequences = new List<ExportEntry> { root };
        for (var i = 0; i < sequences.Count; i++)
        {
            var nested = Write(sb, pcc, sequences[i], fired, filter);
            if (recurse) sequences.AddRange(nested);
        }
        return sb.ToString();
    }

    /// <summary>Writes one sequence; returns the sequences nested in it.</summary>
    private static List<ExportEntry> Write(StringBuilder sb, IMEPackage pcc, ExportEntry sequence, Dictionary<string, List<KismetStep>>? fired, string filter)
    {
        var objects = KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().ToList();
        var ops = objects.Where(o => o.IsA("SequenceOp")).ToList();
        var countFired = fired is null ? "" : $", {ops.Count(o => fired.ContainsKey(EnginePath(o)))} fired";
        sb.AppendLine();
        sb.AppendLine($"#{sequence.UIndex} {sequence.ClassName} {sequence.InstancedFullPath}  ({ops.Count} ops{countFired}, {objects.Count - ops.Count} other)");

        foreach (var op in FlowOrder(ops))
        {
            var steps = fired?.GetValueOrDefault(EnginePath(op));
            if (filter == "fired" && steps is null || filter == "unfired" && steps is not null) continue;

            var mark = fired is null ? "" : (steps is null ? " ✗" : $" ✓ {Pins(steps)}") + Inside(op, fired);
            var comment = KismetHelper.GetComment(op) is { Length: > 0 } c ? $"  // {c}" : "";
            sb.AppendLine($"{op.ObjectName.Instanced} (#{op.UIndex}){mark}{comment}");
            if (Settings(pcc, op) is { Length: > 0 } settings) sb.AppendLine($"   {settings}");

            var names = KismetHelper.GetOutputLinkNames(op);
            var links = KismetHelper.GetOutputLinksOfNode(op);
            for (var i = 0; i < links.Count; i++)
            {
                if (links[i].Count == 0) continue;
                var targets = links[i].Select(t => t.LinkedOp is ExportEntry e ? $"{e.ObjectName.Instanced}.{InputName(e, t.InputLinkIdx)}" : "<none>");
                sb.AppendLine($"   {names.ElementAtOrDefault(i) ?? $"out{i}"} → {string.Join(", ", targets)}");
            }
            foreach (var v in KismetHelper.GetVariableLinksOfNode(op).Where(v => v.LinkedNodes.Count > 0))
            {
                sb.AppendLine($"   [{v.LinkDesc}] {string.Join(", ", v.LinkedNodes.OfType<ExportEntry>().Select(n => Variable(pcc, n)))}");
            }
            foreach (var e in KismetHelper.GetEventLinksOfNode(op).Where(e => e.LinkedNodes.Count > 0))
            {
                sb.AppendLine($"   event [{e.LinkDesc}] {string.Join(", ", e.LinkedNodes.Select(n => n.ObjectName.Instanced))}");
            }
        }

        var unlinked = objects.Where(o => !o.IsA("SequenceOp") && o.IsA("SequenceVariable"))
                              .Where(v => !ops.Any(op => KismetHelper.GetVariableLinksOfNode(op).Any(l => l.LinkedNodes.Contains(v)))).ToList();
        if (unlinked.Count > 0) sb.AppendLine($"unlinked variables: {string.Join(", ", unlinked.Select(v => Variable(pcc, v)))}");

        return ops.Select(o => o.ClassName == "SequenceReference" ? o.GetProperty<ObjectProperty>("oSequenceReference")?.ResolveToEntry(pcc) as ExportEntry
                                : o.IsA("Sequence") ? o : null)
                  .OfType<ExportEntry>().ToList();
    }

    /// <summary>Events and other ops nothing links to first, then breadth-first along output links; the rest by UIndex.</summary>
    private static List<ExportEntry> FlowOrder(List<ExportEntry> ops)
    {
        var targets = ops.SelectMany(o => KismetHelper.GetOutputLinksOfNode(o).SelectMany(l => l)).Select(l => l.LinkedOp).OfType<ExportEntry>().ToHashSet();
        var queue = new Queue<ExportEntry>(ops.Where(o => !targets.Contains(o)).OrderBy(o => o.IsA("SequenceEvent") ? 0 : 1).ThenBy(o => o.UIndex));
        var order = new List<ExportEntry>();
        var seen = new HashSet<ExportEntry>();
        void Visit()
        {
            while (queue.Count > 0)
            {
                var op = queue.Dequeue();
                if (!seen.Add(op)) continue;
                order.Add(op);
                foreach (var next in KismetHelper.GetOutputLinksOfNode(op).SelectMany(l => l).Select(l => l.LinkedOp).OfType<ExportEntry>())
                {
                    if (ops.Contains(next) && !seen.Contains(next)) queue.Enqueue(next);
                }
            }
        }
        Visit();
        foreach (var op in ops.OrderBy(o => o.UIndex).Where(o => !seen.Contains(o)))
        {
            queue.Enqueue(op);
            Visit();
        }
        return order;
    }

    /// <summary>The path the engine (and so the Kismet Logger) gives the object: the file's name first, unless it sits in a package of its own (a conversation's _N).</summary>
    private static string EnginePath(ExportEntry exp)
    {
        IEntry top = exp;
        while (top.Parent is { } parent) top = parent;
        return top.ClassName == "Package" ? exp.InstancedFullPath : $"{Path.GetFileNameWithoutExtension(exp.FileRef.FilePath)}.{exp.InstancedFullPath}";
    }

    private static string Pins(List<KismetStep> steps) =>
        string.Join(", ", steps.GroupBy(s => s.Pin ?? "").Select(g => $"{g.Key}×{g.Count()}"));

    /// <summary>For a nested sequence: how many of its ops (at any depth) fired. A prefab started by its own events shows ✗ itself.</summary>
    private static string Inside(ExportEntry op, Dictionary<string, List<KismetStep>> fired)
    {
        if (!op.IsA("Sequence") && op.ClassName != "SequenceReference") return "";
        var prefix = EnginePath(op) + ".";
        var count = fired.Keys.Count(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return $" (inside: {count} ops fired)";
    }

    /// <summary>The input link's name; an op that keeps its class's links has none serialized, so they come from the class defaults.</summary>
    private static string InputName(ExportEntry op, int index)
    {
        var links = op.GetProperty<ArrayProperty<StructProperty>>("InputLinks")
                    ?? SafeDefaults(op)?.GetProp<ArrayProperty<StructProperty>>("InputLinks");
        return links?.ElementAtOrDefault(index)?.GetProp<StrProperty>("LinkDesc")?.Value ?? $"in{index}";
    }

    private static PropertyCollection? SafeDefaults(ExportEntry op)
    {
        try
        {
            return SequenceObjectCreator.GetSequenceObjectDefaults(op.FileRef, op.ClassName, op.Game);
        }
        catch (Exception e) when (e is NullReferenceException or KeyNotFoundException or ArgumentException)
        {
            return null; // a class LEC has no info for
        }
    }

    /// <summary>A variable as name (#UIndex) 'VarName' = its value(s).</summary>
    private static string Variable(IMEPackage pcc, ExportEntry v)
    {
        var name = v.GetProperty<NameProperty>("VarName")?.Value.Instanced is { Length: > 0 } n && n != "None" ? $" '{n}'" : "";
        var value = Settings(pcc, v, "VarName");
        return $"{v.ObjectName.Instanced} (#{v.UIndex}){name}" + (value.Length > 0 ? $" = {value}" : "");
    }

    /// <summary>The object's own scalar properties (no layout or link plumbing), e.g. EventName=Hench_SetupSquad.</summary>
    private static string Settings(IMEPackage pcc, ExportEntry obj, params string[] skip) =>
        string.Join(", ", obj.GetProperties()
            .Where(p => p is not (NoneProperty or StructProperty or ArrayPropertyBase) && !Plumbing.Contains(p.Name.Instanced) && !skip.Contains(p.Name.Instanced))
            .Select(p => $"{ExportInspector.Label(p)}={ExportInspector.FormatScalar(pcc, p)}"));
}
