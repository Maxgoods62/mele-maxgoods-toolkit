using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace MeleMcp.Core;

public sealed record CloneResult(
    string Path,
    ExportRow Source,
    ExportRow Copy,
    int ClonedExportCount,
    bool AddedToLevelActors,
    string BackupPath);

public static class ExportCloner
{
    /// <summary>
    /// Duplicates an export with all its children (components, per-pawn materials, behavior...). References inside the
    /// cloned tree are relinked to the copies; references to shared assets stay on the originals.
    /// </summary>
    public static CloneResult CloneTree(MEGame game, string fileOrPath, string export, bool addToLevelActors)
    {
        var ((source, copy, count, added, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var original = ExportInspector.Resolve(pcc, export);
            if (addToLevelActors && original.Parent is not ExportEntry { ClassName: "Level" })
            {
                throw new ArgumentException($"{original.InstancedFullPath} is not an actor of a Level; set addToLevelActors=false to clone it anyway.");
            }

            var before = pcc.ExportCount;
            var clone = EntryCloner.CloneTree(original);
            var addedToLevel = addToLevelActors && pcc.AddToLevelActorsIfNotThere(clone);

            return (ExportInspector.Row(original), ExportInspector.Row(clone), pcc.ExportCount - before, addedToLevel, pcc.FilePath);
        });

        return new CloneResult(path, source, copy, count, added, backup);
    }
}
