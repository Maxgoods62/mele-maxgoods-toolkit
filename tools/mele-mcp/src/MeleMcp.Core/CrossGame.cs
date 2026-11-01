using System.Collections.Concurrent;
using System.Text;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace MeleMcp.Core;

/// <summary>One game's copy. <paramref name="Diff"/> is against <paramref name="BaseGame"/>; the base game itself carries <paramref name="Text"/>.</summary>
public sealed record XGameEntry(
    string Game,
    bool Found,
    string? File,
    string? Path,
    string? WslPath,
    string? Source,
    int? Mount,
    int? UIndex,
    string? ClassName,
    string? ExportPath,
    string? BaseGame,
    bool? SameAsBase,
    int? Lines,
    int? LinesAdded,
    int? LinesRemoved,
    string? Text,
    string? Diff,
    string? Note);

public sealed record XGameResult(string Name, string Mode, string? BaseGame, IReadOnlyList<XGameEntry> Games, bool Capped, IReadOnlyList<string> Cuts);

/// <summary>The same script object in several games: where each winning file holds it, and how each differs from the first game that has it.</summary>
public static class CrossGame
{
    private const string NotDecompiled = "Could not decompile ";
    private const string DecompileError = "Error occurred while decompiling ";

    /// <summary>Most classes are in the first two; LEC's always-loaded list adds the rest of the game's core script files.</summary>
    private static readonly string[] FirstFiles = ["SFXGame.pcc", "Engine.pcc"];

    private static readonly string[] ScriptClasses = ["Class", "State", "ScriptStruct", "Enum", "Function"];

    public static XGameResult Compare(string name, IReadOnlyList<MEGame> games, string mode, string? package, bool deepSearch, int maxChars)
    {
        LecHost.EnsureInitialized();
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give a class, Class.Function or state name.");
        mode = mode.Trim().ToLowerInvariant();
        if (mode is not ("diff" or "full")) throw new ArgumentException($"Unsupported mode '{mode}'. Use diff or full.");
        if (games.Count == 0) throw new ArgumentException("Give at least one game, e.g. LE1,LE2.");
        if (maxChars < 1) throw new ArgumentException("maxChars must be positive.");

        var entries = new List<XGameEntry>();
        var bodies = new Dictionary<int, string[]>();
        foreach (var game in games)
        {
            var (entry, lines) = Read(game, name, package, deepSearch);
            if (lines is not null) bodies[entries.Count] = lines;
            entries.Add(entry);
        }

        int? first = bodies.Count > 0 ? bodies.Keys.Min() : null;
        if (first is { } baseIndex)
        {
            var baseEntry = entries[baseIndex];
            foreach (var (index, lines) in bodies.OrderBy(b => b.Key))
            {
                var isBase = index == baseIndex;
                var (diff, added, removed) = isBase ? (null, 0, 0) : UnifiedDiff.Make(bodies[baseIndex], lines, Label(baseEntry), Label(entries[index]));
                entries[index] = entries[index] with
                {
                    BaseGame = baseEntry.Game,
                    SameAsBase = isBase ? null : added == 0 && removed == 0,
                    LinesAdded = isBase ? null : added,
                    LinesRemoved = isBase ? null : removed,
                    Text = mode == "full" || isBase ? string.Join('\n', lines) : null,
                    Diff = mode == "diff" ? diff : null,
                };
            }
        }

        var cuts = new List<string>();
        if (mode == "full")
        {
            Fit(entries, TextSlots(entries), maxChars, cuts);
        }
        else
        {
            // The differences are what this mode is for: the base text gets what they leave.
            var left = Fit(entries, DiffSlots(entries), maxChars, cuts);
            Fit(entries, TextSlots(entries), left, cuts);
        }
        return new XGameResult(name, mode, first is { } b ? entries[b].Game : null, entries, cuts.Count > 0, cuts);
    }

    private static string Label(XGameEntry e) => $"{e.Game} {e.File} #{e.UIndex} {e.ExportPath}";

    private static (XGameEntry Entry, string[]? Lines) Read(MEGame game, string name, string? package, bool deepSearch)
    {
        var (found, note) = Locate(game, name, package, deepSearch);
        if (found is null)
        {
            return (new XGameEntry(game.ToString(), false, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, note), null);
        }

        var file = found.File;
        var entry = new XGameEntry(game.ToString(), true, file.File, file.Path, file.WslPath, file.Source, file.Mount, found.UIndex, found.ClassName, found.ExportPath,
            null, null, null, null, null, null, null, note);
        string text;
        try
        {
            text = ScriptDecompiler.Decompile(game, file.Path!, found.UIndex.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (entry with { Note = Join(note, $"Decompile failed: {ex.GetType().Name}: {ex.Message}") }, null);
        }

        // Decompile's first line is the export's own header, which differs in every game.
        var body = text[(text.IndexOf('\n') + 1)..].Replace("\r\n", "\n");
        if (body.StartsWith(NotDecompiled, StringComparison.Ordinal) || body.StartsWith(DecompileError, StringComparison.Ordinal))
        {
            return (entry with { Note = Join(note, CutAtLine(body, 0, 2000)) }, null);
        }
        var lines = body.Split('\n');
        return (entry with { Lines = lines.Length }, lines);
    }

    private static string? Join(string? a, string b) => a is null ? b : $"{a} {b}";

    private sealed record Hit(FoundFile File, int UIndex, string ClassName, string ExportPath);

    private static (Hit? Hit, string? Note) Locate(MEGame game, string name, string? package, bool deepSearch)
    {
        var winners = FileFinder.Find(game, "*.pcc", int.MaxValue).Files
            .Where(f => f.Path is not null)
            .ToDictionary(f => f.File, StringComparer.OrdinalIgnoreCase);

        if (package is not null)
        {
            var file = PackageFile(package, winners);
            if (file is null) return (null, $"{package} is not a file {game} loads, and not an existing path.");
            var (hit, note) = Search(file, name);
            return (hit, note ?? (hit is null ? $"Not in {file.File}." : null));
        }

        var core = FirstFiles.Concat(EntryImporter.FilesSafeToImportFrom(game)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var fileName in core)
        {
            if (!winners.TryGetValue(fileName, out var file)) continue;
            var (hit, note) = Search(file, name);
            if (hit is not null || note is not null) return (hit, note);
        }

        var searched = string.Join(", ", core.Where(winners.ContainsKey));
        if (!deepSearch) return (null, $"Not in the core script files ({searched}). deepSearch=true reads every package's tables.");

        var hits = new ConcurrentBag<(Hit? Hit, string? Note)>();
        var rest = winners.Values.Where(f => !core.Contains(f.File, StringComparer.OrdinalIgnoreCase)).ToList();
        Parallel.ForEach(rest, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
        {
            var found = Search(file, name);
            if (found.Hit is not null || found.Note is not null) hits.Add(found);
        });
        var inFile = hits.Where(h => h.Hit is not null).OrderBy(h => h.Hit!.File.File, StringComparer.OrdinalIgnoreCase).ToList();
        if (inFile.Count == 0)
        {
            return (null, hits.FirstOrDefault().Note ?? $"Not in the {winners.Count} packages {game} loads.");
        }
        var also = inFile.Skip(1).Select(h => h.Hit!.File.File).ToList();
        return (inFile[0].Hit, also.Count == 0 ? null : $"Also in: {string.Join(", ", also.Take(10))}{(also.Count > 10 ? $" and {also.Count - 10} more" : "")}.");
    }

    private static FoundFile? PackageFile(string package, Dictionary<string, FoundFile> winners)
    {
        var path = GamePaths.ToWindowsPath(package);
        if (File.Exists(path))
        {
            var full = Path.GetFullPath(path);
            return new FoundFile(Path.GetFileName(full), full, GamePaths.ToWslPath(full), null, null, [], null, null, []);
        }
        var fileName = Path.GetFileName(path);
        if (!Path.HasExtension(fileName)) fileName += ".pcc";
        return winners.GetValueOrDefault(fileName);
    }

    /// <summary>Reads only the file's tables: a hit is decompiled afterwards from a full load. A name with several best matches is a note, not a guess.</summary>
    private static (Hit? Hit, string? Note) Search(FoundFile file, string name)
    {
        using var pcc = MEPackageHandler.UnsafePartialLoad(file.Path!, _ => false);
        var best = Matches(pcc, name);
        var prefix = Path.GetFileNameWithoutExtension(file.File) + ".";
        if (best.Count == 0 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) best = Matches(pcc, name[prefix.Length..]);
        if (best.Count == 0) return (null, null);
        if (best.Count == 1)
        {
            var e = best[0];
            return (new Hit(file, e.UIndex, e.ClassName, e.InstancedFullPath), null);
        }
        var listed = string.Join(", ", best.Take(10).Select(e => $"#{e.UIndex} {e.ClassName} {e.InstancedFullPath}"));
        return (null, $"'{name}' is ambiguous in {file.File} ({best.Count} matches): {listed}{(best.Count > 10 ? ", ..." : "")}. Use Class.Name.");
    }

    /// <summary>
    /// The script exports <paramref name="name"/> means: by the end of the path, then Class.Function as a function of one of that class's states.
    /// Among several, the kind that ranks first in <see cref="ScriptClasses"/>: a bare name is a class before a state.
    /// </summary>
    private static List<ExportEntry> Matches(IMEPackage pcc, string name)
    {
        var last = name[(name.LastIndexOf('.') + 1)..];
        var named = pcc.Exports
            .Where(e => ScriptClasses.Contains(e.ClassName) && e.ObjectName.Instanced.Equals(last, StringComparison.OrdinalIgnoreCase))
            .Select(e => (Export: e, Path: e.InstancedFullPath))
            .ToList();

        var byPath = named.Where(e => e.Path.Equals(name, StringComparison.OrdinalIgnoreCase)
                                      || e.Path.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byPath.Count == 0 && last.Length < name.Length)
        {
            var owner = name[..^(last.Length + 1)] + ".";
            byPath = named.Where(e => e.Export.ClassName == "Function"
                                      && e.Path.Length > owner.Length
                                      && (e.Path.StartsWith(owner, StringComparison.OrdinalIgnoreCase) || e.Path.Contains("." + owner, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        if (byPath.Count == 0) return [];
        var rank = byPath.Min(e => Array.IndexOf(ScriptClasses, e.Export.ClassName));
        return byPath.Where(e => Array.IndexOf(ScriptClasses, e.Export.ClassName) == rank).Select(e => e.Export).ToList();
    }

    private readonly record struct Slot(int Index, bool IsDiff);

    private static List<Slot> DiffSlots(List<XGameEntry> entries) =>
        entries.Select((e, i) => (e, i)).Where(x => x.e.Diff is not null).Select(x => new Slot(x.i, true)).ToList();

    private static List<Slot> TextSlots(List<XGameEntry> entries) =>
        entries.Select((e, i) => (e, i)).Where(x => x.e.Text is not null).Select(x => new Slot(x.i, false)).ToList();

    /// <summary>Shares <paramref name="budget"/> evenly, the shortest first, so what one text does not use goes to the longer ones. Returns what is left.</summary>
    private static int Fit(List<XGameEntry> entries, List<Slot> slots, int budget, List<string> cuts)
    {
        string Get(Slot s) => (s.IsDiff ? entries[s.Index].Diff : entries[s.Index].Text)!;
        var left = slots.Count;
        foreach (var slot in slots.OrderBy(s => Get(s).Length).ToList())
        {
            var share = budget / left--;
            var text = Get(slot);
            if (text.Length > share)
            {
                var kept = CutAtLine(text, 0, share);
                entries[slot.Index] = slot.IsDiff ? entries[slot.Index] with { Diff = kept } : entries[slot.Index] with { Text = kept };
                cuts.Add($"{entries[slot.Index].Game} {(slot.IsDiff ? "diff" : "text")}: {kept.Length} of {text.Length} chars");
                text = kept;
            }
            budget -= text.Length;
        }
        return budget;
    }

    /// <summary>At most <paramref name="max"/> chars from <paramref name="start"/>, ending on a line end when the slice holds one.</summary>
    internal static string CutAtLine(string text, int start, int max)
    {
        if (start >= text.Length) return "";
        if (text.Length - start <= max) return text[start..];
        var lastBreak = max > 0 ? text.LastIndexOf('\n', start + max - 1, max) : -1;
        return lastBreak >= start ? text[start..(lastBreak + 1)] : text.Substring(start, max);
    }
}

/// <summary>Line diff in unified format: Myers' linear-space algorithm, so two different 20000-line classes stay cheap in memory.</summary>
internal sealed class UnifiedDiff
{
    private const int Context = 3;

    private readonly int[] a;
    private readonly int[] b;
    private readonly bool[] removed;
    private readonly bool[] added;
    private readonly int[] forward;
    private readonly int[] backward;
    private readonly int middle;

    private UnifiedDiff(int[] a, int[] b)
    {
        this.a = a;
        this.b = b;
        removed = new bool[a.Length];
        added = new bool[b.Length];
        middle = (a.Length + b.Length) / 2 + 2;
        forward = new int[2 * middle + 1];
        backward = new int[2 * middle + 1];
    }

    /// <summary>The diff text, or null when the lines are equal, with the counts of added and removed lines.</summary>
    public static (string? Diff, int Added, int Removed) Make(string[] from, string[] to, string fromLabel, string toLabel)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string line)
        {
            if (!ids.TryGetValue(line, out var id)) ids[line] = id = ids.Count;
            return id;
        }

        var diff = new UnifiedDiff(from.Select(Id).ToArray(), to.Select(Id).ToArray());
        diff.Compare(0, from.Length, 0, to.Length);
        var addedCount = diff.added.Count(x => x);
        var removedCount = diff.removed.Count(x => x);
        return addedCount + removedCount == 0
            ? (null, 0, 0)
            : (diff.Format(from, to, fromLabel, toLabel), addedCount, removedCount);
    }

    private void Compare(int aLow, int aHigh, int bLow, int bHigh)
    {
        while (aLow < aHigh && bLow < bHigh && a[aLow] == b[bLow]) { aLow++; bLow++; }
        while (aLow < aHigh && bLow < bHigh && a[aHigh - 1] == b[bHigh - 1]) { aHigh--; bHigh--; }
        if (aLow == aHigh)
        {
            for (var i = bLow; i < bHigh; i++) added[i] = true;
            return;
        }
        if (bLow == bHigh)
        {
            for (var i = aLow; i < aHigh; i++) removed[i] = true;
            return;
        }
        var (x, y) = MiddleSnake(aLow, aHigh, bLow, bHigh);
        Compare(aLow, x, bLow, y);
        Compare(x, aHigh, y, bHigh);
    }

    /// <summary>A point on a shortest edit path. With both ends already trimmed it is never a corner, so both sides of the split shrink.</summary>
    private (int X, int Y) MiddleSnake(int aLow, int aHigh, int bLow, int bHigh)
    {
        int n = aHigh - aLow, m = bHigh - bLow, delta = n - m;
        var odd = (delta & 1) != 0;
        forward[middle + 1] = 0;
        backward[middle + 1] = 0;
        for (var d = 0; ; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && forward[middle + k - 1] < forward[middle + k + 1]) ? forward[middle + k + 1] : forward[middle + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aLow + x] == b[bLow + y]) { x++; y++; }
                forward[middle + k] = x;
                var other = delta - k;
                if (odd && other > -d && other < d && x + backward[middle + other] >= n) return (aLow + x, bLow + y);
            }
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && backward[middle + k - 1] < backward[middle + k + 1]) ? backward[middle + k + 1] : backward[middle + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aHigh - 1 - x] == b[bHigh - 1 - y]) { x++; y++; }
                backward[middle + k] = x;
                var other = delta - k;
                if (!odd && other >= -d && other <= d && x + forward[middle + other] >= n) return (aLow + n - x, bLow + m - y);
            }
        }
    }

    private string Format(string[] from, string[] to, string fromLabel, string toLabel)
    {
        // Each row is one output line: (line in from or -1, line in to or -1).
        var rows = new List<(int From, int To)>(from.Length + to.Length);
        int i = 0, j = 0;
        while (i < from.Length || j < to.Length)
        {
            if (i < from.Length && removed[i]) rows.Add((i++, -1));
            else if (j < to.Length && added[j]) rows.Add((-1, j++));
            else rows.Add((i++, j++));
        }

        var sb = new StringBuilder();
        sb.Append("--- ").Append(fromLabel).Append('\n');
        sb.Append("+++ ").Append(toLabel).Append('\n');
        var at = 0;
        while (at < rows.Count)
        {
            if (rows[at].From >= 0 && rows[at].To >= 0) { at++; continue; }

            var start = Math.Max(0, at - Context);
            var end = at;
            for (var scan = at; scan < rows.Count && scan <= end + 2 * Context; scan++)
            {
                if (rows[scan].From < 0 || rows[scan].To < 0) end = scan;
            }
            var stop = Math.Min(rows.Count, end + 1 + Context);
            WriteHunk(sb, rows, start, stop, from, to);
            at = end + 1;
        }
        return sb.ToString();
    }

    private static void WriteHunk(StringBuilder sb, List<(int From, int To)> rows, int start, int stop, string[] from, string[] to)
    {
        int fromCount = 0, toCount = 0;
        int fromFirst = -1, toFirst = -1;
        for (var r = start; r < stop; r++)
        {
            if (rows[r].From >= 0) { if (fromCount++ == 0) fromFirst = rows[r].From; }
            if (rows[r].To >= 0) { if (toCount++ == 0) toFirst = rows[r].To; }
        }
        // An empty side names the line before the hunk, as diff -u does.
        if (fromCount == 0) fromFirst = PositionBefore(rows, start, r => r.From);
        if (toCount == 0) toFirst = PositionBefore(rows, start, r => r.To);
        sb.Append($"@@ -{fromFirst + 1},{fromCount} +{toFirst + 1},{toCount} @@\n");
        for (var r = start; r < stop; r++)
        {
            var (f, t) = rows[r];
            if (f >= 0 && t >= 0) sb.Append(' ').Append(from[f]);
            else if (f >= 0) sb.Append('-').Append(from[f]);
            else sb.Append('+').Append(to[t]);
            sb.Append('\n');
        }
    }

    private static int PositionBefore(List<(int From, int To)> rows, int start, Func<(int From, int To), int> side)
    {
        for (var r = start - 1; r >= 0; r--)
        {
            if (side(rows[r]) >= 0) return side(rows[r]);
        }
        return -1;
    }
}
