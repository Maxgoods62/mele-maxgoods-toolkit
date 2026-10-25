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

/// <summary>Game operations built on <see cref="GameBridge"/>, using the LEX Interop commands the LE1 ASI handles.</summary>
public static class GameLink
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static GameStatus Status(MEGame game)
    {
        var running = GameBridge.IsGameRunning(game);
        var pipe = running && GameBridge.IsPipeOpen(game);
        var windows = GameBridge.ReplyWindowCount();
        var replies = false;
        string? problem = null;
        if (!running) problem = $"{game} is not running.";
        else if (!pipe) problem = "Interop pipe missing: install the LEX Interop ASI (M3 > ASI Manager), then restart the game.";
        else
        {
            try
            {
                GameBridge.SendAndWait(game, "LLE_TEST_ACTIVE", m => m == "LIVELEVELEDITOR READY", ReplyTimeout);
                GameBridge.Send(game, "LLE_DEACTIVATE");
                replies = true;
            }
            catch (TimeoutException) { problem = "Commands reach the game but no reply came back."; }
        }
        if (windows > 1) problem = (problem is null ? "" : problem + " ") + "Another 'Legendary Explorer' window is open: close LEX, it may take the replies.";
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
    /// moved since then, fall back to the last report this server saw.
    /// </summary>
    public static PlayerLocation PlayerLocation(MEGame game)
    {
        static bool IsGps(string m) => m.StartsWith("PATHFINDING_GPS PLAYERLOC=", StringComparison.Ordinal);
        string? reply;
        var fresh = true;
        try
        {
            reply = GameBridge.SendAndWait(game, "ACTIVATE_PLAYERGPS", IsGps, TimeSpan.FromSeconds(1.5));
        }
        catch (TimeoutException)
        {
            fresh = false;
            reply = GameBridge.LastReply(game, IsGps)
                ?? throw new TimeoutException("The game only reports the player's position after it changes. Move Shepard one step and retry.");
        }
        finally
        {
            GameBridge.Send(game, "DEACTIVATE_PLAYERGPS");
        }

        // PATHFINDING_GPS PLAYERLOC=x,y,z, PLAYERROT=pitch,yaw,roll
        var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var loc = parts[1]["PLAYERLOC=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var rot = parts.FirstOrDefault(p => p.StartsWith("PLAYERROT=", StringComparison.Ordinal))?["PLAYERROT=".Length..].Split(',') ?? ["0", "0", "0"];
        return new PlayerLocation(F(loc[0]), F(loc[1]), F(loc[2]), I(rot[0]), I(rot[1]), I(rot[2]), fresh);
    }

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

    public static ActorState GetActor(MEGame game, string level, string actor)
    {
        Select(game, level, actor);
        return ReadSelected(game, level, actor);
    }

    /// <summary>Moves, turns or hides one actor in the running game. Not saved: write the result into the package to keep it.</summary>
    public static ActorState SetActor(MEGame game, string level, string actor, float? x, float? y, float? z, int? pitch, int? yaw, int? roll, bool? hidden)
    {
        Select(game, level, actor);
        var current = ReadSelected(game, level, actor);
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
        return ReadSelected(game, level, actor);
    }

    /// <summary>
    /// Swaps a streamed level in the running game for a package file (e.g. the mod's library copy, not yet installed).
    /// The interop ASI serves precached bytes only for names it cannot find on disk, so the copy goes in under a new name
    /// (<c>&lt;level&gt;_LIVE&lt;hhmmss&gt;</c>) after the original and any earlier live copy are streamed out.
    /// The game's own stream triggers can bring the original back when the player moves to another area.
    /// </summary>
    public static HotReloadResult HotReload(MEGame game, string package, string? level)
    {
        var path = GamePaths.ResolvePackage(game, package);
        var original = level ?? Path.GetFileNameWithoutExtension(path);
        var liveName = $"{original}_LIVE{DateTime.Now:HHmmss}";
        var bytes = File.ReadAllBytes(path);
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
        steps.Add($"PRECACHE_FILE {liveName} {bytes.Length}");
        GameBridge.Send(game, $"STREAMLEVELIN {liveName}");
        steps.Add($"STREAMLEVELIN {liveName}");
        return new HotReloadResult(liveName, path, bytes.Length, steps);
    }

    /// <summary>LLE_TEST_ACTIVE resets the ASI's level list, so its next HUD tick sends every visible level with its actors.</summary>
    private static string ReadLevelsJson(MEGame game)
    {
        try
        {
            var reply = GameBridge.SendAndWait(game, "LLE_TEST_ACTIVE",
                m => m.StartsWith("LIVELEVELEDITOR LEVELSUPDATE ", StringComparison.Ordinal), TimeSpan.FromSeconds(6));
            return reply["LIVELEVELEDITOR LEVELSUPDATE ".Length..];
        }
        finally
        {
            GameBridge.Send(game, "LLE_DEACTIVATE");
        }
    }

    private static void Select(MEGame game, string level, string actor)
    {
        var reply = GameBridge.SendAndWait(game, $"LLE_SELECT_ACTOR {level} {actor} -1",
            m => m.StartsWith("LIVELEVELEDITOR ACTORSELECTED", StringComparison.Ordinal), ReplyTimeout);
        if (reply.EndsWith("NOTFOUND", StringComparison.Ordinal))
        {
            throw new ArgumentException($"No actor '{actor}' in loaded level '{level}'. Use game_actors to list what is loaded.");
        }
    }

    private static ActorState ReadSelected(MEGame game, string level, string actor)
    {
        // LIVELEVELEDITOR ACTORLOC x y z ACTORROT p y r ACTORSCALE s sx sy sz HIDDEN 0|1
        var t = GameBridge.SendAndWait(game, "LLE_GET_ACTOR_POSDATA",
            m => m.StartsWith("LIVELEVELEDITOR ACTORLOC ", StringComparison.Ordinal), ReplyTimeout).Split(' ');
        int At(string key) => Array.IndexOf(t, key) is var i and >= 0 ? i : throw new FormatException($"'{key}' missing in position reply.");
        var (loc, rot, scale, hid) = (At("ACTORLOC"), At("ACTORROT"), At("ACTORSCALE"), At("HIDDEN"));
        return new ActorState(level, actor, F(t[loc + 1]), F(t[loc + 2]), F(t[loc + 3]),
            I(t[rot + 1]), I(t[rot + 2]), I(t[rot + 3]), F(t[scale + 1]), t[hid + 1] == "1");
    }

    private static float F(string s) => float.Parse(s, Inv);
    private static int I(string s) => int.Parse(s, Inv);
}
