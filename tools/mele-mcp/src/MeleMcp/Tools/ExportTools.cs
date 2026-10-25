using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

[McpServerToolType]
public static class ExportTools
{
    private const string PackageDescription =
        "Package file name (resolved to the highest mounted copy) or a full Windows or /mnt/<drive> path.";

    private const string ExportDescription =
        "Export UIndex (e.g. 1234), instanced full path, or an object name that is unique in the package.";

    [McpServerTool(Name = "pkg_search", ReadOnly = true),
     Description("Lists exports in a package filtered by class and/or a substring of the instanced full path. Returns UIndex, class and path.")]
    public static ExportSearchResult PkgSearch(
        [Description(PackageDescription)] string package,
        [Description("Exact class name, e.g. MaterialInstanceConstant, BioMorphFace, BioPawn. Omit for all classes.")] string? className = null,
        [Description("Case-insensitive substring of the instanced full path, e.g. Hair.")] string? nameContains = null,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1",
        [Description("Maximum rows returned (TotalMatches still counts all).")] int limit = 100)
        => ToolErrors.Run(() => ExportInspector.Search(PackageTools.ParseGame(game), package, className, nameContains, limit));

    [McpServerTool(Name = "export_get", ReadOnly = true),
     Description("Dumps an export's properties as an indented tree. Object references show class, path and UIndex.")]
    public static string ExportGet(
        [Description(PackageDescription)] string package,
        [Description(ExportDescription)] string export,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => ExportInspector.GetProperties(PackageTools.ParseGame(game), package, export));

    [McpServerTool(Name = "export_refs", ReadOnly = true),
     Description("Lists the exports in the same package that reference an export, and through which property.")]
    public static ExportReferences ExportRefs(
        [Description(PackageDescription)] string package,
        [Description(ExportDescription)] string export,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => ExportInspector.GetReferencers(PackageTools.ParseGame(game), package, export));

    private const string WriteRule =
        " Backs the package up first. Refuses files inside the game install: edit the mod's copy in the M3 library.";

    [McpServerTool(Name = "export_set", Destructive = true),
     Description("Sets one leaf property of an export and saves the package." + WriteRule + " Returns old and new value and the backup path.")]
    public static PropertyEdits ExportSet(
        [Description(PackageDescription)] string package,
        [Description(ExportDescription)] string export,
        [Description("Dot path to a leaf property, with [i] for array elements, e.g. VectorParameterValues[11].ParameterValue.G")] string propertyPath,
        [Description("New value: number, True/False, name, string, $123 for a TLK string ref, #123 for an object reference (0 = None).")] string value,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => PropertyEditor.SetProperties(PackageTools.ParseGame(game), package, [new PropertyChange(export, propertyPath, value)]));

    [McpServerTool(Name = "export_set_batch", Destructive = true),
     Description("Sets several leaf properties (any exports of one package) and saves once. All paths are validated before writing." + WriteRule)]
    public static PropertyEdits ExportSetBatch(
        [Description(PackageDescription)] string package,
        [Description("Changes: each has export (UIndex/path/name), propertyPath (dot path, [i] for arrays) and value (same formats as export_set).")] PropertyChange[] changes,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => PropertyEditor.SetProperties(PackageTools.ParseGame(game), package, changes));

    [McpServerTool(Name = "export_clone", Destructive = true),
     Description("Duplicates an export and all its children; references inside the copy are relinked to the copied objects. "
                 + "For a level actor (e.g. a BioPawn under TheWorld.PersistentLevel) the copy is added to the level's Actors list." + WriteRule)]
    public static CloneResult ExportClone(
        [Description(PackageDescription)] string package,
        [Description(ExportDescription)] string export,
        [Description("Add the copy to TheWorld.PersistentLevel's Actors list (required for an actor to exist in game).")] bool addToLevelActors = true,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => ExportCloner.CloneTree(PackageTools.ParseGame(game), package, export, addToLevelActors));

    [McpServerTool(Name = "export_import", Destructive = true),
     Description("Copies an export from another package into the target package, with everything it references (LEX 'clone all references'). "
                 + "Assets from globally loaded files become imports. Returns the new export and the relink report." + WriteRule)]
    public static PortResult ExportImport(
        [Description("Source package: " + PackageDescription)] string sourcePackage,
        [Description("Export in the source package: " + ExportDescription)] string export,
        [Description("Target package (written): " + PackageDescription)] string targetPackage,
        [Description("Parent in the target (UIndex or path). Default: the export with the source parent's path, e.g. TheWorld.PersistentLevel.")] string? targetParent = null,
        [Description("Add the copy to the target level's Actors list (for actors).")] bool addToLevelActors = false,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => ExportPorter.Port(PackageTools.ParseGame(game), sourcePackage, export, targetPackage, targetParent, addToLevelActors));
}
