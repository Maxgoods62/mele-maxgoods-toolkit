using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Whole mods: build every source file, install with ME3Tweaks Mod Manager.</summary>
[McpServerToolType]
public static class ModTools
{
    [McpServerTool(Name = "mod_build", Destructive = true),
     Description("Builds every YAML file in a mod's source folder: plot files (with transitions, consequences or quests) through plot_build, "
                 + "level files (with npcs, streaming or startPoints) through level_build, then conversation files (with 'conversation') through conv_build, then again "
                 + "the level files whose NPCs named a conversation the level did not have yet. Stops at the first error, naming the file; files "
                 + "built before it stay saved.")]
    public static ModBuildResult ModBuild(
        [Description("Folder with the mod's YAML files, e.g. the repo's mods/<name>.")] string folder,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => ModBuilder.Build(PackageTools.ParseGame(game), folder));

    [McpServerTool(Name = "mod_install", Destructive = true),
     Description("Installs a mod into the game with ME3Tweaks Mod Manager (M3 --installmod, as LEX does): M3 copies the DLC folders, merges "
                 + "ConfigDelta files into Coalesced and runs AutoTOC. Refuses while the game runs. M3 is a GUI app (its window comes up); this "
                 + "waits for the end of the install in M3's log, then compares every mod file with its installed copy.")]
    public static ModInstallResult ModInstall(
        [Description("moddesc.ini, a mod folder, or a mod's folder name in M3's library, e.g. 'Claude Test Chakwas'.")] string mod,
        [Description("Start the game after the install (M3 --bootgame).")] bool bootGame = false,
        [Description("Seconds to wait for M3 (it may first have to start).")] int timeoutSeconds = 180,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => ModInstaller.Install(PackageTools.ParseGame(game), mod, bootGame, timeoutSeconds));
}
