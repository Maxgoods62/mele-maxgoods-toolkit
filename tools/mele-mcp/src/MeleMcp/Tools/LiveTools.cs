using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Live objects in the running game through our own ASI, MeleBridge (tools/mele-bridge). Nothing here edits files.</summary>
[McpServerToolType]
public static class LiveTools
{
    private const string GameParam = "Game: LE1 (the bridge is built for LE1 only).";
    private const string ObjectParam = "Object path as live_find returns it, e.g. BIOA_MYM01.TheWorld.PersistentLevel.BioPawn_3.";
    private const string TimeoutParam = "Seconds to wait for the game thread.";

    [McpServerTool(Name = "live_find", ReadOnly = true),
     Description("Lists live objects in the running game (MeleBridge ASI): by class (subclasses too, any package), name and path filters. "
                 + "Returns paths for live_get, live_set, live_call and live_describe. Needs a class or a filter.")]
    public static JsonObject LiveFind(
        [Description("Class name, e.g. BioPawn, SeqAct_Interp, BioGlobalVariableTable.")] string? className = null,
        [Description("Keep objects whose name contains this, e.g. BioPawn_3.")] string? name = null,
        [Description("Keep objects whose path contains this, e.g. BIOA_MYM01 or BIONPC_MyGuard.")] string? path = null,
        [Description("Include class default objects.")] bool defaults = false,
        [Description("Maximum objects returned (total still counts all).")] int limit = 100,
        [Description(TimeoutParam)] double timeoutSeconds = 5,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Request(PackageTools.ParseGame(game), new JsonObject
        {
            ["op"] = "find", ["class"] = className, ["name"] = name, ["path"] = path, ["defaults"] = defaults, ["limit"] = limit,
        }, timeoutSeconds));

    [McpServerTool(Name = "live_get", ReadOnly = true),
     Description("Reads properties of one live object (MeleBridge ASI): all of them, or one by path: Location, Location.X, Array[2], "
                 + "Struct.Array[0].Member. Objects come back as paths, enums as names, arrays up to 64 items.")]
    public static JsonObject LiveGet(
        [Description(ObjectParam)] string objectPath,
        [Description("Property path; omit for every property.")] string? property = null,
        [Description("How deep structs and arrays expand (default 1 for all properties, 3 for one).")] int? depth = null,
        [Description(TimeoutParam)] double timeoutSeconds = 5,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Request(PackageTools.ParseGame(game), new JsonObject
        {
            ["op"] = "get", ["object"] = objectPath, ["property"] = property, ["depth"] = depth,
        }, timeoutSeconds));

    [McpServerTool(Name = "live_set", Destructive = true),
     Description("Sets one property of one live object in game memory (MeleBridge ASI), no file changes; returns the new value. Same "
                 + "paths as live_get. Value as JSON: true, 12.5, \"text\", an enum name, an object path or null, {\"X\":1} for struct "
                 + "members. Arrays keep their length: set elements as Name[i]. Nothing else runs (no setter, no replication), so what "
                 + "the renderer or physics took in at attach time does not change: for bHidden, meshes, scale or collision, live_call the "
                 + "actor's own function instead (SetHidden, SetDrawScale, SetCollision), which also refreshes its components.")]
    public static JsonObject LiveSet(
        [Description(ObjectParam)] string objectPath,
        [Description("Property path, e.g. bHidden, Location.Z, DrawScale.")] string property,
        [Description("New value as JSON.")] JsonElement value,
        [Description(TimeoutParam)] double timeoutSeconds = 5,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Request(PackageTools.ParseGame(game), new JsonObject
        {
            ["op"] = "set", ["object"] = objectPath, ["property"] = property, ["value"] = JsonNode.Parse(value.GetRawText()),
        }, timeoutSeconds));

    [McpServerTool(Name = "live_call", Destructive = true),
     Description("Calls an UnrealScript function on one live object through ProcessEvent (MeleBridge ASI): script functions, events, "
                 + "and natives that are not opcodes. Arguments in declaration order, as JSON; omitted optional ones are zero/false/None "
                 + "(script defaults do not apply). Returns the return value and out parameters. See live_describe for signatures. "
                 + "E.g. a BioGlobalVariableTable's GetBool [id].")]
    public static JsonObject LiveCall(
        [Description(ObjectParam)] string objectPath,
        [Description("Function name, e.g. SetHidden, GetBool, TriggerEventClass.")] string function,
        [Description("Arguments as a JSON list, e.g. [true] or [8421].")] JsonElement[]? args = null,
        [Description(TimeoutParam)] double timeoutSeconds = 5,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Request(PackageTools.ParseGame(game), new JsonObject
        {
            ["op"] = "call", ["object"] = objectPath, ["function"] = function,
            ["args"] = new JsonArray((args ?? []).Select(a => JsonNode.Parse(a.GetRawText())).ToArray()),
        }, timeoutSeconds));

    [McpServerTool(Name = "live_console", Destructive = true),
     Description("Runs a console command (MeleBridge ASI: the player's console chain, cheats included) and returns what it printed or "
                 + "logged, e.g. getall BioPawn bTickIsDisabled, or a cheat exec's LogInternal answer. LE1 hands no output back itself, so "
                 + "the lines come from the DebugLogger log (needs that ASI); unrelated lines logged meanwhile show too.")]
    public static ConsoleOutput LiveConsole(
        [Description("Console command text.")] string command,
        [Description(TimeoutParam)] double timeoutSeconds = 10,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Console(PackageTools.ParseGame(game), command, timeoutSeconds));

    [McpServerTool(Name = "live_describe", ReadOnly = true),
     Description("A loaded class's chain, properties with types and declaring class, and functions with signatures (MeleBridge ASI), from "
                 + "an object or a class name. What live_get, live_set and live_call can reach.")]
    public static JsonObject LiveDescribe(
        [Description("Object path (its class is described).")] string? objectPath = null,
        [Description("Or a class name, e.g. BioPawn.")] string? className = null,
        [Description("Include functions.")] bool functions = true,
        [Description(TimeoutParam)] double timeoutSeconds = 5,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Request(PackageTools.ParseGame(game), new JsonObject
        {
            ["op"] = "describe", ["object"] = objectPath, ["class"] = className, ["functions"] = functions,
        }, timeoutSeconds));

    [McpServerTool(Name = "live_editor", ReadOnly = true),
     Description("The in-game editor's state (MeleBridge ASI, F8 in the game; docs/design/in-game-editor.md): open, free cam, camera, canvas "
                 + "and back buffer sizes, the selection, the last click, the projection check (the overlay's arrows against the engine's "
                 + "Canvas.Project, in pixels), which hooks are in, thread ids and input counters. Answers without the game thread, so it "
                 + "works while paused or loading.")]
    public static JsonObject LiveEditor(
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Request(PackageTools.ParseGame(game), new JsonObject { ["op"] = "editor" }, 5));

    [McpServerTool(Name = "live_status", ReadOnly = true),
     Description("One cheap look at the running game (MeleBridge ASI 3+): map, its BioWorldInfo (begunPlay, bPlayersOnly, the "
                 + "focus-loss pause flag), player controller and pawn, streaming levels still loading or changing visibility, the "
                 + "loading movie (null when no SFXLoadMovieManager exists), the squad screen (partySelectOpen, its handler, the movie's "
                 + "member count) and the keep_ticking switch with its counters. Times out while a map loads: the game thread runs no "
                 + "script then.")]
    public static JsonObject LiveStatus(
        [Description(TimeoutParam)] double timeoutSeconds = 3,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Request(PackageTools.ParseGame(game), new JsonObject { ["op"] = "status" }, timeoutSeconds));

    [McpServerTool(Name = "live_keep_ticking", Destructive = true),
     Description("Test switch (MeleBridge ASI 3+), off by default and again at every game start. While on, LE1 does not pause its "
                 + "world and Kismet when its window loses focus (BioWorldInfo.Tick's focus pause); real pauses (menus, squad screen, "
                 + "photo mode, the F8 editor) are untouched. Turning it on also ends a focus pause already in place. Omit 'on' to read "
                 + "it. Returns the switch and its counters.")]
    public static JsonObject LiveKeepTicking(
        [Description("true: on; false: off; omit to read.")] bool? on = null,
        [Description(TimeoutParam)] double timeoutSeconds = 5,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Request(PackageTools.ParseGame(game), new JsonObject { ["op"] = "keep_ticking", ["on"] = on }, timeoutSeconds));

    [McpServerTool(Name = "live_place", Destructive = true),
     Description("Moves a live actor, or spawns a copy of one (MeleBridge ASI), in front of Shepard facing her, where she stands, or at "
                 + "coordinates. Props land on the floor, pawns at Shepard's height. A copy goes into the map's level. A static template "
                 + "(StaticMeshActor: most meshes of a level) can't spawn, so a BioInert shows its mesh instead, lit by its light environment "
                 + "(level_build lights it by prop lights instead when a light of the lights section covers it); pawns can't be copied (the "
                 + "engine crashes). Lasts until the map reloads: live_save writes the spot into "
                 + "the level YAML. Find templates with live_find, e.g. className=BioInert or StaticMeshActor, path=<level>.")]
    public static LivePlacement LivePlace(
        [Description("Actor to move (object path as live_find returns it). Give this or template.")] string? objectPath = null,
        [Description("Actor to copy: a new one is spawned from it. Give this or objectPath.")] string? template = null,
        [Description("Tag for the actor (required to spawn): live_save finds its YAML entry by it, e.g. mymod_prop_crate.")] string? tag = null,
        [Description("front (default: 'distance' in front of Shepard, facing her) or shepard (where she stands, facing her way). Ignored with x, y, z.")] string at = "front",
        [Description("How far in front of Shepard, in Unreal units (100 ≈ 1 m).")] float distance = 100,
        [Description("X; give x, y and z together to place it at coordinates.")] float? x = null,
        [Description("Y.")] float? y = null,
        [Description("Z (the actor's own origin: a pawn's middle, a mesh's pivot).")] float? z = null,
        [Description("Yaw in degrees (default: faces Shepard for front, her way for shepard, unchanged with x, y, z).")] float? yaw = null,
        [Description("Degrees added to the yaw, e.g. -90 for meshes whose front is +Y like the VI terminals.")] float turn = 0,
        [Description("Units above the floor (or above z), e.g. 80 for a table top.")] float raise = 0,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LivePlacer.Place(PackageTools.ParseGame(game), objectPath, template, tag, at, distance, x, y, z, yaw, turn, raise));

    [McpServerTool(Name = "live_remove", Destructive = true),
     Description("Destroys a live actor (MeleBridge ASI), e.g. a copy live_place spawned. A level actor that can't be destroyed "
                 + "(bNoDelete) is hidden without collision instead. Until the map reloads; no file changes.")]
    public static LivePlacement LiveRemove(
        [Description(ObjectParam)] string objectPath,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LivePlacer.Remove(PackageTools.ParseGame(game), objectPath));

    [McpServerTool(Name = "live_save", Destructive = true),
     Description("Writes where a live actor stands into a level_build YAML file, keeping its comments, then builds the level: location, "
                 + "and for npcs and usables a yaw; for props a yaw while the actor is upright, else 'rotation: [pitch, yaw, roll]' in place of "
                 + "'yaw' (never both); for lights the location only. The entry is the npcs, usables, props or lights entry with the actor's tag; "
                 + "with none, a props entry is added that "
                 + "copies the template it was spawned from. Install the level (game closed) to keep it after a reload.")]
    public static LiveSave LiveSave(
        [Description("Level YAML file (level_build format).")] string file,
        [Description("The live actor (object path, e.g. what live_place returned).")] string objectPath,
        [Description("For a new props entry: the template given to live_place (a level actor's object path).")] string? template = null,
        [Description("For a new props entry: its name (default: the tag).")] string? name = null,
        [Description("Build the level after writing the file.")] bool build = true,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LivePlacer.Save(PackageTools.ParseGame(game), file, objectPath, name, template, build));

    [McpServerTool(Name = "live_watch", ReadOnly = true),
     Description("Records ProcessEvent calls of functions for a while (MeleBridge ASI): time, object, class and parameters. Events, "
                 + "delegates and timers go through ProcessEvent; script-to-script calls and natives called from script do not. E.g. "
                 + "Engine.SequenceOp.Activated (script Kismet actions), Engine.Actor.Touch, Engine.Actor.Destroyed.")]
    public static LiveEvents LiveWatch(
        [Description("Function paths, e.g. [\"Engine.SequenceOp.Activated\"]; \"Class.Function\" matches any package.")] string[] functions,
        [Description("Keep calls on objects whose path contains this.")] string? objectContains = null,
        [Description("Seconds to record.")] double seconds = 10,
        [Description("Stop after this many events.")] int maxEvents = 200,
        [Description("Include each call's parameters.")] bool withParams = true,
        [Description(GameParam)] string game = "LE1")
        => ToolErrors.Run(() => LiveBridge.Watch(PackageTools.ParseGame(game), functions, objectContains, withParams, seconds, maxEvents));
}
