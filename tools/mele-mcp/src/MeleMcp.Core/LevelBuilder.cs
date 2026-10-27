using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using YamlDotNet.RepresentationModel;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <param name="Action">found (an actor of the level has the tag), imported (copied from the template's package) or cloned (from an actor of the level).</param>
/// <param name="Changes">What this build changed; empty when the level already matched the file.</param>
public sealed record BuiltNpc(string Name, ExportRow Pawn, string Action, IReadOnlyList<string> Changes);

/// <param name="Action">found (an actor of the level has the tag), imported (copied from the template's package) or cloned (from an actor of the level).</param>
public sealed record BuiltUsable(string Name, ExportRow Actor, string Action, IReadOnlyList<string> Changes);

/// <param name="Actor">Null when the prop is removed and the level has no actor with its tag.</param>
/// <param name="Action">found (an actor of the level has the tag), imported (copied from the template's package), cloned (from an actor of the level),
/// stand-in (a copy of the prop template showing the mesh), hidden (removed: the actor hidden without collision) or absent (removed, and no actor has the tag).</param>
public sealed record BuiltProp(string Name, ExportRow? Actor, string Action, IReadOnlyList<string> Changes);

/// <param name="Action">found (a light of the level has the tag), imported (copied from the template's package) or cloned (from a light of the level:
/// the template's, or an earlier copy of the template's package's).</param>
/// <param name="Changes">"light &lt;tag&gt; added" for a new light; for a found one each value this build changed, old → new ("brightness 0.08 → 0.1",
/// "switched off"); empty when the level already matched the file.</param>
public sealed record BuiltLight(string Name, ExportRow Actor, string Action, IReadOnlyList<string> Changes);

/// <param name="LevelChanges">What this build changed in the level itself (streamed files, post-process, character lights, start points, squad).</param>
/// <param name="Lights">The lights of the lights: section; NPC lights are in their NPC's changes.</param>
/// <param name="MissingConversations">Conversations the NPCs name that the level does not have yet: build them, then the level again.</param>
/// <param name="Forks">The receipt of each fork of a streamed file (its vanilla entries), written, unchanged or deleted.</param>
/// <param name="LevelsFile">levels.json, whose key for this level the build wrote; null when it built another package than the file's.</param>
public sealed record LevelBuildResult(string Path, bool CreatedLevel, IReadOnlyList<string> LevelChanges, IReadOnlyList<BuiltNpc> Npcs, IReadOnlyList<BuiltUsable> Usables, IReadOnlyList<BuiltProp> Props,
    IReadOnlyList<BuiltLight> Lights, int StringCount, IReadOnlyList<string> TlkFiles, string? SideloaderConfig, IReadOnlyList<string> MissingConversations, IReadOnlyList<string> Notes,
    IReadOnlyList<string> BackupPaths, IReadOnlyList<BuiltFork> Forks, string? LevelsFile);

/// <summary>
/// Builds a Game 1 level from a YAML file. The level itself: vanilla sub-levels it shows and their stream volume, its
/// post-process, character lights, start points (see <see cref="LevelStreams"/>), the squad spawned on load (<see cref="SquadSpawns"/>). Its NPCs: the pawn (found by tag, else imported from a
/// template), position, name (own actor type), skin, hair and eye colors, fill lights that follow her, and the Kismet that
/// starts her conversation when Shepard uses her. Its usables: placeables Shepard uses to go to another map. Its props: any
/// actor copied from a template and placed (live_place, live_save), or a mesh shown on a copy of the prop template (editor_import),
/// turned by a yaw or a full rotation and scaled; a removed one is hidden without collision; each lit by prop lights when a light of its
/// lights section covers it, else by its light environment. Its lights: point lights on the channels of their kind (props only, or the
/// room too), switched on or off. Vanilla actors of the files it streams:
/// moved, turned, scaled or hidden in a fork of the file, which the level streams instead (<see cref="ForkBuilder"/>). A missing level is
/// created, and the LE1CP Sideloader config that loads it is written. Last, its key of levels.json, which tells MeleBridge which levels
/// are ours.
/// </summary>
public static class LevelBuilder
{
    /// <summary>Where levels.json goes instead of %LOCALAPPDATA%\MeleBridge\LE1\levels.json (tests).</summary>
    public const string LevelsFileEnvVar = "MELE_LEVELS_JSON";

    private const string LevelPath = "TheWorld.PersistentLevel";
    private const string MainSequence = "TheWorld.PersistentLevel.Main_Sequence";

    private const string PropTemplateHint =
        "Give 'propTemplate' (a BioInert, e.g. { package: BIOA_FRE31_00_LAY.pcc, actor: BioInert_0 }): a copy of it shows the mesh, lit by its light environment, "
        + "or by prop lights when a light of the lights section covers it.";

    /// <summary>
    /// What a removed prop changes, as live_remove leaves an actor the engine won't destroy: hidden, no collision. Actor.CollisionType
    /// is transient in LE1, so a file can't set it.
    /// </summary>
    private static readonly string[] ShownFlags = ["bHidden", "bCollideActors", "bBlockActors"];

    private sealed record Source(string? Package, string Actor, int Line);

    /// <summary>A StaticMesh of a file, shown without the actor that placed it (what the in-game editor spawns).</summary>
    private sealed record MeshSource(string Package, string Export, int Line);

    private sealed record NameSpec(string Type, int StrRef, int Line);

    /// <param name="Offset">Forward, right, up from the NPC's location, turned by her yaw.</param>
    private sealed record LightSpec(string Tag, Vector3 Offset, float? Radius, float? Brightness, int Line);

    /// <summary>
    /// A light of the lights: section (docs/07 §11): a point light on the channels of its kind. props: Gameplay_4 only, so it lights
    /// props and nothing else; room: walls, vanilla meshes, pawns and props. Switched off (enabled: false), never removed.
    /// </summary>
    private sealed class LightEntrySpec
    {
        public required string Name;
        public required int Line;
        public string? Tag;
        public string Kind = "props";
        public Vector3? Location;
        public float? Radius, Brightness;
        public byte[] Color = [255, 255, 255];
        public float Falloff = 2;
        public bool Enabled = true;
        public Source? Template;
    }

    /// <summary>
    /// What <see cref="ApplyLight"/> gives a light. <see cref="Kind"/> null: an NPC light, which leaves its colour, falloff, shadows,
    /// channels and level list to its template (Reyes's lights stay on the Dynamic channel); a radius or brightness left out stays the template's too.
    /// </summary>
    private sealed record LightSettings(string Tag, Vector3 Location, int Line, float? Radius, float? Brightness, bool Enabled = true, string? Kind = null,
        byte[]? Color = null, float Falloff = 2);

    private sealed class NpcSpec
    {
        public required string Name;
        public required int Line;
        public string? Tag;
        public Source? Template;
        public Vector3? Location;
        public float? Yaw;
        public bool Active = true;
        public NameSpec? ActorName;
        public float[]? Skin, Hair, Eyes;
        public string? Conversation;
        public IdleStations.Spec? Idle;
        public List<LightSpec> Lights = [];
    }

    /// <summary>A placeable Shepard uses, e.g. a terminal; using it loads <see cref="GoTo"/>.</summary>
    private sealed class UsableSpec
    {
        public required string Name;
        public required int Line;
        public string? Tag;
        public Source? Template;
        public Vector3? Location;
        public float? Yaw;
        public int? NameStrRef;
        public (string Map, string? StartPoint)? GoTo;
    }

    /// <summary>Any actor copied from a template and placed, e.g. a mesh of another level; or a mesh shown on a copy of the prop template.</summary>
    private sealed class PropSpec
    {
        public required string Name;
        public required int Line;
        public string? Tag;
        public Source? Template;
        public MeshSource? Mesh;
        public bool Removed;
        public Vector3? Location;
        public float? Yaw;
        /// <summary>Pitch, yaw, roll in degrees, instead of <see cref="Yaw"/>.</summary>
        public float[]? Rotation;
        public float? Scale;
        public Vector3? Scale3D;
    }

    private sealed class Spec
    {
        public string? Package;
        public string DefaultLanguage = "INT";
        public List<string> GlobalTlk = [];
        public List<string>? Sideloader;
        public Source? LightTemplate;
        public LevelStreams.Spec? Streaming;
        public (string Package, int Line)? PostProcess;
        public List<Source> CharacterLights = [];
        public Source? StartPointTemplate;
        public List<LevelStreams.StartPoint> StartPoints = [];
        public (string Package, int Line)? Squad;
        public List<NpcSpec> Npcs = [];
        public List<UsableSpec> Usables = [];
        public List<PropSpec> Props = [];
        public Source? PropTemplate;
        public List<ForkBuilder.Entry> Vanilla = [];
        public List<LightEntrySpec> Lights = [];
        public readonly Dictionary<int, Dictionary<string, string>> Strings = [];
    }

    public static LevelBuildResult Build(MEGame game, string yamlPath, string? packageOverride)
    {
        if (!game.IsGame1()) throw new ArgumentException("level_build handles Game 1 (LE1) levels.");
        LecHost.EnsureInitialized();
        var file = Path.GetFullPath(GamePaths.ToWindowsPath(yamlPath));
        if (!File.Exists(file)) throw new FileNotFoundException($"No file {file}.");
        var spec = Parse(File.ReadAllText(file));
        var dir = Path.GetDirectoryName(file)!;

        // A missing level is created, but only from a full path: a bare name could resolve into the game install.
        var candidate = GamePaths.ToWindowsPath(packageOverride ?? spec.Package ?? throw new ArgumentException("The file has no 'package'."));
        var create = Path.IsPathRooted(candidate) && !File.Exists(candidate);
        var package = create ? Path.GetFullPath(candidate) : Locate(game, candidate, dir);
        var globals = spec.GlobalTlk.Select(g => Locate(game, g, Path.GetDirectoryName(package)!, dir)).ToList();
        if (spec.Strings.Count > 0 && globals.Count == 0) throw new ArgumentException("The file gives a name text but no 'globalTlk' to write it to (actor names are read from the GlobalTlk).");
        foreach (var target in globals.Prepend(package)) PackageWriter.EnsureWritable(game, target);

        var notes = new List<string>();
        if (create)
        {
            LevelCreator.Create(game, package);
            notes.Add($"Created the level {Path.GetFileName(package)}.");
        }

        // Forks before the map (docs/07 §11): the map never names a file not written yet. Every file's fork is made in memory first,
        // so a refusal in any of them writes none, and the error lists them all. Each streamed file then goes by the name the map
        // streams it under, and the other name of its pair (the vanilla file, or its fork) is renamed in place.
        var map = Path.GetFileNameWithoutExtension(package);
        var levels = spec.Streaming?.Levels ?? [];
        var prepared = levels.Select(l => (File: l, Entries: spec.Vanilla.Where(v => v.File.Equals(l, StringComparison.OrdinalIgnoreCase)).ToList()))
            .Where(f => f.Entries.Count > 0)
            .Select(f => ForkBuilder.Prepare(game, package, f.File, f.Entries))
            .ToList();
        if (prepared.SelectMany(p => p.Refusals).ToList() is { Count: > 0 } refusals)
        {
            throw new ArgumentException($"vanilla actors the build won't change (nothing written): {string.Join("; ", refusals)}.");
        }
        var forks = prepared.Select(p => ForkBuilder.Write(p, notes)).ToList();
        prepared.Clear();
        var forked = forks.Select(f => f.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var streams = levels.Select(l => forked.Contains(l) ? ForkBuilder.ForkName(map, l) : l).ToList();
        var others = levels.Zip(streams).DistinctBy(p => p.Second, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Second, p => forked.Contains(p.First) ? p.First : ForkBuilder.ForkName(map, p.First), StringComparer.OrdinalIgnoreCase);

        var backups = new List<string>();
        var missing = new List<string>();
        var levelChanges = new List<string>();
        var ((npcs, usables, props, lights, named), backup) = PackageWriter.Edit(game, package, pcc =>
        {
            var level = pcc.FindExport(LevelPath) ?? throw new ArgumentException($"{Path.GetFileName(pcc.FilePath)} has no {LevelPath}; is it a level?");
            using var cache = new PackageCache();
            if (spec.Streaming is { } streaming)
            {
                LevelStreams.SetStreaming(game, pcc, level, streaming with { Levels = streams }, others, cache, levelChanges);
                // With 'squad' or not: a wait still naming the other name of a pair would wait for a file the level no longer streams.
                LevelStreams.RenameWaits(pcc, others, levelChanges);
            }
            if (spec.PostProcess is { } post) LevelStreams.SetPostProcess(game, pcc, level, post.Package, post.Line, levelChanges);
            foreach (var light in spec.CharacterLights)
            {
                LevelStreams.SetLight(game, pcc, level, light.Package ?? throw new ArgumentException($"line {light.Line}: a character light needs a 'package'."), light.Actor, light.Line, levelChanges);
            }
            var template = spec.StartPointTemplate is { } t ? (t.Package ?? throw new ArgumentException($"line {t.Line}: 'startPointTemplate' needs a 'package'."), t.Actor) : ((string, string)?)null;
            foreach (var start in spec.StartPoints) LevelStreams.SetStartPoint(game, pcc, level, start, template, levelChanges);
            if (spec.Squad is { } squad)
            {
                var sequence = pcc.FindExport(MainSequence) ?? throw new ArgumentException($"line {squad.Line}: the level has no {MainSequence}.");
                SquadSpawns.Set(game, pcc, sequence, squad.Package, streams, squad.Line, cache, levelChanges);
            }
            var builtNpcs = spec.Npcs.Select(npc => BuildNpc(game, pcc, level, spec, npc, cache, missing, notes)).ToList();
            var builtUsables = spec.Usables.Select(u => BuildUsable(game, pcc, level, u, cache)).ToList();
            var builtLights = spec.Lights.Select(l => BuildLight(game, pcc, level, spec, l, cache)).ToList();
            NoteOrphanLights(pcc, level, spec, notes);
            // Each prop is lit by prop lights or by its light environment, from the lights: entries (docs/07 §11).
            var looks = new Dictionary<(string?, string), (bool Static, StructProperty? Channels)>();
            var builtProps = spec.Props.Select(p => BuildProp(game, pcc, level, spec, p, cache, looks, notes)).ToList();
            var streamed = pcc.Exports.Where(e => e.ClassName == "LevelStreamingKismet").Select(e => e.GetProperty<NameProperty>("PackageName")?.Value.Instanced)
                .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (builtNpcs, builtUsables, builtProps, builtLights, streamed);
        });
        backups.Add(backup);

        // A file with no vanilla entry left streams under its own name again: its fork goes now that the saved map no longer names it.
        foreach (var vanilla in levels.Where(l => !forked.Contains(l)))
        {
            if (named.Contains(ForkBuilder.ForkName(map, vanilla)))
            {
                notes.Add($"{ForkBuilder.ForkName(map, vanilla)} is kept: a streaming entry of the level still names it.");
                continue;
            }
            if (ForkBuilder.Delete(game, package, vanilla) is { } gone) forks.Add(gone);
        }

        var tlkFiles = new List<string>();
        if (spec.Strings.Count > 0)
        {
            foreach (var globalPath in globals)
            {
                var language = LanguageOfFile(Path.GetFileNameWithoutExtension(globalPath));
                var edit = TalkFiles.Set(game, globalPath, StringsFor(spec.Strings, language, spec.DefaultLanguage), null);
                backups.Add(edit.BackupPath);
                tlkFiles.Add($"{Path.GetFileName(globalPath)} ({language})");
            }
        }

        var config = spec.Sideloader is { } prefixes ? EnsureSideloader(package, prefixes, backups, notes) : null;
        // Last: it describes the build, and only the YAML's own package is the level the game loads.
        var levelsFile = packageOverride is null ? WriteLevelsFile(package, spec, levels) : null;
        return new LevelBuildResult(package, create, levelChanges, npcs, usables, props, lights, spec.Strings.Count, tlkFiles, config, missing, notes, backups, forks, levelsFile);
    }

    /// <summary>
    /// Writes this level's key of levels.json (docs/07 §11), which MeleBridge reads on each map change: the vanilla files the level
    /// streams (by their vanilla names), the tags of its props that are not removed (the props it may switch to prop lights) and of
    /// its lights: entries (the lights it lists); 'dlc', the DLC folder it lies in. Other keys stay. Unreadable, the file is kept aside
    /// (.bad-&lt;ticks&gt;) and a new one started. Written beside it, then moved over it.
    /// </summary>
    private static string WriteLevelsFile(string package, Spec spec, IReadOnlyList<string> streams)
    {
        var custom = Environment.GetEnvironmentVariable(LevelsFileEnvVar);
        var path = string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeleBridge", "LE1", "levels.json")
            : Path.GetFullPath(GamePaths.ToWindowsPath(custom));
        JsonObject doc;
        try
        {
            doc = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("not a JSON object") : new JsonObject();
        }
        catch (JsonException)
        {
            File.Move(path, $"{path}.bad-{DateTime.UtcNow.Ticks}");
            doc = new JsonObject();
        }
        doc["version"] = 1;
        if (ForkBuilder.DlcOf(package) is { } dlc) doc["dlc"] = dlc;
        if (doc["levels"] is not JsonObject levels) doc["levels"] = levels = new JsonObject();
        var key = Path.GetFileNameWithoutExtension(package);
        foreach (var old in levels.Select(l => l.Key).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList()) levels.Remove(old);
        levels[key] = new JsonObject
        {
            ["streams"] = new JsonArray(streams.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["props"] = new JsonArray(spec.Props.Where(p => !p.Removed).Select(p => p.Tag).OfType<string>().Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            // Switched off ones too: the editor lists them, and switches them on again. Not NPC lights: those are edited in the YAML.
            ["lights"] = new JsonArray(spec.Lights.Select(l => l.Tag).OfType<string>().Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".build.tmp";
        File.WriteAllText(temporary, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static BuiltNpc BuildNpc(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, NpcSpec npc, PackageCache cache, List<string> missing, List<string> notes)
    {
        var changes = new List<string>();
        var tag = npc.Tag ?? throw new ArgumentException($"line {npc.Line}: '{npc.Name}' needs a 'tag' (how the build finds her again).");
        var pawn = FindActor(pcc, level, tag, e => e.ClassName == "BioPawn");
        var action = "found";
        if (pawn is null)
        {
            var template = npc.Template ?? throw new ArgumentException($"line {npc.Line}: no pawn of the level has tag '{tag}'; give a 'template' to import or clone.");
            pawn = AddActor(game, pcc, level, template, cache);
            action = template.Package is null ? "cloned" : "imported";
        }

        // The pawn: tags, place, visibility.
        var props = pawn.GetProperties();
        var dirty = Changed(changes, "tag", Put(props, new NameProperty(tag, "Tag")) | Put(props, new NameProperty(tag, "UniqueTag")));
        if (npc.Location is { } location) dirty |= Changed(changes, "location", Put(props, CommonStructs.Vector3Prop(location, "Location")));
        var rotation = props.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : new Rotator(0, 0, 0);
        if (npc.Yaw is { } yaw)
        {
            rotation = new Rotator(rotation.Pitch, (int)Math.Round(yaw * 65536 / 360), rotation.Roll);
            dirty |= Changed(changes, "yaw", Put(props, CommonStructs.RotatorProp(rotation, "Rotation")));
        }
        dirty |= Changed(changes, "shown", Put(props, new BoolProperty(!npc.Active, "bHidden")));
        if (dirty) pawn.WriteProperties(props);
        var origin = props.GetProp<StructProperty>("Location") is { } l ? CommonStructs.GetVector3(l) : Vector3.Zero;

        // A story NPC ships with its behavior switched off (bActive=False); our pawn has no Kismet to switch it on.
        var behavior = Object(pcc, props, "m_oBehavior") ?? throw new ArgumentException($"line {npc.Line}: {pawn.InstancedFullPath} has no m_oBehavior.");
        var behaviorProps = behavior.GetProperties();
        var behaviorDirty = Changed(changes, "behavior active", Put(behaviorProps, new BoolProperty(npc.Active, "bActive")));
        if (npc.ActorName is { } name) behaviorDirty |= SetActorType(pcc, behaviorProps, name, changes);
        if (behaviorDirty) behavior.WriteProperties(behaviorProps);

        SetColors(pcc, pawn, props, npc, changes, notes);
        foreach (var light in npc.Lights) SetLight(game, pcc, level, spec, light, origin, rotation.Yaw, cache, changes);
        var talk = npc.Conversation is { } conversation ? LinkConversation(pcc, pawn, conversation, npc.Line, cache, changes) : null;
        if (npc.Conversation is not null && talk is null)
        {
            // The conversation's own file creates it in this level; the level is built first so that it exists.
            missing.Add(npc.Conversation);
            notes.Add($"{npc.Name}: the level has no BioConversation '{npc.Conversation}' yet. Build it (conv_build; 'from' copies a template), then this file again to start it when Shepard uses her.");
        }

        if (npc.Idle is { } idle)
        {
            var sequence = pcc.FindExport(MainSequence) ?? throw new ArgumentException($"line {npc.Line}: the level has no {MainSequence}.");
            IdleStations.Set(game, pcc, sequence, pawn, idle, talk?.Use, talk?.Start, cache, changes);
        }

        return new BuiltNpc(npc.Name, ExportInspector.Row(pawn), action, changes);
    }

    private static BuiltUsable BuildUsable(MEGame game, IMEPackage pcc, ExportEntry level, UsableSpec usable, PackageCache cache)
    {
        var changes = new List<string>();
        var tag = usable.Tag ?? throw new ArgumentException($"line {usable.Line}: '{usable.Name}' needs a 'tag' (how the build finds it again).");
        var actor = FindActor(pcc, level, tag, e => e.ClassName == "BioUseable");
        var action = "found";
        if (actor is null)
        {
            var template = usable.Template ?? throw new ArgumentException($"line {usable.Line}: no BioUseable of the level has tag '{tag}'; give a 'template' to import or clone.");
            actor = AddActor(game, pcc, level, template, cache);
            action = template.Package is null ? "cloned" : "imported";
        }

        var props = actor.GetProperties();
        var dirty = Changed(changes, "tag", Put(props, new NameProperty(tag, "Tag")));
        dirty |= PutPlace(props, usable.Location, usable.Yaw, changes);
        if (dirty) actor.WriteProperties(props);

        // Vanilla terminals ship untargetable and their story's Kismet switches them on; ours is usable from the start.
        var behavior = Object(pcc, props, "m_oBehavior") ?? throw new ArgumentException($"line {usable.Line}: {actor.InstancedFullPath} has no m_oBehavior.");
        var behaviorProps = behavior.GetProperties();
        var behaviorDirty = Changed(changes, "usable", Put(behaviorProps, new BoolProperty(true, "m_bTargetable")) | Put(behaviorProps, new BoolProperty(true, "m_bTargetableOverride")));
        if (usable.NameStrRef is { } strRef)
        {
            behaviorDirty |= Changed(changes, $"name ${strRef}", Put(behaviorProps, new StringRefProperty(strRef, "ActorGameNameStrRef"))
                                                                 | Put(behaviorProps, new BoolProperty(true, "ActorGameNameOverride")));
        }
        if (behaviorDirty) behavior.WriteProperties(behaviorProps);

        if (usable.GoTo is { } go) LinkAreaTransition(pcc, actor, go.Map, go.StartPoint, usable.Line, cache, changes);
        return new BuiltUsable(usable.Name, ExportInspector.Row(actor), action, changes);
    }

    private static BuiltProp BuildProp(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, PropSpec prop, PackageCache cache,
        Dictionary<(string?, string), (bool Static, StructProperty? Channels)> looks, List<string> notes)
    {
        var changes = new List<string>();
        var tag = prop.Tag ?? throw new ArgumentException($"line {prop.Line}: '{prop.Name}' needs a 'tag' (how the build finds it again).");
        var actor = FindActor(pcc, level, tag, _ => true);
        if (prop.Removed)
        {
            if (actor is null) return new BuiltProp(prop.Name, null, "absent", changes);
            var hidden = actor.GetProperties();
            var removed = new Property[] { new BoolProperty(true, "bHidden"), new BoolProperty(false, "bCollideActors"), new BoolProperty(false, "bBlockActors") };
            if (Changed(changes, "hidden, no collision", PutShown(hidden, removed))) actor.WriteProperties(hidden);
            return new BuiltProp(prop.Name, ExportInspector.Row(actor), "hidden", changes);
        }

        var action = "found";
        if (actor is not null && LooksRemoved(actor))
        {
            // Hidden by an earlier build with removed: true: shown again as its source shows.
            if (BuiltShown(game, pcc, spec, prop) is { } shown)
            {
                var restored = actor.GetProperties();
                if (Changed(changes, "shown again, with collision", PutShown(restored, shown))) actor.WriteProperties(restored);
            }
            else
            {
                notes.Add($"{prop.Name}: {actor.ObjectName.Instanced} is hidden without collision (an earlier removed: true?) and the entry has no template or mesh to read its own "
                          + "values from: give it one, or set bHidden, bCollideActors and bBlockActors by hand (export_set).");
            }
        }
        if (actor is null && prop.Mesh is { } mesh)
        {
            var standIn = spec.PropTemplate ?? throw new ArgumentException($"line {prop.Line}: a 'mesh' is shown on a copy of the prop template. {PropTemplateHint}");
            using var source = PackageInspector.Open(game, mesh.Package);
            var shown = ResolveMesh(source, mesh);
            actor = AddActor(game, pcc, level, standIn, cache, cloneOnClash: true);
            ShowMesh(source, shown, [], [], pcc, actor, cache, changes);
            action = "stand-in";
        }
        else if (actor is null)
        {
            var template = prop.Template ?? throw new ArgumentException($"line {prop.Line}: no actor of the level has tag '{tag}'; give a 'template' to import or clone, or a 'mesh' to show.");
            using var source = template.Package is null ? null : PackageInspector.Open(game, template.Package);
            var original = ResolveActor(source ?? pcc, template.Actor, template.Line);
            if (original.ClassName == "StaticMeshActor")
            {
                var standIn = spec.PropTemplate ?? throw new ArgumentException($"line {prop.Line}: {original.ObjectName.Instanced} is a StaticMeshActor, lit by light baked where it stands. {PropTemplateHint}");
                actor = AddActor(game, pcc, level, standIn, cache, cloneOnClash: true);
                ShowActorMesh(source ?? pcc, original, pcc, actor, cache, changes);
                action = "stand-in";
            }
            else
            {
                actor = AddActor(game, pcc, level, template, cache);
                action = template.Package is null ? "cloned" : "imported";
            }
        }

        var props = actor.GetProperties();
        var dirty = Changed(changes, "tag", Put(props, new NameProperty(tag, "Tag")));
        dirty |= PutPlace(props, prop.Location, prop.Yaw, changes, prop.Rotation, prop.Scale, prop.Scale3D);
        if (dirty) actor.WriteProperties(props);
        SwitchLighting(game, pcc, spec, prop, actor, looks, changes, notes);
        return new BuiltProp(prop.Name, ExportInspector.Row(actor), action, changes);
    }

    // ---- Prop lighting (docs/07 §11) ----

    /// <summary>A light covers a prop when its falloff at the prop's bounds centre is at least this (d ≤ 0.71·R at falloff 2).</summary>
    internal const double CoveredFalloff = 0.25;

    /// <summary>Gameplay_4 is in no scanned vanilla light: on it, prop lights light our props and nothing else.</summary>
    private const string PropChannel = "Gameplay_4";

    /// <summary>The channels of each kind of light; a prop lit by prop lights has its mesh on Gameplay_4 alone.</summary>
    private static readonly Dictionary<string, string[]> KindChannels = new()
    {
        ["props"] = [PropChannel],
        ["room"] = ["BSP", "Static", "Dynamic", "CompositeDynamic", PropChannel],
    };

    /// <summary>
    /// A light's falloff at a distance, as UE3's point light: (1 - (d/R)²)^falloff, 0 from the radius on. MeleBridge's covered rule and
    /// its Auto light use the same.
    /// </summary>
    internal static double Attenuation(double distance, double radius, double falloff) =>
        distance >= radius || radius <= 0 ? 0 : Math.Pow(1 - distance * distance / (radius * radius), falloff);

    /// <summary>
    /// The switch of docs/07 §11: a prop that a lights: entry covers (enabled, on Gameplay_4 as both kinds are, falloff at its bounds
    /// centre at least <see cref="CoveredFalloff"/>) is lit by prop lights: its light environment off, its mesh on Gameplay_4 alone,
    /// written in full (a channel left out of a tagged struct takes its archetype's). Any other prop is lit by its light environment,
    /// with the channels a new copy of its source has (today's look), and gets a note: its light environment sums the level's lights
    /// unoccluded. The note names the switched-off entries that would cover it, by the same rule, when there are some. Written only
    /// when it differs. A prop with no static mesh or light environment (an emitter, a lightmapped actor) is left as it is.
    /// </summary>
    private static void SwitchLighting(MEGame game, IMEPackage pcc, Spec spec, PropSpec prop, ExportEntry actor,
        Dictionary<(string?, string), (bool Static, StructProperty? Channels)> looks, List<string> changes, List<string> notes)
    {
        var actorProps = actor.GetProperties();
        if ((Object(pcc, actorProps, "PlaceableMesh") ?? Object(pcc, actorProps, "StaticMeshComponent")) is not { } mesh || !mesh.IsA("StaticMeshComponent")) return;
        var meshProps = mesh.GetProperties();
        var environment = Object(pcc, meshProps, "LightEnvironment") ?? Object(pcc, actorProps, "m_pLightEnvComponent") ?? Object(pcc, actorProps, "LightEnvironment");
        if (environment is null || !environment.IsA("DynamicLightEnvironmentComponent")) return;

        var centre = BoundsCentre(pcc, actor, mesh);
        // The rule without its "enabled": a switched-off entry that reaches the prop is named in its note.
        bool Reaches(LightEntrySpec l) => KindChannels[l.Kind].Contains(PropChannel)
                                          && Attenuation(Vector3.Distance(l.Location!.Value, centre), l.Radius!.Value, l.Falloff) >= CoveredFalloff;
        var covered = spec.Lights.Any(l => l.Enabled && Reaches(l));
        var environmentProps = environment.GetProperties();
        bool lit, channelled;
        if (covered)
        {
            lit = Put(environmentProps, new BoolProperty(false, "bEnabled"));
            channelled = PutChannels(meshProps, Channels([PropChannel]));
        }
        else
        {
            // On: back to what its archetype says, which is on unless an archetype of the level turned it off.
            lit = environmentProps.GetProp<BoolProperty>("bEnabled")?.Value == false && environmentProps.RemoveNamedProperty("bEnabled");
            if (environment.Archetype is ExportEntry archetype && Effective(archetype).GetProp<BoolProperty>("bEnabled")?.Value == false)
            {
                lit |= Put(environmentProps, new BoolProperty(true, "bEnabled"));
            }
            var (known, fresh) = FreshChannels(game, pcc, spec, prop, looks);
            channelled = known && PutChannels(meshProps, fresh);
            if (!known && meshProps.GetProp<StructProperty>("LightingChannels") is { } now && SameExactly(now, Channels([PropChannel])))
            {
                notes.Add($"{prop.Name} (line {prop.Line}): its mesh is still on the prop lights' channel (Gameplay_4) and the entry has no template or mesh to read its "
                          + "own channels from: give it one, or set its LightingChannels by hand (export_set).");
            }
            var off = spec.Lights.Where(l => !l.Enabled && Reaches(l)).Select(l => $"{l.Name} (line {l.Line})").ToList();
            notes.Add(off.Count == 0
                ? $"{prop.Name} (line {prop.Line}): no prop light covers it; lit by its light environment, brighter than vanilla: select it in the editor and press L"
                : $"{prop.Name} (line {prop.Line}): lit by its light environment, brighter than vanilla: {Listed(off)} would cover it but "
                  + $"{(off.Count == 1 ? "is" : "are")} switched off (enabled: false): switching {(off.Count == 1 ? "it" : "one")} on (in the editor, or enabled: true) "
                  + "lights the prop, as does selecting it in the editor and pressing L");
        }
        if (lit) environment.WriteProperties(environmentProps);
        if (channelled) mesh.WriteProperties(meshProps);
        if (lit || channelled) changes.Add(covered ? "lit by prop lights (light environment off, mesh on Gameplay_4)" : "lit by its light environment (mesh channels as its source's)");
    }

    /// <summary>Items as a sentence names them: "a", "a and b", "a, b and c".</summary>
    private static string Listed(IReadOnlyList<string> items) =>
        items.Count < 2 ? string.Join("", items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    /// <summary>
    /// The centre of the prop's mesh bounds where it stands: the StaticMesh's Bounds, scaled by DrawScale × DrawScale3D (and the
    /// component's scale), turned and placed by the actor's pose; its location when the mesh is not in the level.
    /// </summary>
    private static Vector3 BoundsCentre(IMEPackage pcc, ExportEntry actor, ExportEntry mesh)
    {
        var pose = ForkBuilder.PoseOf(actor);
        if (Effective(mesh).GetProp<ObjectProperty>("StaticMesh")?.ResolveToEntry(pcc) is not ExportEntry { ClassName: "StaticMesh" } staticMesh) return pose.Location;
        return ForkBuilder.WorldBox(ObjectBinary.From<StaticMesh>(staticMesh).Bounds, ForkBuilder.TotalScale(mesh, pose), pose).Centre;
    }

    /// <summary>
    /// The mesh channels a new build gives the prop's actor: those of the mesh component of what it is copied from (its template, or
    /// for a mesh or a StaticMeshActor template the prop template), through its archetypes; null when that has none (the class
    /// default decides). A template that is a prop of this level (live_save names a copy of a built prop by its actor) gives what its
    /// own entry gives: it may be lit by prop lights, or be switched later in this build. A source on the prop lights' channel alone is
    /// a covered prop, whose channels are not its look: the prop template's then. Not known when the entry names neither, or only
    /// such sources. Read once per source and build.
    /// </summary>
    private static (bool Known, StructProperty? Channels) FreshChannels(MEGame game, IMEPackage pcc, Spec spec, PropSpec prop,
        Dictionary<(string?, string), (bool Static, StructProperty? Channels)> looks, int depth = 0)
    {
        var propLights = Channels([PropChannel]);
        for (var from = prop.Mesh is null ? prop.Template : spec.PropTemplate; from is not null;)
        {
            if (from.Package is null && depth < spec.Props.Count && PropOf(pcc, spec, from) is { } built && built != prop
                && FreshChannels(game, pcc, spec, built, looks, depth + 1) is { Known: true } inherited)
            {
                return inherited;
            }
            if (!looks.TryGetValue((from.Package, from.Actor), out var look))
            {
                using var source = from.Package is null ? null : PackageInspector.Open(game, from.Package);
                var original = ResolveActor(source ?? pcc, from.Actor, from.Line);
                var props = Effective(original);
                var mesh = (props.GetProp<ObjectProperty>("PlaceableMesh") ?? props.GetProp<ObjectProperty>("StaticMeshComponent"))?.ResolveToEntry(source ?? pcc) as ExportEntry;
                look = (original.ClassName == "StaticMeshActor", mesh is null ? null : Effective(mesh).GetProp<StructProperty>("LightingChannels"));
                looks[(from.Package, from.Actor)] = look;
            }
            var covered = look.Channels is { } channels && SameExactly(channels, propLights);
            if (!covered && (!look.Static || from == spec.PropTemplate)) return (true, look.Channels);
            if (from == spec.PropTemplate) break;
            from = spec.PropTemplate;
        }
        return (false, null);
    }

    /// <summary>The props entry whose actor a template of this level is (by its tag), if any.</summary>
    private static PropSpec? PropOf(IMEPackage pcc, Spec spec, Source from) =>
        ResolveActor(pcc, from.Actor, from.Line).GetProperty<NameProperty>("Tag")?.Value.Instanced is { } tag
            ? spec.Props.FirstOrDefault(p => tag.Equals(p.Tag, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>A LightingChannelContainer in full: these channels and bInitialized on, every other one written off.</summary>
    private static StructProperty Channels(IEnumerable<string> on)
    {
        var channels = GlobalUnrealObjectInfo.getDefaultStructValue(MEGame.LE1, "LightingChannelContainer", true, null)
                       ?? throw new InvalidOperationException("LEC has no LightingChannelContainer for LE1.");
        var wanted = on.Append("bInitialized").ToHashSet();
        var bits = channels.OfType<BoolProperty>().Select(b => (Property)new BoolProperty(wanted.Contains(b.Name.Instanced), b.Name)).ToList();
        if (wanted.Except(bits.Select(b => b.Name.Instanced)).FirstOrDefault() is { } missing) throw new InvalidOperationException($"LEC's LightingChannelContainer has no {missing}.");
        return new StructProperty("LightingChannelContainer", [.. bits], "LightingChannels");
    }

    /// <summary>Sets LightingChannels to exactly these (null: none of its own) when they differ, field for field.</summary>
    private static bool PutChannels(PropertyCollection props, StructProperty? channels)
    {
        var old = props.GetProp<StructProperty>("LightingChannels");
        if (channels is null) return old is not null && props.RemoveNamedProperty("LightingChannels");
        if (old is not null && SameExactly(old, channels)) return false;
        props.AddOrReplaceProp(channels.DeepClone());
        return true;
    }

    /// <summary>Two structs with the same fields, each of the same value: a field one leaves out takes its archetype's, so it counts.</summary>
    private static bool SameExactly(StructProperty a, StructProperty b)
    {
        var (x, y) = (a.Properties.Where(p => p is not NoneProperty).ToList(), b.Properties.Where(p => p is not NoneProperty).ToList());
        return a.StructType == b.StructType && x.Count == y.Count && y.All(p => x.FirstOrDefault(q => q.Name == p.Name) is { } q && Same(q, p));
    }

    /// <summary>
    /// Makes a placeable (a copy of the prop template) show a static mesh actor's mesh, materials and scale. A StaticMeshActor
    /// is lit by light baked where it stands, so a copy elsewhere stays black; the placeable is lit by its DynamicLightEnvironment
    /// where it is, as live_place's stand-in, or by prop lights instead when a light of the lights section covers it (SwitchLighting).
    /// </summary>
    private static void ShowActorMesh(IMEPackage source, ExportEntry original, IMEPackage pcc, ExportEntry placeable, PackageCache cache, List<string> changes)
    {
        var actorProps = Effective(original);
        var component = actorProps.GetProp<ObjectProperty>("StaticMeshComponent")?.ResolveToEntry(source) as ExportEntry
                        ?? throw new ArgumentException($"{original.InstancedFullPath} has no StaticMeshComponent.");
        var componentProps = Effective(component);
        var mesh = componentProps.GetProp<ObjectProperty>("StaticMesh")?.ResolveToEntry(source) ?? throw new ArgumentException($"{component.InstancedFullPath} has no mesh.");
        var materials = componentProps.GetProp<ArrayProperty<ObjectProperty>>("Materials")?.Select(m => m.ResolveToEntry(source)).ToList() ?? [];
        ShowMesh(source, mesh, materials, actorProps, pcc, placeable, cache, changes);
    }

    /// <summary>
    /// Puts a mesh of the source package on the placeable's PlaceableMesh, with these materials (none: the mesh's own) and the
    /// DrawScale, DrawScale3D and Rotation found in <paramref name="placed"/> (none: 1, and not turned). Baked light is dropped: it
    /// was made for another mesh.
    /// </summary>
    private static void ShowMesh(IMEPackage source, IEntry mesh, IReadOnlyList<IEntry?> materials, IEnumerable<Property> placed, IMEPackage pcc, ExportEntry placeable,
                                 PackageCache cache, List<string> changes)
    {
        var shown = Object(pcc, placeable.GetProperties(), "PlaceableMesh") ?? throw new ArgumentException($"The prop template {placeable.ObjectName.Instanced} has no PlaceableMesh: give a BioInert.");
        DropBakedLight(placeable);
        var shownProps = shown.GetProperties();
        shownProps.AddOrReplaceProp(new ObjectProperty(Port(source, mesh, pcc, cache), "StaticMesh"));
        shownProps.RemoveNamedProperty("Materials");
        if (materials.Count > 0)
        {
            var ported = materials.Select(m => new ObjectProperty(m is null ? null : Port(source, m, pcc, cache)));
            shownProps.AddOrReplaceProp(new ArrayProperty<ObjectProperty>(ported, "Materials"));
        }
        shown.WriteProperties(shownProps);

        var props = placeable.GetProperties();
        var values = placed.ToList();
        foreach (var name in new[] { "DrawScale", "DrawScale3D" })
        {
            props.RemoveNamedProperty(name);
            if (values.FirstOrDefault(p => p.Name == name) is { } kept) props.AddOrReplaceProp(kept);
        }
        // Turned as what it shows, not as the actor it was copied from: a 'yaw' then keeps that pitch and roll.
        props.AddOrReplaceProp(values.FirstOrDefault(p => p.Name == "Rotation") ?? CommonStructs.RotatorProp(new Rotator(0, 0, 0), "Rotation"));
        placeable.WriteProperties(props);
        changes.Add($"shows {mesh.InstancedFullPath}");
    }

    /// <summary>The mesh's export in its file, which must be a StaticMesh.</summary>
    private static ExportEntry ResolveMesh(IMEPackage source, MeshSource mesh)
    {
        ExportEntry export;
        try
        {
            export = ExportInspector.Resolve(source, mesh.Export);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"line {mesh.Line}: {Path.GetFileName(source.FilePath)}: {e.Message}");
        }
        return export.ClassName == "StaticMesh" ? export : throw new ArgumentException($"line {mesh.Line}: {export.InstancedFullPath} is a {export.ClassName}, not a StaticMesh.");
    }

    /// <summary>Sets visibility and collision to these values; a flag they lack is dropped, so the actor's archetype (its class defaults) decides it.</summary>
    private static bool PutShown(PropertyCollection props, IReadOnlyList<Property> shown)
    {
        var dirty = false;
        foreach (var name in ShownFlags)
        {
            dirty |= shown.FirstOrDefault(p => p.Name == name) is { } value ? Put(props, value) : props.RemoveNamedProperty(name);
        }
        return dirty;
    }

    /// <summary>Hidden without collision by its own values, as removed: true leaves it.</summary>
    private static bool LooksRemoved(ExportEntry actor)
    {
        var props = actor.GetProperties();
        return props.GetProp<BoolProperty>("bHidden")?.Value == true && props.GetProp<BoolProperty>("bCollideActors")?.Value == false
               && props.GetProp<BoolProperty>("bBlockActors")?.Value == false;
    }

    /// <summary>
    /// The visibility and collision a new build gives the prop's actor: those of what it is copied from (the template's actor; for a
    /// mesh or a StaticMeshActor template, the prop template). Null when the entry names neither.
    /// </summary>
    private static List<Property>? BuiltShown(MEGame game, IMEPackage pcc, Spec spec, PropSpec prop)
    {
        for (var from = prop.Mesh is null ? prop.Template : spec.PropTemplate; from is not null;)
        {
            using var source = from.Package is null ? null : PackageInspector.Open(game, from.Package);
            var original = ResolveActor(source ?? pcc, from.Actor, from.Line);
            if (original.ClassName != "StaticMeshActor" || from == spec.PropTemplate)
            {
                return Effective(original).Where(p => ShownFlags.Contains(p.Name.Name)).ToList();
            }
            from = spec.PropTemplate;
        }
        return null;
    }

    /// <summary>An export's properties with what it inherits from archetype exports (a prefab's) under them; references not remapped.</summary>
    internal static PropertyCollection Effective(ExportEntry export)
    {
        var props = export.Archetype is ExportEntry archetype ? Effective(archetype) : new PropertyCollection();
        foreach (var own in export.GetProperties().Where(p => p is not NoneProperty)) props.AddOrReplaceProp(own);
        return props;
    }

    /// <summary>An object of the source package in the level: an export copied with what it needs, an import added; the same one if the level has it.</summary>
    private static IEntry Port(IMEPackage source, IEntry entry, IMEPackage pcc, PackageCache cache)
    {
        if (source == pcc) return entry;
        if (pcc.FindEntry(entry.InstancedFullPath) is { } existing) return existing;
        if (entry is ExportEntry export)
        {
            EntryExporter.ExportExportToPackage(export, pcc, out var ported, cache);
            return ported;
        }
        return EntryImporter.GetOrAddCrossImportOrPackage(entry.InstancedFullPath, source, pcc, new RelinkerOptionsPackage { Cache = cache });
    }

    /// <summary>Location; yaw in degrees (pitch and roll kept) or rotation [pitch, yaw, roll] in degrees; DrawScale and DrawScale3D.</summary>
    private static bool PutPlace(PropertyCollection props, Vector3? location, float? yaw, List<string> changes, float[]? rotation = null, float? scale = null, Vector3? scale3d = null)
    {
        var dirty = location is { } l && Changed(changes, "location", Put(props, CommonStructs.Vector3Prop(l, "Location")));
        if (yaw is { } y)
        {
            var turned = props.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : new Rotator(0, 0, 0);
            dirty |= Changed(changes, "yaw", Put(props, CommonStructs.RotatorProp(new Rotator(turned.Pitch, Units(y), turned.Roll), "Rotation")));
        }
        if (rotation is { } pyr) dirty |= Changed(changes, "rotation", Put(props, CommonStructs.RotatorProp(new Rotator(Units(pyr[0]), Units(pyr[1]), Units(pyr[2])), "Rotation")));
        if (scale is { } s) dirty |= Changed(changes, "scale", Put(props, new FloatProperty(s, "DrawScale")));
        if (scale3d is { } s3) dirty |= Changed(changes, "scale3d", Put(props, CommonStructs.Vector3Prop(s3, "DrawScale3D")));
        return dirty;
    }

    /// <summary>Degrees to Unreal rotation units (65536 = 360°).</summary>
    internal static int Units(float degrees) => (int)Math.Round(degrees * 65536 / 360);

    /// <summary>Main_Sequence: Shepard uses the placeable (BioSeqEvt_ArtPlaceableUsed) → BioSeqAct_AreaTransition to the map and start point.</summary>
    private static void LinkAreaTransition(IMEPackage pcc, ExportEntry usable, string map, string? startPoint, int line, PackageCache cache, List<string> changes)
    {
        var sequence = pcc.FindExport(MainSequence) ?? throw new ArgumentException($"line {line}: the level has no {MainSequence}.");
        var objects = KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().ToList();
        var used = objects.FirstOrDefault(o => o.ClassName == "BioSeqEvt_ArtPlaceableUsed" && o.GetProperty<ObjectProperty>("Originator")?.Value == usable.UIndex);
        if (used is null)
        {
            used = SequenceObjectCreator.CreateSequenceObject(sequence, "BioSeqEvt_ArtPlaceableUsed", cache);
            KismetHelper.WriteOriginator(used, usable);
            changes.Add("Kismet: use event");
        }
        var outIndex = KismetHelper.GetOutputLinkNames(used).IndexOf("Out");
        if (outIndex < 0) throw new InvalidOperationException($"#{used.UIndex} has no 'Out' link.");
        var transition = KismetHelper.GetOutputLinksOfNode(used)[outIndex].Select(o => o.LinkedOp).OfType<ExportEntry>().FirstOrDefault(o => o.ClassName == "BioSeqAct_AreaTransition");
        if (transition is null)
        {
            transition = SequenceObjectCreator.CreateSequenceObject(sequence, "BioSeqAct_AreaTransition", cache);
            KismetHelper.CreateOutputLink(used, "Out", transition, 0);
            changes.Add("Kismet: use → area transition");
        }

        var props = transition.GetProperties();
        var dirty = Put(props, new NameProperty(map, "AreaName"));
        dirty |= startPoint is null ? props.RemoveNamedProperty("StartPoint") : Put(props, new NameProperty(startPoint, "StartPoint"));
        if (dirty)
        {
            transition.WriteProperties(props);
            changes.Add($"goes to {map}{(startPoint is null ? "" : $" {startPoint}")}");
        }
    }

    /// <summary>The template's actor imported with everything it references, or an actor of this level cloned; added to the level's actors.</summary>
    /// <param name="cloneOnClash">
    /// When the level already has an actor of that name: clone it when it is a copy of this template (an earlier import, see
    /// <see cref="IsCopyOf"/>), else import the template under a free name (another file's actor of the same name, or the level's own),
    /// instead of failing.
    /// </param>
    /// <param name="cloned">Called after such a clone with the template's actor (its file still open, flattened) and the clone.</param>
    private static ExportEntry AddActor(MEGame game, IMEPackage pcc, ExportEntry level, Source template, PackageCache cache, bool cloneOnClash = false,
        Action<ExportEntry, ExportEntry>? cloned = null)
    {
        ExportEntry added;
        if (template.Package is null)
        {
            added = EntryCloner.CloneTree(ResolveActor(pcc, template.Actor, template.Line));
        }
        else
        {
            using var source = PackageInspector.Open(game, template.Package);
            var actor = ResolveActor(source, template.Actor, template.Line);
            Flatten(actor);
            var clash = pcc.FindEntry($"{LevelPath}.{actor.ObjectName.Instanced}");
            if (clash is ExportEntry earlier && cloneOnClash && IsCopyOf(earlier, actor))
            {
                added = EntryCloner.CloneTree(earlier);
                // The earlier copy may be hidden (removed: true); the new one shows as the source does.
                var props = added.GetProperties();
                if (PutShown(props, actor.GetProperties().Where(p => ShownFlags.Contains(p.Name.Name)).ToList())) added.WriteProperties(props);
                cloned?.Invoke(actor, added);
                pcc.AddToLevelActorsIfNotThere(added);
                return added;
            }
            if (clash is ExportEntry && cloneOnClash)
            {
                // A clone would carry the other actor's class and values: the import gets a name no export has (only in memory, in the
                // template's file), so it copies instead of linking.
                actor.ObjectName = pcc.GetNextIndexedName(actor.ObjectName.Name);
                clash = null;
            }
            if (clash is not null)
            {
                throw new ArgumentException($"line {template.Line}: the level already has {clash.InstancedFullPath}, so an import would link to it instead of copying. Clone it: template: {{ actor: {actor.ObjectName.Instanced} }}.");
            }
            var rop = new RelinkerOptionsPackage { Cache = cache };
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, actor, pcc, level, true, rop, out var entry);
            added = entry as ExportEntry ?? throw new InvalidOperationException("The import did not create an export.");
        }
        pcc.AddToLevelActorsIfNotThere(added);
        return added;
    }

    /// <summary>
    /// An actor of the level is a copy, imported or cloned, of a template's actor when it has its class and the same subobjects, each by
    /// its path under the actor and its class. A copy keeps the names the template's components have in their own file
    /// (PointLightComponent_2438), which another file's actor of the same name, or the level's own, rarely has: LE1CP's
    /// SpotLightToggleable_1 of BIOA_NOR10_03_DSG has SpotLightComponent_3, BIOA_NOR10_01_DSG's SpotLightComponent_2.
    /// </summary>
    private static bool IsCopyOf(ExportEntry actor, ExportEntry template)
    {
        static List<string> Tree(ExportEntry root) =>
            Subobjects(root).Select(e => $"{e.InstancedFullPath[(root.InstancedFullPath.Length + 1)..]} {e.ClassName}").Order(StringComparer.Ordinal).ToList();
        return actor.ClassName == template.ClassName && Tree(actor).SequenceEqual(Tree(template));
    }

    /// <summary>Own actor type (name shown in game): a copy of the pawn's current one, renamed, with its own strref.</summary>
    private static bool SetActorType(IMEPackage pcc, PropertyCollection behaviorProps, NameSpec name, List<string> changes)
    {
        var current = Object(pcc, behaviorProps, "m_oActorType");
        var type = pcc.Exports.FirstOrDefault(e => e.ObjectName.Instanced.Equals(name.Type, StringComparison.OrdinalIgnoreCase) && e.IsA("BioActorType"));
        if (type is null)
        {
            var original = current ?? throw new ArgumentException($"line {name.Line}: the pawn has no actor type to copy.");
            type = EntryCloner.CloneTree(original);
            // Actor types are forced exports: two loaded files with the same path share one object, so the copy needs its own.
            type.ObjectName = NameReference.FromInstancedString(name.Type);
            changes.Add($"actor type {type.InstancedFullPath} (copy of {original.ObjectName.Instanced})");
        }
        var typeProps = type.GetProperties();
        if (Changed(changes, $"name ${name.StrRef}", Put(typeProps, new StringRefProperty(name.StrRef, "ActorGameNameStrRef")))) type.WriteProperties(typeProps);
        return Changed(changes, "actor type", Put(behaviorProps, new ObjectProperty(type, "m_oActorType")));
    }

    /// <summary>
    /// Per-pawn materials by mesh: skin on the body and the head's face and scalp, hair on the hair and the face (the
    /// face's copy tints the brows), eyes on the head's eye material. Only materials inside the pawn are changed.
    /// </summary>
    private static void SetColors(IMEPackage pcc, ExportEntry pawn, PropertyCollection props, NpcSpec npc, List<string> changes, List<string> notes)
    {
        if (npc.Skin is null && npc.Hair is null && npc.Eyes is null) return;
        var body = Materials(pcc, props, "Mesh");
        var hair = Materials(pcc, props, "m_oHairMesh");
        var head = Materials(pcc, props, "m_oHeadMesh").Select(m => (Material: m, Parent: Object(pcc, m.GetProperties(), "Parent")?.ObjectName.Name ?? "")).ToList();
        List<ExportEntry> Head(params string[] parts) =>
            head.Where(h => parts.Any(p => h.Parent.Contains(p, StringComparison.OrdinalIgnoreCase))).Select(h => h.Material).ToList();

        foreach (var (color, parameter, targets, what) in new[]
                 {
                     (npc.Skin, "SkinTone", body.Concat(Head("Face", "Scalp")).ToList(), "skin"),
                     (npc.Hair, "HED_Hair_Colour_Vector", hair.Concat(Head("Face")).ToList(), "hair"),
                     (npc.Eyes, "EYE_Iris_Colour_Vector", Head("_EYE_"), "eyes"),
                 })
        {
            if (color is null) continue;
            var own = targets.Where(m => IsInside(m, pawn)).ToList();
            if (own.Count < targets.Count) notes.Add($"{npc.Name}: {targets.Count - own.Count} {what} material(s) are shared with other pawns, not changed.");
            if (own.Count == 0) notes.Add($"{npc.Name}: no {what} material found.");
            var changed = own.Count(m => SetVectorParameter(m, parameter, color));
            if (changed > 0) changes.Add($"{what} on {changed} material(s)");
        }
    }

    private static bool SetVectorParameter(ExportEntry material, string parameter, float[] color)
    {
        var props = material.GetProperties();
        var values = props.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues") ?? new ArrayProperty<StructProperty>("VectorParameterValues");
        var value = values.FirstOrDefault(v => v.GetProp<NameProperty>("ParameterName")?.Value.Instanced == parameter);
        var alpha = color.Length > 3 ? color[3] : value?.GetProp<StructProperty>("ParameterValue")?.GetProp<FloatProperty>("A")?.Value ?? 1;
        // Not CommonStructs.LinearColorProp: at LEC 6.5 it writes R into all four channels.
        var wanted = new StructProperty("LinearColor", [new FloatProperty(color[0], "R"), new FloatProperty(color[1], "G"), new FloatProperty(color[2], "B"), new FloatProperty(alpha, "A")],
            "ParameterValue", true);
        if (value is null)
        {
            values.Add(new StructProperty("VectorParameterValue", false, wanted, CommonStructs.GuidProp(Guid.Empty, "ExpressionGUID"), new NameProperty(parameter, "ParameterName"), new NoneProperty()));
        }
        else if (!Put(value.Properties, wanted))
        {
            return false;
        }
        props.AddOrReplaceProp(values);
        material.WriteProperties(props);
        return true;
    }

    /// <summary>A fill light kept at <see cref="LightSpec.Offset"/> from the NPC, lighting only dynamic objects (see docs/06 §A.8–A.9).</summary>
    private static void SetLight(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, LightSpec light, Vector3 origin, int yaw, PackageCache cache, List<string> changes)
    {
        var angle = yaw * Math.PI * 2 / 65536;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var at = new Vector3(
            (float)(origin.X + light.Offset.X * cos - light.Offset.Y * sin),
            (float)(origin.Y + light.Offset.X * sin + light.Offset.Y * cos),
            (float)(origin.Z + light.Offset.Z));
        var (_, action, changed) = ApplyLight(game, pcc, level, new LightSettings(light.Tag, at, light.Line, light.Radius, light.Brightness), spec.LightTemplate, cache);
        // Among the NPC's own changes: each light on one line.
        if (action != "found") changes.Add($"light {light.Tag} added");
        else if (changed.Count > 0) changes.Add($"light {light.Tag}: {string.Join(", ", changed)}");
    }

    /// <summary>A light of the lights: section, found by its tag, else copied from its template.</summary>
    private static BuiltLight BuildLight(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, LightEntrySpec light, PackageCache cache)
    {
        var settings = new LightSettings(light.Tag!, light.Location!.Value, light.Line, light.Radius, light.Brightness, light.Enabled, light.Kind, light.Color, light.Falloff);
        var (actor, action, changed) = ApplyLight(game, pcc, level, settings, light.Template ?? spec.LightTemplate, cache);
        return new BuiltLight(light.Name, ExportInspector.Row(actor), action, action == "found" ? changed : [$"light {light.Tag} added"]);
    }

    /// <summary>
    /// A light found by its tag, else copied from its template: <paramref name="template"/> (the entry's, else the file's lightTemplate),
    /// else LE1CP's fill light for Chakwas. Not a light of the level picked by export order: in the freighter that was the copied
    /// character lights' collection, which has no LightComponent. A second copy of one template clones the first (an import would
    /// link to it); the first may be a lights: entry's, so a new NPC light then takes back from the template what it leaves to it
    /// (<see cref="TakeTemplateLight"/>). A light of the level that only has the template's name (another file's template of that
    /// name, or the level's own) is not cloned: the template is imported under a free name (<see cref="AddActor"/>). Always
    /// written: its tag and place, the component's bEnabled, bOnlyAffectSameAndSpecifiedLevels false (the level filter never
    /// matches a pawn in a Sideloader level, docs/06 §A.9), the radius and brightness given, CachedParentToWorld's WPlane, new
    /// GUIDs for a new copy, imported or cloned (lightmaps, shadow maps and a primitive's IrrelevantLights know a light by its
    /// LightGuid, so no two lights share one). A lights: entry (<see cref="LightSettings.Kind"/>) also gets the actor's bEnabled,
    /// its colour, falloff, no shadows, dynamic light, its kind's channels in full and no OtherLevelsToAffect (the template's level
    /// list, which the filter left off ignores); an NPC light keeps its template's (Reyes's: Dynamic only).
    /// </summary>
    /// <returns>The light, found, imported or cloned, and what the build changed on a found one, each value old → new.</returns>
    private static (ExportEntry Actor, string Action, List<string> Changed) ApplyLight(MEGame game, IMEPackage pcc, ExportEntry level, LightSettings light, Source? template,
        PackageCache cache)
    {
        var actor = FindActor(pcc, level, light.Tag, e => e.IsA("Light"));
        var isNew = actor is null;
        var action = "found";
        if (actor is null)
        {
            var from = template ?? new Source(DefaultLightTemplate.Package, DefaultLightTemplate.Actor, light.Line);
            var clone = false;
            actor = AddActor(game, pcc, level, from, cache, cloneOnClash: true, (original, copy) =>
            {
                clone = true;
                if (light.Kind is null) TakeTemplateLight(pcc, original, copy);
            });
            if (!actor.IsA("Light")) throw new ArgumentException($"line {from.Line}: the light template {from.Actor} is a {actor.ClassName}, not a light.");
            action = from.Package is null || clone ? "cloned" : "imported";
        }
        var full = light.Kind is not null;
        var changed = new List<string>();

        var props = actor.GetProperties();
        var was = props.GetProp<StructProperty>("Location") is { } l ? CommonStructs.GetVector3(l) : Vector3.Zero;
        var moved = Changed(changed, $"location {ForkBuilder.Moved(was, light.Location)}", Put(props, CommonStructs.Vector3Prop(light.Location, "Location")));
        var dirty = Changed(changed, $"tag {props.GetProp<NameProperty>("Tag")?.Value.Instanced} → {light.Tag}", Put(props, new NameProperty(light.Tag, "Tag"))) | moved;
        // On or off: one receipt line for the actor's flag (a lights: entry's) and the component's.
        var switched = full && Put(props, new BoolProperty(light.Enabled, "bEnabled"));
        if (dirty || switched) actor.WriteProperties(props);

        var component = Object(pcc, props, "LightComponent") ?? throw new ArgumentException($"line {light.Line}: {actor.InstancedFullPath} has no LightComponent.");
        var c = component.GetProperties();
        var edited = Put(c, new BoolProperty(light.Enabled, "bEnabled"));
        Changed(changed, light.Enabled ? "switched on" : "switched off", switched | edited);
        edited |= PutFlag(c, "bOnlyAffectSameAndSpecifiedLevels", false, changed);
        if (light.Radius is { } radius) edited |= PutNumber(c, "Radius", radius, "radius", changed);
        if (light.Brightness is { } brightness) edited |= PutNumber(c, "Brightness", brightness, "brightness", changed);
        if (c.GetProp<StructProperty>("CachedParentToWorld")?.GetProp<StructProperty>("WPlane") is { } plane)
        {
            var followed = Put(plane.Properties, new FloatProperty(light.Location.X, "X")) | Put(plane.Properties, new FloatProperty(light.Location.Y, "Y"))
                           | Put(plane.Properties, new FloatProperty(light.Location.Z, "Z"));
            // It follows the location: a line of its own only when it was off the light's place.
            if (followed && !moved) changed.Add("CachedParentToWorld moved to the light");
            edited |= followed;
        }
        else if (full)
        {
            // A template without one: the identity, moved to the light (the planes named as the game's own files name them).
            static StructProperty Plane(string name, float w, float x, float y, float z) =>
                new("Plane", [new FloatProperty(w, "W"), new FloatProperty(x, "X"), new FloatProperty(y, "Y"), new FloatProperty(z, "Z")], name, true);
            var at = light.Location;
            edited |= Changed(changed, "CachedParentToWorld added", Put(c, new StructProperty("Matrix",
                [Plane("XPlane", 0, 1, 0, 0), Plane("YPlane", 0, 0, 1, 0), Plane("ZPlane", 0, 0, 0, 1), Plane("WPlane", 1, at.X, at.Y, at.Z)], "CachedParentToWorld", true)));
        }
        if (full)
        {
            var rgb = light.Color ?? [255, 255, 255];
            // Alpha 255: a spawned light's is 0.
            var colour = CommonStructs.ColorProp(System.Drawing.Color.FromArgb(255, rgb[0], rgb[1], rgb[2]), "LightColor");
            edited |= Changed(changed, $"colour {Colour(c.GetProp<StructProperty>("LightColor"))} → {Colour(colour)}", Put(c, colour));
            edited |= PutNumber(c, "FalloffExponent", light.Falloff, "falloff", changed);
            edited |= PutFlag(c, "CastShadows", false, changed);
            edited |= PutFlag(c, "bForceDynamicLight", true, changed);
            edited |= Changed(changed, KindChange(c.GetProp<StructProperty>("LightingChannels"), light.Kind!), PutChannels(c, Channels(KindChannels[light.Kind!])));
            if (c.GetProp<ArrayPropertyBase>("OtherLevelsToAffect") is { } others)
            {
                var levels = others.Properties.Select(p => p switch { NameProperty n => n.Value.Instanced, StrProperty t => t.Value, _ => p.ToString() });
                edited |= Changed(changed, $"OtherLevelsToAffect [{string.Join(", ", levels)}] cleared", c.RemoveNamedProperty("OtherLevelsToAffect"));
            }
        }
        if (isNew)
        {
            c.AddOrReplaceProp(CommonStructs.GuidProp(Guid.NewGuid(), "LightGuid"));
            c.AddOrReplaceProp(CommonStructs.GuidProp(Guid.NewGuid(), "LightmapGuid"));
        }
        if (edited || isNew) component.WriteProperties(c);
        return (actor, action, isNew ? [] : changed);
    }

    /// <summary>Sets a float when it differs, with its receipt line: "&lt;what&gt; old → new" (old "default" when it had none of its own).</summary>
    private static bool PutNumber(PropertyCollection props, string name, float value, string what, List<string> changes) =>
        Changed(changes, $"{what} {(props.GetProp<FloatProperty>(name)?.Value is { } old ? ForkBuilder.Number(old) : "default")} → {ForkBuilder.Number(value)}",
            Put(props, new FloatProperty(value, name)));

    /// <summary>Sets a flag when it differs, with its receipt line: "&lt;name&gt; old → new" (old "default" when it had none of its own).</summary>
    private static bool PutFlag(PropertyCollection props, string name, bool value, List<string> changes) =>
        Changed(changes, $"{name} {(props.GetProp<BoolProperty>(name)?.Value is { } old ? Flag(old) : "default")} → {Flag(value)}", Put(props, new BoolProperty(value, name)));

    private static string Flag(bool value) => value ? "true" : "false";

    /// <summary>A Color as a receipt shows it: (r, g, b), with its alpha when that is not 255; "default" when it has none of its own.</summary>
    private static string Colour(StructProperty? color)
    {
        if (color is null) return "default";
        int Channel(string name) => color.GetProp<ByteProperty>(name)?.Value ?? 0;
        return Channel("A") == 255 ? $"({Channel("R")}, {Channel("G")}, {Channel("B")})" : $"({Channel("R")}, {Channel("G")}, {Channel("B")}, alpha {Channel("A")})";
    }

    /// <summary>"kind props → room" when the old channels are a kind's in full, else the kind with the channels it replaced.</summary>
    private static string KindChange(StructProperty? old, string kind)
    {
        if (old is not null && KindChannels.Keys.FirstOrDefault(k => SameExactly(old, Channels(KindChannels[k]))) is { } was) return $"kind {was} → {kind}";
        var on = old?.Properties.OfType<BoolProperty>().Where(b => b.Value && b.Name.Instanced != "bInitialized").Select(b => b.Name.Instanced).ToList();
        return $"kind {kind} (channels were {(on is null ? "the template's" : on.Count == 0 ? "none" : string.Join(", ", on))})";
    }

    /// <summary>LE1CP's fill light for Chakwas in the medbay: a point light any level can copy (what level.yaml names as its lightTemplate).</summary>
    private static readonly Source DefaultLightTemplate = new("BIOA_NOR10_07_DSG.pcc", "PointLightToggleable_7", 0);

    /// <summary>
    /// The component's values a lights: entry writes and an NPC light leaves to its template: its colour, falloff, shadows, dynamic light
    /// and channels, the radius and brightness it may leave out, and the cached transform (whose WPlane the light then moves).
    /// </summary>
    private static readonly string[] TemplateLightFields =
        ["LightColor", "FalloffExponent", "CastShadows", "bForceDynamicLight", "LightingChannels", "Radius", "Brightness", "CachedParentToWorld"];

    /// <summary>
    /// A new NPC light cloned from an earlier copy of its template, which may be a lights: entry's (on Gameplay_4 alone, its colour,
    /// maybe off): the actor's bEnabled and the component's <see cref="TemplateLightFields"/> back to the template's own (flattened)
    /// values, none where it has none, as an import of the template would have them; and the template's OtherLevelsToAffect when the
    /// copy has none.
    /// </summary>
    private static void TakeTemplateLight(IMEPackage pcc, ExportEntry template, ExportEntry copy)
    {
        static bool Take(PropertyCollection to, PropertyCollection from, string name)
        {
            if (from.GetProp<Property>(name) is not { } value) return to.RemoveNamedProperty(name);
            // A struct field for field: Put compares only the fields the template's lists, so the clone's Gameplay_4 could stay.
            if (value is not StructProperty whole || to.GetProp<StructProperty>(name) is not { } old || SameExactly(old, whole)) return Put(to, value);
            to.AddOrReplaceProp(whole);
            return true;
        }

        var templateProps = template.GetProperties();
        var props = copy.GetProperties();
        if (Take(props, templateProps, "bEnabled")) copy.WriteProperties(props);
        if (Object(template.FileRef, templateProps, "LightComponent") is not { } original || Object(pcc, props, "LightComponent") is not { } component) return;
        var originalProps = original.GetProperties();
        var c = component.GetProperties();
        var dirty = TemplateLightFields.Aggregate(false, (any, name) => Take(c, originalProps, name) | any);
        // The level list only when the copy has none (a lights: entry's clears it): an NPC light's own list, as Reyes's, stays.
        if (c.GetProp<ArrayPropertyBase>("OtherLevelsToAffect") is null && originalProps.GetProp<ArrayPropertyBase>("OtherLevelsToAffect") is { } levels)
        {
            c.AddOrReplaceProp(levels);
            dirty = true;
        }
        if (dirty) component.WriteProperties(c);
    }

    /// <summary>
    /// A note for each light of the level on the prop lights' channel whose tag no lights: entry names (its entry deleted by hand): the
    /// build never deletes a light, so it stays as built, and MeleBridge no longer lists it.
    /// </summary>
    private static void NoteOrphanLights(IMEPackage pcc, ExportEntry level, Spec spec, List<string> notes)
    {
        var named = spec.Lights.Select(l => l.Tag).Concat(spec.Npcs.SelectMany(n => n.Lights.Select(l => l.Tag))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var light in pcc.Exports.Where(e => e.Parent == level && e.IsA("Light")))
        {
            var props = light.GetProperties();
            if (props.GetProp<NameProperty>("Tag")?.Value.Instanced is not { } tag || named.Contains(tag)) continue;
            // By its instanced name: Gameplay_4 is the name Gameplay, number 5, which a lookup by the string never matches.
            if (Object(pcc, props, "LightComponent")?.GetProperty<StructProperty>("LightingChannels")?.Properties.OfType<BoolProperty>()
                    .FirstOrDefault(b => b.Name.Instanced == PropChannel)?.Value != true) continue;
            var on = Object(pcc, props, "LightComponent")?.GetProperty<BoolProperty>("bEnabled")?.Value != false;
            notes.Add($"{light.ObjectName.Instanced} (tag {tag}) is a light of the lights section no entry names any more: it stays as built ({(on ? "on" : "off")}), "
                      + "and MeleBridge no longer lists it. To switch it off, give it an entry again with enabled: false.");
        }
    }

    /// <summary>
    /// Main_Sequence: Shepard uses the pawn (BioSeqEvt_OnPlayerActivate) → BioSeqAct_StartConversation, Owner = the pawn.
    /// Returns the use event and the start action; null when the level has no conversation of that name.
    /// </summary>
    private static (ExportEntry Use, ExportEntry Start)? LinkConversation(IMEPackage pcc, ExportEntry pawn, string name, int line, PackageCache cache, List<string> changes)
    {
        var sequence = pcc.FindExport(MainSequence) ?? throw new ArgumentException($"line {line}: the level has no {MainSequence}.");
        var conversation = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioConversation" && e.ObjectName.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (conversation is null) return null;
        var objects = KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().ToList();

        bool Holds(IEntry? variable) => variable is ExportEntry { ClassName: "SeqVar_Object" } v && v.GetProperty<ObjectProperty>("ObjValue")?.Value == pawn.UIndex;
        var start = objects.FirstOrDefault(o => o.ClassName == "BioSeqAct_StartConversation"
                                                && o.GetProperty<ObjectProperty>("Conv")?.Value == conversation.UIndex
                                                && KismetHelper.GetVariableLinksOfNode(o).Any(v => v.LinkDesc == "Owner" && v.LinkedNodes.Any(Holds)));
        if (start is null)
        {
            start = SequenceObjectCreator.CreateSequenceObject(sequence, "BioSeqAct_StartConversation", cache);
            start.WriteProperty(new ObjectProperty(conversation, "Conv"));
            var owner = SequenceObjectCreator.CreateSequenceObject(sequence, "SeqVar_Object", cache);
            KismetHelper.WriteObjValue(owner, pawn);
            KismetHelper.CreateVariableLink(start, "Owner", owner);
            changes.Add($"Kismet: start {conversation.ObjectName.Instanced}");
        }

        var use = objects.FirstOrDefault(o => o.ClassName == "BioSeqEvt_OnPlayerActivate" && o.GetProperty<ObjectProperty>("Originator")?.Value == pawn.UIndex);
        if (use is null)
        {
            use = SequenceObjectCreator.CreateSequenceObject(sequence, "BioSeqEvt_OnPlayerActivate", cache);
            KismetHelper.WriteOriginator(use, pawn);
            changes.Add("Kismet: use event");
        }
        var outIndex = KismetHelper.GetOutputLinkNames(use).IndexOf("Out");
        if (outIndex < 0) throw new InvalidOperationException($"#{use.UIndex} has no 'Out' link.");
        if (!KismetHelper.GetOutputLinksOfNode(use)[outIndex].Any(o => o.LinkedOp == start && o.InputLinkIdx == 0))
        {
            KismetHelper.CreateOutputLink(use, "Out", start, 0);
            changes.Add("Kismet: use → start");
        }
        return (use, start);
    }

    /// <summary>ConfigDelta .m3cd next to the level: LE1CP's Sideloader loads it with the files whose names start with a prefix. M3 merges it at install.</summary>
    private static string EnsureSideloader(string package, List<string> prefixes, List<string> backups, List<string> notes)
    {
        var dir = Path.GetDirectoryName(package)!;
        var name = Path.GetFileNameWithoutExtension(package);
        var wanted = $"+Levels=(LevelName=\"{name}\", LoadIfPrefix=({string.Join(",", prefixes.Select(p => $"\"{p}\""))}))";
        var marker = $"LevelName=\"{name}\"";
        foreach (var config in Directory.EnumerateFiles(dir, "*.m3cd"))
        {
            var text = File.ReadAllText(config);
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(newline);
            var index = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;
            if (lines[index].Trim() == wanted) return config;
            backups.Add(PackageWriter.Backup(config));
            lines[index] = wanted;
            File.WriteAllText(config, string.Join(newline, lines));
            notes.Add($"Updated {Path.GetFileName(config)}: reinstall the mod with M3 (it merges ConfigDelta files into Coalesced at install).");
            return config;
        }
        var path = Path.Combine(dir, $"ConfigDelta-{name}.m3cd");
        File.WriteAllText(path, $"[BioGame.ini SFXGame.SideloaderFramework]\n{wanted}\n");
        notes.Add($"Wrote {Path.GetFileName(path)}: install the mod with M3 (it merges ConfigDelta files into Coalesced at install).");
        return path;
    }

    /// <summary>
    /// Bakes what an actor and its components inherit from archetype exports (a prefab's) into them, and points them at the
    /// archetypes' own archetypes (the class defaults). Imported as is, the actor would bring its archetype, and with it the
    /// whole prefab: every archetype and mesh of the level's props (8 MB for one crate of BIOA_FRE31_00_LAY). Only changes the
    /// source package in memory.
    /// </summary>
    private static void Flatten(ExportEntry actor)
    {
        var tree = Subobjects(actor).Prepend(actor).ToList();
        while (actor.Archetype is ExportEntry archetype)
        {
            // The archetype's subobjects (components, behaviors and theirs) are the ones the actor's subobjects name as their archetypes.
            var inherited = tree.Where(e => e.Archetype is ExportEntry a && (a == archetype || IsInside(a, archetype))).ToList();
            var remap = inherited.ToDictionary(e => e.Archetype.UIndex, e => e.UIndex);
            foreach (var export in inherited)
            {
                var from = (ExportEntry)export.Archetype;
                Inherit(export, from, remap);
                export.Archetype = from.Archetype;
            }
        }
    }

    private static IEnumerable<ExportEntry> Subobjects(ExportEntry export) =>
        export.GetChildren().OfType<ExportEntry>().SelectMany(c => Subobjects(c).Prepend(c));

    /// <summary>Drops the light and shadow maps baked for an actor's meshes: they belong to its old mesh and place.</summary>
    private static void DropBakedLight(ExportEntry actor)
    {
        foreach (var component in actor.GetChildren().OfType<ExportEntry>().Where(c => c.ClassName == "StaticMeshComponent"))
        {
            var binary = ObjectBinary.From<StaticMeshComponent>(component);
            if (binary.LODData.All(l => l.LightMap.LightMapType == ELightMapType.LMT_None && l.ShadowMaps.Length == 0 && l.ShadowVertexBuffers.Length == 0)) continue;
            foreach (var lod in binary.LODData)
            {
                lod.LightMap = new LightMap { LightMapType = ELightMapType.LMT_None };
                lod.ShadowMaps = [];
                lod.ShadowVertexBuffers = [];
            }
            component.WriteBinary(binary);
            // The lights the bake found irrelevant there.
            component.RemoveProperty("IrrelevantLights");
        }
    }

    /// <summary>The archetype's properties, overridden by the export's own; references to the archetype's components go to the export's.</summary>
    private static void Inherit(ExportEntry export, ExportEntry archetype, Dictionary<int, int> remap)
    {
        var merged = archetype.GetProperties();
        foreach (var property in merged) Remap(property, remap);
        foreach (var own in export.GetProperties().Where(p => p is not NoneProperty)) merged.AddOrReplaceProp(own);
        export.WriteProperties(merged);
    }

    private static void Remap(Property property, Dictionary<int, int> remap)
    {
        switch (property)
        {
            case ObjectProperty o when remap.TryGetValue(o.Value, out var to): o.Value = to; break;
            case StructProperty s: foreach (var p in s.Properties) Remap(p, remap); break;
            case ArrayProperty<ObjectProperty> a: foreach (var p in a) Remap(p, remap); break;
            case ArrayProperty<StructProperty> a: foreach (var p in a) Remap(p, remap); break;
        }
    }

    // ---- Properties ----

    /// <summary>Sets a property when its value differs; true when it did.</summary>
    private static bool Put(PropertyCollection props, Property value)
    {
        var old = props.FirstOrDefault(p => p.Name == value.Name);
        if (old is not null && Same(old, value)) return false;
        props.AddOrReplaceProp(value);
        return true;
    }

    private static bool Same(Property a, Property b) => (a, b) switch
    {
        (FloatProperty x, FloatProperty y) => x.Value == y.Value,
        (IntProperty x, IntProperty y) => x.Value == y.Value,
        (BoolProperty x, BoolProperty y) => x.Value == y.Value,
        (ByteProperty x, ByteProperty y) => x.Value == y.Value,
        (NameProperty x, NameProperty y) => x.Value == y.Value,
        (StringRefProperty x, StringRefProperty y) => x.Value == y.Value,
        (ObjectProperty x, ObjectProperty y) => x.Value == y.Value,
        (StructProperty x, StructProperty y) => x.StructType == y.StructType
            && y.Properties.Where(p => p is not NoneProperty).All(p => x.Properties.FirstOrDefault(q => q.Name == p.Name) is { } q && Same(q, p)),
        _ => false,
    };

    private static bool Changed(List<string> changes, string what, bool changed)
    {
        if (changed) changes.Add(what);
        return changed;
    }

    private static ExportEntry? Object(IMEPackage pcc, PropertyCollection props, string name) => props.GetProp<ObjectProperty>(name)?.ResolveToEntry(pcc) as ExportEntry;

    private static List<ExportEntry> Materials(IMEPackage pcc, PropertyCollection pawnProps, string mesh) =>
        Object(pcc, pawnProps, mesh)?.GetProperty<ArrayProperty<ObjectProperty>>("Materials")?.Select(o => o.ResolveToEntry(pcc)).OfType<ExportEntry>().ToList() ?? [];

    private static bool IsInside(IEntry entry, IEntry ancestor)
    {
        for (var e = entry.Parent; e is not null; e = e.Parent)
        {
            if (e == ancestor) return true;
        }
        return false;
    }

    private static ExportEntry? FindActor(IMEPackage pcc, ExportEntry level, string tag, Func<ExportEntry, bool> kind) =>
        pcc.Exports.FirstOrDefault(e => e.Parent == level && kind(e) && e.GetProperty<NameProperty>("Tag")?.Value.Instanced.Equals(tag, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>BioPawn_3, TheWorld.PersistentLevel.BioPawn_3 or #18.</summary>
    private static ExportEntry ResolveActor(IMEPackage pcc, string actor, int line)
    {
        try
        {
            return ExportInspector.Resolve(pcc, actor.Contains('.') || actor.StartsWith('#') ? actor : $"{LevelPath}.{actor}");
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"line {line}: {e.Message}");
        }
    }

    // ---- YAML ----

    private static Spec Parse(string yaml)
    {
        var spec = new Spec();
        foreach (var (key, value) in Entries(Root(yaml), "package", "defaultLanguage", "globalTlk", "sideloader", "lightTemplate", "streaming", "postProcess",
                     "characterLights", "startPointTemplate", "startPoints", "squad", "npcs", "usables", "propTemplate", "props", "vanilla", "lights"))
        {
            switch (key)
            {
                case "package": spec.Package = Scalar(value); break;
                case "defaultLanguage": spec.DefaultLanguage = Language(Scalar(value)); break;
                case "globalTlk": spec.GlobalTlk = Sequence(value).Select(Scalar).ToList(); break;
                case "sideloader": spec.Sideloader = Sequence(value).Select(Scalar).ToList(); break;
                case "lightTemplate": spec.LightTemplate = SourceOf(value); break;
                case "streaming": spec.Streaming = StreamingOf(value); break;
                case "postProcess": spec.PostProcess = (Scalar(value), (int)value.Start.Line); break;
                case "characterLights": spec.CharacterLights = Sequence(value).Select(SourceOf).ToList(); break;
                case "startPointTemplate": spec.StartPointTemplate = SourceOf(value); break;
                case "squad":
                    if (value is not YamlMappingNode squad) throw Fail(value, "'squad' is {package: <mission map with BioWare's party prefab>}.");
                    string? squadPackage = null;
                    foreach (var (k, v) in Entries(squad, "package")) squadPackage = Scalar(v);
                    spec.Squad = (squadPackage ?? throw Fail(value, "'squad' needs 'package'."), (int)value.Start.Line);
                    break;
                case "startPoints":
                    if (value is not YamlMappingNode starts) throw Fail(value, "'startPoints' maps tags to {location: [x, y, z], yaw: <degrees>}.");
                    spec.StartPoints = starts.Children.Select(c => StartPointOf(Scalar(c.Key), c.Value)).ToList();
                    break;
                case "npcs":
                    if (value is not YamlMappingNode npcs) throw Fail(value, "'npcs' maps names to NPCs.");
                    spec.Npcs = npcs.Children.Select(c => NpcOf(Scalar(c.Key), c.Value, spec.Strings)).ToList();
                    break;
                case "usables":
                    if (value is not YamlMappingNode usables) throw Fail(value, "'usables' maps names to placeables.");
                    spec.Usables = usables.Children.Select(c => UsableOf(Scalar(c.Key), c.Value, spec.Strings)).ToList();
                    break;
                case "propTemplate": spec.PropTemplate = SourceOf(value); break;
                case "props":
                    if (value is not YamlMappingNode props) throw Fail(value, "'props' maps names to actors.");
                    spec.Props = props.Children.Select(c => PropOf(Scalar(c.Key), c.Value)).ToList();
                    break;
                case "vanilla":
                    // Its last entry deleted (as its comment says to give an actor its vanilla values back): no entry, no fork.
                    if (Null(value)) break;
                    if (value is not YamlMappingNode vanilla) throw Fail(value, "'vanilla' maps <file>.<actor name> to what changes: {location, rotation or yaw, scale, scale3d, removed}.");
                    spec.Vanilla = vanilla.Children.Select(c => VanillaOf(c.Key, c.Value)).ToList();
                    break;
                case "lights":
                    if (Null(value)) break;
                    if (value is not YamlMappingNode lights) throw Fail(value, "'lights' maps names to lights: {tag, kind, location, radius, brightness, color, falloff, enabled}.");
                    spec.Lights = lights.Children.Select(c => LightEntryOf(Scalar(c.Key), c.Value)).ToList();
                    break;
            }
        }
        // The build finds each actor again by its tag, and MeleBridge each light: an NPC light's included.
        var tags = spec.Npcs.Select(n => (Tag: n.Tag, n.Line, What: $"npcs.{n.Name}"))
            .Concat(spec.Npcs.SelectMany(n => n.Lights.Select(l => (Tag: (string?)l.Tag, l.Line, What: $"npcs.{n.Name}.lights (an NPC light)"))))
            .Concat(spec.Usables.Select(u => (Tag: u.Tag, u.Line, What: $"usables.{u.Name}")))
            .Concat(spec.Props.Select(p => (Tag: p.Tag, p.Line, What: $"props.{p.Name}")))
            .Concat(spec.Lights.Select(l => (Tag: l.Tag, l.Line, What: $"lights.{l.Name}")));
        var twice = tags.Where(t => t.Tag is not null).GroupBy(t => t.Tag!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
        {
            var (first, second) = (twice.MinBy(t => t.Line), twice.OrderBy(t => t.Line).Skip(1).First());
            var npcLight = first.What.EndsWith("(an NPC light)") || second.What.EndsWith("(an NPC light)");
            throw new ArgumentException($"line {second.Line}: {second.What} has tag '{twice.Key}', as {first.What} at line {first.Line}: tags are unique across npcs (their lights "
                                        + "too), usables, props and lights." + (npcLight ? " NPC lights are edited in their npcs entry: give the other entry its own tag." : ""));
        }
        // A vanilla actor ships in a fork, which the map streams instead of the file: only a file it streams can be forked.
        foreach (var entry in spec.Vanilla.Where(v => spec.Streaming?.Levels.Contains(v.File, StringComparer.OrdinalIgnoreCase) != true))
        {
            throw new ArgumentException($"line {entry.Line}: {entry.File} is not in streaming.levels: a vanilla actor ships in a fork of a file the map streams.");
        }
        var again = spec.Vanilla.GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (again is not null) throw new ArgumentException($"line {again.Last().Line}: 'vanilla' has {again.Key} twice.");
        return spec;
    }

    /// <summary>BIOA_FRE31_00_LAY.StaticMeshActor_69: {location: [x, y, z], rotation: [pitch, yaw, roll] or yaw, scale, scale3d, removed}, as props take them.</summary>
    private static ForkBuilder.Entry VanillaOf(YamlNode key, YamlNode body)
    {
        var name = Scalar(key);
        var dot = name.IndexOf('.');
        if (dot <= 0 || dot == name.Length - 1 || name.IndexOf('.', dot + 1) >= 0)
        {
            throw Fail(key, $"'{name}': a vanilla key is <file>.<actor name>, e.g. BIOA_FRE31_00_LAY.StaticMeshActor_69.");
        }
        if (body is not YamlMappingNode m) throw Fail(body, $"vanilla '{name}' must be a mapping, e.g. {{ location: [x, y, z] }} or {{ removed: true }}.");
        Vector3? location = null, scale3d = null;
        float? yaw = null, scale = null;
        float[]? rotation = null;
        var removed = false;
        YamlNode? turned = null;
        foreach (var (k, value) in Entries(m, "location", "rotation", "yaw", "scale", "scale3d", "removed"))
        {
            switch (k)
            {
                case "location":
                    var xyz = Numbers(value, 3, 3);
                    location = new Vector3(xyz[0], xyz[1], xyz[2]);
                    break;
                case "rotation":
                    rotation = Numbers(value, 3, 3);
                    turned = value;
                    break;
                case "yaw": yaw = Number(value); break;
                case "scale": scale = Number(value); break;
                case "scale3d":
                    var s = Numbers(value, 3, 3);
                    scale3d = new Vector3(s[0], s[1], s[2]);
                    break;
                case "removed": removed = Bool(value); break;
            }
        }
        if (yaw is not null && turned is not null)
        {
            throw Fail(turned, $"vanilla '{name}' has 'yaw' and 'rotation': give one ('rotation' is [pitch, yaw, roll] in degrees, 'yaw' keeps the file's pitch and roll).");
        }
        return new ForkBuilder.Entry(name[..dot], name[(dot + 1)..], (int)key.Start.Line, location, yaw, rotation, scale, scale3d, removed);
    }

    private static NpcSpec NpcOf(string name, YamlNode body, Dictionary<int, Dictionary<string, string>> strings)
    {
        if (body is not YamlMappingNode m) throw Fail(body, $"NPC '{name}' must be a mapping.");
        var npc = new NpcSpec { Name = name, Line = (int)body.Start.Line };
        foreach (var (key, value) in Entries(m, "tag", "template", "location", "yaw", "active", "name", "skin", "hair", "eyes", "conversation", "idle", "lights"))
        {
            switch (key)
            {
                case "tag": npc.Tag = Scalar(value); break;
                case "template": npc.Template = SourceOf(value); break;
                case "location":
                    var xyz = Numbers(value, 3, 3);
                    npc.Location = new Vector3(xyz[0], xyz[1], xyz[2]);
                    break;
                case "yaw": npc.Yaw = Number(value); break;
                case "active": npc.Active = Bool(value); break;
                case "name": npc.ActorName = NameOf(value, strings); break;
                case "skin": npc.Skin = Numbers(value, 3, 4); break;
                case "hair": npc.Hair = Numbers(value, 3, 4); break;
                case "eyes": npc.Eyes = Numbers(value, 3, 4); break;
                case "conversation": npc.Conversation = Scalar(value); break;
                case "idle": npc.Idle = IdleOf(value); break;
                case "lights": npc.Lights = Sequence(value).Select(LightOf).ToList(); break;
            }
        }
        return npc;
    }

    private static UsableSpec UsableOf(string name, YamlNode body, Dictionary<int, Dictionary<string, string>> strings)
    {
        if (body is not YamlMappingNode m) throw Fail(body, $"usable '{name}' must be a mapping.");
        var usable = new UsableSpec { Name = name, Line = (int)body.Start.Line };
        foreach (var (key, value) in Entries(m, "tag", "template", "location", "yaw", "name", "goTo"))
        {
            switch (key)
            {
                case "tag": usable.Tag = Scalar(value); break;
                case "template": usable.Template = SourceOf(value); break;
                case "location":
                    var xyz = Numbers(value, 3, 3);
                    usable.Location = new Vector3(xyz[0], xyz[1], xyz[2]);
                    break;
                case "yaw": usable.Yaw = Number(value); break;
                case "name": usable.NameStrRef = LabelOf(value, strings); break;
                case "goTo": usable.GoTo = GoToOf(value); break;
            }
        }
        return usable;
    }

    private static PropSpec PropOf(string name, YamlNode body)
    {
        if (body is not YamlMappingNode m) throw Fail(body, $"prop '{name}' must be a mapping.");
        var prop = new PropSpec { Name = name, Line = (int)body.Start.Line };
        YamlNode? rotation = null;
        foreach (var (key, value) in Entries(m, "tag", "template", "mesh", "removed", "location", "yaw", "rotation", "scale", "scale3d"))
        {
            switch (key)
            {
                case "tag": prop.Tag = Scalar(value); break;
                case "template": prop.Template = SourceOf(value); break;
                case "mesh": prop.Mesh = MeshOf(value); break;
                case "removed": prop.Removed = Bool(value); break;
                case "location":
                    var xyz = Numbers(value, 3, 3);
                    prop.Location = new Vector3(xyz[0], xyz[1], xyz[2]);
                    break;
                case "yaw": prop.Yaw = Number(value); break;
                case "rotation":
                    prop.Rotation = Numbers(value, 3, 3);
                    rotation = value;
                    break;
                case "scale": prop.Scale = Number(value); break;
                case "scale3d":
                    var scale = Numbers(value, 3, 3);
                    prop.Scale3D = new Vector3(scale[0], scale[1], scale[2]);
                    break;
            }
        }
        if (prop.Yaw is not null && rotation is not null)
        {
            throw Fail(rotation, $"prop '{name}' has 'yaw' and 'rotation': give one ('rotation' is [pitch, yaw, roll] in degrees, 'yaw' keeps the actor's pitch and roll).");
        }
        if (prop.Template is not null && prop.Mesh is not null)
        {
            throw Fail(body, $"prop '{name}' has a 'template' and a 'mesh': give one (a template's actor is copied; a mesh is shown on a copy of 'propTemplate').");
        }
        return prop;
    }

    /// <summary>{package: BIOA_FRE31_00_LAY.pcc, export: biog_apl_phy_crate_xlarge_01_l.Crate_XLarge_03}: a StaticMesh of that file.</summary>
    private static MeshSource MeshOf(YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "a mesh is {package: <file>, export: <its StaticMesh>}.");
        string? package = null, export = null;
        foreach (var (key, v) in Entries(m, "package", "export"))
        {
            if (key == "package") package = Scalar(v);
            else export = Scalar(v);
        }
        if (package is null || export is null) throw Fail(value, "the mesh needs 'package' and 'export' (pkg_search className=StaticMesh lists a file's meshes).");
        return new MeshSource(package, export, (int)value.Start.Line);
    }

    /// <summary>{strref: 74210060, fr: "...", en: "..."}: the text shown when Shepard aims at it.</summary>
    private static int LabelOf(YamlNode value, Dictionary<int, Dictionary<string, string>> strings)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "a name is {strref: <id>, fr: \"...\"}.");
        int? strRef = null;
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in m.Children)
        {
            var key = Scalar(k);
            if (key == "strref") strRef = Int(v);
            else text[Language(key)] = Scalar(v);
        }
        if (strRef is null) throw Fail(value, "the name needs 'strref'.");
        if (text.Count > 0) Collect(strings, strRef.Value, text, (int)m.Start.Line);
        return strRef.Value;
    }

    /// <summary>{map: BIOA_CLD00, startPoint: start_cld00}.</summary>
    private static (string Map, string? StartPoint) GoToOf(YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "'goTo' is {map: <level file name>, startPoint: <start point tag>}.");
        string? map = null, startPoint = null;
        foreach (var (key, v) in Entries(m, "map", "startPoint"))
        {
            if (key == "map") map = Path.GetFileNameWithoutExtension(Scalar(v));
            else startPoint = Scalar(v);
        }
        return (map ?? throw Fail(value, "'goTo' needs 'map'."), startPoint);
    }

    /// <summary>{package: BIONPC_Chakwas.pcc, actor: BioPawn_3}, or an actor of this level to clone.</summary>
    private static Source SourceOf(YamlNode value)
    {
        if (value is YamlScalarNode) return new Source(null, Scalar(value), (int)value.Start.Line);
        if (value is not YamlMappingNode m) throw Fail(value, "a template is {package: <file>, actor: <name>} or an actor of this level.");
        string? package = null, actor = null;
        foreach (var (key, v) in Entries(m, "package", "actor"))
        {
            if (key == "package") package = Scalar(v);
            else actor = Scalar(v);
        }
        return new Source(package, actor ?? throw Fail(value, "the template needs 'actor'."), (int)value.Start.Line);
    }

    /// <summary>{type: ClaudeNurseReyes, strref: 74210001, fr: "...", en: "..."}.</summary>
    private static NameSpec NameOf(YamlNode value, Dictionary<int, Dictionary<string, string>> strings)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "a name is {type: <actor type>, strref: <id>, fr: \"...\"}.");
        string? type = null;
        int? strRef = null;
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in m.Children)
        {
            var key = Scalar(k);
            if (key == "type") type = Scalar(v);
            else if (key == "strref") strRef = Int(v);
            else text[Language(key)] = Scalar(v);
        }
        if (type is null || strRef is null) throw Fail(value, "the name needs 'type' (its own actor type) and 'strref'.");
        if (text.Count > 0) Collect(strings, strRef.Value, text, (int)m.Start.Line);
        return new NameSpec(type, strRef.Value, (int)m.Start.Line);
    }

    /// <summary>{package: BIOA_NOR10_03_DSG.pcc, pose: HMM_DP_ArmsCrossed, weights: {Talk: 0, ...}}.</summary>
    private static IdleStations.Spec IdleOf(YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "an idle is {package: <file with the pose>, pose: <animation set>, weights: {<fidget>: <weight>}}.");
        string? package = null, pose = null;
        var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, v) in Entries(m, "package", "pose", "weights"))
        {
            switch (key)
            {
                case "package": package = Scalar(v); break;
                case "pose": pose = Scalar(v); break;
                case "weights":
                    if (v is not YamlMappingNode w) throw Fail(v, "weights map fidget names to numbers: {Talk: 0, Shrug: 1}.");
                    foreach (var (name, weight) in w.Children) weights[Scalar(name)] = Number(weight);
                    break;
            }
        }
        return new IdleStations.Spec(package ?? throw Fail(value, "the idle needs 'package' (a file whose Kismet has the pose)."),
            pose ?? throw Fail(value, "the idle needs 'pose' (an animation set, e.g. HMM_DP_ArmsCrossed)."), weights, (int)m.Start.Line);
    }

    /// <summary>{volume: {package: BIOA_NOR00.pcc, actor: BioTriggerStream_27}, levels: [BIOA_NOR10_09_ART, BIOA_NOR10_09_LAY]}.</summary>
    private static LevelStreams.Spec StreamingOf(YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "'streaming' is {volume: {package: <file>, actor: <BioTriggerStream>}, levels: [<file>, ...]}.");
        Source? volume = null;
        List<string>? levels = null;
        foreach (var (key, v) in Entries(m, "volume", "levels"))
        {
            if (key == "volume") volume = SourceOf(v);
            else levels = Sequence(v).Select(l => Path.GetFileNameWithoutExtension(Scalar(l))).ToList();
        }
        if (volume?.Package is null) throw Fail(value, "'streaming' needs 'volume': {package: <vanilla level>, actor: <its BioTriggerStream around the room>}.");
        if (levels is not { Count: > 0 }) throw Fail(value, "'streaming' needs 'levels', the files shown inside the volume.");
        return new LevelStreams.Spec(volume.Package, volume.Actor, levels, (int)m.Start.Line);
    }

    private static LevelStreams.StartPoint StartPointOf(string tag, YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, $"start point '{tag}' is {{location: [x, y, z], yaw: <degrees>}}.");
        Vector3? location = null;
        float? yaw = null;
        foreach (var (key, v) in Entries(m, "location", "yaw"))
        {
            if (key == "location")
            {
                var xyz = Numbers(v, 3, 3);
                location = new Vector3(xyz[0], xyz[1], xyz[2]);
            }
            else yaw = Number(v);
        }
        return new LevelStreams.StartPoint(tag, location ?? throw Fail(value, $"start point '{tag}' needs a 'location'."), yaw, (int)m.Start.Line);
    }

    private static LightSpec LightOf(YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "a light is {tag, offset: [forward, right, up], radius, brightness}.");
        string? tag = null;
        var offset = Vector3.Zero;
        float? radius = null, brightness = null;
        foreach (var (key, v) in Entries(m, "tag", "offset", "radius", "brightness"))
        {
            switch (key)
            {
                case "tag": tag = Scalar(v); break;
                case "offset":
                    var f = Numbers(v, 3, 3);
                    offset = new Vector3(f[0], f[1], f[2]);
                    break;
                case "radius": radius = Number(v); break;
                case "brightness": brightness = Number(v); break;
            }
        }
        return new LightSpec(tag ?? throw Fail(value, "the light needs a 'tag'."), offset, radius, brightness, (int)m.Start.Line);
    }

    /// <summary>
    /// {tag, kind: props | room, location: [x, y, z], radius, brightness, color: [r, g, b], falloff, enabled, template}: kind props, colour
    /// white, falloff 2 and enabled true when left out; the template is the file's lightTemplate, else LE1CP's fill light.
    /// </summary>
    private static LightEntrySpec LightEntryOf(string name, YamlNode body)
    {
        if (body is not YamlMappingNode m) throw Fail(body, $"light '{name}' must be a mapping: {{tag, kind, location, radius, brightness}}.");
        if (m.Children.Keys.FirstOrDefault(k => k is YamlScalarNode { Value: "removed" }) is { } removed)
        {
            throw Fail(removed, $"light '{name}': a light is switched off (enabled: false), never removed.");
        }
        var light = new LightEntrySpec { Name = name, Line = (int)body.Start.Line };
        foreach (var (key, value) in Entries(m, "tag", "kind", "location", "radius", "brightness", "color", "falloff", "enabled", "template"))
        {
            switch (key)
            {
                case "tag": light.Tag = Scalar(value); break;
                case "kind":
                    light.Kind = Scalar(value) is "props" or "room" ? Scalar(value) : throw Fail(value, "'kind' is props (lights props only: the Gameplay_4 channel) or room (walls, vanilla meshes, pawns and props).");
                    break;
                case "location":
                    var xyz = Numbers(value, 3, 3);
                    light.Location = new Vector3(xyz[0], xyz[1], xyz[2]);
                    break;
                case "radius": light.Radius = Number(value) is > 0 and var r ? r : throw Fail(value, "'radius' must be over 0."); break;
                case "brightness": light.Brightness = Number(value) is >= 0 and var b ? b : throw Fail(value, "'brightness' can't be negative."); break;
                case "color":
                    var rgb = Numbers(value, 3, 3);
                    light.Color = rgb.All(c => c is >= 0 and <= 255 && c == MathF.Round(c)) ? rgb.Select(c => (byte)c).ToArray() : throw Fail(value, "'color' is [r, g, b], whole numbers from 0 to 255.");
                    break;
                case "falloff": light.Falloff = Number(value) is > 0 and var f ? f : throw Fail(value, "'falloff' must be over 0 (2 by default)."); break;
                case "enabled": light.Enabled = Bool(value); break;
                case "template": light.Template = SourceOf(value); break;
            }
        }
        if (light.Tag is null) throw Fail(body, $"light '{name}' needs a 'tag' (how the build and the editor find it again).");
        if (light.Location is null || light.Radius is null || light.Brightness is null) throw Fail(body, $"light '{name}' needs 'location', 'radius' and 'brightness'.");
        return light;
    }
}
