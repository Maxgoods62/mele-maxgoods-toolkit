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
        EnsureWritable(pcc);
        var result = mutate(pcc);
        var backup = Backup(pcc.FilePath);
        pcc.Save();
        return (result, backup);
    }

    /// <summary>
    /// The game install is a deploy target, never an edit target: a file name can resolve to the base game or to another
    /// mod's installed copy (e.g. LE1CP overrides many level files). Edits go to the mod's source folder in the M3 library.
    /// </summary>
    private static void EnsureWritable(IMEPackage pcc)
    {
        var gameRoot = MEDirectories.GetDefaultGamePath(pcc.Game);
        if (gameRoot != null && pcc.FilePath.StartsWith(Path.GetFullPath(gameRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to modify a file inside the game install ({pcc.FilePath}). Edit the mod's copy in the M3 library, then install or copy it.");
        }
    }

    private static string Backup(string path)
    {
        var dir = Environment.GetEnvironmentVariable(BackupDirEnvVar);
        dir = string.IsNullOrWhiteSpace(dir) ? DefaultBackupDir : GamePaths.ToWindowsPath(dir);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Path.GetFileName(path)}");
        File.Copy(path, target);
        return target;
    }
}
