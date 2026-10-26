using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Live link to the running game through the LEX Interop ASI. Nothing here edits files.</summary>
[McpServerToolType]
public static class GameTools
{
    private const string GameParam = "Game: LE1, LE2 or LE3.";
    private const string LevelParam = "Loaded level package name without .pcc, e.g. BIONPC_Chakwas (see game_actors).";
    private const string ActorParam = "Actor object name in that level, e.g. BioPawn_4.";

    [McpServerTool(Name = "game_status", ReadOnly = true),
     Description("Checks the live link: game running, LEX Interop pipe present, and a round trip that proves replies come back. Close LEX first: it takes the replies.")]
    public static GameStatus GameStatus([Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => GameLink.Status(PackageTools.ParseGame(game)));

    [McpServerTool(Name = "game_console"),
     Description("Runs a console command in the game (e.g. 'ShowDebug', 'ghost', 'teleport'). No output comes back.")]
    public static string GameConsole([Description("Console command text.")] string command, [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => { GameLink.Console(PackageTools.ParseGame(game), command); return $"Sent: {command}"; });

    [McpServerTool(Name = "game_get", ReadOnly = true),
     Description("Reads a property of every live object of a class in the running game, with the console command getall; the answer is "
                 + "read from the DebugLogger ASI's log (needs that ASI). E.g. BioPawn bTickIsDisabled (stasis), BioWorldInfo bHasPathNodes, "
                 + "BioTriggerStream StreamingStates. Subclasses are included; class defaults are not.")]
    public static LiveValues GameGet(
        [Description("Class name, e.g. BioPawn.")] string className,
        [Description("Property name, exact, e.g. bTickIsDisabled.")] string property,
        [Description("Keep objects whose path contains this, e.g. BIOA_CLD01 or BioPawn_4.")] string? nameContains = null,
        [Description("Seconds to wait for the answer.")] double timeoutSeconds = 3,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveProperties.Get(PackageTools.ParseGame(game), className, property, nameContains, timeoutSeconds));

    [McpServerTool(Name = "game_plot", Destructive = true),
     Description("Reads, or reads then sets, LE1 plot variables in the running game (a save must be loaded), through BioWare's cheat "
                 + "execs DumpPlotManagerValueByIndex / InitPlotManagerValueByIndex; answers come from the DebugLogger ASI's log. By id or "
                 + "by name from LEX's plot databases (base game + ModPlotsLE1). Setting changes only the value: no transition, consequence "
                 + "or journal update runs, and the change is saved with the next save. Test a conversation branch or quest step without replaying.")]
    public static PlotValues GamePlot(
        [Description("bool, int or float.")] string kind = "bool",
        [Description("Plot ids, e.g. [8421, 8422].")] int[]? ids = null,
        [Description("Also every id whose LEX database path contains this, e.g. Paragon.")] string? name = null,
        [Description("Set this value after reading the current one (one id; Was holds the old value): True/False or 1/0 for a bool, a number for int and float. Omit to only read.")] string? value = null,
        [Description("Seconds to wait for the answers (for each of read and set).")] double timeoutSeconds = 4,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LivePlot.Run(PackageTools.ParseGame(game), kind, ids ?? [], name, value, timeoutSeconds));

    [McpServerTool(Name = "game_log", ReadOnly = true),
     Description("Reads the DebugLogger ASI's log of the current or last game session (Binaries\\Win64\\Logs\\DebugLogger.log): UnrealScript "
                 + "LogInternal lines, warnings, and script errors with their arguments and locals. Filters: from the last load of a map, "
                 + "errors only, text. Something works in vanilla and not in ours? Play both in one session and compare their entries.")]
    public static DebugLogResult GameLog(
        [Description("Start at the last load of this map (persistent level file name without .pcc, e.g. BIOA_CLD01).")] string? sinceMap = null,
        [Description("Only entries containing this text (case-insensitive), e.g. stasis, Hench, Main_Sequence.")] string? contains = null,
        [Description("Only errors, warnings and 'Accessed None'.")] bool errorsOnly = false,
        [Description("Maximum entries returned: the last ones that match.")] int limit = 100,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => DebugLog.Read(PackageTools.ParseGame(game), sinceMap, contains, errorsOnly, limit));

    [McpServerTool(Name = "game_kismet_log", ReadOnly = true),
     Description("Reads the Kismet Logger ASI's trace of the current or last game session (Binaries\\Win64\\Logs\\KismetLogger.log): every "
                 + "sequence op activated, in order, with its level, instanced path (for export_get) and input pin. Did my Kismet run? Do the "
                 + "thing in game, then read the last seconds filtered by your level; grouped shows each op once with its count.")]
    public static KismetLogResult GameKismetLog(
        [Description("Only activations whose class, path or pin contains this (case-insensitive), e.g. BIOA_CLD01, Main_Sequence, RemoteEvent.")] string? contains = null,
        [Description("Only the last seconds of the log, e.g. 30 right after doing something in game.")] double? lastSeconds = null,
        [Description("One entry per op and pin, in order of first activation, with its count.")] bool grouped = false,
        [Description("Maximum entries returned: the last ones that match.")] int limit = 100,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => KismetLog.Read(PackageTools.ParseGame(game), contains, lastSeconds, grouped, limit));

    [McpServerTool(Name = "game_screenshot", ReadOnly = true),
     Description("Saves a PNG of the game window (windowed or borderless; works behind other windows) and returns its Windows and WSL "
                 + "paths: open the WSL path with Read to see it. Check a change yourself (lit? placed? visible?) instead of asking the user.")]
    public static Screenshot GameScreenshot(
        [Description("Where to save the PNG (Windows or /mnt/<drive> path). Default: %LOCALAPPDATA%\\mele-mcp\\screenshots\\<game>_<time>.png.")] string? path = null,
        [Description("Scale down to this width in pixels (0: full size).")] int maxWidth = 1280,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => Core.GameScreenshot.Take(PackageTools.ParseGame(game), path is null ? null : GamePaths.ToWindowsPath(path), maxWidth));

    [McpServerTool(Name = "game_cycle", Destructive = true),
     Description("The whole test loop in one call: closes the running game WITHOUT SAVING (console exit; force kills it), runs mod_check "
                 + "(stops on errors) and mod_install with boot (or only boots, with no mod), waits for the main menu in the DebugLogger "
                 + "log, loads a developer save slot (BioLoadGame <n> loads Game\\ME1\\BioGame\\Save\\BioSaveGame<n>.sav, which the console's "
                 + "BioSaveGame <n> writes where Shepard stands; career saves can't be loaded this way), runs console "
                 + "commands (e.g. 'at BIOA_CLD01 start_cld01'), waits for the map and takes a screenshot to Read. Takes 1–3 minutes.")]
    public static GameCycleResult GameCycle(
        [Description("Mod to check and install first (as mod_install takes it, e.g. 'Claude Test Chakwas'); omit to only restart.")] string? mod = null,
        [Description("Developer save slot to load from the menu: n for BioSaveGame<n>.sav, made once in game with game_console 'BioSaveGame <n>'.")] int? loadSlot = null,
        [Description("Console commands to run after the load, in order.")] string[]? commands = null,
        [Description("Map to wait for (persistent level file name without .pcc, e.g. BIOA_CLD01); default the first one the save loads.")] string? waitForMap = null,
        [Description("Run mod_check before installing and stop on errors.")] bool check = true,
        [Description("Languages mod_check requires text for, comma-separated.")] string languages = "INT,FR",
        [Description("Take a screenshot at the end.")] bool screenshot = true,
        [Description("Seconds to wait after the menu and after the map, for the loading screen to clear.")] double settleSeconds = 8,
        [Description("Kill the game if it does not close within 30 s of exit.")] bool force = false,
        [Description("Seconds to wait for the menu, and again for the map.")] int timeoutSeconds = 240,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => Core.GameCycle.Run(PackageTools.ParseGame(game), new Core.GameCycle.Options(
            mod, check, languages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), loadSlot,
            commands ?? [], waitForMap, settleSeconds, screenshot, force, timeoutSeconds)));

    [McpServerTool(Name = "game_remote_event"),
     Description("Fires every enabled Kismet SeqEvent_RemoteEvent with this name in the running game. "
                 + "Do not fire LE1CP NPC framework events (Live_NPC_*, Poll_NPC_*): one hung LE1 in a streaming loop on 2026-10-25.")]
    public static string GameRemoteEvent([Description("Remote event name.")] string eventName, [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => { GameLink.RemoteEvent(PackageTools.ParseGame(game), eventName); return $"Fired remote event {eventName}"; });

    [McpServerTool(Name = "game_stream_level"),
     Description("Streams a level package in or out of the running game ('only' = load without making it visible).")]
    public static string GameStreamLevel(
        [Description("Level package name without .pcc.")] string level,
        [Description("in, out or only.")] string mode = "in",
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => { GameLink.StreamLevel(PackageTools.ParseGame(game), level, mode); return $"Stream {mode}: {level}"; });

    [McpServerTool(Name = "game_player_location", ReadOnly = true),
     Description("Shepard's position and rotation in the running game (Unreal units; yaw 65536 = 360°). Use it to place things where the player stands.")]
    public static PlayerLocation GamePlayerLocation([Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => GameLink.PlayerLocation(PackageTools.ParseGame(game)));

    [McpServerTool(Name = "game_actors", ReadOnly = true),
     Description("Lists the actors of every visible level in the running game (level, object name, tag), with optional filters.")]
    public static LiveActors GameActors(
        [Description("Substring of the level name, e.g. BIONPC.")] string? levelContains = null,
        [Description("Substring of the actor object name, e.g. BioPawn.")] string? nameContains = null,
        [Description("Substring of the actor tag, e.g. claude.")] string? tagContains = null,
        [Description("Maximum actors returned.")] int limit = 200,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => GameLink.Actors(PackageTools.ParseGame(game), levelContains, nameContains, tagContains, limit));

    [McpServerTool(Name = "game_actor_get", ReadOnly = true),
     Description("Live location, rotation, draw scale and hidden flag of one actor in the running game.")]
    public static ActorState GameActorGet([Description(LevelParam)] string level, [Description(ActorParam)] string actor, [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => GameLink.GetActor(PackageTools.ParseGame(game), level, actor));

    [McpServerTool(Name = "game_actor_set"),
     Description("Moves, turns or hides one actor in the running game, then reads it back. Live only: write the values into the package with export_set_batch to keep them.")]
    public static ActorState GameActorSet(
        [Description(LevelParam)] string level,
        [Description(ActorParam)] string actor,
        float? x = null, float? y = null, float? z = null,
        [Description("Unreal rotation units (65536 = 360°).")] int? pitch = null,
        [Description("Unreal rotation units (65536 = 360°).")] int? yaw = null,
        [Description("Unreal rotation units (65536 = 360°).")] int? roll = null,
        bool? hidden = null,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => GameLink.SetActor(PackageTools.ParseGame(game), level, actor, x, y, z, pitch, yaw, roll, hidden));

    [McpServerTool(Name = "game_hot_reload"),
     Description("Swaps a streamed level in the running game for a package file, without restarting. The copy loads under a new name "
                 + "(<level>_LIVE<hhmmss>, since the game prefers files on disk) after the original is streamed out; use that name with game_actor_*. "
                 + "Pass the mod's library path to test an edit before installing it. LE1: any streaming refresh (BioTriggerStream touch, Kismet "
                 + "SetStreamingState) unloads the copy and the LE1CP Sideloader re-forces its own levels; on the Normandy this happened within 4 s.")]
    public static HotReloadResult GameHotReload(
        [Description("Package path (Windows or /mnt/<drive>) or file name.")] string package,
        [Description("Level name in game if it differs from the file name.")] string? level = null,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => GameLink.HotReload(PackageTools.ParseGame(game), package, level));

    [McpServerTool(Name = "npc_place", Destructive = true),
     Description("Puts an NPC of a level_build YAML file where Shepard stands in the running game (or at given x, y, z): writes her "
                 + "'location' and 'yaw' into the file (comments kept), runs level_build, and moves her and her lights in the running game "
                 + "as a preview. She faces the way Shepard faces (add 'turn' degrees, e.g. 180 to face him). Install the level afterwards "
                 + "(game closed, mod_install) to keep her there.")]
    public static NpcPlacement NpcPlace(
        [Description("Level YAML file (level_build format).")] string file,
        [Description("NPC name in the file's 'npcs' (optional when it has one).")] string? npc = null,
        float? x = null, float? y = null, float? z = null,
        [Description("Yaw in degrees, instead of Shepard's.")] float? yaw = null,
        [Description("Degrees added to the yaw (180: face where Shepard came from).")] float turn = 0,
        [Description("Run level_build after editing the file.")] bool build = true,
        [Description("Move the actors in the running game too.")] bool live = true,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => NpcPlacer.Place(PackageTools.ParseGame(game), file, npc, x, y, z, yaw, turn, build, live));
}
