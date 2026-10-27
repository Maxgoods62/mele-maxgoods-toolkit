using System.Text;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using YamlDotNet.RepresentationModel;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <param name="From">player (Shepard's position in the running game) or given.</param>
/// <param name="Live">Actors moved in the running game to preview the result (until the level is installed); empty when the game is not running.</param>
public sealed record NpcPlacement(string File, string Npc, string From, float X, float Y, float Z, float YawDegrees, IReadOnlyList<string> YamlChanges,
    LevelBuildResult? Build, IReadOnlyList<ActorState> Live, IReadOnlyList<string> Notes);

/// <summary>
/// Puts an NPC of a level YAML file where Shepard stands (or at given coordinates): writes 'location' and 'yaw' into the
/// file, keeping its comments, builds the level, and moves the NPC and her lights in the running game as a preview.
/// </summary>
public static class NpcPlacer
{
    public static NpcPlacement Place(MEGame game, string yamlPath, string? npc, float? x, float? y, float? z, float? yaw, float turn, bool build, bool live)
    {
        if (!game.IsGame1()) throw new ArgumentException("npc_place handles Game 1 (LE1) levels.");
        var file = Path.GetFullPath(GamePaths.ToWindowsPath(yamlPath));
        if (!File.Exists(file)) throw new FileNotFoundException($"No file {file}.");
        var notes = new List<string>();

        string from;
        float px, py, pz, degrees;
        if (x is not null || y is not null || z is not null)
        {
            if (x is null || y is null || z is null) throw new ArgumentException("Give x, y and z together, or none to use Shepard's position.");
            (px, py, pz, degrees, from) = (x.Value, y.Value, z.Value, yaw ?? 0, "given");
        }
        else
        {
            if (!GameBridge.IsGameRunning(game)) throw new InvalidOperationException("The game is not running. Start it and stand where the NPC should be, or give x, y and z.");
            var player = GameLink.PlayerLocation(game);
            if (!player.Fresh) notes.Add("Shepard had not moved since the last report; used the last position the game sent.");
            (px, py, pz, from) = (player.X, player.Y, player.Z, "player");
            degrees = yaw ?? player.Yaw * 360f / 65536;
        }
        degrees = (float)(((degrees + turn) % 360 + 360) % 360);

        var text = File.ReadAllText(file);
        var (name, edited, changes) = Edit(text, npc, [px, py, pz], degrees);
        if (edited != text) File.WriteAllText(file, edited, new UTF8Encoding(false));

        var result = build ? LevelBuilder.Build(game, file, null) : null;
        var moved = new List<ActorState>();
        if (live && result is not null && GameBridge.IsGameRunning(game))
        {
            try
            {
                moved.AddRange(MoveLive(game, result.Path, name, Path.GetFullPath(file)));
            }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException or IOException)
            {
                notes.Add($"Live preview failed ({e.Message}); the level is built, install it to see her there.");
            }
        }
        if (result is not null) notes.Add("Install the level (game closed: mod_install) to keep her there; the live move lasts until the level reloads.");
        return new NpcPlacement(file, name, from, px, py, pz, degrees, changes, result, moved, notes);
    }

    /// <summary>Replaces the NPC's location and yaw values in the YAML text (or adds them after her tag), leaving everything else as written.</summary>
    private static (string Name, string Text, List<string> Changes) Edit(string text, string? npc, float[] location, float yaw)
    {
        var entry = YamlEdits.Entry(Root(text), "npcs", npc);
        var (edited, changes) = YamlEdits.SetKeys(text, entry.Value, [("location", YamlEdits.Location(location[0], location[1], location[2])), ("yaw", YamlEdits.Degrees(yaw))]);
        return (Scalar(entry.Key), edited, changes);
    }

    /// <summary>The NPC's pawn and lights as the build left them in the level, pushed to the same actors in the running game.</summary>
    private static List<ActorState> MoveLive(MEGame game, string levelPath, string npcName, string yamlPath)
    {
        var npc = (Root(File.ReadAllText(yamlPath)).Children.First(c => Scalar(c.Key) == "npcs").Value as YamlMappingNode)!
            .Children.First(c => Scalar(c.Key) == npcName).Value as YamlMappingNode;
        var tags = new List<string>();
        foreach (var (key, value) in npc!.Children)
        {
            if (Scalar(key) == "tag") tags.Insert(0, Scalar(value));
            if (Scalar(key) == "lights") tags.AddRange(Sequence(value).OfType<YamlMappingNode>().SelectMany(l => l.Children.Where(c => Scalar(c.Key) == "tag").Select(c => Scalar(c.Value))));
        }

        using var pcc = PackageInspector.Open(game, levelPath);
        var level = Path.GetFileNameWithoutExtension(levelPath);
        var moved = new List<ActorState>();
        foreach (var tag in tags)
        {
            var actor = pcc.Exports.FirstOrDefault(e => e.Parent?.InstancedFullPath == "TheWorld.PersistentLevel"
                                                        && e.GetProperty<NameProperty>("Tag")?.Value.Instanced.Equals(tag, StringComparison.OrdinalIgnoreCase) == true);
            if (actor is null) continue;
            var location = CommonStructs.GetVector3(actor, "Location", default);
            var rotation = actor.GetProperty<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : new LegendaryExplorerCore.Unreal.BinaryConverters.Rotator(0, 0, 0);
            moved.Add(GameLink.SetActor(game, level, actor.ObjectName.Instanced, location.X, location.Y, location.Z, rotation.Pitch, rotation.Yaw, rotation.Roll, null));
        }
        return moved;
    }
}
