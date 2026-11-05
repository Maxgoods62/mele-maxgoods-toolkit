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
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("Package file name (e.g. BIOA_NOR10.pcc, resolved to the highest mounted copy) or a full Windows or /mnt/<drive> path.")] string package,
        [Description("How many export classes to list.")] int topClasses = 15)
        => ToolErrors.Run(() => PackageInspector.Summarize(ParseGame(game), package, topClasses));

    [McpServerTool(Name = "pkg_find_name", ReadOnly = true),
     Description("Finds loaded packages whose name table contains a substring: tags, object names, Kismet variable names... "
                 + "Answers 'which files mention X'. Narrow with fileFilter (wildcards * and ?), a full scan of LE1 takes a while.")]
    public static NameSearchResult PkgFindName(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("Case-insensitive substring of a name, e.g. nor10_doctor.")] string nameContains,
        [Description("File name wildcard, e.g. BIOA_NOR10*. Default: all packages.")] string fileFilter = "*",
        [Description("Maximum files returned (FilesScanned still counts all).")] int limit = 100)
        => ToolErrors.Run(() => NameFinder.Find(ParseGame(game), nameContains, fileFilter, limit));

    [McpServerTool(Name = "pkg_to_text", Destructive = true, Idempotent = true),
     Description("A package's export properties as text, git-friendly: one 'Begin Object Class=… Name='<path>' … End Object' block per export, "
                 + "in export order, the format LEC's CompileBulkPropertiesFile reads back. Leaves out script exports (export_decompile), "
                 + "class defaults and trash; an export that fails to decompile is listed in Skipped, the rest still written. "
                 + "BinaryNotInText counts exports whose binary data (meshes, textures, sounds) the text does not hold. "
                 + "Returns the text, or writes it to outPath. roundTrip=true proves the text compiles back to the same properties. "
                 + "Never modifies the package.")]
    public static PackageTextResult PkgToText(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("Package file name (e.g. BIOA_NOR10.pcc, resolved to the highest mounted copy) or a full Windows or /mnt/<drive> path.")] string package,
        [Description("Exact class name to keep, e.g. BioPawn. Omit for all classes.")] string? className = null,
        [Description("Case-insensitive substring of the instanced full path to keep, e.g. PersistentLevel.BioPawn_0.")] string? pathContains = null,
        [Description("Text file to write (Windows or /mnt/<drive> path), e.g. in the mod's source folder. Replaces this package's earlier text there; "
                     + "refuses the game install and any other existing file. Omitted, the text is returned (up to maxChars).")] string? outPath = null,
        [Description("Put a //#<UIndex> line before each block (as LEC does). False gives smaller diffs when exports are added or removed.")] bool uIndexComments = true,
        [Description("Empty each export's properties in an unsaved copy of the package, compile the text back, then compare: RoundTrip.Same (decompiles "
                     + "to the same text) and BytesSame (byte-identical properties) say the text alone rebuilds them. Errors lists what LEC's compiler rejects.")] bool roundTrip = false,
        [Description("Largest text returned when there is no outPath.")] int maxChars = 60000)
        => ToolErrors.Run(() => PackageText.Dump(ParseGame(game), package, className, pathContains, outPath, uIndexComments, roundTrip, maxChars));

    [McpServerTool(Name = "level_new", Destructive = false),
     Description("Creates an empty level package (TheWorld.PersistentLevel with its WorldInfo) for new actors, e.g. a file the LE1CP Sideloader loads. "
                 + "The file name becomes the level name. Refuses an existing file and anything inside the game install.")]
    public static NewLevelResult LevelNew(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("New .pcc path (Windows or /mnt/<drive>), usually in the mod's CookedPCConsole folder in the M3 library.")] string path)
        => ToolErrors.Run(() => LevelCreator.Create(ParseGame(game), path));

    internal static MEGame ParseGame(string game) => game.Trim().ToUpperInvariant() switch
    {
        "LE1" => MEGame.LE1,
        "LE2" => MEGame.LE2,
        "LE3" => MEGame.LE3,
        _ => throw new ArgumentException($"Unsupported game '{game}'. Use LE1, LE2 or LE3."),
    };
}
