using System.Globalization;
using System.Text;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

/// <summary>
/// A conversation as a readable graph: starting entries, entries (NPC lines) with their reply choices, replies (Shepard lines)
/// with the entries they lead to. Game 1 text comes from the conversation's own BioTlkFile for one language, LE2/LE3 text from the
/// .tlk files, as those games keep no strings in packages.
/// </summary>
public static class ConversationReader
{
    public static string Read(MEGame game, string fileOrPath, string export, string language)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        var conv = ExportInspector.Resolve(pcc, export);
        if (conv.ClassName != "BioConversation") throw new ArgumentException($"#{conv.UIndex} is a {conv.ClassName}, not a BioConversation.");

        var props = conv.GetProperties();
        var (lookup, female, tlkName) = TextSource(game, pcc, props, language);
        string Text(int id) => id <= 0 ? "" : lookup(id);
        string Quoted(int id) => $"\"{Text(id)}\"" + (female(id) is { } f ? $" female: \"{f}\"" : "");

        var speakers = SpeakerTags(game, props);
        string Speaker(int i) => i switch { -1 => "owner", -2 => "player", _ when i >= 0 && i < speakers.Count => speakers[i], _ => $"speaker{i}" };

        // LEC resolves node -> BioSeqEvt_ConvNode (m_nNodeID == nExportID) -> SeqAct_Interp -> InterpData.
        var parsed = new ConversationExtended(conv);
        parsed.LoadConversation();
        var voTracks = pcc.Exports.Where(x => x.ClassName == "BioEvtSysTrackVOElements").ToList();
        string NodeData(DialogueNodeExtended? node)
        {
            var interp = node is null ? null : parsed.ParseSingleNodeInterpData(node);
            if (interp is null) return " (no node data)";
            var length = interp.GetProperty<FloatProperty>("InterpLength")?.Value;
            var vo = voTracks.FirstOrDefault(x => x.InstancedFullPath.StartsWith(interp.InstancedFullPath + ".", StringComparison.Ordinal));
            // With a decimal point on every Windows: the length is pasted back into export_set and YAML files.
            return string.Create(CultureInfo.InvariantCulture, $" interp=#{interp.UIndex} {length:0.#}s")
                   + (vo is null ? "" : $" vo=#{vo.UIndex}(${vo.GetProperty<IntProperty>("m_nStrRefID")?.Value})");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"#{conv.UIndex} {conv.InstancedFullPath}  m_nResRefID={props.GetProp<IntProperty>("m_nResRefID")?.Value}  text: {tlkName ?? "no local TLK"}");
        sb.AppendLine($"node data: {(props.GetProp<ObjectProperty>(game.IsGame1() ? "m_pEvtSystemSeq" : "MatineeSequence")?.ResolveToEntry(pcc)?.InstancedFullPath ?? "none")}");
        sb.AppendLine($"speakers: {string.Join(", ", speakers.Select((s, i) => $"[{i}] {s}"))}");
        var starts = props.GetProp<ArrayProperty<IntProperty>>("m_StartingList")?.Select(i => $"E{i.Value}") ?? [];
        sb.AppendLine($"start: {string.Join(", ", starts)}");

        var entries = Structs(props, "m_EntryList");
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var id = e.GetProp<StringRefProperty>("srText")?.Value ?? 0;
            sb.AppendLine($"E{i} [{Speaker(e.GetProp<IntProperty>("nSpeakerIndex")?.Value ?? -1)}] ${id} {Quoted(id)}{Plot(e)} exportID={e.GetProp<IntProperty>("nExportID")?.Value}{NodeData(parsed.EntryList.ElementAtOrDefault(i))}");
            foreach (var r in Structs(e.Properties, "ReplyListNew"))
            {
                var para = r.GetProp<StringRefProperty>("srParaphrase")?.Value ?? 0;
                var category = r.GetProp<EnumProperty>("Category")?.Value.Instanced.Replace("REPLY_CATEGORY_", "");
                sb.AppendLine($"   -> R{r.GetProp<IntProperty>("nIndex")?.Value} {category} wheel ${para} {Quoted(para)}");
            }
        }

        var replies = Structs(props, "m_ReplyList");
        for (var i = 0; i < replies.Count; i++)
        {
            var r = replies[i];
            var id = r.GetProp<StringRefProperty>("srText")?.Value ?? 0;
            var next = r.GetProp<ArrayProperty<IntProperty>>("EntryList")?.Select(x => $"E{x.Value}") ?? [];
            var type = r.GetProp<EnumProperty>("ReplyType")?.Value.Instanced.Replace("REPLY_", "");
            sb.AppendLine($"R{i} [player] {type} ${id} {Quoted(id)}{Plot(r)} exportID={r.GetProp<IntProperty>("nExportID")?.Value}{NodeData(parsed.ReplyList.ElementAtOrDefault(i))} -> {string.Join(", ", next)}");
        }
        return sb.ToString();
    }

    /// <summary>The conversation's line text by string id, its female text where that differs (LE2/LE3), and what to call the source in the header.</summary>
    private static (Func<int, string> Lookup, Func<int, string?> Female, string? Name) TextSource(MEGame game, IMEPackage pcc, PropertyCollection props, string language)
    {
        if (game.IsGame1())
        {
            var (tlk, name) = FindTalkFile(pcc, props, language);
            return (id => tlk?.FindDataById(id, returnNullIfNotFound: true, noQuotes: true) ?? "<not in local TLK>", _ => null, name);
        }
        var (male, female) = TalkTables.Load(game, language);
        return (id => male.GetValueOrDefault(id, "<not in any .tlk>"), id => female.GetValueOrDefault(id), $"{TalkTables.Language(language)} .tlk files, highest mount wins");
    }

    /// <summary>Speaker tags by nSpeakerIndex: Game 3 keeps plain names in m_aSpeakerList, the others structs in m_SpeakerList.</summary>
    internal static List<string> SpeakerTags(MEGame game, PropertyCollection props) => game.IsGame3()
        ? props.GetProp<ArrayProperty<NameProperty>>("m_aSpeakerList")?.Select(n => n.Value.Instanced).ToList() ?? []
        : props.GetProp<ArrayProperty<StructProperty>>("m_SpeakerList")?
            .Select(s => s.GetProp<NameProperty>("sSpeakerTag")?.Value.Instanced ?? "?").ToList() ?? [];

    internal static List<StructProperty> Structs(PropertyCollection props, string name)
        => props.GetProp<ArrayProperty<StructProperty>>(name)?.ToList() ?? [];

    internal static string Plot(StructProperty node)
    {
        var cond = node.GetProp<IntProperty>("nConditionalFunc")?.Value ?? -1;
        var trans = node.GetProp<IntProperty>("nStateTransition")?.Value ?? -1;
        var sb = new StringBuilder();
        if (cond != -1)
        {
            var isFunc = node.GetProp<BoolProperty>("bFireConditional")?.Value ?? false;
            sb.Append($" if {(isFunc ? "cond" : "bool")} {cond}({node.GetProp<IntProperty>("nConditionalParam")?.Value})");
        }
        if (trans != -1) sb.Append($" then transition {trans}({node.GetProp<IntProperty>("nStateTransitionParam")?.Value})");
        return sb.ToString();
    }

    /// <summary>The conversation's TLK set holds one BioTlkFile per language, named tlk (INT) or tlk_&lt;LANG&gt;.</summary>
    internal static (ME1TalkFile? Tlk, string? Name) FindTalkFile(IMEPackage pcc, PropertyCollection props, string language)
    {
        var set = props.GetProp<ObjectProperty>("m_oTlkFileSet")?.ResolveToEntry(pcc);
        var siblings = pcc.Exports.Where(x => x.ClassName == "BioTlkFile" && (set is null || x.Parent == set.Parent)).ToList();
        var wanted = language.Equals("INT", StringComparison.OrdinalIgnoreCase) ? "tlk" : $"tlk_{language}";
        var pick = siblings.FirstOrDefault(x => x.ObjectName.Instanced.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                   ?? siblings.FirstOrDefault(x => x.ObjectName.Instanced.Equals("tlk", StringComparison.OrdinalIgnoreCase))
                   ?? siblings.FirstOrDefault();
        return pick is null ? (null, null) : (new ME1TalkFile(pick), pick.InstancedFullPath);
    }
}
