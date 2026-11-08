using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

/// <param name="Text">LEC's decompile.</param>
/// <param name="Hex">The bytes, when Text has lost part of the conditional: LEC prints an operator it does not know as ().</param>
public sealed record CndEntry(int Id, int Size, string? Text, string? Hex);

/// <param name="Id">Conditional id.</param>
/// <param name="Recompiled">What the recompiled bytes decompile to, when they differ from Text.</param>
/// <param name="Error">Why it did not decompile or compile.</param>
public sealed record CndRoundTripMiss(int Id, string? Text, string? Recompiled, string? Error);

/// <param name="ByteIdentical">Recompiled to the same bytes.</param>
/// <param name="SameMeaning">Other bytes, same conditional: its text differs only in 'X == false', which the compiler writes as 'X == Bool false'.</param>
/// <param name="OtherText">The recompiled bytes decompile to another text.</param>
/// <param name="Failed">Does not recompile: LEC's compiler rejects its own decompiler's text.</param>
public sealed record CndRoundTrip(int Total, int ByteIdentical, int SameMeaning, int OtherText, int Failed, IReadOnlyList<CndRoundTripMiss> Misses);

public sealed record CndListing(string Path, int EntryCount, IReadOnlyList<int> Ids, IReadOnlyList<CndEntry> Entries, CndRoundTrip? RoundTrip);

public sealed record CndEdit(string Path, int Id, bool FileCreated, string? OldText, string NewText, int Size, int EntryCount, string? BackupPath, IReadOnlyList<string> Warnings);

/// <summary>
/// Game 3 conditionals: <c>.cnd</c> files, each entry an id and its compiled expression. A DLC's own file is
/// <c>Conditionals&lt;DLC folder name&gt;.cnd</c> in its CookedPCConsole: the exe builds that name as it does <c>Default_&lt;DLC&gt;.bin</c>,
/// and DLC_MOD_Framework's config lists no other. Vanilla DLC name a second file, ConditionalsDLC_Shared.cnd, in BioEngine's
/// [AlternateNonPackageResources] ConditionalsFiles.
/// </summary>
public static partial class ConditionalFiles
{
    private const int MaxMisses = 25;

    // The decompiler prints a NOT as 'X == false'; the compiler rejects a bare false. Vanilla files also hold 'X == Bool false'.
    [GeneratedRegex(@"(?<!bool\s*)==\s*false\b", RegexOptions.IgnoreCase)]
    private static partial Regex BareFalse();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Every token the decompiler prints. LEC's parser loops forever on any other word and on a '-' before a non-number; it also treats
    // some non-ASCII letters as digits or matches them case-insensitively, so Compile takes ASCII only.
    [GeneratedRegex(@"\G\s*(?:\(|\)|&&|\|\||==|!=|<=|>=|<|>|\+|\*|plot\.(?:bools|ints|floats)\[\d+\]|bool\s+(?:true|false)\b|false\b|[iaf]-?\d+(?:\.\d+)?\b|-?\d+\b"
                    + @"|function\s*:\s*\w+\s+value\s*:\s*-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    public static CndListing Get(MEGame game, string fileOrPath, int? id, bool all, int limit, bool roundTrip)
    {
        RequireGame3(game);
        var cnd = CNDFile.FromFile(Resolve(game, fileOrPath));
        var entries = cnd.ConditionalEntries.OrderBy(e => e.ID).ToList();

        IEnumerable<CNDFile.ConditionalEntry> shown = [];
        if (id is { } wanted)
        {
            var one = entries.Find(e => e.ID == wanted) ?? throw new ArgumentException($"{cnd.FilePath} has no conditional {wanted}.");
            shown = [one];
        }
        else if (all)
        {
            shown = entries.Take(limit);
        }

        return new CndListing(cnd.FilePath, entries.Count, entries.Select(e => e.ID).ToList(),
            shown.Select(Show).ToList(),
            roundTrip ? RoundTrip(entries) : null);
    }

    /// <summary>Compiles one conditional into a mod's file, added or replaced. The file is created when it does not exist.</summary>
    public static CndEdit Set(MEGame game, string path, int id, string text)
    {
        RequireGame3(game);
        LecHost.EnsureInitialized();
        var full = GamePaths.ToWindowsPath(path);
        if (!Path.IsPathFullyQualified(full)) throw new ArgumentException($"'{path}' is not a full path: cnd_set writes a mod's file, never a loaded one by name.");
        full = Path.GetFullPath(full);
        if (!Path.GetExtension(full).Equals(".cnd", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{full} is not a .cnd file.");
        PackageWriter.EnsureWritable(game, full);

        var (data, newText) = CompileChecked(id, text);

        var created = !File.Exists(full);
        var cnd = created ? new CNDFile { FilePath = full, ConditionalEntries = [] } : CNDFile.FromFile(full);
        var old = cnd.ConditionalEntries.Find(e => e.ID == id);
        var oldText = old?.Decompile();
        if (old is null) cnd.ConditionalEntries.Add(new CNDFile.ConditionalEntry { ID = id, Data = data });
        else old.Data = data;

        var backup = created ? null : PackageWriter.Backup(full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Save(cnd, full);
        return new CndEdit(full, id, created, oldText, newText, data.Length, cnd.ConditionalEntries.Count, backup, NameWarnings(full));
    }

    /// <summary>
    /// LEC's compiler checks little: it drops what follows an expression without parentheses, returns nothing for operators mixed
    /// at one level, and writes every '-' as '+'. So the bytes count only when they decompile to the text that went in.
    /// </summary>
    private static (byte[] Data, string Text) CompileChecked(int id, string text)
    {
        byte[] data;
        try
        {
            data = Compile(text);
        }
        catch (Exception e)
        {
            throw new ArgumentException($"Conditional {id} does not compile: {e.Message}", e);
        }
        var stored = data.Length == 0 ? "" : new CNDFile.ConditionalEntry { ID = id, Data = data }.Decompile();
        if (Normal(stored) != Normal(text))
        {
            throw new ArgumentException($"Conditional {id}: the compiler kept '{stored}' of '{text}'. Nothing written. Put each comparison and each run of one operator "
                                        + "in its own parentheses, e.g. ((plot.bools[1] == Bool true) && (plot.ints[2] >= i3)). '-', '/' and fractional floats do not compile.");
        }
        return (data, stored);
    }

    private const int MaxTextLength = 64 * 1024;
    private const int MaxParenDepth = 200;

    private static byte[] Compile(string text)
    {
        if (!Ascii.IsValid(text)) throw new ArgumentException("Conditional text is ASCII only.");
        if (text.Length > MaxTextLength) throw new ArgumentException($"Conditional text is {text.Length} characters: the limit is {MaxTextLength}.");
        // LEC's parser recurses per parenthesis, and .NET can't catch a stack overflow: it would end the whole server.
        var open = 0;
        foreach (var c in text)
        {
            if (c == '(' && ++open > MaxParenDepth) throw new ArgumentException($"Parentheses nest deeper than {MaxParenDepth} levels.");
            if (c == ')') open--;
        }
        var at = 0;
        while (at < text.Length && !text.AsSpan(at).IsWhiteSpace())
        {
            var token = Token().Match(text, at);
            if (!token.Success) throw new ArgumentException($"Unknown token at character {at + 1}: '{text[at..].TrimStart()}'");
            at += token.Length;
        }
        if (Spaces().Replace(text, "").Contains("()")) throw new ArgumentException("() stands for an operator LEC cannot decompile, and it does not compile.");
        return ME3ConditionalsCompiler.Compile(BareFalse().Replace(text, "== Bool false"));
    }

    private static string Normal(string text) => BareFalse().Replace(Spaces().Replace(text, ""), "==boolfalse").ToLowerInvariant();

    /// <summary>
    /// Writes a .cnd through <see cref="AtomicFile"/>: LEC's ToFile opens the file with FileMode.Create, so a write cut off part
    /// way would leave the mod an empty or half file. src/BannedSymbols.txt keeps ToFile here.
    /// </summary>
    [SuppressMessage("ApiDesign", "RS0030", Justification = "ToFile writes AtomicFile's temporary file, never the target.")]
    private static void Save(CNDFile cnd, string path) => AtomicFile.Write(path, temporary => cnd.ToFile(temporary));

    private static string Resolve(MEGame game, string fileOrPath)
    {
        LecHost.EnsureInitialized();
        var candidate = GamePaths.ToWindowsPath(fileOrPath);
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        var name = Path.GetFileName(candidate);
        if (!Path.HasExtension(name)) name += ".cnd";
        var loaded = GamePaths.LoadedFilesWith(game, ".cnd");
        return loaded.TryGetValue(name, out var found) ? found : throw new FileNotFoundException($"'{fileOrPath}' is not an existing path and no file named '{name}' is loaded by {game}.");
    }

    private static CndRoundTrip RoundTrip(IReadOnlyList<CNDFile.ConditionalEntry> entries)
    {
        int identical = 0, sameMeaning = 0, otherText = 0, failed = 0;
        var misses = new List<CndRoundTripMiss>();
        void Miss(CndRoundTripMiss miss)
        {
            if (misses.Count < MaxMisses) misses.Add(miss);
        }

        foreach (var entry in entries)
        {
            string? text = null;
            try
            {
                text = entry.Decompile();
                var bytes = Compile(text);
                if (bytes.AsSpan().SequenceEqual(entry.Data))
                {
                    identical++;
                    continue;
                }
                var again = new CNDFile.ConditionalEntry { ID = entry.ID, Data = bytes }.Decompile();
                if (Normal(again) == Normal(text))
                {
                    sameMeaning++;
                    continue;
                }
                otherText++;
                Miss(new CndRoundTripMiss(entry.ID, text, again, null));
            }
            catch (Exception e)
            {
                failed++;
                Miss(new CndRoundTripMiss(entry.ID, text, null, e.Message));
            }
        }
        return new CndRoundTrip(entries.Count, identical, sameMeaning, otherText, failed, misses);
    }

    private static CndEntry Show(CNDFile.ConditionalEntry entry)
    {
        var text = entry.Decompile();
        return new CndEntry(entry.ID, entry.Data.Length, text, text.Contains("()") ? Convert.ToHexString(entry.Data) : null);
    }

    /// <summary>The game finds a DLC's own conditionals by name: under any other name the file is written but not loaded.</summary>
    private static List<string> NameWarnings(string full)
    {
        var name = Path.GetFileName(full);
        var folder = Path.GetDirectoryName(full)!;
        var dlc = Path.GetFileName(Path.GetDirectoryName(folder));
        if (!Path.GetFileName(folder).Equals("CookedPCConsole", StringComparison.OrdinalIgnoreCase) || dlc?.StartsWith("DLC_", StringComparison.OrdinalIgnoreCase) != true)
        {
            return [$"{full} is not in a DLC_<name>\\CookedPCConsole folder, where the game looks for Conditionals<DLC folder name>.cnd."];
        }
        return name.Equals($"Conditionals{dlc}.cnd", StringComparison.OrdinalIgnoreCase)
            ? []
            : [$"{dlc} loads Conditionals{dlc}.cnd by name; {name} loads only if the DLC's config lists it in [AlternateNonPackageResources] ConditionalsFiles."];
    }

    private static void RequireGame3(MEGame game)
    {
        if (!game.IsGame3()) throw new ArgumentException($".cnd files are LE3 only. {game} conditionals are F<id> functions of BioAutoConditionals: read them with export_decompile, change them with script_compile.");
    }
}
