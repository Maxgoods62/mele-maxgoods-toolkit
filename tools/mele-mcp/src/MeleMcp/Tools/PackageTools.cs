using System.ComponentModel;
using LegendaryExplorerCore.Packages;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

[McpServerToolType]
public static class PackageTools
{
    [McpServerTool(Name = "pkg_info", ReadOnly = true),
     Description("Summarizes a package (.pcc): counts of names/imports/exports, embedded LE1 talk files, and the most common export classes.")]
    public static PackageSummary PkgInfo(
        [Description("Package file name (e.g. BIOA_NOR10.pcc, resolved to the highest mounted copy) or a full Windows or /mnt/<drive> path.")] string package,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1",
        [Description("How many export classes to list.")] int topClasses = 15)
        => ToolErrors.Run(() => PackageInspector.Summarize(ParseGame(game), package, topClasses));

    [McpServerTool(Name = "pkg_find_name", ReadOnly = true),
     Description("Finds loaded packages whose name table contains a substring: tags, object names, Kismet variable names... "
                 + "Answers 'which files mention X'. Narrow with fileFilter (wildcards * and ?), a full scan of LE1 takes a while.")]
    public static NameSearchResult PkgFindName(
        [Description("Case-insensitive substring of a name, e.g. nor10_doctor.")] string nameContains,
        [Description("File name wildcard, e.g. BIOA_NOR10*. Default: all packages.")] string fileFilter = "*",
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1",
        [Description("Maximum files returned (FilesScanned still counts all).")] int limit = 100)
        => ToolErrors.Run(() => NameFinder.Find(ParseGame(game), nameContains, fileFilter, limit));

    [McpServerTool(Name = "level_new", Destructive = false),
     Description("Creates an empty level package (TheWorld.PersistentLevel with its WorldInfo) for new actors, e.g. a file the LE1CP Sideloader loads. "
                 + "The file name becomes the level name. Refuses an existing file and anything inside the game install.")]
    public static NewLevelResult LevelNew(
        [Description("New .pcc path (Windows or /mnt/<drive>), usually in the mod's CookedPCConsole folder in the M3 library.")] string path,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => LevelCreator.Create(ParseGame(game), path));

    internal static MEGame ParseGame(string game) => game.Trim().ToUpperInvariant() switch
    {
        "LE1" => MEGame.LE1,
        "LE2" => MEGame.LE2,
        "LE3" => MEGame.LE3,
        _ => throw new ArgumentException($"Unsupported game '{game}'. Use LE1, LE2 or LE3."),
    };
}
