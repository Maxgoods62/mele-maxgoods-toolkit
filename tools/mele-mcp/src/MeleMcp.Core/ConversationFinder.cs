using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

/// <param name="Node">E&lt;n&gt; (entry: NPC or henchman line) or R&lt;n&gt; (reply: Shepard), as conv_read and conv YAML files name them.</param>
/// <param name="Speaker">Entry speaker tag (m_SpeakerList, e.g. hench_asari), "owner" (whoever owns the conversation) or "player".</param>
/// <param name="Wheel">A reply's paraphrase on the dialogue wheel.</param>
/// <param name="Plot">Its plot gate and effect as conv_read shows them, e.g. "if cond 415(-1)", "then transition 123(-1)".</param>
/// <param name="Female">LE2/LE3: the line's text for a female Shepard, where it differs from Text.</param>
/// <param name="WheelFemale">The same for Wheel.</param>
public sealed record ConvLine(string File, string Conversation, int UIndex, string Node, string Speaker, int StrRef, string Text, string? Wheel, string? Plot,
                              string? Female = null, string? WheelFemale = null);

/// <param name="Skipped">Packages that could not be read, with the reason.</param>
public sealed record ConvFindResult(int FilesScanned, int Conversations, int Matches, IReadOnlyList<ConvLine> Lines, IReadOnlyList<string> Skipped);

/// <summary>
/// Searches every conversation the game loads (base game, DLC and mods) for lines by speaker, text or plot id. Only the asked
/// language's _LOC_&lt;LANG&gt; packages are read. An LE1 conversation has its text in that package (a mod's conversation kept in its level
/// reads the global strings); LE2/LE3 text comes from the .tlk files.
/// </summary>
public static partial class ConversationFinder
{
    [GeneratedRegex(@"_LOC_(?<lang>[A-Z]+)\.pcc$", RegexOptions.IgnoreCase)]
    private static partial Regex LocFile();

    /// <param name="speaker">Substring of the speaker tag, or owner / player.</param>
    /// <param name="plotId">A conditional, plot bool or transition id the line uses.</param>
    public static ConvFindResult Find(MEGame game, string? speaker, string? text, int? plotId, string? conversation,
                                      string fileFilter, string language, int limit)
    {
        LecHost.EnsureInitialized();
        var lang = game.IsGame1() ? language.Trim().ToUpperInvariant() : TalkTables.Language(language);
        var files = MELoadedFiles.GetFilesLoadedInGame(game)
            .Where(kv => kv.Key.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase)
                         && FileSystemName.MatchesSimpleExpression(fileFilter, kv.Key, ignoreCase: true)
                         && LocFile().Match(kv.Key) is var m && (!m.Success || m.Groups["lang"].Value.Equals(lang, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var global = new Lazy<(Dictionary<int, string> Male, Dictionary<int, string> Female)>(() => game.IsGame1() ? (GlobalStrings.Load(game, lang), []) : TalkTables.Load(game, lang));
        var lines = new ConcurrentBag<ConvLine>();
        var skipped = new ConcurrentBag<string>();
        var conversations = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, kv =>
        {
            try
            {
                using var names = MEPackageHandler.UnsafePartialLoad(kv.Value, _ => false);
                if (!names.Names.Contains("BioConversation")) return;
                using var pcc = MEPackageHandler.UnsafePartialLoad(kv.Value, e => e.ClassName is "BioConversation" or "BioTlkFile");
                foreach (var conv in pcc.Exports.Where(e => e.ClassName == "BioConversation"))
                {
                    if (!string.IsNullOrEmpty(conversation) && !conv.ObjectName.Instanced.Contains(conversation, StringComparison.OrdinalIgnoreCase)) continue;
                    Interlocked.Increment(ref conversations);
                    foreach (var line in Lines(game, kv.Key, pcc, conv, lang, global))
                    {
                        if (Matches(line, speaker, text, plotId)) lines.Add(line);
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
            {
                skipped.Add($"{kv.Key}: {e.GetType().Name}: {e.Message}");
            }
        });

        var sorted = lines.OrderBy(l => l.File, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.UIndex)
                          .ThenBy(l => l.Node[0]).ThenBy(l => int.Parse(l.Node[1..])).ToList();
        return new ConvFindResult(files.Count, conversations, sorted.Count, sorted.Take(Math.Max(1, limit)).ToList(),
                                  skipped.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool Matches(ConvLine line, string? speaker, string? text, int? plotId) =>
        (string.IsNullOrEmpty(speaker) || line.Speaker.Contains(speaker, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrEmpty(text) || line.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
                                       || (line.Wheel?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                                       || (line.Female?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                                       || (line.WheelFemale?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
        && (plotId is not { } id || line.Plot is { } plot && Regex.IsMatch(plot, $@"\b{id}\("));

    private static IEnumerable<ConvLine> Lines(MEGame game, string file, IMEPackage pcc, ExportEntry conv, string lang,
                                               Lazy<(Dictionary<int, string> Male, Dictionary<int, string> Female)> global)
    {
        var props = conv.GetProperties();
        var (tlk, _) = ConversationReader.FindTalkFile(pcc, props, lang);
        string Text(int id) => id <= 0 ? ""
            : tlk?.FindDataById(id, returnNullIfNotFound: true, noQuotes: true) ?? global.Value.Male.GetValueOrDefault(id, "<no text>");
        string? Female(int id) => tlk is null && id > 0 ? global.Value.Female.GetValueOrDefault(id) : null;

        var speakers = ConversationReader.SpeakerTags(game, props);
        var entries = ConversationReader.Structs(props, "m_EntryList");
        var wheel = new Dictionary<int, int>();
        foreach (var r in entries.SelectMany(e => ConversationReader.Structs(e.Properties, "ReplyListNew")))
        {
            wheel.TryAdd(r.GetProp<IntProperty>("nIndex")?.Value ?? -1, r.GetProp<StringRefProperty>("srParaphrase")?.Value ?? 0);
        }

        string? Plot(StructProperty node) => ConversationReader.Plot(node).Trim() is { Length: > 0 } p ? p : null;
        for (var i = 0; i < entries.Count; i++)
        {
            var index = entries[i].GetProp<IntProperty>("nSpeakerIndex")?.Value ?? -1;
            var who = index >= 0 && index < speakers.Count ? speakers[index] : index == -2 ? "player" : "owner";
            var id = entries[i].GetProp<StringRefProperty>("srText")?.Value ?? 0;
            yield return new ConvLine(file, conv.ObjectName.Instanced, conv.UIndex, $"E{i}", who, id, Text(id), null, Plot(entries[i]), Female(id));
        }

        var replies = ConversationReader.Structs(props, "m_ReplyList");
        for (var i = 0; i < replies.Count; i++)
        {
            var id = replies[i].GetProp<StringRefProperty>("srText")?.Value ?? 0;
            var para = wheel.GetValueOrDefault(i);
            yield return new ConvLine(file, conv.ObjectName.Instanced, conv.UIndex, $"R{i}", "player", id, Text(id),
                                      para > 0 ? Text(para) : null, Plot(replies[i]), Female(id), Female(para));
        }
    }
}
