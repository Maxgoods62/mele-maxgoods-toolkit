using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

[McpServerToolType]
public static class StatusTools
{
    [McpServerTool(Name = "le_status", ReadOnly = true),
     Description("Initializes LegendaryExplorerCore if needed and reports its version, the resolved LE1/LE2/LE3 game paths and whether the Oodle decompressor loaded.")]
    public static LecStatus LeStatus() => ToolErrors.Run(LecHost.EnsureInitialized);

    [McpServerTool(Name = "le_refresh_files", Idempotent = true),
     Description("Forgets the cached list of game and DLC mod files. Call after installing a mod or copying files into the game.")]
    public static string LeRefreshFiles() => ToolErrors.Run(() =>
    {
        LecHost.EnsureInitialized();
        GamePaths.RefreshLoadedFiles();
        return "File caches cleared; the next lookup rescans the game folders.";
    });

    [McpServerTool(Name = "le_find_file", ReadOnly = true),
     Description("Which copy of a file the game loads: every copy in BioGame, Engine and the DLC folders, the winner first, and why each "
                 + "other one is skipped (overridden by a higher mount, folder not mounted, dropped on a mount tie, extension not in the TOC). "
                 + "Reads the disk on each call, no cache. LE1 emulates the installed AutoTOC ASI, which writes the TOC the game loads from "
                 + "(Rules says which: v2 takes DLC_ folders and [ME1DLCMOUNT] ModMount, v3 DLC_MOD_ folders), then compares with "
                 + "PCConsoleTOC.bin from the last boot. LE2/LE3 follow LEC (DLC_ folders with a Mount.dlc). FolderWarnings: mods that stop "
                 + "the game at boot, shared mounts, folders the Autoload ASI ignores. Notes: stale TOC, and LecPath when the other tools "
                 + "(LEC's cached list) open a different copy.")]
    public static FindFileResult LeFindFile(
        [Description("File name, e.g. BIOA_NOR10_07_DSG (no extension = .pcc) or Textures_DLC_MOD_X.tfc, or a wildcard (* and ?), e.g. BIONPC_*. "
                     + "A path keeps only its file name.")] string name,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1",
        [Description("Maximum files returned (TotalMatches still counts all).")] int limit = 50)
        => ToolErrors.Run(() => FileFinder.Find(PackageTools.ParseGame(game), name, limit));
}
