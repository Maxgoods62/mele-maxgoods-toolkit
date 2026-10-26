using System.Text.RegularExpressions;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <param name="From">Time of the first entry read: the map load when one was asked for.</param>
public sealed record DebugLogResult(string Path, string? From, int Read, int Matches, IReadOnlyList<string> Entries);

/// <summary>
/// The DebugLogger ASI's log (Binaries\Win64\Logs\DebugLogger.log): every UnrealScript LogInternal, warning and script
/// error, from game start until it closes; the next start rewrites it. An entry is a timestamped line plus the untimed
/// lines after it (a script error's arguments and locals).
/// </summary>
public static partial class DebugLog
{
    private const int MaxEntryLength = 1500;

    [GeneratedRegex(@"^\[(?<time>[\d:.]+) (?<level>\w+)\] ")]
    private static partial Regex EntryStart();

    private sealed record Entry(string Time, string Level, string Text);

    /// <param name="sinceMap">Start at the last load of this map (its persistent level file name).</param>
    /// <param name="errorsOnly">Only errors, warnings and Accessed None.</param>
    /// <param name="limit">The last entries that match, at most this many.</param>
    public static DebugLogResult Read(MEGame game, string? sinceMap, string? contains, bool errorsOnly, int limit)
    {
        var path = LogPath(game);
        var entries = Parse(path);
        var start = 0;
        if (!string.IsNullOrWhiteSpace(sinceMap))
        {
            var load = $"Loading package synchronously: {sinceMap.Trim()}";
            start = entries.FindLastIndex(e => e.Text.Equals(load, StringComparison.OrdinalIgnoreCase));
            if (start < 0) throw new ArgumentException($"{sinceMap} was not loaded in this game session (no '{load}' line).");
        }

        var read = entries.Skip(start).ToList();
        var matches = read.Where(e => (!errorsOnly || IsProblem(e))
                                      && (string.IsNullOrEmpty(contains) || e.Text.Contains(contains, StringComparison.OrdinalIgnoreCase))).ToList();
        var shown = matches.Skip(Math.Max(0, matches.Count - Math.Max(1, limit)))
                           .Select(e => Shorten($"[{e.Time} {e.Level}] {e.Text}")).ToList();
        return new DebugLogResult(path, read.FirstOrDefault()?.Time, read.Count, matches.Count, shown);
    }

    internal static string LogPath(MEGame game) => AsiLog(game, "DebugLogger");

    /// <summary>The log an ASI writes in Binaries\Win64\Logs\&lt;asi&gt;.log, rewritten at each game start.</summary>
    internal static string AsiLog(MEGame game, string asi)
    {
        LecHost.EnsureInitialized();
        var gamePath = MEDirectories.GetDefaultGamePath(game) ?? throw new InvalidOperationException($"No {game} install found.");
        var path = Path.Combine(gamePath, "Binaries", "Win64", "Logs", $"{asi}.log");
        if (!File.Exists(path)) throw new FileNotFoundException($"No {path}: is the {asi} ASI installed, and has the game started since?");
        return path;
    }

    /// <summary>
    /// Makes the DebugLogger write out what it holds: it flushes on warnings and errors, otherwise only every 5 s, so a
    /// LogInternal answer can sit in its buffer. A getall answer is logged as an error, which flushes everything before it.
    /// </summary>
    internal static void Flush(MEGame game) => GameLink.Console(game, FlushCommand);

    internal const string FlushCommand = "getall BioWorldInfo bHasPathNodes";

    /// <summary>A line of FlushCommand's answer.</summary>
    internal static bool IsFlushLine(string line) =>
        line.Contains("appLogErrorf: ", StringComparison.Ordinal)
        && (line.EndsWith(".bHasPathNodes = True", StringComparison.Ordinal) || line.EndsWith(".bHasPathNodes = False", StringComparison.Ordinal));

    /// <summary>The log's size now: where the next lines will start.</summary>
    internal static long Length(string path) => new FileInfo(path).Length;

    /// <summary>The whole lines written after <paramref name="offset"/>.</summary>
    internal static List<string> LinesAfter(string path, long offset)
    {
        using var stream = Open(path);
        stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>Errors and warnings, but not getall answers (game_get), which the engine logs as errors.</summary>
    private static bool IsProblem(Entry e) =>
        (e.Level is "error" or "critical" or "warning" && !LiveProperties.IsGetAllLine(e.Text))
        || e.Text.Contains("Accessed None", StringComparison.Ordinal);

    /// <summary>Read while the game writes it: shared, not locked.</summary>
    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string Shorten(string text) => text.Length <= MaxEntryLength ? text : text[..MaxEntryLength] + " …";

    private static List<Entry> Parse(string path)
    {
        using var stream = Open(path);
        using var reader = new StreamReader(stream);
        var entries = new List<Entry>();
        while (reader.ReadLine() is { } line)
        {
            var m = EntryStart().Match(line);
            if (m.Success)
            {
                entries.Add(new Entry(m.Groups["time"].Value, m.Groups["level"].Value, line[m.Length..]));
            }
            else if (line.Trim().Length > 0 && entries.Count > 0)
            {
                var last = entries[^1];
                entries[^1] = last with { Text = $"{last.Text}\n{line.Trim()}" };
            }
        }
        return entries;
    }
}
