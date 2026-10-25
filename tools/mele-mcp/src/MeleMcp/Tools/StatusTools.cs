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
}
