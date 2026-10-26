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
