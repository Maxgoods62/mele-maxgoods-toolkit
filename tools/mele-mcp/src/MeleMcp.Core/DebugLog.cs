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
        LecHost.EnsureInitialized();
        var gamePath = MEDirectories.GetDefaultGamePath(game) ?? throw new InvalidOperationException($"No {game} install found.");
        var path = Path.Combine(gamePath, "Binaries", "Win64", "Logs", "DebugLogger.log");
        if (!File.Exists(path)) throw new FileNotFoundException($"No {path}: is the DebugLogger ASI installed, and has the game started since?");

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

    private static bool IsProblem(Entry e) =>
        e.Level is "error" or "critical" or "warning" || e.Text.Contains("Accessed None", StringComparison.Ordinal);

    private static string Shorten(string text) => text.Length <= MaxEntryLength ? text : text[..MaxEntryLength] + " …";

    /// <summary>Read while the game writes it: shared, not locked.</summary>
    private static List<Entry> Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
