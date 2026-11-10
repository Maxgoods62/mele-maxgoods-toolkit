using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <param name="Ready">The game reached the menu (and the map, when one was loaded or awaited).</param>
/// <param name="Map">The last map the game loaded, from the DebugLogger log.</param>
/// <param name="Steps">What happened, with seconds since the start.</param>
public sealed record GameCycleResult(bool Ready, string? Map, IReadOnlyList<string> Steps, ModCheckResult? Check, ModInstallResult? Install, Screenshot? Screenshot);

/// <param name="Closed">No process of the game is left.</param>
/// <param name="Steps">What happened, with seconds since the start.</param>
public sealed record GameCloseResult(bool Closed, IReadOnlyList<string> Steps);

/// <summary>
/// The test loop in one call: quit the game (console "exit"), check and install the mod, boot through M3 once the install is
/// verified, wait for the main menu, load a save (LE1: cheat exec BioLoadGame; LE2 and LE3: QuickLoad, the latest save), run
/// console commands once each map load before them has ended, wait for the map, take a screenshot. Progress is read from the
/// DebugLogger ASI's log, which every game start rewrites. Every wait after the boot ends when the game closes, and the stop
/// names the phase.
/// </summary>
public static partial class GameCycle
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    [GeneratedRegex(@"^\[(?<time>\d\d:\d\d:\d\d)\.\d+ \w+\] ")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"Loading package synchronously: (?<package>\S+)$")]
    private static partial Regex SyncLoad();

    [GeneratedRegex(@"appLogErrorf: Loading (?<save>\S+) on device")]
    private static partial Regex SaveLoad();

    /// <summary>BioLoadGame's error, or loadgame's (a career save's name it can't find; the logger's prefix is not checked).</summary>
    [GeneratedRegex(@"(?<error>Failed to load game\..*|Unable to load .* on device Id.*)$")]
    private static partial Regex LoadFailed();

    public sealed record Options(string? Mod, bool Check, IReadOnlyList<string> Languages, int? LoadSlot, bool LoadLatest, IReadOnlyList<string> Commands,
                                 string? WaitForMap, double SettleSeconds, bool Screenshot, bool Force, int TimeoutSeconds);

    /// <summary>
    /// MeleBridge's refusal of a request its game thread did not take in time (RunOnGameThread in tools/mele-bridge/src/GameThread.cpp):
    /// such a request never ran, so it can be sent again.
    /// </summary>
    private const string NotTaken = "did not take the request";

    /// <summary>The longest a map load is awaited before console input: one that holds the game longer is not a load.</summary>
    private const double LoadWaitSeconds = 90;

    /// <summary>Where a cycle is, for its stop message: a game that closes, or a call that fails, is reported with the phase it happened in.</summary>
    private enum Phase { Quit, Check, Install, Boot, Menu, AtMenu, Load, Commands, Map, Screenshot }

    private static string Words(Phase phase) => phase switch
    {
        Phase.Quit => "the quit",
        Phase.Check => "mod_check",
        Phase.Install => "the install",
        Phase.Boot => "the boot request",
        Phase.Menu => "the boot, before the main menu",
        Phase.AtMenu => "the wait at the main menu",
        Phase.Load => "the save load",
        Phase.Commands => "the commands",
        Phase.Map => "the wait for the map",
        _ => "the screenshot",
    };

    /// <summary>How a wait ended. A wait that watches the game also ends when it closes, rather than running out its time.</summary>
    private enum Waited { Done, TimedOut, GameClosed }

    /// <summary>The console's exit, without saving.</summary>
    public static GameCloseResult Close(MEGame game, bool force)
    {
        using var held = GameLock.ForGame(game);
        var clock = Stopwatch.StartNew();
        var steps = new List<string>();
        void Step(string text) => steps.Add($"{clock.Elapsed.TotalSeconds,5:0.0}s {text}");
        if (Quit(game, force, Step) is not { } why) return new GameCloseResult(true, steps);
        Step(why);
        return new GameCloseResult(false, steps);
    }

    public static GameCycleResult Run(MEGame game, Options o)
    {
        if (o.LoadSlot is not null && game != MEGame.LE1) throw new ArgumentException($"loadSlot is BioLoadGame, which {game} lacks: use loadLatest.");
        if (o.LoadLatest && game == MEGame.LE1) throw new ArgumentException("loadLatest is for LE2 and LE3: LE1 uses loadSlot, or a loadgame command.");

        // Held to the end: another session's cycle, close or install of this game waits, then is refused.
        using var held = GameLock.ForGame(game);
        var clock = Stopwatch.StartNew();
        var steps = new List<string>();
        void Step(string text) => steps.Add($"{clock.Elapsed.TotalSeconds,5:0.0}s {text}");
        ModCheckResult? check = null;
        ModInstallResult? install = null;
        var phase = Phase.Quit;
        GameCycleResult Stop(string why)
        {
            Step(why);
            return new GameCycleResult(false, null, steps, check, install, null);
        }
        // A game that closes ends every wait at once, and the stop names the phase rather than a wait that ran out.
        GameCycleResult Closed() => Stop($"the game closed during {Words(phase)} (a crash? see the Windows Application log)");
        // Null once the command is out; else the cycle's end.
        GameCycleResult? Send(string command)
        {
            if (TrySend(game, command, Math.Min(o.TimeoutSeconds, LoadWaitSeconds), Step) is not { } why)
            {
                Step($"sent {command}");
                return null;
            }
            return GameBridge.IsGameRunning(game) ? Stop(why) : Closed();
        }
        // The end of the load of a map, then the settle; null when the cycle goes on.
        GameCycleResult? Loaded(string loading) =>
            WaitLoaded(game, loading, Math.Min(o.TimeoutSeconds, LoadWaitSeconds), Step) == Waited.GameClosed || !Settle(game, o.SettleSeconds) ? Closed() : null;

        try
        {
            // 1. Quit.
            if (Quit(game, o.Force, Step) is { } stillUp) return Stop(stillUp);

            // 2. Check and install, then boot, or only boot. The boot waits for the install's check: M3's own --bootgame would
            // start the game after an install that turns out not verified.
            if (o.Mod is { Length: > 0 } mod)
            {
                if (o.Check && game.IsGame1())
                {
                    phase = Phase.Check;
                    check = ModChecker.Check(game, mod, [], o.Languages, 50);
                    Step($"mod_check: {check.Errors} errors, {check.Warnings} warnings");
                    if (check.Errors > 0) return Stop("stopped before installing: mod_check found errors");
                }
                phase = Phase.Install;
                install = ModInstaller.Install(game, mod, bootGame: false, timeoutSeconds: 180);
                Step($"installed in {install.Seconds} s, {install.FilesChecked} files checked, {install.Differences.Count} differ");
                if (!install.Completed || install.Differences.Count > 0) return Stop("install not verified, so the game was not booted");
            }
            phase = Phase.Boot;
            var bootAsked = DateTime.Now;
            ModInstaller.Boot(game);
            Step("asked M3 to boot the game");

            // 3. The menu: a game started after the boot request, whose log reached EntryMenu. A game that dies before it (seen
            // once while the ASI loader hooked ProcessEvent: a call through address 0) is booted again, once.
            phase = Phase.Menu;
            string? log = null;
            string? crashedFirst = null;
            for (var attempt = 1; ; attempt++)
            {
                var seen = false;
                var crashed = false;
                var menu = WaitUntil(() =>
                {
                    var running = GameBridge.IsGameRunning(game);
                    seen |= running;
                    // A game that dies between two polls is never seen running, but its DebugLogger log says it started.
                    crashed = !running && (seen || SessionStarted(game, bootAsked, crashedFirst, out _));
                    return crashed || running && SessionStarted(game, bootAsked, crashedFirst, out log) && Lines(log!).Any(IsEntryMenu);
                }, o.TimeoutSeconds) && !crashed;
                if (menu) break;
                if (!crashed) return Stop(seen ? $"no main menu within {o.TimeoutSeconds} s" : $"the game did not start within {o.TimeoutSeconds} s of the boot request");
                if (attempt > 1) return Stop("the game closed before the main menu twice (see the Windows Application log)");
                Step("the game closed before the main menu: booting it again");
                crashedFirst = FirstLine(game);
                bootAsked = DateTime.Now;
                ModInstaller.Boot(game);
            }
            Step("main menu loaded");
            phase = Phase.AtMenu;
            switch (WaitAlive(game, () => ConsoleOpen(game), 30))
            {
                case Waited.GameClosed: return Closed();
                case Waited.TimedOut: return Stop(game == MEGame.LE1 ? "the LEX Interop pipe did not open" : "the MeleBridge pipe did not open");
            }
            if (!Settle(game, o.SettleSeconds)) return Closed();

            // 4. Save, then commands; each map load is read from the log.
            var map = "EntryMenu";
            var offset = DebugLog.Length(log!);
            var load = o.LoadSlot is { } slot ? $"BioLoadGame {slot}" : o.LoadLatest ? "QuickLoad" : null;
            if (load is not null)
            {
                phase = Phase.Load;
                if (Send(load) is { } unsent) return unsent;
                // With commands to run, the save's own map comes first, whatever it is; the wanted one is awaited after them.
                var wanted = o.Commands.Count > 0 ? null : o.WaitForMap;
                switch (WaitForMap(game, log!, offset, wanted, o.TimeoutSeconds, Step, out map))
                {
                    case Waited.GameClosed: return Closed();
                    case Waited.TimedOut: return Stop(LoadFailure(log!, offset) ?? $"no map loaded after {load}");
                }
                offset = DebugLog.Length(log!);
                // The load line comes before the loading screen, and console input sent during the load is lost (LE1) or refused.
                if (o.Commands.Count > 0 && Loaded(map) is { } stop) return stop;
            }
            phase = Phase.Commands;
            for (var i = 0; i < o.Commands.Count; i++)
            {
                var at = DebugLog.Length(log!);
                if (Send(o.Commands[i]) is { } unsent) return unsent;
                if (!Settle(game, 1)) return Closed();
                // A command that loads a map ('at BIOA_MYM01 ...', 'loadgame ...') holds the game: the next one waits for that load.
                if (i < o.Commands.Count - 1 && MapLoads(game, DebugLog.LinesAfter(log!, at)).FirstOrDefault() is { } loading)
                {
                    Step($"{o.Commands[i]} loads {loading}");
                    if (Loaded(loading) is { } stop) return stop;
                }
            }
            if (o.Commands.Count > 0 && o.WaitForMap is { Length: > 0 } && !map.Equals(o.WaitForMap, StringComparison.OrdinalIgnoreCase))
            {
                phase = Phase.Map;
                switch (WaitForMap(game, log!, offset, o.WaitForMap, o.TimeoutSeconds, Step, out map))
                {
                    case Waited.GameClosed: return Closed();
                    case Waited.TimedOut: return Stop(LoadFailure(log!, offset) ?? $"{o.WaitForMap} did not load");
                }
            }

            // 5. Let streaming and the loading screen finish, then look.
            Screenshot? shot = null;
            if (o.Screenshot)
            {
                phase = Phase.Screenshot;
                if (!Settle(game, o.SettleSeconds)) return Closed();
                try
                {
                    shot = GameScreenshot.Take(game, null, 1280);
                    Step($"screenshot {shot.WslPath}");
                }
                catch (Exception e) when (GameBridge.IsGameRunning(game))
                {
                    // The map is there: a picture that failed (a minimized window) does not undo that.
                    Step($"no screenshot: {e.Message}");
                }
            }
            return new GameCycleResult(true, map, steps, check, install, shot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whatever failed, the steps say what was already done to the game: closed, installed, booted.
            return phase > Phase.Boot && !GameBridge.IsGameRunning(game) ? Closed() : Stop($"stopped during {Words(phase)}: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// The console's exit lets the engine shut down; killing it only when asked. Returns why the game is still up, or null when it
    /// is gone.
    /// </summary>
    private static string? Quit(MEGame game, bool force, Action<string> step)
    {
        if (!GameBridge.IsGameRunning(game)) return null;
        try
        {
            Console(game, "exit");
            step("sent exit");
        }
        catch (Exception e)
        {
            // MeleBridge can go down before it answers, or refuse while the game loads; a game that stayed up is caught below.
            step($"exit: {e.Message}");
        }
        if (WaitUntil(() => !GameBridge.IsGameRunning(game), 30))
        {
            step("game closed");
            return null;
        }
        if (!force) return "the game did not close within 30 s (pass force to kill it)";
        foreach (var p in Process.GetProcessesByName(GameBridge.ProcessName(game))) using (p) p.Kill();
        if (!WaitUntil(() => !GameBridge.IsGameRunning(game), 15)) return "the game survived Kill";
        step("killed the game");
        return null;
    }

    /// <summary>LE1 reaches the console through the LEX Interop ASI, LE2 and LE3 through MeleBridge.</summary>
    private static bool ConsoleOpen(MEGame game) => game == MEGame.LE1 ? GameBridge.IsPipeOpen(game) : LiveBridge.IsAvailable(game);

    private static void Console(MEGame game, string command)
    {
        if (game == MEGame.LE1) GameLink.Console(game, command);
        else LiveBridge.Request(game, new JsonObject { ["op"] = "console", ["command"] = command });
    }

    /// <summary>
    /// Waits for a save load and the map it brings (or <paramref name="wanted"/>), after <paramref name="offset"/> in the log.
    /// TimedOut also when the game logged that the load failed (<see cref="LoadFailure"/> reads which).
    /// </summary>
    private static Waited WaitForMap(MEGame game, string log, long offset, string? wanted, int timeoutSeconds, Action<string> step, out string map)
    {
        string? found = null;
        var failed = false;
        var waited = WaitAlive(game, () =>
        {
            var lines = DebugLog.LinesAfter(log, offset);
            var save = lines.Select(l => SaveLoad().Match(l)).FirstOrDefault(m => m.Success);
            var loads = MapLoads(game, lines);
            found = wanted is { Length: > 0 } ? loads.FirstOrDefault(p => p.Equals(wanted, StringComparison.OrdinalIgnoreCase)) : loads.FirstOrDefault();
            if (save is not null && found is not null) step($"save {save.Groups["save"].Value}");
            // A failed load logs an error and leaves the menu as it was: no map will come.
            failed = found is null && lines.Any(l => LoadFailed().IsMatch(l));
            return found is not null || failed;
        }, timeoutSeconds);
        map = found ?? "";
        if (waited == Waited.Done && failed) return Waited.TimedOut;
        if (waited == Waited.Done) step($"map {found} loaded");
        return waited;
    }

    /// <summary>The persistent levels the log lines load, in order: BIOA_NOR10 in LE1, BioP_Nor in LE2 and LE3, not their _LOC files.</summary>
    private static List<string> MapLoads(MEGame game, IEnumerable<string> lines)
    {
        var persistent = game == MEGame.LE1 ? "BIOA_" : "BioP_";
        return lines.Select(l => SyncLoad().Match(l)).Where(m => m.Success).Select(m => m.Groups["package"].Value)
                    .Where(p => p.StartsWith(persistent, StringComparison.OrdinalIgnoreCase) && !p.EndsWith("_LOC", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static bool IsEntryMenu(string line) =>
        SyncLoad().Match(line) is { Success: true } m && m.Groups["package"].Value.Equals("EntryMenu", StringComparison.OrdinalIgnoreCase);

    /// <summary>"the game says: ..." when the log after <paramref name="offset"/> holds a failed load, else null.</summary>
    private static string? LoadFailure(string log, long offset) =>
        DebugLog.LinesAfter(log, offset).Select(l => LoadFailed().Match(l)).FirstOrDefault(m => m.Success) is { } failure
            ? $"the game says: {failure.Groups["error"].Value.Trim()}" : null;

    /// <summary>
    /// Sends one console command; null when it went out, else why not (<see cref="Failed"/>). MeleBridge refuses a request its game
    /// thread did not take within 5 s (a load holds it), and such a request never ran, so it goes again, once, when the thread answers
    /// a ping. LE1's LEX Interop takes a command with no reply, so nothing shows there: <see cref="WaitLoaded"/> runs before.
    /// </summary>
    private static string? TrySend(MEGame game, string command, double seconds, Action<string> step)
    {
        try
        {
            Console(game, command);
            return null;
        }
        catch (Exception e) when (game != MEGame.LE1 && e.Message.Contains(NotTaken, StringComparison.Ordinal))
        {
            step($"{command}: {e.Message.TrimEnd('.')}; sending it again once the game thread answers");
            if (WaitAlive(game, () => GameThreadAnswers(game), seconds) != Waited.Done) return Failed(game, command, e);
        }
        catch (Exception e)
        {
            return Failed(game, command, e);
        }
        try
        {
            Console(game, command);
            return null;
        }
        catch (Exception e)
        {
            return Failed(game, command, e);
        }
    }

    /// <summary>
    /// The stop line for a command that did not go through. Only MeleBridge's refusal of a request its game thread never took says
    /// the command did not run. Any other failure may come once it was out: no answer in time, or still running when the bridge
    /// stopped waiting (LiveBridge's IOException, whose text says it may still complete), a QuickLoad that goes on loading. So it
    /// only says the command failed, with the error.
    /// </summary>
    internal static string Failed(MEGame game, string command, Exception e) =>
        game != MEGame.LE1 && e.Message.Contains(NotTaken, StringComparison.Ordinal)
            ? $"{command} did not run: {e.Message}"
            : $"{command} failed: {e.Message}";

    /// <summary>MeleBridge answers ping on the game thread: true once that thread takes requests again.</summary>
    private static bool GameThreadAnswers(MEGame game)
    {
        try
        {
            LiveBridge.Request(game, new JsonObject { ["op"] = "ping" }, 2);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for the load of <paramref name="map"/> to end: its log line comes before the loading screen, and console input sent
    /// during the load is lost (LE1's LEX Interop, with no reply to say so) or refused (MeleBridge). MeleBridge's status tells the
    /// end (<see cref="LoadProblem"/>), on two polls in a row as scenario.py's wait_loaded reads it. Without MeleBridge (optional
    /// on LE1) nothing tells, and only the settle is left. A load that has not ended after <paramref name="seconds"/> is noted and
    /// the cycle goes on. GameClosed is the only result that stops it.
    /// </summary>
    private static Waited WaitLoaded(MEGame game, string map, double seconds, Action<string> step)
    {
        if (!LiveBridge.IsAvailable(game))
        {
            step($"no MeleBridge pipe, so the end of the {map} load can't be seen: only settleSeconds is waited, and a command sent before the load ends is lost");
            return GameBridge.IsGameRunning(game) ? Waited.Done : Waited.GameClosed;
        }
        var streak = 0;
        string? why = null, unreadable = null;
        var waited = WaitAlive(game, () =>
        {
            try
            {
                why = LoadProblem(LiveBridge.Request(game, new JsonObject { ["op"] = "status" }, 2), map);
            }
            catch (Exception e) when (e is IOException || e.Message.Contains(NotTaken, StringComparison.Ordinal))
            {
                why = "the game thread is busy";
            }
            catch (Exception e)
            {
                unreadable = e.Message;
                return true;
            }
            streak = why is null ? streak + 1 : 0;
            return streak >= 2;
        }, seconds);
        if (waited == Waited.GameClosed) return waited;
        step(unreadable is not null ? $"MeleBridge status failed ({unreadable}): only settleSeconds is waited"
             : waited == Waited.Done ? $"{map} load ended (MeleBridge status)"
             : $"the {map} load has not ended after {seconds:0} s ({why}): going on");
        return Waited.Done;
    }

    /// <summary>
    /// What keeps a MeleBridge status reply from saying the load of <paramref name="map"/> has ended; null when it has. scenario.py's
    /// load_problems reads the same fields, but for the pause: an unfocused LE1 holds bPlayersOnly every frame, and its loads end
    /// all the same. An unknown loading movie (null: no SFXLoadMovieManager found) does not hold the wait: the settle follows.
    /// </summary>
    internal static string? LoadProblem(JsonObject status, string map)
    {
        static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var at = Text(status["map"]);
        if (!string.Equals(at, map, StringComparison.OrdinalIgnoreCase)) return $"the map is {at ?? "none"}";
        if (!(status["begunPlay"] is JsonValue begun && begun.TryGetValue<bool>(out var b) && b)) return "the world has not begun play";
        if (string.IsNullOrEmpty(Text(status["pawn"]))) return "no pawn";
        if (status["streaming"] is JsonObject streaming && streaming["pending"] is JsonValue p && p.TryGetValue<int>(out var pending) && pending > 0)
            return $"{pending} streaming levels pending";
        if (status["loadingMovie"] is JsonObject movie)
        {
            var playing = movie["playing"] is JsonValue playingValue && playingValue.TryGetValue<bool>(out var on) && on;
            var state = Text(movie["state"]);
            if (playing || state is not null && state != "LMS_NotPlaying") return $"loading movie {state ?? "playing"}";
        }
        return null;
    }

    /// <summary>
    /// The DebugLogger log of a game started after <paramref name="after"/>: the log is rewritten at start, its first line stamped
    /// with the time. Also true once that game has closed: the log stays. The stamp is to the second, so a session that began
    /// up to 2 s before <paramref name="after"/> passes: <paramref name="crashed"/>, the first line of the session a reboot
    /// replaces, keeps that one out.
    /// </summary>
    private static bool SessionStarted(MEGame game, DateTime after, string? crashed, out string? log)
    {
        log = null;
        try
        {
            var path = DebugLog.LogPath(game);
            var first = DebugLog.LinesAfter(path, 0).FirstOrDefault();
            if (first is null || first == crashed || Stamp().Match(first) is not { Success: true } m) return false;
            var now = DateTime.Now;
            var started = now.Date + TimeSpan.ParseExact(m.Groups["time"].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture);
            // The stamp has no date: one later than now is from an earlier day, a stale log that must not pass for a new session.
            if (started > now) started = started.AddDays(-1);
            if (started < after.AddSeconds(-2)) return false;
            log = path;
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The DebugLogger log's first line (its session's start, to the millisecond), or null with no log.</summary>
    private static string? FirstLine(MEGame game)
    {
        try
        {
            return DebugLog.LinesAfter(DebugLog.LogPath(game), 0).FirstOrDefault();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static List<string> Lines(string log) => DebugLog.LinesAfter(log, 0);

    private static bool WaitUntil(Func<bool> condition, double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return true;
            Thread.Sleep(Poll);
        }
        return condition();
    }

    /// <summary>Like <see cref="WaitUntil"/>, but a game that closes ends the wait at once.</summary>
    private static Waited WaitAlive(MEGame game, Func<bool> condition, double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return Waited.Done;
            if (!GameBridge.IsGameRunning(game)) return Waited.GameClosed;
            if (clock.Elapsed.TotalSeconds >= seconds) return Waited.TimedOut;
            Thread.Sleep(Poll);
        }
    }

    /// <summary>Waits <paramref name="seconds"/>; false as soon as the game has closed.</summary>
    private static bool Settle(MEGame game, double seconds) => WaitAlive(game, () => false, seconds) != Waited.GameClosed;
}

/// <summary>
/// Named mutexes that keep two mele-mcp calls off one game, or off M3, at the same time, whether they run in one server or in
/// two sessions' servers. Every server here is a Windows process in the user's logon session, so a Local\ name reaches them all.
/// A mutex belongs to the thread that took it: these calls run start to end on one thread, and that thread may take a lock it
/// holds again (game_cycle's install). Servers deployed before these locks take none, so one of those can still run beside.
/// </summary>
internal sealed class GameLock : IDisposable
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(5);
    private readonly Mutex mutex;

    private GameLock(Mutex mutex) => this.mutex = mutex;

    /// <summary>
    /// game_cycle, game_close and mod_install of one game, for their whole run: Quit kills every process of the game's name, and
    /// mod_install checks that the game is closed before M3 starts.
    /// </summary>
    public static GameLock ForGame(MEGame game) => Take($"MeleMcp.Game.{game}", ShortWait,
        $"Another mele-mcp call (this session's or another's) is closing, cycling or installing {game} right now. Wait for it to end "
        + "(game_cycle takes up to a few minutes), then retry.");

    /// <summary>M3 from a request to its end: two installs at once would read each other's lines in M3's log.</summary>
    public static GameLock ForM3() => Take("MeleMcp.M3", ShortWait,
        "Another mele-mcp call is installing a mod or booting a game through M3 right now. Wait for it to end, then retry.");

    /// <summary>One LEX Interop request sequence (GameLink): a few seconds at most.</summary>
    public static GameLock ForLexInterop(MEGame game) => Take($"MeleMcp.LexInterop.{game}", TimeSpan.FromSeconds(15),
        $"Another mele-mcp call has held the {game} LEX Interop link for 15 s. Retry when it ends.");

    /// <summary>
    /// The LEX Interop reply window's title (GameBridge.HoldReplies): the games answer the one window that carries it, so one
    /// holder at a time on the machine, whatever the game. Taken inside a sequence's own lock, for a few seconds at most.
    /// </summary>
    public static GameLock ForLexReplies() => Take("MeleMcp.LexReplies", TimeSpan.FromSeconds(15),
        "Another mele-mcp call has held the LEX Interop reply window for 15 s. Retry when it ends.");

    /// <summary>Waits up to <paramref name="wait"/> for the lock, then refuses with <paramref name="busy"/>.</summary>
    internal static GameLock Take(string name, TimeSpan wait, string busy)
    {
        var mutex = new Mutex(false, @"Local\" + name);
        try
        {
            if (!mutex.WaitOne(wait)) throw new InvalidOperationException(busy);
        }
        catch (AbandonedMutexException)
        {
            // Its holder ended without letting go (a server stopped by stop-server.ps1): the lock is ours now.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        return new GameLock(mutex);
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
