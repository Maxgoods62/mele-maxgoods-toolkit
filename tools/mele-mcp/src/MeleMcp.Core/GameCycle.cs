using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <param name="Ready">The game reached the menu (and the map, when one was loaded or awaited).</param>
/// <param name="Map">The last map the game loaded, from the DebugLogger log.</param>
/// <param name="Steps">What happened, with seconds since the start.</param>
public sealed record GameCycleResult(bool Ready, string? Map, IReadOnlyList<string> Steps, ModCheckResult? Check, ModInstallResult? Install, Screenshot? Screenshot);

/// <summary>
/// The test loop in one call: quit the game (console "exit"), check and install the mod, boot through M3, wait for the main
/// menu, load a save (cheat exec BioLoadGame), run console commands, wait for the map, take a screenshot. Progress is read from
/// the DebugLogger ASI's log, which every game start rewrites.
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

    [GeneratedRegex(@"appLogErrorf: (?<error>Failed to load game\..*)$")]
    private static partial Regex LoadFailed();

    public sealed record Options(string? Mod, bool Check, IReadOnlyList<string> Languages, int? LoadSlot, IReadOnlyList<string> Commands,
                                 string? WaitForMap, double SettleSeconds, bool Screenshot, bool Force, int TimeoutSeconds);

    public static GameCycleResult Run(MEGame game, Options o)
    {
        var clock = Stopwatch.StartNew();
        var steps = new List<string>();
        void Step(string text) => steps.Add($"{clock.Elapsed.TotalSeconds,5:0.0}s {text}");
        GameCycleResult Stop(string why, ModCheckResult? check = null, ModInstallResult? install = null)
        {
            Step(why);
            return new GameCycleResult(false, null, steps, check, install, null);
        }

        // 1. Quit: the console's exit lets the engine shut down; killing it only when asked.
        if (GameBridge.IsGameRunning(game))
        {
            GameLink.Console(game, "exit");
            Step("sent exit");
            if (!WaitUntil(() => !GameBridge.IsGameRunning(game), 30))
            {
                if (!o.Force) return Stop("the game did not close within 30 s (pass force to kill it)");
                foreach (var p in Process.GetProcessesByName(GameBridge.ProcessName(game))) using (p) p.Kill();
                if (!WaitUntil(() => !GameBridge.IsGameRunning(game), 15)) return Stop("the game survived Kill");
                Step("killed the game");
            }
            else Step("game closed");
        }

        // 2. Check, install and boot, or only boot.
        ModCheckResult? check = null;
        ModInstallResult? install = null;
        var bootAsked = DateTime.Now;
        if (o.Mod is { Length: > 0 } mod)
        {
            if (o.Check)
            {
                check = ModChecker.Check(game, mod, [], o.Languages, 50);
                Step($"mod_check: {check.Errors} errors, {check.Warnings} warnings");
                if (check.Errors > 0) return Stop("stopped before installing: mod_check found errors", check);
            }
            bootAsked = DateTime.Now;
            install = ModInstaller.Install(game, mod, bootGame: true, timeoutSeconds: 180);
            Step($"installed in {install.Seconds} s, {install.FilesChecked} files checked, {install.Differences.Count} differ");
            if (!install.Completed || install.Differences.Count > 0) return Stop("install not verified, so the game was not awaited (M3 may still start it)", check, install);
        }
        else
        {
            ModInstaller.Boot(game);
            Step("asked M3 to boot the game");
        }

        // 3. The menu: a game started after the boot request, whose log reached EntryMenu. A game that dies before it (seen once
        // while the ASI loader hooked ProcessEvent: a call through address 0) is booted again, once.
        string? log = null;
        for (var attempt = 1; ; attempt++)
        {
            var seen = false;
            var crashed = false;
            var menu = WaitUntil(() =>
            {
                var running = GameBridge.IsGameRunning(game);
                seen |= running;
                crashed = seen && !running;
                return crashed || NewSession(game, bootAsked, out log) && Lines(log!).Any(l => SyncLoad().Match(l) is { Success: true } m && m.Groups["package"].Value.Equals("EntryMenu", StringComparison.OrdinalIgnoreCase));
            }, o.TimeoutSeconds) && !crashed;
            if (menu) break;
            if (!crashed) return Stop($"no main menu within {o.TimeoutSeconds} s", check, install);
            if (attempt > 1) return Stop("the game closed before the main menu twice (see the Windows Application log)", check, install);
            Step("the game closed before the main menu: booting it again");
            bootAsked = DateTime.Now;
            ModInstaller.Boot(game);
        }
        Step("main menu loaded");
        if (!WaitUntil(() => GameBridge.IsPipeOpen(game), 30)) return Stop("the LEX Interop pipe did not open", check, install);
        Thread.Sleep(TimeSpan.FromSeconds(o.SettleSeconds));

        // 4. Save, then commands; each map load is read from the log.
        var map = "EntryMenu";
        var offset = DebugLog.Length(log!);
        if (o.LoadSlot is { } slot)
        {
            GameLink.Console(game, $"BioLoadGame {slot}");
            Step($"sent BioLoadGame {slot}");
            // With commands to run, the save's own map comes first, whatever it is; the wanted one is awaited after them.
            var wanted = o.Commands.Count > 0 ? null : o.WaitForMap;
            if (!WaitForMap(log!, offset, wanted, o.TimeoutSeconds, Step, out map))
            {
                var failure = DebugLog.LinesAfter(log!, offset).Select(l => LoadFailed().Match(l)).FirstOrDefault(m => m.Success);
                return Stop(failure is null ? "no map loaded after BioLoadGame" : $"the game says: {failure.Groups["error"].Value.Trim()}", check, install);
            }
            offset = DebugLog.Length(log!);
            // The load line comes before the loading screen; console input sent during it is lost.
            if (o.Commands.Count > 0) Thread.Sleep(TimeSpan.FromSeconds(o.SettleSeconds));
        }
        foreach (var command in o.Commands)
        {
            GameLink.Console(game, command);
            Step($"sent {command}");
            Thread.Sleep(1000);
        }
        if (o.Commands.Count > 0 && o.WaitForMap is { Length: > 0 } && !map.Equals(o.WaitForMap, StringComparison.OrdinalIgnoreCase))
        {
            if (!WaitForMap(log!, offset, o.WaitForMap, o.TimeoutSeconds, Step, out map)) return Stop($"{o.WaitForMap} did not load", check, install);
        }

        // 5. Let streaming and the loading screen finish, then look.
        Screenshot? shot = null;
        if (o.Screenshot)
        {
            Thread.Sleep(TimeSpan.FromSeconds(o.SettleSeconds));
            shot = GameScreenshot.Take(game, null, 1280);
            Step($"screenshot {shot.WslPath}");
        }
        return new GameCycleResult(true, map, steps, check, install, shot);
    }

    /// <summary>Waits for a save load and the map it brings (or <paramref name="wanted"/>), after <paramref name="offset"/> in the log.</summary>
    private static bool WaitForMap(string log, long offset, string? wanted, int timeoutSeconds, Action<string> step, out string map)
    {
        string? found = null;
        var ok = WaitUntil(() =>
        {
            var lines = DebugLog.LinesAfter(log, offset);
            var save = lines.Select(l => SaveLoad().Match(l)).FirstOrDefault(m => m.Success);
            var loads = lines.Select(l => SyncLoad().Match(l)).Where(m => m.Success).Select(m => m.Groups["package"].Value)
                             .Where(p => p.StartsWith("BIOA_", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("_LOC", StringComparison.OrdinalIgnoreCase)).ToList();
            found = wanted is { Length: > 0 } ? loads.FirstOrDefault(p => p.Equals(wanted, StringComparison.OrdinalIgnoreCase)) : loads.FirstOrDefault();
            if (save is not null && found is not null) step($"save {save.Groups["save"].Value}");
            // A failed load logs an error and leaves the menu as it was: no map will come.
            return found is not null || lines.Any(l => LoadFailed().IsMatch(l));
        }, timeoutSeconds) && found is not null;
        map = found ?? "";
        if (ok) step($"map {found} loaded");
        return ok;
    }

    /// <summary>The DebugLogger log of a game started after <paramref name="after"/>: the log is rewritten at start, its first line stamped with the time.</summary>
    private static bool NewSession(MEGame game, DateTime after, out string? log)
    {
        log = null;
        if (!GameBridge.IsGameRunning(game)) return false;
        try
        {
            var path = DebugLog.LogPath(game);
            var first = DebugLog.LinesAfter(path, 0).FirstOrDefault();
            if (first is null || Stamp().Match(first) is not { Success: true } m) return false;
            var started = DateTime.Today + TimeSpan.ParseExact(m.Groups["time"].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture);
            if (started < after.AddSeconds(-2)) return false;
            log = path;
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
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
}
