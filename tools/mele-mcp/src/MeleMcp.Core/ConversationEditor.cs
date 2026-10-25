using System.Text.RegularExpressions;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core;

public sealed record NodeIdChange(int Old, int New);

public sealed record ConvIdsResult(string Path, ExportRow Conversation, int? OldResRefId, int NewResRefId,
    IReadOnlyList<NodeIdChange> NodeIds, string BackupPath);

/// <param name="Node">New node label, E&lt;n&gt; or R&lt;n&gt;, as conv_read shows it.</param>
/// <param name="NodeData">Created Kismet objects: ConvNode event, Interp, InterpData, EndCurrentConvNode.</param>
public sealed record ConvNodeAdded(string Path, ExportRow Conversation, string Node, int ExportId, int StrRef, float? Length,
    IReadOnlyList<ExportRow> NodeData, IReadOnlyList<string> FaceFxLines, IReadOnlyList<string> Notes, string BackupPath);

public sealed record ConvLinkResult(string Path, ExportRow Conversation, string Change, string BackupPath);

/// <summary>Edits Game 1 conversations.</summary>
public static partial class ConversationEditor
{
    [GeneratedRegex(@"^(?<kind>[ER])(?<index>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NodeLabel();

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

    /// <summary>
    /// Appends a copy of <paramref name="template"/> (E&lt;n&gt; or R&lt;n&gt;) with its own text strref and export id, and no links.
    /// Like LEX's CloneNodeAndSequence it also copies the node data (ConvNode → Interp → InterpData → EndCurrentConvNode),
    /// so the new line gets the template's cameras, gestures and timing; its VO track gets <paramref name="strRef"/>.
    /// It then copies the template's FaceFX lines (FXA_&lt;old strref&gt;[_M]) to FXA_&lt;strRef&gt;[_M] without sound, so lips move.
    /// Plot conditions and transitions are cleared unless <paramref name="keepPlot"/>.
    /// </summary>
    public static ConvNodeAdded AddNode(MEGame game, string fileOrPath, string export, string template, int strRef, float? length, bool keepPlot)
    {
        if (!game.IsGame1()) throw new ArgumentException("conv_add_node handles Game 1 (LE1) conversations.");
        if (strRef <= 0) throw new ArgumentException("strRef must be positive.");
        if (length is <= 0) throw new ArgumentException("length must be positive.");

        var ((row, label, exportId, nodeData, faceFx, notes, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var conv = Conversation(pcc, export);
            var props = conv.GetProperties();
            var (isReply, index) = ParseNode(template);
            var list = NodeList(props, isReply);
            if (index >= list.Count) throw new ArgumentException($"{template} does not exist: the conversation has {list.Count} {(isReply ? "replies" : "entries")}.");
            var source = list[index];
            var sequence = props.GetProp<ObjectProperty>("m_pEvtSystemSeq")?.ResolveToEntry(pcc) as ExportEntry
                           ?? throw new ArgumentException($"#{conv.UIndex} has no node data sequence (m_pEvtSystemSeq).");
            var elements = KismetHelper.GetAllSequenceElements(sequence).OfType<ExportEntry>().ToList();

            var newId = NodeLists(props).SelectMany(l => l).Select(n => n.GetProp<IntProperty>("nExportID")?.Value ?? 0)
                .Concat(elements.Where(e => e.ClassName == "BioSeqEvt_ConvNode").Select(e => e.GetProperty<IntProperty>("m_nNodeID")?.Value ?? 0))
                .Max() + 1;

            var notes = new List<string>();
            var nodeData = new List<ExportRow>();
            var faceFx = new List<string>();

            var parsed = new ConversationExtended(conv);
            parsed.LoadConversation();
            var oldInterpData = parsed.ParseSingleNodeInterpData((isReply ? parsed.ReplyList : parsed.EntryList)[index]);
            if (oldInterpData is null)
            {
                notes.Add($"{template} has no node data: the new line has no camera, timing or lip sync.");
            }
            else
            {
                var (created, oldStrRef) = CloneNodeData(pcc, sequence, elements, oldInterpData, newId, strRef, length, notes);
                nodeData.AddRange(created.Select(ExportInspector.Row));

                var speaker = isReply ? -2 : source.GetProp<IntProperty>("nSpeakerIndex")?.Value ?? -1;
                if (oldStrRef is { } old) faceFx.AddRange(CopyFaceFx(pcc, props, speaker, old, strRef, notes));
            }

            var node = source.DeepClone();
            var np = node.Properties;
            np.AddOrReplaceProp(new StringRefProperty(strRef, "srText"));
            np.AddOrReplaceProp(new IntProperty(newId, "nExportID"));
            if (isReply) np.AddOrReplaceProp(new ArrayProperty<IntProperty>("EntryList"));
            else np.AddOrReplaceProp(new ArrayProperty<StructProperty>("ReplyListNew"));
            if (!keepPlot)
            {
                np.AddOrReplaceProp(new IntProperty(-1, "nConditionalFunc"));
                np.AddOrReplaceProp(new IntProperty(-1, "nStateTransition"));
            }
            list.Add(node);
            conv.WriteProperties(props);

            return (ExportInspector.Row(conv), $"{(isReply ? 'R' : 'E')}{list.Count - 1}", newId, nodeData, faceFx, notes, pcc.FilePath);
        });

        return new ConvNodeAdded(path, row, label, exportId, strRef, length, nodeData, faceFx, notes, backup);
    }

    /// <summary>
    /// Adds or removes one link: start → E (m_StartingList), E → R (wheel choice in ReplyListNew, with paraphrase and category),
    /// R → E (the reply's EntryList). Adding an existing E → R link updates its paraphrase and category.
    /// </summary>
    public static ConvLinkResult Link(MEGame game, string fileOrPath, string export, string from, string to, int? paraphrase, string? category, bool remove)
    {
        if (!game.IsGame1()) throw new ArgumentException("conv_link handles Game 1 (LE1) conversations.");

        var ((row, change, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var conv = Conversation(pcc, export);
            var props = conv.GetProperties();
            var (toReply, toIndex) = ParseNode(to);
            if (toIndex >= NodeList(props, toReply).Count) throw new ArgumentException($"{to} does not exist.");

            string change;
            if (from.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                if (toReply) throw new ArgumentException("A conversation starts on an entry (E<n>), not a reply.");
                var starts = props.GetProp<ArrayProperty<IntProperty>>("m_StartingList") ?? new ArrayProperty<IntProperty>("m_StartingList");
                change = EditIntList(starts, toIndex, remove, $"start → {to}");
                props.AddOrReplaceProp(starts);
            }
            else
            {
                var (fromReply, fromIndex) = ParseNode(from);
                var fromList = NodeList(props, fromReply);
                if (fromIndex >= fromList.Count) throw new ArgumentException($"{from} does not exist.");
                if (fromReply == toReply) throw new ArgumentException("Links go entry → reply or reply → entry.");
                var node = fromList[fromIndex].Properties;

                if (fromReply)
                {
                    var next = node.GetProp<ArrayProperty<IntProperty>>("EntryList") ?? new ArrayProperty<IntProperty>("EntryList");
                    change = EditIntList(next, toIndex, remove, $"{from} → {to}");
                    node.AddOrReplaceProp(next);
                }
                else
                {
                    var choices = node.GetProp<ArrayProperty<StructProperty>>("ReplyListNew") ?? new ArrayProperty<StructProperty>("ReplyListNew");
                    var existing = choices.FirstOrDefault(c => c.GetProp<IntProperty>("nIndex")?.Value == toIndex);
                    if (remove)
                    {
                        if (existing is null) throw new ArgumentException($"{from} has no wheel choice to {to}.");
                        choices.Remove(existing);
                        change = $"removed {from} → {to}";
                    }
                    else
                    {
                        var cat = Category(game, category ?? "DEFAULT");
                        var choice = existing ?? new StructProperty("BioDialogReplyListDetails", new PropertyCollection
                        {
                            new IntProperty(toIndex, "nIndex"),
                            new StringRefProperty(0, "srParaphrase"),
                            new StrProperty("", "sParaphrase"),
                            new EnumProperty(cat, "EReplyCategory", game, "Category"),
                            new NoneProperty(),
                        });
                        if (paraphrase is { } para) choice.Properties.AddOrReplaceProp(new StringRefProperty(para, "srParaphrase"));
                        if (category is not null || existing is null) choice.Properties.AddOrReplaceProp(new EnumProperty(cat, "EReplyCategory", game, "Category"));
                        if (existing is null) choices.Add(choice);
                        change = $"{(existing is null ? "added" : "updated")} {from} → {to} {cat.Instanced.Replace("REPLY_CATEGORY_", "")} wheel ${choice.GetProp<StringRefProperty>("srParaphrase")?.Value}";
                    }
                    node.AddOrReplaceProp(choices);
                }
            }

            conv.WriteProperties(props);
            return (ExportInspector.Row(conv), change, pcc.FilePath);
        });

        return new ConvLinkResult(path, row, change, backup);
    }

    private static ExportEntry Conversation(IMEPackage pcc, string export)
    {
        var conv = ExportInspector.Resolve(pcc, export);
        if (conv.ClassName != "BioConversation") throw new ArgumentException($"#{conv.UIndex} is a {conv.ClassName}, not a BioConversation.");
        return conv;
    }

    private static (bool IsReply, int Index) ParseNode(string label)
    {
        var m = NodeLabel().Match(label.Trim());
        if (!m.Success) throw new ArgumentException($"'{label}' is not a node label. Use E<n> for entries and R<n> for replies, as conv_read prints them.");
        return (char.ToUpperInvariant(m.Groups["kind"].Value[0]) == 'R', int.Parse(m.Groups["index"].Value));
    }

    private static ArrayProperty<StructProperty> NodeList(PropertyCollection props, bool replies)
        => props.GetProp<ArrayProperty<StructProperty>>(replies ? "m_ReplyList" : "m_EntryList")
           ?? throw new ArgumentException($"The conversation has no {(replies ? "m_ReplyList" : "m_EntryList")}.");

    private static IEnumerable<ArrayProperty<StructProperty>> NodeLists(PropertyCollection props)
        => new[] { "m_EntryList", "m_ReplyList" }.Select(n => props.GetProp<ArrayProperty<StructProperty>>(n)).OfType<ArrayProperty<StructProperty>>();

    private static string EditIntList(ArrayProperty<IntProperty> list, int value, bool remove, string label)
    {
        if (remove)
        {
            var found = list.Where(i => i.Value == value).ToList();
            if (found.Count == 0) throw new ArgumentException($"No link {label}.");
            foreach (var i in found) list.Remove(i);
            return $"removed {label}";
        }
        if (list.Any(i => i.Value == value)) return $"{label} already there";
        list.Add(new IntProperty(value));
        return $"added {label}";
    }

    private static NameReference Category(MEGame game, string category)
    {
        var name = category.StartsWith("REPLY_CATEGORY_", StringComparison.OrdinalIgnoreCase) ? category : "REPLY_CATEGORY_" + category;
        var values = GlobalUnrealObjectInfo.GetEnumValues(game, "EReplyCategory");
        return values?.FirstOrDefault(v => v.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } value && value != NameReference.None
            ? value
            : throw new ArgumentException($"Unknown reply category '{category}'. Use one of: {string.Join(", ", values?.Select(v => v.Instanced.Replace("REPLY_CATEGORY_", "")) ?? [])}.");
    }

    /// <summary>Port of the Kismet part of LEX's CloneNodeAndSequence. Returns the new objects and the template VO strref.</summary>
    private static (List<ExportEntry> Created, int? OldStrRef) CloneNodeData(IMEPackage pcc, ExportEntry sequence, List<ExportEntry> elements,
        ExportEntry oldInterpData, int newId, int strRef, float? length, List<string> notes)
    {
        var oldInterp = elements.FirstOrDefault(e => e.ClassName == "SeqAct_Interp"
                                                     && KismetHelper.GetVariableLinksOfNode(e).Any(v => v.LinkDesc == "Data" && v.LinkedNodes.Contains(oldInterpData)))
                        ?? throw new ArgumentException($"No SeqAct_Interp uses #{oldInterpData.UIndex}.");

        var created = new List<ExportEntry>();
        var newInterp = KismetHelper.CloneObject(oldInterp, sequence); // also strips the copy's links
        var newInterpData = EntryCloner.CloneTree(oldInterpData);
        KismetHelper.AddObjectToSequence(newInterpData, sequence, true);

        var oldConvNode = KismetHelper.FindOutputConnectionsToNode(oldInterp, elements).FirstOrDefault(e => e.ClassName == "BioSeqEvt_ConvNode");
        if (oldConvNode is null)
        {
            notes.Add("The template's Interp has no ConvNode event: the new line's node data will not start.");
        }
        else
        {
            var newConvNode = KismetHelper.CloneObject(oldConvNode, sequence);
            RequireOutput(newConvNode, "Started");
            KismetHelper.CreateOutputLink(newConvNode, "Started", newInterp, 0);
            newConvNode.WriteProperty(new IntProperty(newId, "m_nNodeID"));
            created.Add(newConvNode);
        }
        created.Add(newInterp);
        created.Add(newInterpData);

        if (KismetHelper.GetOutputLinksOfNode(oldInterp).SelectMany(l => l)
                .FirstOrDefault(l => l.LinkedOp?.ClassName == "BioSeqAct_EndCurrentConvNode")?.LinkedOp is ExportEntry oldEnd)
        {
            var newEnd = KismetHelper.CloneObject(oldEnd, sequence);
            RequireOutput(newInterp, "Completed");
            KismetHelper.CreateOutputLink(newInterp, "Completed", newEnd, 0);
            KismetHelper.CreateOutputLink(newInterp, "Reversed", newEnd, 0);
            created.Add(newEnd);
        }
        else
        {
            notes.Add("The template's Interp has no EndCurrentConvNode: the line may not end on its own.");
        }

        var varLinks = KismetHelper.GetVariableLinksOfNode(oldInterp);
        foreach (var link in varLinks.Where(l => l.LinkDesc == "Data")) link.LinkedNodes = [];
        KismetHelper.WriteVariableLinksToNode(newInterp, varLinks);
        KismetHelper.CreateVariableLink(newInterp, "Data", newInterpData);

        if (length is { } seconds) newInterpData.WriteProperty(new FloatProperty(seconds, "InterpLength"));

        int? oldStrRef = null;
        var prefix = newInterpData.InstancedFullPath + ".";
        var vo = pcc.Exports.Where(x => x.ClassName == "BioEvtSysTrackVOElements" && x.InstancedFullPath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (vo.Count == 0) notes.Add("The template's node data has no VO track: no subtitle timing or lip sync from strref.");
        foreach (var track in vo)
        {
            oldStrRef ??= track.GetProperty<IntProperty>("m_nStrRefID")?.Value;
            track.WriteProperty(new IntProperty(strRef, "m_nStrRefID"));
        }
        return (created, oldStrRef);
    }

    private static void RequireOutput(ExportEntry obj, string link)
    {
        if (!KismetHelper.GetOutputLinkNames(obj).Contains(link))
        {
            throw new InvalidOperationException($"#{obj.UIndex} {obj.ClassName} has no output '{link}'.");
        }
    }

    /// <summary>Speaker id + 2 indexes m_aMaleFaceSets / m_aFemaleFaceSets (player -2 → 0, owner -1 → 1).</summary>
    private static IEnumerable<string> CopyFaceFx(IMEPackage pcc, PropertyCollection props, int speaker, int oldStrRef, int strRef, List<string> notes)
    {
        var copied = new List<string>();
        foreach (var (setList, suffix) in new[] { ("m_aMaleFaceSets", "_M"), ("m_aFemaleFaceSets", "") })
        {
            var sets = props.GetProp<ArrayProperty<ObjectProperty>>(setList);
            if (sets is null || speaker + 2 < 0 || speaker + 2 >= sets.Count || sets[speaker + 2].ResolveToEntry(pcc) is not ExportEntry set)
            {
                notes.Add($"No {setList} entry for speaker {speaker}: no lip sync for that Shepard gender.");
                continue;
            }
            var (created, _, missing) = FaceFxSets.CopyIn(set, [new FaceFxLineCopy($"FXA_{oldStrRef}{suffix}", $"FXA_{strRef}{suffix}")], withSound: false, skipMissing: true);
            copied.AddRange(created.Select(l => $"{set.ObjectName.Instanced}.{l.Name}"));
            notes.AddRange(missing.Select(m => $"{set.ObjectName.Instanced} has no line {m}: no lip sync for that Shepard gender."));
        }
        return copied;
    }
}
