using System.Diagnostics;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <param name="Level">The object's package: its level for an actor, e.g. BIOA_MYM01.</param>
/// <param name="Name">Its object name, e.g. BioPawn_4: the actor name game_actor_get takes.</param>
public sealed record LiveValue(string Class, string Level, string Name, string Path, string Value);

public sealed record LiveValues(string Command, int Count, IReadOnlyList<LiveValue> Values, string? Note);

/// <summary>
/// Live property values read with the engine's console command getall, whose answer the DebugLogger ASI writes to its log:
/// one line per object, "appLogErrorf: 0) BioPawn BIOA_MYM01.TheWorld:PersistentLevel.BioPawn_5.bTickIsDisabled = False"; an array
/// ends its line at "=" and puts each element on a line of its own, "appLogErrorf: \t0: (...)". A wrong name logs "Unrecognized
/// property" (or class). No reply comes through the interop pipe, so the log is read from where it stood before the command.
/// </summary>
public static partial class LiveProperties
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);

    [GeneratedRegex(@"^appLogErrorf: \d+\) (?<class>\S+) (?<path>\S+)\.(?<prop>\w+) =(?: (?<value>.*))?$")]
    private static partial Regex GetAllLine();

    [GeneratedRegex(@"^appLogErrorf: \t(?<element>\d+: .*)$")]
    private static partial Regex ElementLine();

    [GeneratedRegex(@"^appLogf: (?<what>Unrecognized \w+ \w+)")]
    private static partial Regex Unrecognized();

    [GeneratedRegex(@"^\[[\d:.]+ \w+\] ")]
    private static partial Regex Stamp();

    internal static bool IsGetAllLine(string text) => GetAllLine().IsMatch(text) || ElementLine().IsMatch(text);

    /// <param name="className">Objects of this class and its subclasses (getall does not list class defaults).</param>
    /// <param name="nameContains">Keeps the objects whose path contains this, e.g. a level or an object name.</param>
    public static LiveValues Get(MEGame game, string className, string property, string? nameContains, double timeoutSeconds)
    {
        if (!Regex.IsMatch(className, @"^\w+$") || !Regex.IsMatch(property, @"^\w+$"))
            throw new ArgumentException("Class and property are single names, e.g. BioPawn and bTickIsDisabled.");
        if (!GameBridge.IsGameRunning(game)) throw new InvalidOperationException($"{game} is not running.");

        var path = DebugLog.LogPath(game);
        var start = DebugLog.Length(path);
        var command = $"getall {className} {property}";
        GameLink.Console(game, command);

        // The answer comes within a frame or two, one line per object: done once lines came and two polls brought no new one.
        var values = new List<LiveValue>();
        string? unrecognized = null;
        var quietPolls = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds && unrecognized is null && quietPolls < 2)
        {
            Thread.Sleep(Poll);
            (var found, unrecognized) = Parse(DebugLog.LinesAfter(path, start), property);
            quietPolls = found.Count > 0 && found.Count == values.Count ? quietPolls + 1 : 0;
            values = found;
        }

        var shown = values.Where(v => string.IsNullOrEmpty(nameContains) || v.Path.Contains(nameContains, StringComparison.OrdinalIgnoreCase)).ToList();
        var note = unrecognized is not null ? $"The game says: {unrecognized} (names are exact)."
            : values.Count == 0 ? $"No answer within {timeoutSeconds} s: no live object of class {className}, or the game has not run the command yet (a load screen)."
            : null;
        return new LiveValues(command, shown.Count, shown, note);
    }

    /// <summary>The objects answered so far (an array's elements joined by new lines), or the game's "Unrecognized ..." line.</summary>
    private static (List<LiveValue> Values, string? Unrecognized) Parse(List<string> lines, string property)
    {
        var values = new List<LiveValue>();
        var inAnswer = false;
        foreach (var raw in lines)
        {
            var line = Stamp().Replace(raw, "");
            if (Unrecognized().Match(line) is { Success: true } u) return (values, u.Groups["what"].Value);
            if (ElementLine().Match(line) is { Success: true } e && inAnswer)
            {
                var last = values[^1];
                values[^1] = last with { Value = last.Value.Length == 0 ? e.Groups["element"].Value : $"{last.Value}\n{e.Groups["element"].Value}" };
                continue;
            }
            var m = GetAllLine().Match(line);
            inAnswer = m.Success && m.Groups["prop"].Value.Equals(property, StringComparison.OrdinalIgnoreCase);
            if (!inAnswer) continue;
            var objectPath = m.Groups["path"].Value;
            var level = objectPath.Split('.')[0];
            var name = objectPath[(objectPath.LastIndexOfAny(['.', ':']) + 1)..];
            values.Add(new LiveValue(m.Groups["class"].Value, level, name, objectPath, m.Groups["value"].Value));
        }
        return (values, null);
    }
}
