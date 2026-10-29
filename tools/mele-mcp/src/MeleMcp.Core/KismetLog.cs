using System.Globalization;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <param name="Time">Seconds since the ASI started (game start), as logged; the first one when grouped.</param>
/// <param name="Level">The op's package, e.g. BIOA_MYM01.</param>
/// <param name="Object">Its instanced full path in that package, e.g. TheWorld.PersistentLevel.Main_Sequence.SeqAct_Delay_0: what export_get takes.</param>
/// <param name="Pin">The input link that had the impulse; none for ops activated natively.</param>
/// <param name="Count">Activations: 1, or how many when grouped.</param>
public sealed record KismetStep(string Time, string Class, string Level, string Object, string? Pin, int Count);

/// <param name="Read">Activations in the window read (whole session, or the last seconds asked for).</param>
public sealed record KismetLogResult(string Path, int Read, int Matches, IReadOnlyList<KismetStep> Steps);

/// <summary>
/// The Kismet Logger ASI's log (Binaries\Win64\Logs\KismetLogger.log): one line per sequence op activation, in order,
/// "[0012.345] SeqAct_Delay BIOA_MYM01.TheWorld:PersistentLevel.Main_Sequence.SeqAct_Delay_0 Start"; rewritten at each game start.
/// </summary>
public static partial class KismetLog
{
    [GeneratedRegex(@"^\[(?<time>\d+\.\d+)\] (?<class>\S+) (?<path>\S+)(?: (?<pin>.+))?$")]
    private static partial Regex Line();

    /// <param name="contains">Only activations whose class, path or pin contains this (case-insensitive).</param>
    /// <param name="lastSeconds">Only the activations of the last seconds of the log (before its last line), e.g. right after doing something in game.</param>
    /// <param name="grouped">One step per op and pin, in order of first activation, with its count.</param>
    /// <param name="limit">The last matching steps, at most this many.</param>
    public static KismetLogResult Read(MEGame game, string? contains, double? lastSeconds, bool grouped, int limit)
    {
        var path = DebugLog.AsiLog(game, "KismetLogger");
        var steps = Parse(path);
        if (lastSeconds is { } window && steps.Count > 0)
        {
            var from = Seconds(steps[^1]) - window;
            steps = steps.Where(s => Seconds(s) >= from).ToList();
        }

        var matches = steps.Where(s => string.IsNullOrEmpty(contains)
                                       || $"{s.Class} {s.Level}.{s.Object} {s.Pin}".Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();
        if (grouped)
            matches = matches.GroupBy(s => (s.Class, s.Level, s.Object, s.Pin))
                             .Select(g => g.First() with { Count = g.Count() }).ToList();
        var shown = matches.Skip(Math.Max(0, matches.Count - Math.Max(1, limit))).ToList();
        return new KismetLogResult(path, steps.Count, matches.Count, shown);
    }

    /// <summary>Every activation of the current or last game session, or none when the ASI has never written its log.</summary>
    internal static (string Path, List<KismetStep> Steps)? TryReadAll(MEGame game)
    {
        try
        {
            var path = DebugLog.AsiLog(game, "KismetLogger");
            return (path, Parse(path));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static double Seconds(KismetStep s) => double.Parse(s.Time, CultureInfo.InvariantCulture);

    private static List<KismetStep> Parse(string path)
    {
        using var stream = DebugLog.Open(path);
        using var reader = new StreamReader(stream);
        var steps = new List<KismetStep>();
        while (reader.ReadLine() is { } line)
        {
            var m = Line().Match(line);
            if (!m.Success) continue;
            // The engine writes "Package.TheWorld:PersistentLevel.X"; LEC's instanced paths use dots only.
            var full = m.Groups["path"].Value;
            var dot = full.IndexOf('.');
            var level = dot < 0 ? "" : full[..dot];
            var obj = full[(dot + 1)..].Replace(':', '.');
            var pin = m.Groups["pin"].Success ? m.Groups["pin"].Value : null;
            steps.Add(new KismetStep(m.Groups["time"].Value, m.Groups["class"].Value, level, obj, pin, 1));
        }
        return steps;
    }
}
