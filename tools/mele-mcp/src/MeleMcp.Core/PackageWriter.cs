using System.Diagnostics.CodeAnalysis;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <summary>
/// The only way tools modify packages: open, refuse game-install files, back up, mutate, save. A package is written beside its
/// file, then moved over it (<see cref="AtomicFile"/>), so the file is always the old package or the new one.
/// src/BannedSymbols.txt keeps LEC's package Save and CreateAndSavePackage to this class.
/// </summary>
public static class PackageWriter
{
    /// <summary>Backups go outside the mod folder, otherwise M3 would install them into the game.</summary>
    public const string BackupDirEnvVar = "MELE_BACKUP_DIR";

    /// <summary>%LOCALAPPDATA%\mele-mcp\backups, when <see cref="BackupDirEnvVar"/> is not set.</summary>
    private static string DefaultBackupDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mele-mcp", "backups");

    /// <summary>
    /// The callback LecHost and lectool give LEC's InitLib. LEC's Save catches every exception outside its DEBUG builds, passes a
    /// message here and returns as if the package were saved. Thrown from here, the failure reaches the code that called Save: LEC
    /// calls this on the saving thread, from its catch block.
    /// </summary>
    public static void SaveFailed(string lecMessage) =>
        // "Error saving <path>:", then "<exception type>: <message>", then stack traces: the first two lines say it all.
        throw new PackageSaveException(string.Join(" ", lecMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2)));

    public static (T Result, string BackupPath) Edit<T>(MEGame game, string fileOrPath, Func<IMEPackage, T> mutate)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        EnsureWritable(pcc.Game, pcc.FilePath);
        var result = mutate(pcc);
        var backup = Backup(pcc.FilePath);
        WriteBeside(pcc, pcc.FilePath, backup);
        return (result, backup);
    }

    /// <summary>
    /// Saves a package a tool opened and changed itself: refuses a path in the game install, backs up the file already there unless
    /// <paramref name="backup"/> is false (a file the tool made new, or one any build makes again), then saves. Returns the backup.
    /// </summary>
    /// <param name="path">Where to save; by default the package's own file. A level created in memory has none and passes its path.</param>
    public static string? Save(IMEPackage pcc, bool backup = true, string? path = null)
    {
        // As LEC's own Save: a package made in memory has no file of its own.
        if (path is null && (pcc.IsMemoryPackage || pcc.FilePath is null))
        {
            throw new InvalidOperationException("A package made in memory is saved with its path.");
        }
        var target = path ?? pcc.FilePath;
        EnsureWritable(pcc.Game, target);
        var copy = backup && File.Exists(target) ? Backup(target) : null;
        WriteBeside(pcc, target, copy);
        return copy;
    }

    /// <summary>A new empty package file at <paramref name="path"/> (LEC's CreateAndSavePackage), refused in the game install.</summary>
    [SuppressMessage("ApiDesign", "RS0030", Justification = "The one place packages are created on disk.")]
    public static void Create(string path, MEGame game)
    {
        EnsureWritable(game, path);
        try
        {
            // LEC writes the temporary file: the empty package does not depend on its path. A failure LEC reports throws (SaveFailed).
            AtomicFile.Write(path, temporary => MEPackageHandler.CreateAndSavePackage(temporary, game));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new PackageSaveException($"{path} was not created. {Reason(e)}", e);
        }
    }

    /// <summary>
    /// Writes the package to <paramref name="target"/>. An LE package is serialized in memory exactly as LEC's own Save does it
    /// (compressed), written to a file beside the target, and that file is moved over the target: a crash, a full disk or a file
    /// another program holds open leaves the target as it was (ForkBuilder writes its forks the same way). LEC's Save has checks of
    /// its own for the older games (ME1's texture files, UDK), which no tool here works on: their packages keep its in-place save.
    /// </summary>
    [SuppressMessage("ApiDesign", "RS0030", Justification = "The one place packages are saved.")]
    private static void WriteBeside(IMEPackage pcc, string target, string? backup)
    {
        var beside = pcc.Game.IsLEGame();
        try
        {
            if (!beside)
            {
                pcc.Save(target);
                return;
            }
            using var bytes = pcc.SaveToStream(compress: true);
            AtomicFile.Write(target, temporary => LegendaryExplorerCore.Helpers.StreamHelpers.WriteToFile(bytes, temporary));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var state = beside ? "is as it was" : "may be damaged";
            var kept = backup is null ? "" : $" Its backup from before this change is {backup}.";
            throw new PackageSaveException($"{target} was not saved and {state}.{kept} {Reason(e)}", e);
        }
    }

    /// <summary>What went wrong, for the message: LEC's own report (SaveFailed) as it is, anything else with its type.</summary>
    private static string Reason(Exception e) => e is PackageSaveException ? e.Message : $"{e.GetType().Name}: {e.Message}";

    /// <summary>
    /// The game install is a deploy target, never an edit target: a file name can resolve to the base game or to another
    /// mod's installed copy (e.g. LE1CP overrides many level files). Edits go to the mod's source folder in the M3 library.
    /// lectool calls this too, for every file it writes.
    /// </summary>
    public static void EnsureWritable(MEGame game, string path) =>
        EnsureOutside(path, LegendaryExplorerCoreLibSettings.Instance.LEDirectory, MEDirectories.GetDefaultGamePath(game));

    /// <summary>Throws when path is inside one of the roots (null or empty roots are skipped).</summary>
    internal static void EnsureOutside(string path, params string?[] roots)
    {
        // Both spelled the same way, so \\?\D:\..., \\wsl$\<distro>\mnt\d\... and D:\... all match; the whole Legendary Edition
        // folder counts, not only Game\ME1-3. PhysicalPaths catches what the spelling hides: shares, junctions, volume names.
        var full = GamePaths.Canonical(path);
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (full.StartsWith(GamePaths.Canonical(root).TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase) || PhysicalPaths.IsInside(path, root))
            {
                throw new InvalidOperationException($"Refusing to modify a file inside the game install ({path}). Edit the mod's copy in the M3 library, then install or copy it.");
            }
        }
    }

    internal static string BackupRoot()
    {
        var dir = Environment.GetEnvironmentVariable(BackupDirEnvVar);
        return string.IsNullOrWhiteSpace(dir) ? DefaultBackupDir : GamePaths.ToWindowsPath(dir);
    }

    /// <summary>
    /// &lt;backups&gt;\&lt;yyyyMMdd-HHmmss-fff&gt;_&lt;file&gt;. A second backup of a file of that name in the same millisecond (two files
    /// of one name in different folders, a file saved twice by one build) is &lt;time&gt;-2_&lt;file&gt;, then -3...: File.Copy never
    /// overwrites a backup, and used to throw on the second one, after the build had saved other files.
    /// </summary>
    internal static string Backup(string path)
    {
        var dir = BackupRoot();
        Directory.CreateDirectory(dir);
        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        var target = Path.Combine(dir, $"{stamp}_{Path.GetFileName(path)}");
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{stamp}-{n}_{Path.GetFileName(path)}");
        File.Copy(path, target);
        return target;
    }
}

/// <summary>A package that could not be saved or created. The message names the file and, when one was taken, its backup.</summary>
public sealed class PackageSaveException(string message, Exception? inner = null) : IOException(message, inner);
