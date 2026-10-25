using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

public sealed record NodeIdChange(int Old, int New);

public sealed record ConvIdsResult(string Path, ExportRow Conversation, int? OldResRefId, int NewResRefId,
    IReadOnlyList<NodeIdChange> NodeIds, string BackupPath);

/// <summary>Edits Game 1 conversations.</summary>
public static class ConversationEditor
{
    /// <summary>
    /// Port of LEX's ChangeConvoIDandConvNodeIDs. A BioSeqEvt_ConvNode fires for the conversation whose m_nResRefID equals
    /// its m_nConvResRefID, on the node whose nExportID equals its m_nNodeID, so a copied conversation needs its own resref
    /// and node ids. Node ids are renumbered from <paramref name="nodeIdBase"/>, ConvNode events first, then entries and
    /// replies that have no event.
    /// </summary>
    public static ConvIdsResult SetIds(MEGame game, string fileOrPath, string export, int resRefId, int nodeIdBase)
    {
        if (!game.IsGame1()) throw new ArgumentException("conv_set_ids handles Game 1 (LE1) conversations.");
        if (resRefId <= 0 || nodeIdBase <= 0) throw new ArgumentException("resRefId and nodeIdBase must be positive.");

        var ((row, oldResRefId, nodeIds, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var conv = ExportInspector.Resolve(pcc, export);
            if (conv.ClassName != "BioConversation") throw new ArgumentException($"#{conv.UIndex} is a {conv.ClassName}, not a BioConversation.");

            var props = conv.GetProperties();
            var sequence = props.GetProp<ObjectProperty>("m_pEvtSystemSeq")?.ResolveToEntry(pcc) as ExportEntry
                           ?? throw new ArgumentException($"#{conv.UIndex} has no node data sequence (m_pEvtSystemSeq).");
            var oldResRef = props.GetProp<IntProperty>("m_nResRefID")?.Value;
            props.AddOrReplaceProp(new IntProperty(resRefId, "m_nResRefID"));

            var remap = new Dictionary<int, int>();
            int Map(int old)
            {
                if (!remap.TryGetValue(old, out var id))
                {
                    id = nodeIdBase + remap.Count;
                    remap[old] = id;
                }
                return id;
            }

            var convNodes = KismetHelper.GetAllSequenceElements(sequence).OfType<ExportEntry>().Where(e => e.ClassName == "BioSeqEvt_ConvNode");
            foreach (var node in convNodes)
            {
                var nodeProps = node.GetProperties();
                if (nodeProps.GetProp<IntProperty>("m_nNodeID") is not { } nodeId) continue;
                nodeProps.AddOrReplaceProp(new IntProperty(Map(nodeId.Value), "m_nNodeID"));
                nodeProps.AddOrReplaceProp(new IntProperty(resRefId, "m_nConvResRefID"));
                node.WriteProperties(nodeProps);
            }

            foreach (var list in new[] { "m_EntryList", "m_ReplyList" })
            {
                foreach (var node in props.GetProp<ArrayProperty<StructProperty>>(list)?.ToList() ?? [])
                {
                    if (node.GetProp<IntProperty>("nExportID") is { } exportId) exportId.Value = Map(exportId.Value);
                }
            }

            conv.WriteProperties(props);
            return (ExportInspector.Row(conv), oldResRef, remap.Select(p => new NodeIdChange(p.Key, p.Value)).ToList(), pcc.FilePath);
        });

        return new ConvIdsResult(path, row, oldResRefId, resRefId, nodeIds, backup);
    }
}
