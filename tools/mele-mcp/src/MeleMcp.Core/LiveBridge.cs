using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <param name="Functions">The functions watched, as the game names them.</param>
/// <param name="Dropped">Events the bridge dropped because more than 10000 were waiting.</param>
/// <param name="Truncated">Stopped at maxEvents before the time was up.</param>
public sealed record LiveEvents(IReadOnlyList<string> Functions, IReadOnlyList<JsonNode> Events, long Dropped, bool Truncated);

/// <param name="Lines">What the command printed or logged, from the DebugLogger log, timestamps removed.</param>
public sealed record ConsoleOutput(string Command, IReadOnlyList<string> Lines, string? Note);

/// <summary>
/// Client of our own ASI, MeleBridge (tools/mele-bridge): newline-delimited JSON over \\.\pipe\MeleBridge_LE1, one reply line per
/// request line, each request run on the game thread. Any property of one live object, UnrealScript function calls, console
/// output, and a stream of ProcessEvent calls. Replies come back on the same pipe, so LEX can stay open.
/// </summary>
public static partial class LiveBridge
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string PipeName(MEGame game) => $"MeleBridge_{game}";

    /// <summary>Enumerating \\.\pipe\ does not connect.</summary>
    public static bool IsAvailable(MEGame game) =>
        GameBridge.IsGameRunning(game)
        && Directory.EnumerateFiles(@"\\.\pipe\").Any(p => p.EndsWith(@"\" + PipeName(game), StringComparison.OrdinalIgnoreCase));

    /// <summary>One request; the reply without its "ok". A refused request throws with the bridge's message.</summary>
    public static JsonObject Request(MEGame game, JsonObject request, double timeoutSeconds = 5) => RequestAll(game, [request], timeoutSeconds)[0];

    /// <summary>Several requests over one connection, in order.</summary>
    public static List<JsonObject> RequestAll(MEGame game, IReadOnlyList<JsonObject> requests, double timeoutSeconds = 5)
    {
        using var pipe = Connect(game);
        var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(pipe, Utf8);
        var replies = new List<JsonObject>();
        foreach (var request in requests)
        {
            // An absent key takes the bridge's default; a null one would be refused as the wrong type.
            foreach (var key in request.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) request.Remove(key);
            request["timeoutMs"] = (int)(timeoutSeconds * 1000);
            writer.WriteLine(request.ToJsonString());
            var line = ReadLine(reader, TimeSpan.FromSeconds(timeoutSeconds + 2))
                       ?? throw new IOException($"MeleBridge did not answer '{request["op"]}' within {timeoutSeconds + 2} s.");
            replies.Add(Check(line));
        }
        return replies;
    }

    /// <summary>
    /// Runs a console command and collects what it logged. LE1 hands no console output back (ConsoleCommand returns nothing, and
    /// DebugLogger's Logf hooks keep every line for its log), so the lines come from the DebugLogger log: the command, then a
    /// getall whose answer is logged as an error, which makes the logger write out everything before it.
    /// </summary>
    public static ConsoleOutput Console(MEGame game, string command, double timeoutSeconds)
    {
        string? log = null;
        long start = 0;
        try
        {
            log = DebugLog.LogPath(game);
            start = DebugLog.Length(log);
        }
        catch (FileNotFoundException)
        {
        }

        var isFlush = command.Trim().Equals(DebugLog.FlushCommand, StringComparison.OrdinalIgnoreCase);
        var requests = new List<JsonObject> { new() { ["op"] = "console", ["command"] = command } };
        if (log is not null && !isFlush) requests.Add(new JsonObject { ["op"] = "console", ["command"] = DebugLog.FlushCommand });
        RequestAll(game, requests, timeoutSeconds);
        if (log is null) return new ConsoleOutput(command, [], "Ran. No DebugLogger log, so no output: install the DebugLogger ASI to see it.");

        var lines = new List<string>();
        var clock = Stopwatch.StartNew();
        do
        {
            Thread.Sleep(100);
            lines = DebugLog.LinesAfter(log, start);
        } while (!lines.Any(DebugLog.IsFlushLine) && clock.Elapsed.TotalSeconds < 3);

        var output = lines.Where(l => isFlush || !DebugLog.IsFlushLine(l)).Select(l => LogPrefix().Replace(l, "")).Where(l => l.Length > 0).ToList();
        return new ConsoleOutput(command, output, lines.Any(DebugLog.IsFlushLine) ? null : "The log was not flushed within 3 s: info lines may still be missing.");
    }

    [GeneratedRegex(@"^\[[\d:.]+ \w+\] (?:appLogf: |appLogErrorf: )?")]
    private static partial Regex LogPrefix();

    /// <summary>Subscribes to ProcessEvent calls of these functions and collects them for a while.</summary>
    public static LiveEvents Watch(MEGame game, IReadOnlyList<string> functions, string? objectContains, bool withParams, double seconds, int maxEvents)
    {
        if (functions.Count == 0) throw new ArgumentException("Give at least one function, e.g. Engine.SequenceOp.Activated.");
        using var pipe = Connect(game);
        var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(pipe, Utf8);
        var request = new JsonObject
        {
            ["op"] = "subscribe",
            ["functions"] = new JsonArray(functions.Select(f => (JsonNode)f).ToArray()),
            ["objectContains"] = objectContains ?? "",
            ["params"] = withParams,
        };
        writer.WriteLine(request.ToJsonString());
        var ack = Check(ReadLine(reader, TimeSpan.FromSeconds(7)) ?? throw new IOException("MeleBridge did not answer the subscription."));
        var watched = ack["functions"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();

        var events = new List<JsonNode>();
        long dropped = 0;
        var clock = Stopwatch.StartNew();
        while (events.Count < maxEvents)
        {
            var left = TimeSpan.FromSeconds(seconds) - clock.Elapsed;
            if (left <= TimeSpan.Zero) break;
            var line = ReadLine(reader, left);
            if (line is null) break;
            var node = JsonNode.Parse(line)!.AsObject();
            if (node.ContainsKey("heartbeat")) continue;
            if (node["dropped"] is { } d)
            {
                dropped += d.GetValue<long>();
                continue;
            }
            events.Add(node);
        }
        return new LiveEvents(watched, events, dropped, events.Count >= maxEvents);
    }

    private static JsonObject Check(string line)
    {
        var reply = JsonNode.Parse(line)?.AsObject() ?? throw new IOException($"Bad reply from MeleBridge: {line}");
        if (reply["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(reply["error"]?.GetValue<string>() ?? line);
        reply.Remove("ok");
        reply.Remove("id");
        return reply;
    }

    /// <summary>Null when nothing came within the time (or the bridge closed the connection).</summary>
    private static string? ReadLine(StreamReader reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static NamedPipeClientStream Connect(MEGame game)
    {
        if (!GameBridge.IsGameRunning(game)) throw new InvalidOperationException($"{game} is not running.");
        if (!IsAvailable(game))
            throw new InvalidOperationException($"Pipe {PipeName(game)} not found: MeleBridge.asi is not installed (tools/mele-bridge/build.sh --install, game closed), or the game is still starting.");
        var pipe = new NamedPipeClientStream(".", PipeName(game), PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(3000);
        return pipe;
    }
}
