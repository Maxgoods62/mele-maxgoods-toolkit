using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

public sealed record NewLevelResult(string Path, string LevelName, int ExportCount, IReadOnlyList<ExportRow> Exports);

public static class LevelCreator
{
    /// <summary>
    /// Writes LEC's embedded empty level for the game (TheWorld, PersistentLevel, WorldInfo...), renamed after the file.
    /// The level name is the file name, which is what streaming and the LE1CP Sideloader load by.
    /// </summary>
    public static NewLevelResult Create(MEGame game, string path)
    {
        var full = Checked(game, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // What LEC's CreateEmptyLevel does, except that it writes into the file itself: written beside it, then moved, a write
        // that fails leaves no half-made level, which a second level_new would refuse as "already exists".
        using (var level = MEPackageHandler.CreateEmptyLevelStream(Path.GetFileNameWithoutExtension(full), game))
        {
            AtomicFile.Write(full, temporary => LegendaryExplorerCore.Helpers.StreamHelpers.WriteToFile(level, temporary));
        }

        using var pcc = MEPackageHandler.OpenMEPackage(full, forceLoadFromDisk: true);
        return new NewLevelResult(full, Path.GetFileNameWithoutExtension(full), pcc.ExportCount, pcc.Exports.Select(ExportInspector.Row).ToList());
    }

    /// <summary>
    /// The same empty level, in memory only, with the same checks: level_build fills it and saves it to <paramref name="path"/>
    /// (<c>Save(path)</c>) once every check of its YAML passed, so a build that fails creates no file.
    /// </summary>
    internal static IMEPackage CreateInMemory(MEGame game, string path) => MEPackageHandler.CreateMemoryEmptyLevel(Checked(game, path), game);

    /// <summary>The full path of a level to create: a .pcc that does not exist yet, outside the game install.</summary>
    private static string Checked(MEGame game, string path)
    {
        LecHost.EnsureInitialized();
        var full = Path.GetFullPath(GamePaths.ToWindowsPath(path));
        if (!full.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Level files end in .pcc.");
        }
        if (File.Exists(full))
        {
            throw new ArgumentException($"{full} already exists.");
        }
        PackageWriter.EnsureWritable(game, full);
        return full;
    }
}
