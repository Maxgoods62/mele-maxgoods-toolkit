using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Whole mods: build every source file, install with ME3Tweaks Mod Manager.</summary>
[McpServerToolType]
public static class ModTools
{
    [McpServerTool(Name = "mod_build", Destructive = true),
     Description("Builds the YAML files at the top of a mod's source folder (not its subfolders): plot files (with transitions, consequences or quests) through plot_build, "
                 + "then level files (with npcs, usables, props, streaming, characterLights, startPoints or squad) through level_build, then conversation files (with 'conversation') through conv_build, then again "
                 + "the level files whose NPCs named a conversation the level did not have yet. A YAML file with none of these keys is skipped and listed. Stops at the first error, naming the file; files "
                 + "built before it stay saved, and the error lists each file they saved with its backup. A file's 'package' is a full path, or a path under the mod root from the DLC folder on "
                 + "(DLC_MOD_MyMod\\CookedPCConsole\\BIOA_MyMap.pcc): the YAML's folder when it has that DLC folder, else the mod of M3's library "
                 + "that has it; a missing level or plot package is created from either. A file may have a top-level 'requires: mele-mcp >= X.Y.Z' "
                 + "(the oldest server that builds it): every file is checked first, and an older server builds nothing.")]
    public static ModBuildResult ModBuild(
        [Description("Game: LE1.")] string game,
        [Description("Folder with the mod's YAML files, e.g. the repo's mods/<name>.")] string folder,
        [Description("Mod root to build into instead (Windows or /mnt/<drive> path), e.g. a temp folder: every package goes under it by "
                     + "its DLC folder, and a DLC folder it does not have yet is first copied from where the build would write without out. "
                     + "GlobalTlk files and the AutoLoad.ini are changed in that copy (a build that would change one outside it is refused), "
                     + "and levels.json is left alone: the build changes nothing outside out (backups aside). Omitted: each package where its file puts it.")] string? @out = null)
        => ToolErrors.Run(() => ModBuilder.Build(PackageTools.ParseGame(game), folder, @out));

    [McpServerTool(Name = "mod_check", ReadOnly = true),
     Description("Checks an LE1 mod's built files in M3's library before installing: refs (LEX's reference check), streaming (stream state "
                 + "without InChunkName: pawns frozen and black), startpoints (pathless start point: henchmen 'destination blocked'), strings "
                 + "(strref without text per language), facefx (spoken line without FXA_<strref> lip sync), ids (plot, quest or string ids "
                 + "another installed mod also defines). Run it after mod_build, before mod_install.")]
    public static ModCheckResult ModCheck(
        [Description("Game: LE1.")] string game,
        [Description("moddesc.ini, a mod folder, or a mod's folder name in M3's library, e.g. 'My Mod'.")] string mod,
        [Description("Comma-separated checks (default all): refs, streaming, startpoints, strings, facefx, ids.")] string? checks = null,
        [Description("Languages whose text must exist, comma-separated.")] string languages = "INT,FR",
        [Description("Maximum findings returned (Errors and Warnings still count all).")] int limit = 100)
        => ToolErrors.Run(() => ModChecker.Check(PackageTools.ParseGame(game), mod, Split(checks), Split(languages), limit));

    private static string[] Split(string? list) => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [McpServerTool(Name = "mod_install", Destructive = true),
     Description("Installs a mod into the game with ME3Tweaks Mod Manager (M3 --installmod, as LEX does): M3 copies the DLC folders, merges "
                 + "ConfigDelta files into Coalesced and runs AutoTOC. Refuses while the game runs, or while another mele-mcp call closes, "
                 + "cycles or installs that game or installs or boots through M3 (it waits 5 s first). M3 is a GUI app (its window comes up); this "
                 + "waits for the end of the install in M3's log, then compares every [CUSTOMDLC] file with its installed copy. A merge mod patches a core "
                 + "file in place: Differences names it when M3 logged no save, and the log holds M3's merge lines. A failed install stops the wait "
                 + "at once, with M3's dialog left open for a click.")]
    public static ModInstallResult ModInstall(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("moddesc.ini, a mod folder, or a mod's folder name in M3's library, e.g. 'My Mod'.")] string mod,
        [Description("Start the game after the install (M3 --bootgame).")] bool bootGame = false,
        [Description("Seconds to wait for M3 (it may first have to start).")] int timeoutSeconds = 180)
        => ToolErrors.Run(() => ModInstaller.Install(PackageTools.ParseGame(game), mod, bootGame, timeoutSeconds));
}
