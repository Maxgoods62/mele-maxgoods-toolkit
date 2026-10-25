using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace MeleMcp.Core;

public sealed record PortResult(
    string SourcePath,
    string TargetPath,
    ExportRow Source,
    ExportRow Ported,
    int NewExportCount,
    int NewImportCount,
    bool AddedToLevelActors,
    IReadOnlyList<string> RelinkReport,
    string BackupPath);

public static class ExportPorter
{
    /// <summary>
    /// Copies an export from another package of the same game into the target package, with everything it references
    /// (LEX "clone all references"). Classes and assets from globally loaded files become imports instead of copies.
    /// </summary>
    public static PortResult Port(MEGame game, string sourcePackage, string export, string targetPackage, string? targetParent, bool addToLevelActors)
    {
        using var source = PackageInspector.Open(game, sourcePackage);
        var sourceExport = ExportInspector.Resolve(source, export);

        var ((ported, newExports, newImports, added, report, targetPath), backup) = PackageWriter.Edit(game, targetPackage, pcc =>
        {
            if (source.FilePath.Equals(pcc.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Source and target are the same file; use export_clone.");
            }

            var parent = ResolveParent(pcc, sourceExport, targetParent);
            if (pcc.FindEntry(Combine(parent, sourceExport.ObjectName.Instanced)) is { } existing)
            {
                throw new ArgumentException($"Target already has {existing.InstancedFullPath} (#{existing.UIndex}); porting would link to it instead of copying.");
            }
            if (addToLevelActors && parent is not ExportEntry { ClassName: "Level" })
            {
                throw new ArgumentException("addToLevelActors needs a Level as target parent (e.g. TheWorld.PersistentLevel).");
            }

            var (exportsBefore, importsBefore) = (pcc.ExportCount, pcc.ImportCount);
            var rop = new RelinkerOptionsPackage { Cache = new PackageCache() };
            var relinkReport = EntryImporter.ImportAndRelinkEntries(
                EntryImporter.PortingOption.CloneAllDependencies, sourceExport, pcc, parent, true, rop, out var newEntry);

            var newExport = newEntry as ExportEntry ?? throw new InvalidOperationException("Porting did not create an export.");
            var addedToLevel = addToLevelActors && pcc.AddToLevelActorsIfNotThere(newExport);

            return (ExportInspector.Row(newExport), pcc.ExportCount - exportsBefore, pcc.ImportCount - importsBefore, addedToLevel,
                (relinkReport ?? []).Select(r => r.ToString()).ToList(), pcc.FilePath);
        });

        return new PortResult(source.FilePath, targetPath, ExportInspector.Row(sourceExport), ported, newExports, newImports, added, report, backup);
    }

    /// <summary>Explicit parent, else the export with the source parent's path (e.g. TheWorld.PersistentLevel), else the package root.</summary>
    private static IEntry? ResolveParent(IMEPackage pcc, ExportEntry sourceExport, string? targetParent)
    {
        if (!string.IsNullOrWhiteSpace(targetParent)) return ExportInspector.Resolve(pcc, targetParent);
        return sourceExport.Parent is { } p ? pcc.FindExport(p.InstancedFullPath) : null;
    }

    private static string Combine(IEntry? parent, string name) => parent is null ? name : $"{parent.InstancedFullPath}.{name}";
}
