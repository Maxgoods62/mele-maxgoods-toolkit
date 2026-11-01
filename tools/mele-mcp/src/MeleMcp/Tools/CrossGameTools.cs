using System.ComponentModel;
using LegendaryExplorerCore.Packages;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>One script object compared across the three games.</summary>
[McpServerToolType]
public static class CrossGameTools
{
    [McpServerTool(Name = "xgame_decompile", ReadOnly = true),
     Description("Decompiles one script object in several games and compares them: for each game the winning copy of the file (le_find_file's answer), the export, "
                 + "and a unified diff against the first game that has it, or a note when it is absent. Searches the core script files (SFXGame, Engine, Core, GFxUI...) "
                 + "without a package name; deepSearch reads every package's tables. Inherited members are not followed: Class.Function means declared on that class. Read-only.")]
    public static XGameResult XGameDecompile(
        [Description("A class (SFXGameModePhoto), Class.Function (SFXGameModePhoto.TakePhoto), Class.State, Class.State.Function or a bare state name, optionally after the file name (SFXGame.SFXGameModePhoto). "
                     + "A name with several matches of the same kind comes back as a note listing them.")] string name,
        [Description("Comma-separated games, compared in this order: the first that has the name is the base.")] string games = "LE1,LE2,LE3",
        [Description("diff: the base game's text and a diff for each other game. full: each game's whole text, no diffs.")] string mode = "diff",
        [Description("Only this package (file name, resolved to its winning copy per game, or a full path), e.g. Startup_INT.pcc.")] string? package = null,
        [Description("When the core script files lack the name, read the tables of every package the game loads: 5000 to 7000 files, 3 to 7 seconds per game measured with the files in the disk cache.")] bool deepSearch = false,
        [Description("Most chars of text and diffs in all. In diff mode the diffs come first, the base text gets the rest. Cuts lists what was cut.")] int maxChars = 60000)
        => ToolErrors.Run(() => CrossGame.Compare(name, ParseGames(games), mode, package, deepSearch, maxChars));

    private static List<MEGame> ParseGames(string games) =>
        games.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(PackageTools.ParseGame).Distinct().ToList();
}
