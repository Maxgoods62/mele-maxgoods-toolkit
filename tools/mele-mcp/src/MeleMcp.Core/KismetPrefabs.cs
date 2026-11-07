using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

/// <summary>Copies of BioWare's Kismet prefabs: a SequenceReference and its sequence, taken from a vanilla file.</summary>
internal static class KismetPrefabs
{
    /// <summary>
    /// Imports the SequenceReference <paramref name="find"/> picks in <paramref name="package"/> into <paramref name="sequence"/>.
    /// The source is opened fresh and changed in memory only: its links to the vanilla Kismet around it are cut first, or the
    /// import would copy that Kismet too.
    /// </summary>
    internal static ExportEntry Import(MEGame game, IMEPackage pcc, ExportEntry sequence, string package, Func<IMEPackage, ExportEntry> find)
    {
        using var source = PackageInspector.Open(game, package);
        var reference = find(source);
        if (reference.ClassName != "SequenceReference") throw new InvalidOperationException($"{reference.InstancedFullPath} is a {reference.ClassName}, not a SequenceReference.");

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

    /// <summary>The prefab copy in <paramref name="sequence"/> that holds an object matching <paramref name="inside"/>.</summary>
    internal static ExportEntry? Find(ExportEntry sequence, Func<ExportEntry, bool> inside) =>
        KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().FirstOrDefault(o => o.ClassName == "SequenceReference"
            && o.FileRef.Exports.Any(e => e.InstancedFullPath.StartsWith(o.InstancedFullPath + ".", StringComparison.Ordinal) && inside(e)));

    /// <summary>The level's SeqEvent_LevelLoaded, created when it has none.</summary>
    internal static ExportEntry LevelLoaded(ExportEntry sequence, PackageCache cache) =>
        FindLevelLoaded(sequence) ?? SequenceObjectCreator.CreateSequenceObject(sequence, "SeqEvent_LevelLoaded", cache);

    /// <summary>The level's SeqEvent_LevelLoaded; null when it has none (a build that only cuts links creates nothing).</summary>
    internal static ExportEntry? FindLevelLoaded(ExportEntry sequence) =>
        KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().FirstOrDefault(o => o.ClassName == "SeqEvent_LevelLoaded");

    /// <summary>The first object of class <paramref name="className"/> an output of <paramref name="from"/> links to.</summary>
    internal static ExportEntry? Next(ExportEntry from, string output, string className)
    {
        var index = KismetHelper.GetOutputLinkNames(from).IndexOf(output);
        if (index < 0) throw new InvalidOperationException($"#{from.UIndex} {from.ClassName} has no output '{output}'.");
        return KismetHelper.GetOutputLinksOfNode(from)[index].Select(o => o.LinkedOp).OfType<ExportEntry>().FirstOrDefault(o => o.ClassName == className);
    }

    /// <summary>Links an output to input <paramref name="input"/> of <paramref name="to"/> unless it already is; notes <paramref name="change"/> when it links.</summary>
    internal static void Link(ExportEntry from, string output, ExportEntry to, List<string> changes, string change, int input = 0)
    {
        var index = KismetHelper.GetOutputLinkNames(from).IndexOf(output);
        if (index < 0) throw new InvalidOperationException($"#{from.UIndex} {from.ClassName} has no output '{output}'.");
        if (KismetHelper.GetOutputLinksOfNode(from)[index].Any(o => o.LinkedOp == to && o.InputLinkIdx == input)) return;
        KismetHelper.CreateOutputLink(from, output, to, input);
        changes.Add(change);
    }

    /// <summary>
    /// Cuts the links of output <paramref name="output"/> of <paramref name="from"/> to the objects <paramref name="drop"/> picks, and
    /// returns those objects (each once, in link order). <see cref="Link"/> only adds, so a build whose YAML no longer asks for a link
    /// cuts it with this, or the old target fires beside the new one. Only that output's links are rewritten; the objects stay.
    /// </summary>
    internal static List<ExportEntry> Unlink(ExportEntry from, string output, Func<ExportEntry, bool> drop)
    {
        var index = KismetHelper.GetOutputLinkNames(from).IndexOf(output);
        if (index < 0) throw new InvalidOperationException($"#{from.UIndex} {from.ClassName} has no output '{output}'.");
        var outputs = from.GetProperty<ArrayProperty<StructProperty>>("OutputLinks")!;
        var cut = new List<ExportEntry>();
        if (outputs[index].GetProp<ArrayProperty<StructProperty>>("Links") is not { } links) return cut;
        for (var i = 0; i < links.Count; i++)
        {
            if (links[i].GetProp<ObjectProperty>("LinkedOp")?.ResolveToEntry(from.FileRef) is not ExportEntry to || !drop(to)) continue;
            if (!cut.Contains(to)) cut.Add(to);
            links.RemoveAt(i--);
        }
        if (cut.Count > 0) from.WriteProperty(outputs);
        return cut;
    }
}
