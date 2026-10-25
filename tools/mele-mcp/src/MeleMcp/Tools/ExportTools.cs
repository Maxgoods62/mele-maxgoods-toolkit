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

    [McpServerTool(Name = "export_set", Destructive = true),
     Description("Sets one leaf property of an export and saves the package, after copying it to the backup folder. "
                 + "Refuses base-game files: edit a copy inside a DLC_MOD_ folder. Returns old and new value and the backup path.")]
    public static PropertyEdit ExportSet(
        [Description(PackageDescription)] string package,
        [Description(ExportDescription)] string export,
        [Description("Dot path to a leaf property, with [i] for array elements, e.g. VectorParameterValues[11].ParameterValue.G")] string propertyPath,
        [Description("New value: number, True/False, name, string, $123 for a TLK string ref, #123 for an object reference (0 = None).")] string value,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => PropertyEditor.SetProperty(PackageTools.ParseGame(game), package, export, propertyPath, value));
}
