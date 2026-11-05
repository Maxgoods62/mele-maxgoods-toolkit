using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Picking plot and string ids that no other content uses.</summary>
[McpServerToolType]
public static class IdTools
{
    [McpServerTool(Name = "id_alloc", ReadOnly = true),
     Description("Finds free LE1 plot or string ids, or checks given ones, against: LEX's base-game plot database, LEX mod plot databases "
                 + "(ModPlotsLE1), the base game's plot maps, conditionals and GlobalTlk, and what the base game and every installed DLC wire "
                 + "in Autoload.ini (StateTransitionMap/ConsequenceMap events and the variables they write, QuestMap quest ids, BioAutoConditionals F<id> functions, "
                 + "GlobalTalkTable strings). Each used id lists who uses it.")]
    public static IdReport IdAlloc(
        [Description("Game: LE1.")] string game,
        [Description("bool, int, float, conditional, transition, consequence, quest (BioQuestMap quest id) or strref.")] string kind,
        [Description("Ids to check. Omit to search from 'from'.")] int[]? ids = null,
        [Description("First id to try, e.g. the start of your mod's block (8421 for plot, 74210000 for strings).")] int? from = null,
        [Description("How many free ids to return when searching.")] int count = 1,
        [Description("DLC folders to scan besides the installed ones, e.g. your mod's M3 library copy (Windows or /mnt/<drive> path).")] string[]? extraDlcFolders = null,
        [Description("Extra packages whose plot maps or BioTlkFile exports count, e.g. a conversation's level or _LOC_ file. File name or full path.")] string[]? packages = null)
        => ToolErrors.Run(() => IdAllocator.Find(PackageTools.ParseGame(game), kind, ids, from, count, extraDlcFolders, packages));
}
