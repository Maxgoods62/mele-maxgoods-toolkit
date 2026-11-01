using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>LE3 plot conditionals (.cnd files). LE1 and LE2 keep theirs as script functions: export_decompile and script_compile.</summary>
[McpServerToolType]
public static class ConditionalTools
{
    [McpServerTool(Name = "cnd_get", ReadOnly = true),
     Description("Reads an LE3 conditionals file (.cnd): the ids, and the text of one conditional or of all of them. An entry with Hex has an operator LEC cannot decompile (it prints ()). "
                 + "roundTrip=true decompiles and recompiles every entry and counts how many give the same bytes. LE1 and LE2 have no .cnd files.")]
    public static CndListing CndGet(
        [Description("File name of a loaded .cnd (default the base game's; a DLC's is Conditionals<DLC folder name>.cnd), or a full Windows or /mnt/<drive> path.")] string file = "Conditionals.cnd",
        [Description("Conditional id to decompile. Omit to list ids.")] int? id = null,
        [Description("Decompile every entry, up to limit.")] bool all = false,
        [Description("Maximum entries with text when all is true.")] int limit = 200,
        [Description("Decompile and recompile every entry, then report the counts. Writes nothing.")] bool roundTrip = false,
        [Description("Game: LE3.")] string game = "LE3")
        => ToolErrors.Run(() => ConditionalFiles.Get(PackageTools.ParseGame(game), file, id, all, limit, roundTrip));

    [McpServerTool(Name = "cnd_set", Destructive = true),
     Description("Compiles conditional text into a mod's LE3 .cnd file: adds the id or replaces it, and creates the file if it is missing. "
                 + "A DLC's file is found by name: <DLC folder>\\CookedPCConsole\\Conditionals<DLC folder name>.cnd, e.g. DLC_MOD_Example\\CookedPCConsole\\ConditionalsDLC_MOD_Example.cnd "
                 + "(the exe's name pattern, vanilla DLC, and the LE3 Community Framework's file); Warnings says when the path differs. "
                 + "Text as cnd_get prints it, each comparison in its own parentheses, e.g. "
                 + "((plot.bools[12345] == Bool true) && (plot.ints[678] >= i2)). The result is decompiled and must match the text, else nothing is written: LEC's compiler drops "
                 + "what it does not parse without an error. It has no '-', no '/' and no fractional float constants. "
                 + "Tested on scratch copies, not in game: that the game reads a file written here is not shown. A new file must be in the DLC's TOC (AutoTOC at install). "
                 + "Backs an existing file up first and refuses files inside the game install.")]
    public static CndEdit CndSet(
        [Description("The mod's .cnd file: full Windows or /mnt/<drive> path.")] string file,
        [Description("Conditional id.")] int id,
        [Description("Conditional text, as cnd_get prints it.")] string text,
        [Description("Game: LE3.")] string game = "LE3")
        => ToolErrors.Run(() => ConditionalFiles.Set(PackageTools.ParseGame(game), file, id, text));
}
