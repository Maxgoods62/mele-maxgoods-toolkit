using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

/// <summary>
/// An NPC's ambient pose in Kismet, as vanilla NPCs have it: a copy of an action-station prefab (a SequenceReference to
/// BioWare's BIOG_ActionStations_Q sequence: enter the pose, then random fidgets with random delays between them), run on
/// her pawn. It starts when the level is loaded and visible; when Shepard uses her she leaves it (BioSeqAct_LeaveActionStation,
/// as Chakwas does), and the end of her conversation starts it again.
/// </summary>
internal static class IdleStations
{
    /// <summary>The prefab's switches left off: no teleport to a path node, no pose snap, collision and floor snap kept.</summary>
    private static readonly string[] Off = ["Teleport", "Snap To Pose", "Coll/PhysOFF", "SnapToFloor"];

    /// <param name="Pose">Animation set of the pose, as the station's m_EntranceTransition names it, e.g. HMM_DP_ArmsCrossed.</param>
    /// <param name="Weights">Fidget weights by name without "Wt_" (Talk, Shrug, LookLeft...); the ones left out keep the prefab's default.</param>
    internal sealed record Spec(string Package, string Pose, Dictionary<string, float> Weights, int Line);

    /// <param name="use">The use event that starts her conversation, and <paramref name="start"/> its start action; null without one.</param>
    internal static void Set(MEGame game, IMEPackage pcc, ExportEntry sequence, ExportEntry pawn, Spec idle, ExportEntry? use, ExportEntry? start,
        PackageCache cache, List<string> changes)
    {
        var objects = KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().ToList();
        var pawnVar = objects.FirstOrDefault(o => o.ClassName == "SeqVar_Object" && o.GetProperty<ObjectProperty>("ObjValue")?.Value == pawn.UIndex);
        if (pawnVar is null)
        {
            pawnVar = SequenceObjectCreator.CreateSequenceObject(sequence, "SeqVar_Object", cache);
            KismetHelper.WriteObjValue(pawnVar, pawn);
        }

        var station = objects.FirstOrDefault(o => o.ClassName == "SequenceReference" && Holds(o, "Pawn", pawn));
        if (station is not null && PoseOf(station) is var old && !string.Equals(old, idle.Pose, StringComparison.OrdinalIgnoreCase))
        {
            var oldVars = Variables(station);
            KismetHelper.RemoveFromSequence(station, trash: true);
            RemoveUnused(sequence, oldVars);
            changes.Add($"idle {old} removed");
            station = null;
        }
        if (station is null)
        {
            station = Import(game, pcc, sequence, idle);
            changes.Add($"idle {idle.Pose} (prefab from {idle.Package})");
        }

        SetVariables(sequence, station, pawnVar, idle, cache, changes);

        var loaded = objects.FirstOrDefault(o => o.ClassName == "SeqEvent_LevelLoaded") ?? SequenceObjectCreator.CreateSequenceObject(sequence, "SeqEvent_LevelLoaded", cache);
        Link(loaded, "Loaded and Visible", station, changes, "Kismet: level loaded → idle");
        if (use is not null && start is not null)
        {
            var leave = objects.FirstOrDefault(o => o.ClassName == "BioSeqAct_LeaveActionStation" && Holds(o, "Pawn", pawn));
            if (leave is null)
            {
                leave = SequenceObjectCreator.CreateSequenceObject(sequence, "BioSeqAct_LeaveActionStation", cache);
                KismetHelper.CreateVariableLink(leave, "Pawn", pawnVar);
            }
            Link(use, "Out", leave, changes, "Kismet: use → leave idle");
            Link(start, "Out", station, changes, "Kismet: conversation end → idle");
        }
    }

    /// <summary>
    /// Copies the first prefab of the package whose station enters <see cref="Spec.Pose"/>. The source is opened fresh and changed
    /// in memory only: its links to the vanilla Kismet around it are cut first, or the import would copy that Kismet too.
    /// </summary>
    private static ExportEntry Import(MEGame game, IMEPackage pcc, ExportEntry sequence, Spec idle)
    {
        using var source = PackageInspector.Open(game, idle.Package);
        var station = source.Exports.FirstOrDefault(e => e.ClassName == "BioSeqAct_ActionStation"
                                                         && string.Equals(EntrancePose(e), idle.Pose, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"line {idle.Line}: {idle.Package} has no action station entering pose '{idle.Pose}'. "
                                                     + $"Poses there: {string.Join(", ", source.Exports.Where(e => e.ClassName == "BioSeqAct_ActionStation").Select(EntrancePose).OfType<string>().Distinct().Order())}.");
        IEntry? prefab = station;
        while (prefab is not null && prefab.ClassName != "SequenceReference") prefab = prefab.Parent;
        if (prefab is not ExportEntry reference) throw new ArgumentException($"line {idle.Line}: the {idle.Pose} station in {idle.Package} is not inside a prefab (SequenceReference).");

        KismetHelper.RemoveAllLinks(reference);
        var props = reference.GetProperties();
        props.RemoveNamedProperty("ParentSequence");
        reference.WriteProperties(props);
        // A free name: an import onto an existing path links to it instead of copying.
        var index = 1;
        while (pcc.FindEntry($"{sequence.InstancedFullPath}.{new NameReference("SequenceReference", index).Instanced}") is not null) index++;
        reference.ObjectName = new NameReference("SequenceReference", index);

        var rop = new RelinkerOptionsPackage { Cache = new PackageCache() };
        EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, reference, pcc, sequence, true, rop, out var entry);
        var copy = entry as ExportEntry ?? throw new InvalidOperationException("The import did not create an export.");
        KismetHelper.AddObjectToSequence(copy, sequence);
        return copy;
    }

    /// <summary>Pawn, the switches (off) and the given weights; every other slot empty. Writes only when a slot differs.</summary>
    private static void SetVariables(ExportEntry sequence, ExportEntry station, ExportEntry pawnVar, Spec idle, PackageCache cache, List<string> changes)
    {
        var links = KismetHelper.GetVariableLinksOfNode(station);
        foreach (var name in idle.Weights.Keys.Where(k => links.All(l => l.LinkDesc != $"Wt_{k}")))
        {
            throw new ArgumentException($"line {idle.Line}: the {idle.Pose} prefab has no weight '{name}'. It has: {string.Join(", ", links.Where(l => l.LinkDesc.StartsWith("Wt_")).Select(l => l.LinkDesc[3..]))}.");
        }
        var before = Variables(station);

        var created = new List<ExportEntry>();
        ExportEntry Find(string className, Func<ExportEntry, bool> matches, Action<ExportEntry> init)
        {
            if (before.Concat(created).FirstOrDefault(v => v.ClassName == className && matches(v)) is { } found) return found;
            var made = SequenceObjectCreator.CreateSequenceObject(sequence, className, cache);
            init(made);
            created.Add(made);
            return made;
        }

        var dirty = false;
        foreach (var link in links)
        {
            ExportEntry? wanted = link.LinkDesc switch
            {
                "Pawn" => pawnVar,
                _ when Off.Contains(link.LinkDesc) => Find("SeqVar_Bool", v => (v.GetProperty<IntProperty>("bValue")?.Value ?? 0) == 0, _ => { }),
                _ when link.LinkDesc.StartsWith("Wt_") && idle.Weights.TryGetValue(link.LinkDesc[3..], out var weight)
                    => Find("SeqVar_Float", v => (v.GetProperty<FloatProperty>("FloatValue")?.Value ?? 0f) == weight,
                            v => { if (weight != 0) v.WriteProperty(new FloatProperty(weight, "FloatValue")); }),
                _ => null,
            };
            if (link.LinkedNodes.Count == (wanted is null ? 0 : 1) && (wanted is null || link.LinkedNodes[0] == wanted)) continue;
            link.LinkedNodes = wanted is null ? [] : [wanted];
            dirty = true;
        }
        if (!dirty) return;
        KismetHelper.WriteVariableLinksToNode(station, links);
        RemoveUnused(sequence, before);
        changes.Add($"idle variables (weights: {(idle.Weights.Count == 0 ? "defaults" : string.Join(", ", idle.Weights.Select(w => $"{w.Key} {w.Value}")))})");
    }

    /// <summary>The prefab's bool and float variables no object of the sequence uses any more.</summary>
    private static void RemoveUnused(ExportEntry sequence, List<ExportEntry> variables)
    {
        var objects = KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().ToList();
        foreach (var variable in variables.Where(v => v.ClassName is "SeqVar_Bool" or "SeqVar_Float"))
        {
            if (KismetHelper.FindVariableConnectionsToNode(variable, objects).Count == 0) KismetHelper.RemoveFromSequence(variable, trash: true);
        }
    }

    private static void Link(ExportEntry from, string output, ExportEntry to, List<string> changes, string change)
    {
        var index = KismetHelper.GetOutputLinkNames(from).IndexOf(output);
        if (index < 0) throw new InvalidOperationException($"#{from.UIndex} {from.ClassName} has no output '{output}'.");
        if (KismetHelper.GetOutputLinksOfNode(from)[index].Any(o => o.LinkedOp == to && o.InputLinkIdx == 0)) return;
        KismetHelper.CreateOutputLink(from, output, to, 0);
        changes.Add(change);
    }

    private static bool Holds(ExportEntry obj, string slot, ExportEntry pawn) =>
        KismetHelper.GetVariableLinksOfNode(obj).Any(l => l.LinkDesc == slot && l.LinkedNodes.Any(v =>
            v is ExportEntry { ClassName: "SeqVar_Object" } o && o.GetProperty<ObjectProperty>("ObjValue")?.Value == pawn.UIndex));

    private static List<ExportEntry> Variables(ExportEntry station) =>
        KismetHelper.GetVariableLinksOfNode(station).SelectMany(l => l.LinkedNodes).OfType<ExportEntry>().Distinct().ToList();

    /// <summary>The pose of the station inside a prefab copy.</summary>
    private static string? PoseOf(ExportEntry reference) =>
        reference.FileRef.Exports.Where(e => e.ClassName == "BioSeqAct_ActionStation" && e.InstancedFullPath.StartsWith(reference.InstancedFullPath + ".", StringComparison.Ordinal))
            .Select(EntrancePose).FirstOrDefault();

    private static string? EntrancePose(ExportEntry station) =>
        station.GetProperty<StructProperty>("m_EntranceTransition")?.GetProp<NameProperty>("nmPoseSet")?.Value.Instanced;
}
