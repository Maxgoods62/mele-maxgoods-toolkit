using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.PlotDatabase.Databases;
using LegendaryExplorerCore.PlotDatabase.PlotElements;

namespace MeleMcp.Core;

/// <param name="Name">Its path in LEX's plot databases (base game, then mod databases in ModPlotsLE1), when it has one.</param>
/// <param name="Was">The value before a set.</param>
public sealed record PlotValue(string Kind, int Id, string? Name, string Value, string? Was = null);

/// <param name="Missing">Ids the game did not answer for within the timeout.</param>
/// <param name="Via">MeleBridge, or the cheat execs read back from the DebugLogger log.</param>
public sealed record PlotValues(IReadOnlyList<PlotValue> Values, IReadOnlyList<int> Missing, string? Note, string Via = "cheat execs");

/// <summary>
/// LE1 plot variables in the running game. With our MeleBridge ASI: the plot table's own GetBool/SetBool (and Int, Float),
/// answered on the pipe. Without it: BioWare's cheat execs DumpPlotManagerValueByIndex and InitPlotManagerValueByIndex
/// (BioCheatManager), which write their answer with LogInternal, which the DebugLogger ASI logs, e.g. "PlotIndex(8421) Boolean(True)";
/// those are info lines, which the DebugLogger writes out only on a warning or every 5 s, so the wait flushes it each second.
/// Either way a set runs no transition or consequence: only the value changes.
/// </summary>
public static partial class LivePlot
{
    private const int MaxIds = 50;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(1);

    [GeneratedRegex(@"PlotIndex\((?<id>\d+)\) (?:Boolean|Int|Float)\((?<value>[^)]*)\)")]
    private static partial Regex Answer();

    [GeneratedRegex(@"PlotManagerVariable\((?<id>\d+)\) (?:Boolean|Int|Float)\((?<value>[^)]*)\)")]
    private static partial Regex SetAnswer();

    /// <param name="kind">bool, int or float.</param>
    /// <param name="nameContains">Finds ids by name in LEX's plot databases, e.g. "Wrex" or "Paragon".</param>
    /// <param name="value">Sets this value after reading the current one (one id only): True/False or 1/0 for a bool, a number otherwise.</param>
    public static PlotValues Run(MEGame game, string kind, IReadOnlyList<int> ids, string? nameContains, string? value, double timeoutSeconds)
    {
        if (!game.IsGame1()) throw new ArgumentException("game_plot handles LE1.");
        kind = kind.Trim().ToLowerInvariant();
        if (kind is not ("bool" or "int" or "float")) throw new ArgumentException("kind is bool, int or float.");
        if (!GameBridge.IsGameRunning(game)) throw new InvalidOperationException($"{game} is not running.");

        var names = Names(kind);
        var wanted = ids.ToList();
        if (!string.IsNullOrWhiteSpace(nameContains))
            wanted.AddRange(names.Where(kv => kv.Value.Contains(nameContains, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key));
        wanted = wanted.Distinct().ToList();
        if (wanted.Count == 0) throw new ArgumentException(nameContains is null ? "Give ids or a name." : $"No {kind} named like '{nameContains}' in LEX's plot databases.");
        if (wanted.Count > MaxIds) throw new ArgumentException($"{wanted.Count} {kind}s match; narrow it down (at most {MaxIds}).");

        if (value is not null && wanted.Count != 1)
            throw new ArgumentException($"Set one {kind} at a time ({wanted.Count} match: {string.Join(", ", wanted.Take(10))}).");
        var number = value is null ? null : Number(kind, value);
        if (LiveBridge.IsAvailable(game)) return ThroughBridge(game, kind, wanted, names, number, timeoutSeconds);

        // Read first, then set: commands sent together can run in any order (see GameBridge.Send), so each waits for its answer.
        var log = DebugLog.LogPath(game);
        var start = DebugLog.Length(log);
        foreach (var id in wanted) GameLink.Console(game, $"DumpPlotManagerValueByIndex {id} {kind}");
        var answers = new Dictionary<int, string>();
        Await(game, log, start, Answer(), answers, wanted.Count, timeoutSeconds);

        var missing = wanted.Where(id => !answers.ContainsKey(id)).ToList();
        if (number is not null && missing.Count == 0)
        {
            var id = wanted[0];
            start = DebugLog.Length(log);
            GameLink.Console(game, $"InitPlotManagerValueByIndex {id} {kind} {number}");
            var set = new Dictionary<int, string>();
            if (!Await(game, log, start, SetAnswer(), set, 1, timeoutSeconds))
                return new PlotValues([new PlotValue(kind, id, names.GetValueOrDefault(id), answers[id])], [id],
                    $"The game did not confirm the set within {timeoutSeconds} s: the command was sent, so it may still apply once the game runs it. Read the id again before you set it again.");
            return new PlotValues([new PlotValue(kind, id, names.GetValueOrDefault(id), set[id], answers[id])], [], null);
        }

        var values = wanted.Where(answers.ContainsKey).Select(id => new PlotValue(kind, id, names.GetValueOrDefault(id), answers[id])).ToList();
        var note = missing.Count == 0 ? null
            : values.Count == 0 ? "No answer: is a save loaded (the cheat manager exists only in game), and is the DebugLogger ASI installed?"
            : $"{missing.Count} ids not answered within {timeoutSeconds} s.";
        if (number is not null && missing.Count > 0) note += " Nothing was set.";
        return new PlotValues(values, missing, note);
    }

    /// <summary>Calls on the one BioGlobalVariableTable (a subobject of BioWorldInfo's defaults), reads then set, over one connection.</summary>
    private static PlotValues ThroughBridge(MEGame game, string kind, List<int> wanted, Dictionary<int, string> names, string? number, double timeoutSeconds)
    {
        var found = LiveBridge.Request(game, new JsonObject { ["op"] = "find", ["class"] = "BioGlobalVariableTable", ["limit"] = 1 }, timeoutSeconds);
        var table = found["objects"]!.AsArray().FirstOrDefault()?["path"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("No BioGlobalVariableTable is loaded.");
        var type = kind switch { "bool" => "Bool", "int" => "Int", _ => "Float" };
        JsonObject Call(string function, params JsonNode?[] args) =>
            new() { ["op"] = "call", ["object"] = table, ["function"] = function, ["args"] = new JsonArray(args) };

        var reads = LiveBridge.RequestAll(game, wanted.Select(id => Call("Get" + type, id)).ToList(), timeoutSeconds);
        var values = wanted.Select((id, i) => new PlotValue(kind, id, names.GetValueOrDefault(id), Text(reads[i]["return"]))).ToList();
        if (number is null) return new PlotValues(values, [], null, "MeleBridge");

        var setId = wanted[0];
        JsonNode newValue = kind switch
        {
            "bool" => number == "1",
            "int" => int.Parse(number, CultureInfo.InvariantCulture),
            _ => NumberText.Double(number),
        };
        // SetBool's third argument (bEvaluateAchievement) stays false: no achievement check.
        var set = kind == "bool" ? Call("SetBool", setId, newValue, false) : Call("Set" + type, setId, newValue);
        var after = LiveBridge.RequestAll(game, [set, Call("Get" + type, setId)], timeoutSeconds)[1];
        return new PlotValues([values[0] with { Value = Text(after["return"]), Was = values[0].Value }], [], null, "MeleBridge");
    }

    /// <summary>Same text as the cheat execs print: True/False, 272, 0.5.</summary>
    private static string Text(JsonNode? value) => value?.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => "True",
        System.Text.Json.JsonValueKind.False => "False",
        _ => value?.ToJsonString() ?? "",
    };

    /// <summary>Collects id → value from the log lines after <paramref name="offset"/> until <paramref name="count"/> ids answered.</summary>
    private static bool Await(MEGame game, string log, long offset, Regex answer, Dictionary<int, string> found, int count, double timeoutSeconds)
    {
        var clock = Stopwatch.StartNew();
        var flushed = -FlushEvery;
        while (true)
        {
            if (clock.Elapsed - flushed >= FlushEvery)
            {
                DebugLog.Flush(game);
                flushed = clock.Elapsed;
            }
            Thread.Sleep(Poll);
            foreach (Match m in DebugLog.LinesAfter(log, offset).Select(l => answer.Match(l)).Where(m => m.Success))
                found[int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture)] = m.Groups["value"].Value;
            if (found.Count >= count) return true;
            if (clock.Elapsed.TotalSeconds >= timeoutSeconds) return false;
        }
    }

    /// <summary>The console takes a float for every kind: True/False become 1/0.</summary>
    internal static string Number(string kind, string value)
    {
        var v = value.Trim();
        if (kind == "bool" && bool.TryParse(v, out var b)) return b ? "1" : "0";
        if (!NumberText.TryDouble(v, out var d)) throw new ArgumentException($"'{value}' is not a {kind} value.");
        if (kind == "int" && d != Math.Floor(d)) throw new ArgumentException($"'{value}' is not an int.");
        return d.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Id → path, from the base game's database and each mod database in LEX's ModPlotsLE1 folder (later ones win).</summary>
    private static Dictionary<int, string> Names(string kind)
    {
        LecHost.EnsureInitialized();
        var names = new Dictionary<int, string>();
        void Add(PlotDatabaseBase db)
        {
            IEnumerable<KeyValuePair<int, PlotElement>> elements = kind switch
            {
                "bool" => db.Bools.Select(kv => new KeyValuePair<int, PlotElement>(kv.Key, kv.Value)),
                "int" => db.Ints,
                _ => db.Floats,
            };
            foreach (var (id, e) in elements) names[id] = e.Path;
        }

        Add(PlotDatabases.Le1PlotDatabase);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LegendaryExplorer", "ModPlotsLE1");
        foreach (var json in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json") : [])
        {
            try
            {
                var db = new ModPlotDatabase { Game = MEGame.LE1 };
                db.LoadPlotsFromFile(json);
                Add(db);
            }
            catch (Exception e) when (e is IOException or FormatException or InvalidOperationException or Newtonsoft.Json.JsonException)
            {
                // An unreadable mod database only loses its names.
            }
        }
        return names;
    }
}
