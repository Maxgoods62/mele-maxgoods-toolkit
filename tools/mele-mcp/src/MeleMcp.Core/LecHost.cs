using System.Diagnostics;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Compression;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;

namespace MeleMcp.Core;

/// <param name="LegendaryEditionDirFrom">Where <paramref name="LegendaryEditionDir"/> came from: MELE_LE_DIR, or the registry key and value; null when neither gave one.</param>
public sealed record LecStatus(
    string LecVersion,
    string? LegendaryEditionDir,
    string? LegendaryEditionDirFrom,
    string? Le1GamePath,
    string? Le2GamePath,
    string? Le3GamePath,
    bool OodleLoaded,
    long InitMilliseconds);

/// <summary>
/// One-time, thread-safe initialization of LegendaryExplorerCore for a headless process.
/// </summary>
public static class LecHost
{
    /// <summary>Root of the Legendary Edition install (the folder that contains Game\ME1, Game\ME2, Game\ME3).</summary>
    public const string LeDirEnvVar = "MELE_LE_DIR";

    private static readonly object Gate = new();
    private static LecStatus? status;

    public static LecStatus EnsureInitialized()
    {
        lock (Gate)
        {
            if (status != null) return status;

            var sw = Stopwatch.StartNew();

            // Set before InitLib, because InitLib sources the Oodle DLL from the game folder: MELE_LE_DIR, else the registry.
            var leDir = Environment.GetEnvironmentVariable(LeDirEnvVar);
            var found = !string.IsNullOrWhiteSpace(leDir) ? (GamePaths.ToWindowsPath(leDir), LeDirEnvVar) : FromRegistry();
            if (found is { } use)
            {
                LegendaryExplorerCoreLibSettings.Instance.LEDirectory = use.Dir;
                LE1Directory.ReloadDefaultGamePath();
                LE2Directory.ReloadDefaultGamePath();
                LE3Directory.ReloadDefaultGamePath();
            }

            // Console hosts have no SynchronizationContext, so the default scheduler stands in for LEX's UI scheduler.
            LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default,
                packageSavingFailed: msg => Console.Error.WriteLine($"[LEC] package save failed: {msg}"),
                objectDBsToLoad: [MEGame.LE1, MEGame.LE2, MEGame.LE3]);

            status = new LecStatus(
                LecVersion: typeof(LegendaryExplorerCoreLib).Assembly.GetName().Version?.ToString() ?? "unknown",
                LegendaryEditionDir: LegendaryExplorerCoreLibSettings.Instance.LEDirectory,
                LegendaryEditionDirFrom: found?.From,
                Le1GamePath: LE1Directory.DefaultGamePath,
                Le2GamePath: LE2Directory.DefaultGamePath,
                Le3GamePath: LE3Directory.DefaultGamePath,
                OodleLoaded: OodleHelper.EnsureOodleDll(),
                InitMilliseconds: sw.ElapsedMilliseconds);
            return status;
        }
    }

    /// <summary>
    /// The Legendary Edition folder as its installer records it, in the keys LEC's own lookup reads (that lookup is compiled out of
    /// the LEC we build): any HKLM\SOFTWARE\BioWare\&lt;...Legendary Edition&gt; 'Install Dir', then Steam's uninstall entry's
    /// 'InstallLocation', each in the 64-bit view, then the 32-bit one. Only a folder with Game\ME1 counts.
    /// </summary>
    private static (string Dir, string From)? FromRegistry()
    {
        RegistryView[] views = [RegistryView.Registry64, RegistryView.Registry32];
        foreach (var view in views)
            if (Guarded(() => FromBioWare(view)) is { } found) return found;
        foreach (var view in views)
            if (Guarded(() => FromSteam(view)) is { } found) return found;
        return null;
    }

    private const string BioWareKey = @"SOFTWARE\BioWare", SteamKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1328670";

    private static (string Dir, string From)? FromBioWare(RegistryView view)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = hive.OpenSubKey(BioWareKey);
        foreach (var name in key?.GetSubKeyNames().Where(k => k.EndsWith("Legendary Edition", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            using var game = key!.OpenSubKey(name);
            if (Valid(game?.GetValue("Install Dir") as string) is { } dir) return (dir, From(view, $@"{BioWareKey}\{name}", "Install Dir"));
        }
        return null;
    }

    private static (string Dir, string From)? FromSteam(RegistryView view)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = hive.OpenSubKey(SteamKey);
        return Valid(key?.GetValue("InstallLocation") as string) is { } dir ? (dir, From(view, SteamKey, "InstallLocation")) : null;
    }

    // A key this account may not read, or one deleted while it is read, counts as absent: the lookup moves on to the next.
    private static (string Dir, string From)? Guarded(Func<(string Dir, string From)?> lookup)
    {
        try
        {
            return lookup();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string From(RegistryView view, string key, string value) =>
        $@"registry: HKLM\{key} '{value}' ({(view == RegistryView.Registry64 ? "64" : "32")}-bit view)";

    /// <summary>The folder without a trailing backslash, when it has Game\ME1; else null.</summary>
    private static string? Valid(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var trimmed = dir.Trim().TrimEnd('\\', '/');
        return Directory.Exists(Path.Combine(trimmed, "Game", "ME1")) ? trimmed : null;
    }
}
