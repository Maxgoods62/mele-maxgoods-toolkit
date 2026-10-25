using System.Diagnostics;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Compression;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

public sealed record LecStatus(
    string LecVersion,
    string? LegendaryEditionDir,
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

            // Override the registry-based lookup before InitLib, because InitLib sources the Oodle DLL from the game folder.
            var leDir = Environment.GetEnvironmentVariable(LeDirEnvVar);
            if (!string.IsNullOrWhiteSpace(leDir))
            {
                LegendaryExplorerCoreLibSettings.Instance.LEDirectory = GamePaths.ToWindowsPath(leDir);
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
                Le1GamePath: LE1Directory.DefaultGamePath,
                Le2GamePath: LE2Directory.DefaultGamePath,
                Le3GamePath: LE3Directory.DefaultGamePath,
                OodleLoaded: OodleHelper.EnsureOodleDll(),
                InitMilliseconds: sw.ElapsedMilliseconds);
            return status;
        }
    }
}
