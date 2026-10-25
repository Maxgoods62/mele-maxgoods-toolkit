using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core;

public sealed record KismetSlots(IReadOnlyList<string> Outputs, IReadOnlyList<string> Variables, IReadOnlyList<string> Events);

public sealed record KismetAddResult(string Path, ExportRow Sequence, ExportRow Created, KismetSlots Slots, string BackupPath);

public sealed record KismetLinkResult(string Path, ExportRow Source, string Kind, string Link, ExportRow Target, string BackupPath);

/// <summary>Adds Kismet objects to a sequence and links them, through LEC's SequenceObjectCreator and KismetHelper.</summary>
public static class KismetEditor
{
    public static KismetAddResult Add(MEGame game, string fileOrPath, string sequence, string className)
    {
        var ((seqRow, created, slots, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var seq = ExportInspector.Resolve(pcc, sequence);
            if (!seq.IsA("Sequence")) throw new ArgumentException($"#{seq.UIndex} is a {seq.ClassName}, not a Sequence.");

            using var cache = new PackageCache();
            var obj = SequenceObjectCreator.CreateSequenceObject(seq, className, cache);
            return (ExportInspector.Row(seq), ExportInspector.Row(obj), Slots(obj), pcc.FilePath);
        });

        return new KismetAddResult(path, seqRow, created, slots, backup);
    }

    /// <summary>
    /// kind: output (source's output link named <paramref name="link"/> → target's input <paramref name="inputIndex"/>),
    /// variable (source's variable slot → target variable), event (source's event slot → target event).
    /// </summary>
    public static KismetLinkResult Link(MEGame game, string fileOrPath, string source, string kind, string link, string target, int inputIndex)
    {
        var ((src, dst, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var from = ExportInspector.Resolve(pcc, source);
            var to = ExportInspector.Resolve(pcc, target);
            var slots = Slots(from);
            var (names, create) = kind.ToLowerInvariant() switch
            {
                "output" => (slots.Outputs, (Action)(() => KismetHelper.CreateOutputLink(from, link, to, inputIndex))),
                "variable" => (slots.Variables, () => KismetHelper.CreateVariableLink(from, link, to)),
                "event" => (slots.Events, () => KismetHelper.CreateEventLink(from, link, to)),
                _ => throw new ArgumentException("kind must be output, variable or event."),
            };
            // KismetHelper silently does nothing for an unknown link name.
            if (!names.Contains(link))
            {
                throw new ArgumentException($"#{from.UIndex} has no {kind} link '{link}'. It has: {string.Join(", ", names.Select(n => $"'{n}'"))}.");
            }

            create();
            return (ExportInspector.Row(from), ExportInspector.Row(to), pcc.FilePath);
        });

        return new KismetLinkResult(path, src, kind, link, dst, backup);
    }

    private static KismetSlots Slots(ExportEntry obj) => new(
        KismetHelper.GetOutputLinkNames(obj),
        KismetHelper.GetVariableLinksOfNode(obj).Select(v => v.LinkDesc).ToList(),
        KismetHelper.GetEventLinksOfNode(obj).Select(e => e.LinkDesc).ToList());
}
