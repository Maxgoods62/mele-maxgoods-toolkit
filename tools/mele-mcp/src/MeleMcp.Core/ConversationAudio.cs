using System.IO.Enumeration;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MeleMcp.Core;

/// <param name="Node">E&lt;n&gt; (NPC line) or R&lt;n&gt; (Shepard), as conv_read names them.</param>
/// <param name="StrRef">The VO track's strref: the key of the line's FaceFX and audio (the subtitle's when the node has no VO track).</param>
/// <param name="Status">The worse of Female and Male; see <see cref="ConversationAudio"/>.</param>
/// <param name="Female">Status in the female-Shepard FaceFX set (line FXA_&lt;strref&gt;).</param>
/// <param name="Male">Status in the male-Shepard FaceFX set (line FXA_&lt;strref&gt;_M).</param>
/// <param name="Reachable">The line can play: the starting list leads to it.</param>
public sealed record AudioLine(string File, string Conversation, string Node, string Speaker, int StrRef, string Text,
                               string Status, string Female, string Male, bool Reachable, string? Detail);

/// <param name="Statuses">Line count per status, over every line read (filters only shorten Lines).</param>
public sealed record ConvAudioResult(int Files, int Conversations, int LinesRead, IReadOnlyDictionary<string, int> Statuses,
                                     IReadOnlyList<AudioLine> Lines, IReadOnlyList<string> Notes);

/// <summary>
/// Which LE1 conversation lines have a voice. LE1 plays a line through its VO track's strref: the speaker's FaceFX set
/// has a line FXA_&lt;strref&gt; (FXA_&lt;strref&gt;_M in the male-Shepard set), whose sound cue index picks a SoundCue in the
/// set's ReferencedSoundCues; the cue leads to a SoundNodeWave whose ISACT sound is embedded or listed in its
/// BioSoundNodeWaveStreamingData. Statuses, worst first:
/// broken (the chain points at something missing), other-voice (plays a wave named for another line, e.g. a template's:
/// wrong words), no-vo-track, no-set (speaker has no FaceFX set), no-facefx (no lips, no voice), silent (lips move, cue
/// index -1: what conv_build and facefx_copy_lines make), unchecked (an import we did not follow), voiced.
/// </summary>
public static class ConversationAudio
{
    private static readonly string[] Order = ["broken", "other-voice", "no-vo-track", "no-set", "no-facefx", "silent", "unchecked", "voiced"];

    /// <param name="mod">M3 library mod (all its packages), or null to read <paramref name="package"/>.</param>
    /// <param name="language">Text language, and the only _LOC_ copies read (each language has its own audio).</param>
    public static ConvAudioResult Check(MEGame game, string? mod, string? package, string? conversation, string language,
                                        bool includeVoiced, bool includeUnreachable, int limit)
    {
        if (!game.IsGame1()) throw new ArgumentException("conv_audio handles Game 1 (LE1) conversations.");
        if (string.IsNullOrWhiteSpace(mod) == string.IsNullOrWhiteSpace(package)) throw new ArgumentException("Give mod or package (one of them).");
        LecHost.EnsureInitialized();
        var lang = language.Trim().ToUpperInvariant();

        List<string> files;
        IReadOnlyList<string> folders = [];
        if (!string.IsNullOrWhiteSpace(mod))
        {
            (_, folders) = ModInstaller.Locate(game, mod);
            files = folders.SelectMany(f => Directory.EnumerateFiles(f, "*.pcc", SearchOption.AllDirectories))
                           .Where(f => !FileSystemName.MatchesSimpleExpression("*_LOC_*", Path.GetFileName(f), ignoreCase: true)
                                       || Path.GetFileNameWithoutExtension(f).EndsWith($"_LOC_{lang}", StringComparison.OrdinalIgnoreCase))
                           .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        else files = [GamePaths.ResolvePackage(game, package!)];

        var global = new Lazy<Dictionary<int, string>>(() => GlobalStrings.Load(game, lang, folders));
        var lines = new List<AudioLine>();
        var conversations = 0;
        foreach (var path in files)
        {
            using (var names = MEPackageHandler.UnsafePartialLoad(path, _ => false))
            {
                if (!names.Names.Contains("BioConversation")) continue;
            }
            using var pcc = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            var reader = new PackageAudio(pcc);
            foreach (var conv in pcc.Exports.Where(e => e.ClassName == "BioConversation"))
            {
                if (!string.IsNullOrEmpty(conversation) && !conv.ObjectName.Instanced.Contains(conversation, StringComparison.OrdinalIgnoreCase)) continue;
                conversations++;
                lines.AddRange(Lines(Path.GetFileName(path), pcc, conv, lang, global, reader));
            }
        }

        var statuses = lines.GroupBy(l => l.Status).OrderBy(g => Array.IndexOf(Order, g.Key)).ToDictionary(g => g.Key, g => g.Count());
        var shown = lines.Where(l => (includeVoiced || l.Status != "voiced") && (includeUnreachable || l.Reachable))
                         .OrderBy(l => Array.IndexOf(Order, l.Status)).ThenBy(l => l.File, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(l => l.Conversation).ThenBy(l => l.Node[0]).ThenBy(l => int.Parse(l.Node[1..])).ToList();
        var notes = new List<string>();
        if (shown.Count > limit) notes.Add($"{shown.Count} lines match; the first {limit} are shown (raise limit).");
        var unreachable = lines.Count(l => !l.Reachable && l.Status != "voiced");
        if (!includeUnreachable && unreachable > 0) notes.Add($"{unreachable} unvoiced lines can't play (template leftovers no link leads to); includeUnreachable lists them.");
        if (lines.Any(l => l.Status == "silent"))
            notes.Add("To voice a line: author ISACT sounds named by the line's strref (VO_<strref>, VO_<strref>_M), then point FXA_<strref>[_M] at a "
                      + "SoundCue → SoundNodeWave <base>:VO_<strref>[_M] (LEX Dialogue Editor 'Generate LE1 Audio Links' makes these, "
                      + "but skips a FaceFX line that already exists, as a silent line's does).");
        return new ConvAudioResult(files.Count, conversations, lines.Count, statuses, shown.Take(Math.Max(1, limit)).ToList(), notes);
    }

    private static IEnumerable<AudioLine> Lines(string file, IMEPackage pcc, ExportEntry conv, string lang,
                                                Lazy<Dictionary<int, string>> global, PackageAudio audio)
    {
        var props = conv.GetProperties();
        var (tlk, _) = ConversationReader.FindTalkFile(pcc, props, lang);
        string Text(int id) => tlk?.FindDataById(id, returnNullIfNotFound: true, noQuotes: true) ?? global.Value.GetValueOrDefault(id, "<no text>");

        var speakers = props.GetProp<ArrayProperty<StructProperty>>("m_SpeakerList")?
            .Select(s => s.GetProp<NameProperty>("sSpeakerTag")?.Value.Instanced ?? "?").ToList() ?? [];
        var entries = ConversationReader.Structs(props, "m_EntryList");
        var replies = ConversationReader.Structs(props, "m_ReplyList");
        var (reachE, reachR) = Reachable(props, entries, replies);
        var female = props.GetProp<ArrayProperty<ObjectProperty>>("m_aFemaleFaceSets");
        var male = props.GetProp<ArrayProperty<ObjectProperty>>("m_aMaleFaceSets");

        var parsed = new ConversationExtended(conv);
        parsed.LoadConversation();

        IEnumerable<(string Node, StructProperty Data, int Speaker, bool Reachable, DialogueNodeExtended? Parsed)> nodes =
            entries.Select((e, i) => ($"E{i}", e, e.GetProp<IntProperty>("nSpeakerIndex")?.Value ?? -1, reachE.Contains(i), parsed.EntryList.ElementAtOrDefault(i)))
                   .Concat(replies.Select((r, i) => ($"R{i}", r, -2, reachR.Contains(i), parsed.ReplyList.ElementAtOrDefault(i))));
        foreach (var (node, data, speaker, reachable, parsedNode) in nodes)
        {
            // Autocontinues and dialog ends are camera beats without a line.
            var type = data.GetProp<EnumProperty>("ReplyType")?.Value.Instanced;
            if (type is "REPLY_AUTOCONTINUE" or "REPLY_DIALOGEND") continue;
            var textId = data.GetProp<StringRefProperty>("srText")?.Value ?? 0;
            if (textId <= 0 || Text(textId).Trim().Length == 0) continue;

            var who = speaker switch { -1 => "owner", -2 => "player", _ when speaker < speakers.Count => speakers[speaker], _ => $"speaker{speaker}" };
            var interp = parsedNode is null ? null : parsed.ParseSingleNodeInterpData(parsedNode);
            var voId = interp is null ? null : audio.VoStrRef(interp);
            if (voId is not { } strref)
            {
                yield return new AudioLine(file, conv.ObjectName.Instanced, node, who, textId, Text(textId), "no-vo-track", "no-vo-track", "no-vo-track", reachable,
                                           interp is null ? "the node has no node data (nExportID → BioSeqEvt_ConvNode → InterpData)" : $"#{interp.UIndex} has no BioEvtSysTrackVOElements");
                continue;
            }

            var (f, fDetail) = audio.Line(SetAt(pcc, female, speaker), $"FXA_{strref}", strref);
            var (m, mDetail) = audio.Line(SetAt(pcc, male, speaker), $"FXA_{strref}_M", strref);
            var status = Array.IndexOf(Order, f) <= Array.IndexOf(Order, m) ? f : m;
            var details = new[] { strref != textId ? $"VO track ${strref}, subtitle ${textId}" : null,
                                  fDetail is null ? null : $"female: {fDetail}", mDetail is null ? null : $"male: {mDetail}" }
                .Where(d => d is not null).ToList();
            yield return new AudioLine(file, conv.ObjectName.Instanced, node, who, strref, Text(textId), status, f, m, reachable,
                                       details.Count == 0 ? null : string.Join("; ", details));
        }
    }

    /// <summary>Set index = speaker index + 2: [0] player, [1] owner, [2+] m_SpeakerList.</summary>
    private static IEntry? SetAt(IMEPackage pcc, ArrayProperty<ObjectProperty>? sets, int speaker)
        => sets is not null && speaker + 2 >= 0 && speaker + 2 < sets.Count ? sets[speaker + 2].ResolveToEntry(pcc) : null;

    /// <summary>Entries and replies the starting list leads to (entry → its wheel replies, reply → its next entries).</summary>
    private static (HashSet<int> Entries, HashSet<int> Replies) Reachable(PropertyCollection props, List<StructProperty> entries, List<StructProperty> replies)
    {
        var seenE = new HashSet<int>();
        var seenR = new HashSet<int>();
        var queue = new Queue<int>(props.GetProp<ArrayProperty<IntProperty>>("m_StartingList")?.Select(i => i.Value) ?? []);
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            if (e < 0 || e >= entries.Count || !seenE.Add(e)) continue;
            foreach (var r in ConversationReader.Structs(entries[e].Properties, "ReplyListNew").Select(x => x.GetProp<IntProperty>("nIndex")?.Value ?? -1))
            {
                if (r < 0 || r >= replies.Count || !seenR.Add(r)) continue;
                foreach (var next in replies[r].GetProp<ArrayProperty<IntProperty>>("EntryList")?.Select(x => x.Value) ?? []) queue.Enqueue(next);
            }
        }
        return (seenE, seenR);
    }

    /// <summary>One package's audio objects, each parsed once: VO tracks, FaceFX sets, ISACT indexes.</summary>
    private sealed class PackageAudio(IMEPackage pcc)
    {
        private readonly List<ExportEntry> voTracks = pcc.Exports.Where(e => e.ClassName == "BioEvtSysTrackVOElements").ToList();
        private readonly Dictionary<int, (FaceFXAnimSet Bin, ArrayProperty<ObjectProperty>? Cues)> sets = [];
        private readonly Dictionary<int, HashSet<string>> isactTitles = [];

        public int? VoStrRef(ExportEntry interp)
        {
            var prefix = interp.InstancedFullPath + ".";
            return voTracks.FirstOrDefault(t => t.InstancedFullPath.StartsWith(prefix, StringComparison.Ordinal))?.GetProperty<IntProperty>("m_nStrRefID")?.Value;
        }

        public (string Status, string? Detail) Line(IEntry? setEntry, string lineName, int strref)
        {
            if (setEntry is null) return ("no-set", "no FaceFX set for this speaker");
            if (setEntry is not ExportEntry setExport) return ("unchecked", $"FaceFX set {setEntry.InstancedFullPath} is an import");
            if (!sets.TryGetValue(setExport.UIndex, out var set))
                sets[setExport.UIndex] = set = (ObjectBinary.From<FaceFXAnimSet>(setExport), setExport.GetProperty<ArrayProperty<ObjectProperty>>("ReferencedSoundCues"));

            var line = set.Bin.Lines.FirstOrDefault(l => l.NameAsString.Equals(lineName, StringComparison.OrdinalIgnoreCase));
            if (line is null) return ("no-facefx", $"{setExport.ObjectName.Instanced} has no line {lineName}");
            if (line.Index < 0) return ("silent", null);
            if (set.Cues is null || line.Index >= set.Cues.Count)
                return ("broken", $"{lineName} uses sound cue {line.Index}; {setExport.ObjectName.Instanced} references {set.Cues?.Count ?? 0}");

            var cue = set.Cues[line.Index].ResolveToEntry(pcc);
            if (cue is null) return ("broken", $"{lineName}: ReferencedSoundCues[{line.Index}] is null");
            if (cue is not ExportEntry cueExport) return ("unchecked", $"cue {cue.InstancedFullPath} is an import");
            if (Wave(cueExport) is not { } wave) return ("broken", $"cue {cueExport.InstancedFullPath} leads to no SoundNodeWave");

            var (ok, problem) = HasSound(wave);
            if (problem is not null) return (ok ? "unchecked" : "broken", problem);
            return wave.ObjectName.Instanced.Contains($"{strref}", StringComparison.Ordinal) ? ("voiced", null) : ("other-voice", $"plays {wave.ObjectName.Instanced}");
        }

        private ExportEntry? Wave(ExportEntry cue)
        {
            var queue = new Queue<IEntry?>([cue.GetProperty<ObjectProperty>("FirstNode")?.ResolveToEntry(pcc)]);
            var seen = new HashSet<int>();
            while (queue.Count > 0)
            {
                if (queue.Dequeue() is not ExportEntry node || !seen.Add(node.UIndex)) continue;
                if (node.ClassName == "SoundNodeWave") return node;
                foreach (var child in node.GetProperty<ArrayProperty<ObjectProperty>>("ChildNodes")?.Select(c => c.ResolveToEntry(pcc)) ?? []) queue.Enqueue(child);
            }
            return null;
        }

        /// <summary>Embedded ISACT data, or the wave's name (after "ISB:"-style prefix) in its streaming data's content index.</summary>
        /// <returns>Problem null: has sound. Ok true with a problem: could not be checked.</returns>
        private (bool Ok, string? Problem) HasSound(ExportEntry wave)
        {
            if (ObjectBinary.From<SoundNodeWave>(wave).RawData.Length > 0) return (true, null);
            var data = wave.GetProperty<ObjectProperty>("BioStreamingData")?.ResolveToEntry(pcc);
            if (data is null) return (false, $"{wave.ObjectName.Instanced} has no audio (no embedded data, no BioStreamingData)");
            if (data is not ExportEntry streaming) return (true, $"streaming data {data.InstancedFullPath} is an import");

            if (!isactTitles.TryGetValue(streaming.UIndex, out var titles))
            {
                try
                {
                    var banks = ISACTHelper.GetPairedBanks(streaming.GetBinaryData()[4..]);
                    titles = banks.ICBBank.GetAllBankChunks().OfType<ContentIndexBankChunk>().SelectMany(c => c.IndexPages ?? [])
                                  .SelectMany(p => p.IndexEntries).Select(e => e.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception e) // LEC's bank reader throws plain Exception on a bank it does not know
                {
                    return (true, $"could not read the ISACT banks of {streaming.ObjectName.Instanced}: {e.Message}");
                }
                isactTitles[streaming.UIndex] = titles;
            }
            var name = wave.ObjectName.Instanced;
            var title = name[(name.IndexOf(':') + 1)..];
            return titles.Contains(title) ? (true, null) : (false, $"{title} is not in the ISACT index of {streaming.ObjectName.Instanced}");
        }
    }
}
