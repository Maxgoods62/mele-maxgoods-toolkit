using System.Text;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <param name="Path">The live object, for live_get, live_call and live_save.</param>
/// <param name="StandIn">For a static template: the mesh a spawned BioInert shows in its place (static actors can't spawn).</param>
public sealed record PlacedActor(string Path, string Class, string Tag, float X, float Y, float Z, float YawDegrees, string? StandIn);

/// <param name="Action">spawned, moved, removed or hidden.</param>
/// <param name="From">front (in front of Shepard, facing her), shepard (where she stands, facing her way) or given (coordinates).</param>
public sealed record LivePlacement(string Action, PlacedActor? Actor, string? From, IReadOnlyList<string> Notes);

/// <param name="Entry">The YAML entry written, as section.name.</param>
public sealed record LiveSave(string File, string Entry, bool Added, IReadOnlyList<string> YamlChanges, PlacedActor Actor, LevelBuildResult? Build, IReadOnlyList<string> Notes);

/// <summary>
/// Places actors in the running game through MeleBridge: spawns copies of live actors, moves them, removes them, and writes where
/// one stands into a level YAML file. SetLocation, SetRotation and Destroy are opcode natives, which ProcessEvent drops, so a
/// move writes Location and Rotation and re-attaches the components (ForceUpdateComponents), and a removal goes through the
/// script handler of Kismet's Destroy action (OnDestroy), which calls Destroy() from script.
/// </summary>
public static class LivePlacer
{
    /// <summary>Shepard's Z minus the floor under her: the VI terminals of cabin.yaml and freighter.yaml stand at this height.</summary>
    private const float FloorBelowShepard = 94;

    private sealed record Shepard(string Pawn, float X, float Y, float Z, int Yaw);

    public static LivePlacement Place(MEGame game, string? objectPath, string? template, string? tag, string at, float distance,
        float? x, float? y, float? z, float? yaw, float turn, float raise)
    {
        if ((objectPath is null) == (template is null)) throw new ArgumentException("Give objectPath (move that actor) or template (spawn a copy of it).");
        if (template is not null && string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("A spawned copy needs a 'tag': live_save finds its YAML entry by it.");
        var notes = new List<string>();
        var source = objectPath ?? template!;
        var chain = Chain(game, source);
        if (!chain.Contains("Actor")) throw new ArgumentException($"{source} is a {chain[0]}, not an actor.");
        var isPawn = chain.Contains("Pawn");
        if (template is not null && isPawn)
        {
            // Seen 2026-10-26: Spawn with Dorn's BioPawn as the template faulted inside the engine and the game closed.
            throw new ArgumentException("Spawning a copy of a pawn crashes LE1. Add the NPC to the level YAML instead (npcs, with a template), build and install it, then move it here with live_place objectPath.");
        }

        // Where: given coordinates, else from Shepard.
        string from;
        float px, py, pz;
        float? degrees = yaw;
        if (x is not null || y is not null || z is not null)
        {
            if (x is null || y is null || z is null) throw new ArgumentException("Give x, y and z together, or none to place it from Shepard.");
            (px, py, pz, from) = (x.Value, y.Value, z.Value + raise, "given");
        }
        else
        {
            var shepard = Player(game);
            var facing = shepard.Yaw * 360f / 65536;
            from = at.ToLowerInvariant() switch
            {
                "front" => "front",
                "shepard" or "player" => "shepard",
                _ => throw new ArgumentException($"at is front or shepard, not '{at}'; or give x, y and z."),
            };
            var ahead = from == "front" ? distance : 0;
            px = shepard.X + ahead * MathF.Cos(facing * MathF.PI / 180);
            py = shepard.Y + ahead * MathF.Sin(facing * MathF.PI / 180);
            // A pawn's location is the middle of its collision cylinder, like Shepard's; anything else stands on the floor.
            pz = (isPawn ? shepard.Z : shepard.Z - FloorBelowShepard) + raise;
            degrees ??= from == "front" ? facing + 180 : facing;
        }

        var rotation = Get(game, source, "Rotation")!.AsObject();
        var units = degrees is { } d ? (int)Math.Round(Normalize(d + turn) * 65536 / 360) : rotation["Yaw"]!.GetValue<int>() + (int)Math.Round(turn * 65536 / 360);
        var location = new JsonObject { ["X"] = px, ["Y"] = py, ["Z"] = pz };
        var placed = new JsonObject { ["Pitch"] = rotation["Pitch"]!.GetValue<int>(), ["Yaw"] = units, ["Roll"] = rotation["Roll"]!.GetValue<int>() };

        string path, action;
        string? standIn = null;
        if (template is not null)
        {
            // Spawned where it goes, so nothing it does on spawn (touching volumes, falling) happens elsewhere.
            (path, standIn) = Spawn(game, template, tag!, location, placed, notes);
            action = "spawned";
        }
        else
        {
            path = objectPath!;
            if (tag is not null) Set(game, path, "Tag", tag);
            action = "moved";
            if (Bool(game, path, "bStatic")) notes.Add("A static actor: its baked lighting and shadows stay where it was.");
        }
        Set(game, path, "Location", location.DeepClone());
        Set(game, path, "Rotation", placed.DeepClone());
        // Re-attaching the components moves what is drawn and what collides; a spawned copy's components are still where its template is.
        Call(game, path, "ForceUpdateComponents", true, false);

        if (from != "given") notes.Add(isPawn ? "At Shepard's height (a pawn's location is its middle)." : $"On the floor (Shepard's Z - {FloorBelowShepard}){(raise != 0 ? $", raised {raise}" : "")}.");
        notes.Add("Lasts until the map reloads: live_save writes it into the level YAML.");
        return new LivePlacement(action, Actor(game, path, standIn), from, notes);
    }

    /// <summary>
    /// Destroys a live actor as Kismet's Destroy action does (its OnDestroy handler, given the action's defaults). That handler
    /// only shuts down bNoDelete actors (hidden, no collision), and the engine won't destroy bStatic ones, which are hidden here.
    /// </summary>
    public static LivePlacement Remove(MEGame game, string objectPath)
    {
        var chain = Chain(game, objectPath);
        if (!chain.Contains("Actor")) throw new ArgumentException($"{objectPath} is a {chain[0]}, not an actor.");
        if (Player(game).Pawn == objectPath) throw new ArgumentException("That is Shepard.");
        Call(game, objectPath, "OnDestroy", "Engine.Default__SeqAct_Destroy");
        if (Bool(game, objectPath, "bDeleteMe")) return new LivePlacement("removed", null, null, []);
        if (Bool(game, objectPath, "bNoDelete")) return new LivePlacement("hidden", null, null, ["A level actor the engine won't destroy (bNoDelete): shut down (hidden, no collision) until the map reloads."]);

        // SetCollision is an opcode native: the flags, then the components re-attached.
        Call(game, objectPath, "SetHidden", true);
        Set(game, objectPath, "bCollideActors", false);
        Set(game, objectPath, "bBlockActors", false);
        Call(game, objectPath, "ForceUpdateComponents", true, false);
        return new LivePlacement("hidden", null, null, ["A level actor the engine won't destroy (bStatic): hidden and without collision until the map reloads."]);
    }

    /// <summary>
    /// Writes a live actor's location and rotation into the level YAML entry with its tag (npcs, usables or props); with none, adds a
    /// props entry copying the template. npcs and usables keep a yaw; props a yaw while upright, else a full rotation. Then builds the level.
    /// </summary>
    public static LiveSave Save(MEGame game, string yamlPath, string objectPath, string? name, string? template, bool build)
    {
        if (!game.IsGame1()) throw new ArgumentException("live_save handles Game 1 (LE1) levels.");
        var file = Path.GetFullPath(GamePaths.ToWindowsPath(yamlPath));
        if (!File.Exists(file)) throw new FileNotFoundException($"No file {file}.");
        var actor = Actor(game, objectPath, null);
        if (actor.Tag is "" or "None") throw new ArgumentException($"{objectPath} has no tag: give it one (live_place tag=...) so the build can find it again.");
        var notes = new List<string>();

        var text = File.ReadAllText(file);
        var root = Root(text);
        var location = YamlEdits.Location(MathF.Round(actor.X, 2), MathF.Round(actor.Y, 2), MathF.Round(actor.Z, 2));
        var yaw = YamlEdits.Degrees(MathF.Round(actor.YawDegrees, 2));
        var turned = Get(game, objectPath, "Rotation")!.AsObject();
        double[] rotation = [turned["Pitch"]!.GetValue<int>(), turned["Yaw"]!.GetValue<int>(), turned["Roll"]!.GetValue<int>()];
        var found = YamlEdits.Tagged(root, actor.Tag);

        string entry, edited;
        List<string> changes;
        var added = found is null;
        if (found is { } f && f.Section == "props")
        {
            // 'yaw' and 'rotation' together are an error: the shared writer keeps one, as editor_import does.
            entry = $"props.{Scalar(f.Entry.Key)}";
            (edited, changes) = YamlEdits.SetKeys(text, f.Entry.Value, [("location", location)]);
            (edited, var rotated) = YamlEdits.SetRotation(edited, YamlEdits.Tagged(Root(edited), actor.Tag)!.Value.Entry.Value, rotation);
            changes.AddRange(rotated);
        }
        else if (found is { } other)
        {
            entry = $"{other.Section}.{Scalar(other.Entry.Key)}";
            (edited, changes) = YamlEdits.SetKeys(text, other.Entry.Value, [("location", location), ("yaw", yaw)]);
        }
        else
        {
            var source = template ?? throw new ArgumentException($"No npcs, usables or props entry has tag '{actor.Tag}'. To add one to props, give the template it was spawned from.");
            var level = Path.GetFileNameWithoutExtension(GamePaths.ToWindowsPath(root.Children.FirstOrDefault(c => Scalar(c.Key) == "package").Value is { } p ? Scalar(p) : ""));
            var key = name ?? actor.Tag;
            entry = $"props.{key}";
            var keys = new List<(string, string)> { ("tag", actor.Tag), ("template", TemplateOf(source, level)), ("location", location), YamlEdits.RotationKey(rotation) };
            edited = YamlEdits.AddEntry(text, root, "props", key, keys, "Actors copied from a template and placed in the running game (live_place, then live_save).");
            changes = keys.Select(k => $"{k.Item1}: {k.Item2}").ToList();
            if (Get(game, source, "Class")!.GetValue<string>() == "Engine.StaticMeshActor")
            {
                // The build shows a static mesh on a copy of a BioInert, lit where it stands (as live_place's stand-in).
                (edited, var standIn) = YamlEdits.AddPropTemplate(game, edited, $"{source.Split('.')[0]}.pcc");
                if (standIn is not null) changes.Insert(0, standIn);
            }
        }
        if (edited != text) File.WriteAllText(file, edited, new UTF8Encoding(false));

        var result = build ? LevelBuilder.Build(game, file, null) : null;
        if (result is not null) notes.Add("Install the level (game closed: mod_install) to see it there after a reload; the live copy lasts until then.");
        return new LiveSave(file, entry, added, changes, actor, result, notes);
    }

    /// <summary>{ package: BIOA_FRE31_00_LAY.pcc, actor: StaticMeshActor_12 }, or the bare actor name when it is in the level built.</summary>
    private static string TemplateOf(string livePath, string level)
    {
        var parts = livePath.Split('.');
        if (parts.Length != 4 || parts[1] != "TheWorld" || parts[2] != "PersistentLevel")
        {
            throw new ArgumentException($"The template must be a level actor (<file>.TheWorld.PersistentLevel.<actor>), not {livePath}.");
        }
        return parts[0].Equals(level, StringComparison.OrdinalIgnoreCase) ? parts[3] : $"{{ package: {parts[0]}.pcc, actor: {parts[3]} }}";
    }

    /// <summary>
    /// A copy of a live actor through Actor.Spawn with it as the template. Classes whose defaults are bStatic or bNoDelete (like
    /// StaticMeshActor, most meshes of a level) can't spawn, so a BioInert copied from the loaded maps shows the template's mesh instead.
    /// </summary>
    private static (string Path, string? StandIn) Spawn(MEGame game, string template, string tag, JsonObject location, JsonObject rotation, List<string> notes)
    {
        var cls = Get(game, template, "Class")!.GetValue<string>();
        var defaults = cls.Insert(cls.LastIndexOf('.') + 1, "Default__");
        if (!Bool(game, defaults, "bStatic") && !Bool(game, defaults, "bNoDelete"))
        {
            var spawned = Spawned(game, cls, tag, template, location, rotation);
            // A placed actor is often bNoDelete (the engine checks the class, not the template); the copy can go again.
            if (Bool(game, spawned, "bNoDelete")) Set(game, spawned, "bNoDelete", false);
            return (spawned, null);
        }

        var component = Get(game, template, "StaticMeshComponent")?.GetValue<string>()
                        ?? throw new ArgumentException($"{cls} can't spawn (bStatic or bNoDelete) and {template} has no StaticMeshComponent to show in its place.");
        var mesh = Get(game, component, "StaticMesh")?.GetValue<string>() ?? throw new ArgumentException($"{component} has no mesh.");
        var inert = Find(game, "BioInert", "TheWorld.PersistentLevel").FirstOrDefault(p => Get(game, p, "PlaceableMesh") is not null)
                    ?? throw new InvalidOperationException("No BioInert in the loaded maps to copy as a stand-in for a static mesh.");
        var standIn = Spawned(game, "SFXGame.BioInert", tag, inert, location, rotation);
        var shown = Get(game, standIn, "PlaceableMesh")!.GetValue<string>();
        Call(game, shown, "SetStaticMesh", mesh, true);
        var materials = Get(game, component, "Materials") as JsonArray ?? [];
        for (var i = 0; i < materials.Count; i++)
        {
            if (materials[i] is { } material) Call(game, shown, "SetMaterial", i, material.GetValue<string>());
        }
        Call(game, standIn, "SetDrawScale", Get(game, template, "DrawScale")!.GetValue<float>());
        Call(game, standIn, "SetDrawScale3D", Get(game, template, "DrawScale3D")!.DeepClone());
        notes.Add($"{template} is static, so a BioInert shows its mesh ({mesh}), lit by its own light environment. level_build shows it the same way, on a copy of the YAML's propTemplate.");
        return (standIn, mesh);
    }

    /// <summary>Actor.Spawn called on the template itself: any actor can spawn, and the copy goes into the template's level.</summary>
    private static string Spawned(MEGame game, string cls, string tag, string template, JsonObject location, JsonObject rotation) =>
        Call(game, template, "Spawn", cls, null, tag, location.DeepClone(), rotation.DeepClone(), template, null, true, false)["return"]?.GetValue<string>()
        ?? throw new InvalidOperationException($"The game spawned nothing from {template} (an abstract class, or the spawn was refused).");

    private static PlacedActor Actor(MEGame game, string path, string? standIn)
    {
        var location = Get(game, path, "Location")!;
        var yaw = Get(game, path, "Rotation.Yaw")!.GetValue<int>();
        return new PlacedActor(path, Get(game, path, "Class")!.GetValue<string>(), Get(game, path, "Tag")!.GetValue<string>(),
            location["X"]!.GetValue<float>(), location["Y"]!.GetValue<float>(), location["Z"]!.GetValue<float>(), Normalize(yaw * 360f / 65536), standIn);
    }

    private static Shepard Player(MEGame game)
    {
        var controller = Find(game, "PlayerController", null).FirstOrDefault() ?? throw new InvalidOperationException("No player controller: is a map loaded?");
        var pawn = Get(game, controller, "Pawn")?.GetValue<string>() ?? throw new InvalidOperationException("Shepard has no pawn right now (a cinematic or a conversation?).");
        var location = Get(game, pawn, "Location")!;
        return new Shepard(pawn, location["X"]!.GetValue<float>(), location["Y"]!.GetValue<float>(), location["Z"]!.GetValue<float>(), Get(game, pawn, "Rotation.Yaw")!.GetValue<int>());
    }

    private static float Normalize(float degrees) => (degrees % 360 + 360) % 360;

    private static List<string> Chain(MEGame game, string objectPath) =>
        LiveBridge.Request(game, new JsonObject { ["op"] = "describe", ["object"] = objectPath, ["functions"] = false })["chain"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();

    private static List<string> Find(MEGame game, string cls, string? path) =>
        LiveBridge.Request(game, new JsonObject { ["op"] = "find", ["class"] = cls, ["path"] = path, ["limit"] = 50 })["objects"]!.AsArray()
            .Select(o => o!["path"]!.GetValue<string>()).ToList();

    private static JsonNode? Get(MEGame game, string obj, string property) =>
        LiveBridge.Request(game, new JsonObject { ["op"] = "get", ["object"] = obj, ["property"] = property })["value"];

    private static bool Bool(MEGame game, string obj, string property) => Get(game, obj, property)?.GetValue<bool>() == true;

    private static void Set(MEGame game, string obj, string property, JsonNode? value) =>
        LiveBridge.Request(game, new JsonObject { ["op"] = "set", ["object"] = obj, ["property"] = property, ["value"] = value });

    private static JsonObject Call(MEGame game, string obj, string function, params JsonNode?[] args) =>
        LiveBridge.Request(game, new JsonObject { ["op"] = "call", ["object"] = obj, ["function"] = function, ["args"] = new JsonArray(args) });
}
