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

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        MEPackageHandler.CreateEmptyLevel(full, game);

        using var pcc = MEPackageHandler.OpenMEPackage(full, forceLoadFromDisk: true);
        return new NewLevelResult(full, Path.GetFileNameWithoutExtension(full), pcc.ExportCount, pcc.Exports.Select(ExportInspector.Row).ToList());
    }
}
