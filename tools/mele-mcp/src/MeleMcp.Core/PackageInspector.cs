using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

public sealed record ClassCount(string ClassName, int Count);

public sealed record PackageSummary(
    string Path,
    string Game,
    long FileSize,
    bool IsCompressed,
    int NameCount,
    int ImportCount,
    int ExportCount,
    int LocalTalkFileCount,
    IReadOnlyList<ClassCount> TopExportClasses);

public static class PackageInspector
{
    /// <summary>
    /// Fully loads a package (not quickLoad, so the export table is decompressed and readable). Caller disposes it.
    /// </summary>
    public static IMEPackage Open(MEGame game, string fileOrPath)
    {
        LecHost.EnsureInitialized();
        var path = GamePaths.ResolvePackage(game, fileOrPath);
        return MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
    }

    public static PackageSummary Summarize(MEGame game, string fileOrPath, int topClasses = 15)
    {
        using var pcc = Open(game, fileOrPath);

        var classes = pcc.Exports
            .GroupBy(e => e.ClassName)
            .Select(g => new ClassCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.ClassName, StringComparer.Ordinal)
            .Take(topClasses)
            .ToList();

        return new PackageSummary(
            Path: pcc.FilePath,
            Game: pcc.Game.ToString(),
            FileSize: pcc.FileSize,
            IsCompressed: pcc.IsCompressed,
            NameCount: pcc.NameCount,
            ImportCount: pcc.ImportCount,
            ExportCount: pcc.ExportCount,
            LocalTalkFileCount: pcc.LocalTalkFiles?.Count ?? 0,
            TopExportClasses: classes);
    }
}
