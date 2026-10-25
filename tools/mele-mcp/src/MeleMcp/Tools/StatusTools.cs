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
}
