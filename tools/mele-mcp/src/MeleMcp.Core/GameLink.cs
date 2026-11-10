using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

public sealed record GameStatus(string Game, bool Running, bool InteropPipe, bool RepliesReachUs, int ReplyWindows, string? Problem);

public sealed record PlayerLocation(float X, float Y, float Z, int Pitch, int Yaw, int Roll, bool Fresh);

public sealed record LiveActor(string Level, string Name, string? Tag, int Components);

public sealed record LiveActors(IReadOnlyList<string> Levels, int TotalActors, int Matches, IReadOnlyList<LiveActor> Actors);

public sealed record ActorState(string Level, string Actor, float X, float Y, float Z, int Pitch, int Yaw, int Roll, float DrawScale, bool Hidden);

public sealed record HotReloadResult(string Level, string SourcePath, int Bytes, IReadOnlyList<string> Steps);

/// <summary>Game operations built on <see cref="GameBridge"/>, using the commands the LEX Interop ASI handles.</summary>
public static class GameLink
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string LleOff = "LLE_DEACTIVATE", GpsOff = "DEACTIVATE_PLAYERGPS", Ready = "LIVELEVELEDITOR READY";

    /// <summary>
    /// What this server may have left in one game's LEX Interop ASI. Two modes stay on until their deactivate arrives: the live
    /// level editor (LLE_TEST_ACTIVE) and the player GPS (ACTIVATE_PLAYERGPS). And replies carry no request id, so the late
    /// reply to a request that timed out would answer the next request of its kind (the last actor's position read as this
    /// one's). Read and changed only inside <see cref="Sequence{T}"/>, which holds the game's LEX Interop lock. The mark of a
    /// request that timed out is the machine's, not this server's: it is in <see cref="LexLinkShared"/>, since the late reply
    /// can reach any server that holds the reply window next.
    /// </summary>
    private sealed class LinkState
    {
        /// <summary>
        /// Deactivates still owed, sent first by the next sequence. Both at server start: a server ended by stop-server.ps1
        /// (Stop-Process -Force) runs no finally, so what it turned on stays on. Sending one with its mode off does no harm: LEX
        /// sends both whenever its Live Level Editor closes.
        /// </summary>
        public readonly HashSet<string> Owed = [LleOff, GpsOff];
    }

    private static readonly ConcurrentDictionary<MEGame, LinkState> States = new();

    /// <summary>
    /// What all servers share about the link (see <see cref="LexLinkShared"/>). Not a plain Lazy: that would keep a first
    /// failure for the life of the server.
    /// </summary>
    private static readonly Lazy<LexLinkShared> Shared = new(() => new LexLinkShared(LexLinkShared.SectionName), LazyThreadSafetyMode.PublicationOnly);

    public static GameStatus Status(MEGame game)
    {
        var running = GameBridge.IsGameRunning(game);
        var pipe = running && GameBridge.IsPipeOpen(game);
        var windows = GameBridge.ReplyWindowCount();
        var replies = false;
        string? problem = null;
        if (!running) problem = $"{game} is not running.";
        else if (!pipe) problem = "Interop pipe missing: install the LEX Interop ASI from Legendary Explorer (Live Level Editor, requirements), then restart the game.";
        else
        {
            try
            {
                replies = Sequence(game, state =>
                {
                    state.Owed.Add(LleOff);
                    try
                    {
                        Ask(game, state, "LLE_TEST_ACTIVE", m => m == Ready, ReplyTimeout);
                    }
                    finally
                    {
                        // Also after a timeout: the commands reached the game, so the mode is on.
                        Deactivate(game, state, LleOff);
                    }
                    return true;
                });
            }
            catch (TimeoutException) { problem = "Commands reach the game but no reply came back."; }
        }
        if (windows > 1)
        {
            problem = (problem is null ? "" : problem + " ")
                      + "Another window titled 'Legendary Explorer' is open and may take the replies: close LEX, and restart any mele-mcp server started before this version (in Claude Code: /mcp).";
        }
        return new GameStatus(game.ToString(), running, pipe, replies, windows, problem);
    }

    public static void Console(MEGame game, string command) => GameBridge.Send(game, $"CONSOLECOMMAND {command}");

    public static void RemoteEvent(MEGame game, string eventName) => GameBridge.Send(game, $"REMOTEEVENT {eventName}");

    public static void StreamLevel(MEGame game, string level, string mode) => GameBridge.Send(game, mode.ToLowerInvariant() switch
    {
        "in" => $"STREAMLEVELIN {level}",
        "out" => $"STREAMLEVELOUT {level}",
        "only" => $"ONLYLOADLEVEL {level}",
        _ => throw new ArgumentException("mode must be in, out or only."),
    });

    /// <summary>
    /// The ASI reports the player's position on PlayerTick, but only when it changed since its last report. If Shepard has not
    /// moved since then, fall back to the last report any server on this machine got from this game process.
    /// </summary>
    public static PlayerLocation PlayerLocation(MEGame game) => Sequence(game, state =>
    {
        static bool IsGps(string m) => m.StartsWith("PATHFINDING_GPS PLAYERLOC=", StringComparison.Ordinal);
        string? reply;
        var fresh = true;
        state.Owed.Add(GpsOff);
        try
        {
            // Not Ask: no reply while Shepard stands still is how the GPS works, and a late report is still where he stood.
            reply = GameBridge.SendAndWait(game, "ACTIVATE_PLAYERGPS", IsGps, TimeSpan.FromSeconds(1.5));
        }
        catch (TimeoutException)
        {
            fresh = false;
            reply = Shared.Value.GetPlayerGps(game)
                ?? throw new TimeoutException("The game only reports the player's position after it changes. Move Shepard one step and retry.");
        }
        finally
        {
            Deactivate(game, state, GpsOff);
        }
        // For every server: the game does not repeat a report, whoever asks next. The last one this window got, as more may
        // have come after the first.
        if (fresh) Shared.Value.SetPlayerGps(game, GameBridge.LastReply(game, IsGps) ?? reply);

        // PATHFINDING_GPS PLAYERLOC=x,y,z, PLAYERROT=pitch,yaw,roll
        var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var loc = parts[1]["PLAYERLOC=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var rot = parts.FirstOrDefault(p => p.StartsWith("PLAYERROT=", StringComparison.Ordinal))?["PLAYERROT=".Length..].Split(',') ?? ["0", "0", "0"];
        return new PlayerLocation(F(loc[0]), F(loc[1]), F(loc[2]), I(rot[0]), I(rot[1]), I(rot[2]), fresh);
    });

    public static LiveActors Actors(MEGame game, string? levelContains, string? nameContains, string? tagContains, int limit)
    {
        var levels = new List<string>();
        var all = new List<LiveActor>();
        using var doc = JsonDocument.Parse(ReadLevelsJson(game));
        foreach (var level in doc.RootElement.EnumerateArray())
        {
            var levelName = level.GetProperty("Name").GetString() ?? "";
            levels.Add(levelName);
            foreach (var actor in level.GetProperty("Actors").EnumerateArray())
            {
                var tag = actor.TryGetProperty("Tag", out var t) ? t.GetString() : null;
                var components = actor.TryGetProperty("ActorComponents", out var c) ? c.GetArrayLength()
                    : actor.TryGetProperty("StaticMeshCollectionComponents", out var s) ? s.GetArrayLength() : 0;
                all.Add(new LiveActor(levelName, actor.GetProperty("Name").GetString() ?? "", tag, components));
            }
        }

        static bool Has(string? value, string? part) => part is null || (value?.Contains(part, StringComparison.OrdinalIgnoreCase) ?? false);
        var matches = all.Where(a => Has(a.Level, levelContains) && Has(a.Name, nameContains) && Has(a.Tag, tagContains)).ToList();
        return new LiveActors(levels, all.Count, matches.Count, matches.Take(limit).ToList());
    }

    public static ActorState GetActor(MEGame game, string level, string actor) => Sequence(game, state =>
    {
        Select(game, state, level, actor);
        return ReadSelected(game, state, level, actor);
    });

    /// <summary>
    /// Moves, turns or hides one actor in the running game. Not saved: write the result into the package to keep it. The updates
    /// go to the selected actor, so select, read and update are one sequence (<see cref="Sequence{T}"/>).
    /// </summary>
    public static ActorState SetActor(MEGame game, string level, string actor, float? x, float? y, float? z, int? pitch, int? yaw, int? roll, bool? hidden) => Sequence(game, state =>
    {
        Select(game, state, level, actor);
        var current = ReadSelected(game, state, level, actor);
        if (x is not null || y is not null || z is not null)
        {
            GameBridge.Send(game, string.Create(Inv, $"LLE_UPDATE_ACTOR_POS {x ?? current.X} {y ?? current.Y} {z ?? current.Z}"));
        }
        if (pitch is not null || yaw is not null || roll is not null)
        {
            GameBridge.Send(game, $"LLE_UPDATE_ACTOR_ROT {pitch ?? current.Pitch} {yaw ?? current.Yaw} {roll ?? current.Roll}");
        }
        if (hidden is not null) GameBridge.Send(game, $"LLE_SET_ACTOR_HIDDEN {(hidden.Value ? "true" : "false")}");

        // Updates are queued for the game thread while position reads answer at once, so give the game a few frames.
        Thread.Sleep(300);
        return ReadSelected(game, state, level, actor);
    });

    /// <summary>
    /// Swaps a streamed level in the running game for a package file (e.g. the mod's library copy, not yet installed).
    /// The interop ASI serves precached bytes only for names it cannot find on disk, so the copy goes in under a new name
    /// (<c>&lt;level&gt;_LIVE&lt;hhmmss&gt;</c>) after the original and any earlier live copy are streamed out.
    /// The game's own stream triggers can bring the original back when the player moves to another area.
    /// The bytes must be uncompressed (as LEX sends them): the engine reads a precached package through a memory reader that
    /// cannot decompress, falls back to opening the file by name, and since that file is not on disk the load never finishes.
    /// </summary>
    public static HotReloadResult HotReload(MEGame game, string package, string? level)
    {
        string path;
        byte[] bytes;
        using (var pcc = PackageInspector.Open(game, package))
        {
            path = pcc.FilePath;
            using var uncompressed = pcc.SaveToStream(compress: false);
            bytes = uncompressed.ToArray();
        }
        var original = level ?? Path.GetFileNameWithoutExtension(path);
        var liveName = $"{original}_LIVE{DateTime.Now:HHmmss}";
        var steps = new List<string>();

        using (var doc = JsonDocument.Parse(ReadLevelsJson(game)))
        {
            var visible = doc.RootElement.EnumerateArray().Select(l => l.GetProperty("Name").GetString() ?? "");
            foreach (var name in visible.Where(n => n.Equals(original, StringComparison.OrdinalIgnoreCase)
                                                   || n.StartsWith(original + "_LIVE", StringComparison.OrdinalIgnoreCase)))
            {
                GameBridge.Send(game, $"STREAMLEVELOUT {name}");
                steps.Add($"STREAMLEVELOUT {name}");
            }
        }
        GameBridge.SendPackage(game, liveName, bytes);
        steps.Add($"PRECACHE_FILE {liveName} {bytes.Length} (uncompressed)");
        // The ASI adds the bytes to the precache map from a game-thread action; LEX waits the same 500 ms before streaming.
        Thread.Sleep(500);
        GameBridge.Send(game, $"STREAMLEVELIN {liveName}");
        steps.Add($"STREAMLEVELIN {liveName}");
        return new HotReloadResult(liveName, path, bytes.Length, steps);
    }

    /// <summary>LLE_TEST_ACTIVE resets the ASI's level list, so its next HUD tick sends every visible level with its actors.</summary>
    private static string ReadLevelsJson(MEGame game) => Sequence(game, state =>
    {
        state.Owed.Add(LleOff);
        try
        {
            var reply = Ask(game, state, "LLE_TEST_ACTIVE",
                m => m.StartsWith("LIVELEVELEDITOR LEVELSUPDATE ", StringComparison.Ordinal), TimeSpan.FromSeconds(6));
            return reply["LIVELEVELEDITOR LEVELSUPDATE ".Length..];
        }
        finally
        {
            Deactivate(game, state, LleOff);
        }
    });

    /// <summary>
    /// Runs requests that belong together (select an actor, then read or move it) with no other call's in between: one lock per
    /// game, across threads and mele-mcp processes (<see cref="GameLock.ForLexInterop"/>), so another call's LLE_SELECT_ACTOR
    /// cannot retarget this one's updates. Inside it, the reply window (<see cref="GameBridge.HoldReplies"/>), one holder for the
    /// whole machine across threads and mele-mcp processes, so the game's replies come to this server for the whole sequence.
    /// Servers deployed before the locks take none. First binds the shared state to the running game process, sends the
    /// deactivates an earlier call left owed, then fences when a request timed out, in this server or another.
    /// </summary>
    private static T Sequence<T>(MEGame game, Func<LinkState, T> body)
    {
        using var held = GameLock.ForLexInterop(game);
        // The games answer the one window titled "Legendary Explorer": ours carries the title for this sequence only, so the
        // servers of other sessions get their turn.
        using var replies = GameBridge.HoldReplies();
        var state = States.GetOrAdd(game, _ => new LinkState());
        if (GameBridge.RunningProcess(game) is { } process) Shared.Value.Bind(game, process.Id, process.Started);
        foreach (var off in state.Owed.ToList()) Deactivate(game, state, off);
        if (Shared.Value.GetUnsettled(game)) Fence(game, state);
        return body(state);
    }

    /// <summary>
    /// A request whose reply answers it alone. Marked unsettled before the send and settled by the reply: a timeout leaves the
    /// mark, and so does a server stopped while it waits, or a send that failed part-way. The next sequence then fences first,
    /// in whichever server it runs.
    /// </summary>
    private static string Ask(MEGame game, LinkState state, string command, Func<string, bool> isReply, TimeSpan timeout)
    {
        Shared.Value.SetUnsettled(game, true);
        var reply = GameBridge.SendAndWait(game, command, isReply, timeout);
        Shared.Value.SetUnsettled(game, false);
        return reply;
    }

    /// <summary>
    /// After a timeout: LLE_TEST_ACTIVE, and its READY. The ASI runs its queue in order (console commands aside, see
    /// GameBridge.MinGapMs), so a late reply to the request that timed out comes before this READY, and the requests sent after
    /// it get their own replies. Not covered: when the READY that ends a fence answers an earlier LLE_TEST_ACTIVE that timed out,
    /// the fence's own READY is still to come and can end the next fence too early. That takes two stalls in a row.
    /// </summary>
    private static void Fence(MEGame game, LinkState state)
    {
        state.Owed.Add(LleOff);
        try
        {
            GameBridge.SendAndWait(game, "LLE_TEST_ACTIVE", m => m == Ready, ReplyTimeout);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"An earlier request got no reply, and the game still does not answer. {e.Message}", e);
        }
        finally
        {
            Deactivate(game, state, LleOff);
        }
        Shared.Value.SetUnsettled(game, false);
    }

    /// <summary>Sends a mode's deactivate; one that does not go out (game gone, pipe busy) stays owed. Never throws: it runs in finally blocks.</summary>
    private static void Deactivate(MEGame game, LinkState state, string off)
    {
        try
        {
            GameBridge.Send(game, off);
            state.Owed.Remove(off);
        }
        catch (Exception)
        {
            // Owed still: the next sequence sends it first.
        }
    }

    private static void Select(MEGame game, LinkState state, string level, string actor)
    {
        var reply = Ask(game, state, $"LLE_SELECT_ACTOR {level} {actor} -1",
            m => m.StartsWith("LIVELEVELEDITOR ACTORSELECTED", StringComparison.Ordinal), ReplyTimeout);
        if (reply.EndsWith("NOTFOUND", StringComparison.Ordinal))
        {
            throw new ArgumentException($"No actor '{actor}' in loaded level '{level}'. Use game_actors to list what is loaded.");
        }
    }

    private static ActorState ReadSelected(MEGame game, LinkState state, string level, string actor)
    {
        // LIVELEVELEDITOR ACTORLOC x y z ACTORROT p y r ACTORSCALE s sx sy sz HIDDEN 0|1
        var t = Ask(game, state, "LLE_GET_ACTOR_POSDATA",
            m => m.StartsWith("LIVELEVELEDITOR ACTORLOC ", StringComparison.Ordinal), ReplyTimeout).Split(' ');
        int At(string key) => Array.IndexOf(t, key) is var i and >= 0 ? i : throw new FormatException($"'{key}' missing in position reply.");
        var (loc, rot, scale, hid) = (At("ACTORLOC"), At("ACTORROT"), At("ACTORSCALE"), At("HIDDEN"));
        return new ActorState(level, actor, F(t[loc + 1]), F(t[loc + 2]), F(t[loc + 3]),
            I(t[rot + 1]), I(t[rot + 2]), I(t[rot + 3]), F(t[scale + 1]), t[hid + 1] == "1");
    }

    private static float F(string s) => float.Parse(s, Inv);
    private static int I(string s) => int.Parse(s, Inv);
}
