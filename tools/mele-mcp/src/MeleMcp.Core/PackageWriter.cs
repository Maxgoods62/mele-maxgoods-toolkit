using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <summary>
/// The only way tools modify packages: open, refuse game-install files, back up, mutate, save.
/// </summary>
public static class PackageWriter
{
    /// <summary>Backups go outside the mod folder, otherwise M3 would install them into the game.</summary>
    public const string BackupDirEnvVar = "MELE_BACKUP_DIR";
    private const string DefaultBackupDir = @"D:\MELE\backups\mele-mcp";

    public static (T Result, string BackupPath) Edit<T>(MEGame game, string fileOrPath, Func<IMEPackage, T> mutate)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        EnsureWritable(pcc.Game, pcc.FilePath);
        var result = mutate(pcc);
        var backup = Backup(pcc.FilePath);
        pcc.Save();
        return (result, backup);
    }

    /// <summary>
    /// The game install is a deploy target, never an edit target: a file name can resolve to the base game or to another
    /// mod's installed copy (e.g. LE1CP overrides many level files). Edits go to the mod's source folder in the M3 library.
    /// </summary>
    internal static void EnsureWritable(MEGame game, string path)
    {
        // Both spelled the same way, so \\?\D:\..., \\wsl$\<distro>\mnt\d\... and D:\... all match; the whole Legendary Edition
        // folder counts, not only Game\ME1-3.
        var full = GamePaths.Canonical(path);
        foreach (var root in new[] { LegendaryExplorerCoreLibSettings.Instance.LEDirectory, MEDirectories.GetDefaultGamePath(game) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (full.StartsWith(GamePaths.Canonical(root).TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to modify a file inside the game install ({path}). Edit the mod's copy in the M3 library, then install or copy it.");
            }
        }
    }

    internal static string Backup(string path)
    {
        var dir = Environment.GetEnvironmentVariable(BackupDirEnvVar);
        dir = string.IsNullOrWhiteSpace(dir) ? DefaultBackupDir : GamePaths.ToWindowsPath(dir);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Path.GetFileName(path)}");
        File.Copy(path, target);
        return target;
    }
}
