using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Career saves of LE2 and LE3. Trilogy Save Editor has no command line, so these read and write the files themselves.</summary>
[McpServerToolType]
public static class SaveTools
{
    private const string GameParam = "Game: LE2 or LE3. LE1 saves have another layout and are refused.";
    private const string SaveParam = "A .pcsav: full Windows or /mnt/<drive> path, or a path under the game's save folder, e.g. John_12_Soldier_010121_1a2b3c4/Save_0084.";
    private const string RunningParam = "Write while this game runs. A session already loaded keeps what it read, and its next save writes that back.";

    [McpServerTool(Name = "save_list", ReadOnly = true),
     Description("Lists a game's careers (saves, default and morph heads), or one career's saves (player, class, hours, map, the save's own time, head). "
                 + "Each file is read to its checksum: a damaged one is listed with its error. .bak files are counted, not read.")]
    public static SaveListResult SaveList(
        [Description(GameParam)] string game,
        [Description("Career folder name, e.g. John_12_Soldier_010121_1a2b3c4. Omit for every career.")] string? career = null)
        => ToolErrors.Run(() => SaveFiles.List(PackageTools.ParseGame(game), career));

    [McpServerTool(Name = "save_read", ReadOnly = true),
     Description("One save: player, class, map, difficulty, face code, head, table sizes, DLC, every field save_set can write, and any plot value asked for. "
                 + "Refuses a file whose checksum fails or that does not parse to its checksum.")]
    public static SaveDetails SaveRead(
        [Description(GameParam)] string game,
        [Description(SaveParam)] string save,
        [Description("Plot values to read: bool:<id>, int:<id>, float:<id>.")] string[]? values = null)
        => ToolErrors.Run(() => SaveFiles.Read(PackageTools.ParseGame(game), save, values));

    [McpServerTool(Name = "save_head_default", Destructive = true, Idempotent = true),
     Description("Gives one save, or every .pcsav of a career, the game's default head for Shepard's sex: clears bHasMorphHead, drops the morph, redoes the checksum. "
                 + "On one LE3 save it gave the same bytes as Trilogy Save Editor's head removal. Every other byte stays, and the file keeps its times. Backs each changed file up to "
                 + "<backups>\\saves\\<game>\\<career>\\<time>. A file that fails is reported and left as it is; .bak files are not touched. Refuses while the game runs unless allowRunning.")]
    public static SaveHeadResult SaveHeadDefault(
        [Description(GameParam)] string game,
        [Description("A career folder (every save in it) or one save. " + SaveParam)] string target,
        [Description("Check and report, write nothing.")] bool dryRun = false,
        [Description(RunningParam)] bool allowRunning = false)
        => ToolErrors.Run(() => SaveFiles.HeadDefault(PackageTools.ParseGame(game), target, dryRun, allowRunning));

    [McpServerTool(Name = "save_set", Destructive = true, Idempotent = true),
     Description("Writes values in one save: a field listed by save_read (level, xp, talentPoints, credits and the other resources, fuel; LE3: health, grenades) or a plot value "
                 + "(bool:<id>, int:<id>, float:<id>). All or none. Only the value changes: no plot transition or journal update runs. An id past the save's table is refused, "
                 + "except LE3 ints and floats, which gain a pair. Backs the file up first. Refuses while the game runs unless allowRunning.")]
    public static SaveSetResult SaveSet(
        [Description(GameParam)] string game,
        [Description(SaveParam)] string save,
        [Description("Values: {name, value}. True/False or 1/0 for a bool, a number otherwise.")] SaveValue[] values,
        [Description("Check and report, write nothing.")] bool dryRun = false,
        [Description(RunningParam)] bool allowRunning = false)
        => ToolErrors.Run(() => SaveFiles.Set(PackageTools.ParseGame(game), save, values, dryRun, allowRunning));
}
