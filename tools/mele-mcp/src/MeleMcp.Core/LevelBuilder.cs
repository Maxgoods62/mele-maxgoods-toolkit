using System.Globalization;
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
/// <param name="LevelsFile">levels.json, whose key for this level the build wrote; null when it built another package than the file's, or into mod_build out=.</param>
public sealed record LevelBuildResult(string Path, bool CreatedLevel, IReadOnlyList<string> LevelChanges, IReadOnlyList<BuiltNpc> Npcs, IReadOnlyList<BuiltUsable> Usables, IReadOnlyList<BuiltProp> Props,
    IReadOnlyList<BuiltLight> Lights, int StringCount, IReadOnlyList<string> TlkFiles, string? SideloaderConfig, IReadOnlyList<string> MissingConversations, IReadOnlyList<string> Notes,
    IReadOnlyList<string> BackupPaths, IReadOnlyList<BuiltFork> Forks, string? LevelsFile);

/// <summary>
/// Builds a Game 1 level from a YAML file. The level itself: vanilla sub-levels it shows and their stream volume, its
/// post-process, character lights, start points (see <see cref="LevelStreams"/>), the squad spawned on load (<see cref="SquadSpawns"/>). Its NPCs: the pawn (found by tag, else imported from a
/// template), position, name (own actor type), skin, hair and eye colors, fill lights that follow her (their colour, falloff, on or off), and the Kismet that
/// starts her conversation when Shepard uses her. Its usables: placeables Shepard uses to go to another map. Its props: any
/// actor copied from a template and placed (live_place, live_save), or a mesh shown on a copy of the prop template (editor_import),
/// turned by a yaw or a full rotation and scaled; a removed one is hidden without collision; each lit by prop lights when a light of its
/// lights section covers it, else by its light environment. Its lights: point lights on the channels of their kind (props only, or the
/// room too), switched on or off. Vanilla actors of the files it streams:
/// moved, turned, scaled or hidden in a fork of the file, which the level streams instead (<see cref="ForkBuilder"/>); an effect (an emitter)
/// is moved, turned or hidden only; a light of a light collection gets its brightness, colour, radius, falloff, on/off and place
/// (docs/design/in-game-editor.md §12.4), in a fork for a file it streams, in the map for its characterLights copies. A missing level is created, and the
/// LE1CP Sideloader config that loads it is written. Right after the level, its key of levels.json, which tells MeleBridge which levels are ours, where
/// its NPCs stand and their lights, and its character lights. Every check runs on the forks and the level in memory before any file
/// is written (<see cref="BuildLevel"/>).
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
    /// <param name="Color">[r, g, b]; null: the template's (white). <paramref name="Falloff"/> likewise (2).</param>
    private sealed record LightSpec(string Tag, Vector3 Offset, float? Radius, float? Brightness, int Line, byte[]? Color = null, float? Falloff = null, bool Enabled = true);

    /// <summary>
    /// An NPC's pose as this build leaves it (location, yaw in Unreal units) and as the level had it before (<see cref="Was"/>,
    /// <see cref="WasYaw"/>): an NPC light's receipt names an offset change against the pose the light was built from.
    /// </summary>
    private sealed record NpcPose(Vector3 Origin, int Yaw, Vector3 Was, int WasYaw);

    /// <summary>An NPC's pose as built, for levels.json (docs/design/in-game-editor.md §12): MeleBridge measures her lights' offsets from it.</summary>
    private sealed record BuiltPose(NpcSpec Npc, string Tag, Vector3 Location, int Yaw);

    /// <summary>
    /// A light of the lights: section (docs/design/in-game-editor.md §11): a point light on the channels of its kind. props: Gameplay_4 only, so it lights
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
    /// What <see cref="ApplyLight"/> gives a light. <see cref="Kind"/> null: an NPC light, which leaves its shadows, channels and level
    /// list to its template (the medic's lights stay on the Dynamic channel), and its colour and falloff when its item leaves them out; a
    /// radius or brightness left out stays as the level has it. <see cref="Npc"/>: its NPC's pose, for the receipt.
    /// </summary>
    private sealed record LightSettings(string Tag, Vector3 Location, int Line, float? Radius, float? Brightness, bool Enabled = true, string? Kind = null,
        byte[]? Color = null, float? Falloff = null, (Vector3 Offset, NpcPose Pose)? Npc = null);

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

    /// <summary>
    /// One way out of a usable: the map it loads when plot conditional <see cref="Conditional"/> (a <c>BioAutoConditionals</c> function,
    /// with <see cref="Param"/>) is true, or always when it is null: the last step of a <c>goTo</c> list, or its only one.
    /// </summary>
    private sealed record GoToStep(int? Conditional, int Param, string Map, string? StartPoint, int Line);

    /// <summary>A placeable Shepard uses, e.g. a terminal; using it loads the map of the first step of <see cref="GoTo"/> that applies.</summary>
    private sealed class UsableSpec
    {
        public required string Name;
        public required int Line;
        public string? Tag;
        public Source? Template;
        public Vector3? Location;
        public float? Yaw;
        public int? NameStrRef;
        public List<GoToStep>? GoTo;
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

    /// <param name="outRoot">mod_build out=: the mod root to build into instead (see <see cref="YamlFiles.Target"/>).</param>
    /// <param name="writes">
    /// The files written so far, each with its backup: an out= copy of the DLC folder, the forks, the level, its .m3cd, levels.json,
    /// each GlobalTlk. They are saved one after the other with no rollback, so a failure after the first becomes a
    /// <see cref="PartialBuildException"/> naming them. mod_build may pass one for all its files; null: this build's own.
    /// </param>
    public static LevelBuildResult Build(MEGame game, string yamlPath, string? packageOverride, string? outRoot = null, BuildWrites? writes = null)
    {
        if (!game.IsGame1()) throw new ArgumentException("level_build handles Game 1 (LE1) levels.");
        LecHost.EnsureInitialized();
        var file = Path.GetFullPath(GamePaths.ToWindowsPath(yamlPath));
        if (!File.Exists(file)) throw new FileNotFoundException($"No file {file}.");
        var spec = Parse(File.ReadAllText(file));
        var dir = Path.GetDirectoryName(file)!;

        // A missing level is created from a full path or one under the mod root, never from a bare name: it could resolve into the game install.
        var notes = new List<string>();
        var written = writes ?? new BuildWrites();
        var before = written.Written.Count;
        var folders = OutFolders(outRoot);
        var (package, create) = Target(game, packageOverride ?? spec.Package ?? throw new ArgumentException("The file has no 'package'."), dir, outRoot, notes);
        // With out=, Target may have just copied the DLC folder: a write, which every later error names.
        if (CopiedNow(folders, package) is { } copy) written.Wrote(copy);
        // Only the YAML's own package is the level the game loads (not another package, nor an out= copy): levels.json describes it.
        var describe = packageOverride is null && outRoot is null;
        return written.Guard(() => BuildLevel(game, spec, dir, package, create, describe, outRoot, notes, written, before));
    }

    /// <summary>
    /// <see cref="Build"/> once the level's path is known. The order is the state. First every fork and the level itself are made in
    /// memory, and every check of the file runs: a refusal or an error writes nothing. Then the forks (the map never names a file not
    /// written yet), the level, what describes it (its .m3cd, levels.json), the forks it no longer streams go, and last the names.
    /// <paramref name="written"/> records each write; <paramref name="before"/> is how many it held when this build started.
    /// </summary>
    private static LevelBuildResult BuildLevel(MEGame game, Spec spec, string dir, string package, bool create, bool describe, string? outRoot, List<string> notes,
        BuildWrites written, int before)
    {
        var globals = spec.GlobalTlk.Select(g => InOut(Locate(game, g, Path.GetDirectoryName(package)!, dir), outRoot, "globalTlk")).ToList();
        if (spec.Strings.Count > 0 && globals.Count == 0) throw new ArgumentException("The file gives a name text but no 'globalTlk' to write it to (actor names are read from the GlobalTlk).");
        foreach (var target in globals.Prepend(package)) PackageWriter.EnsureWritable(game, target);
        // A mesh or the prop template from the mod's own files, as editor_import names it (the YAML's own 'package' string), is found
        // as 'package' is (with out=, in the copy).
        if (spec.PropTemplate is { Package: { } standIn }) spec.PropTemplate = spec.PropTemplate with { Package = Source(game, standIn, dir, outRoot) };
        foreach (var prop in spec.Props.Where(p => p.Mesh is not null)) prop.Mesh = prop.Mesh! with { Package = Source(game, prop.Mesh.Package, dir, outRoot) };

        // This build's backups, for its result; written may hold an earlier build's files too (mod_build).
        var backups = new List<string>();
        void Wrote(string path, string? backup)
        {
            if (backup is not null) backups.Add(backup);
            written.Wrote(path, backup);
        }

        // Forks before the map (docs/design/in-game-editor.md §11): the map never names a file not written yet. Every file's fork is made in memory first,
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
            // Before the level is created too. An out= copy this build made is named by the PartialBuildException instead.
            var nothing = written.Written.Count == before ? " (nothing written)" : "";
            throw new ArgumentException($"vanilla actors the build won't change{nothing}: {string.Join("; ", refusals)}.");
        }
        var forked = prepared.Select(p => p.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var streams = levels.Select(l => forked.Contains(l) ? ForkBuilder.ForkName(map, l) : l).ToList();
        var others = levels.Zip(streams).DistinctBy(p => p.Second, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Second, p => forked.Contains(p.First) ? p.First : ForkBuilder.ForkName(map, p.First), StringComparer.OrdinalIgnoreCase);

        // The level is built in memory, a missing one too, before any file is written: every check of the file against it (a tag,
        // a template, a light, a Kismet sequence...) throws while the disk is as it was. A fork written first would load with the
        // old map, which may stream it already.
        var missing = new List<string>();
        var levelChanges = new List<string>();
        var poses = new List<BuiltPose>();
        using var pcc = create ? LevelCreator.CreateInMemory(game, package) : PackageInspector.Open(game, package);
        PackageWriter.EnsureWritable(pcc.Game, pcc.FilePath);
        (List<BuiltNpc>, List<BuiltUsable>, List<BuiltProp>, List<BuiltLight>, HashSet<string>, List<string>) Fill()
        {
            var level = pcc.FindExport(LevelPath) ?? throw new ArgumentException($"{Path.GetFileName(pcc.FilePath)} has no {LevelPath}; is it a level?");
            using var cache = new PackageCache();
            List<string> left = [];
            if (spec.Streaming is { } streaming)
            {
                left = LevelStreams.SetStreaming(game, pcc, level, streaming with { Levels = streams }, others, cache, levelChanges);
                // With 'squad' or not: a wait still naming the other name of a pair would wait for a file the level no longer streams.
                LevelStreams.RenameWaits(pcc, others, levelChanges);
            }
            // No 'streaming' (any more): the forks an earlier build streamed name their vanilla files again, and are deleted below.
            else left = LevelStreams.DropAllForks(game, pcc, level, levelChanges);
            if (spec.PostProcess is { } post) LevelStreams.SetPostProcess(game, pcc, level, post.Package, post.Line, levelChanges);
            // The first copy of a name is the one the build keeps (levels.json names it): its lights take the vanilla: entries keyed by its
            // source file, and the source's own values where they have none (docs/design/in-game-editor.md §12.4).
            var copies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var light in spec.CharacterLights)
            {
                var source = light.Package ?? throw new ArgumentException($"line {light.Line}: a character light needs a 'package'.");
                var copy = LevelStreams.SetLight(game, pcc, level, source, light.Actor, light.Line, levelChanges);
                if (!copies.Add(copy.ObjectName.Instanced)) continue;
                var entries = CopyEntries(spec, source, copy.ObjectName.Instanced);
                if (copy.ClassName == "StaticLightCollectionActor") SlcaLights.SetCopy(game, copy, source, light.Actor, entries, cache, levels, ForkBuilder.DlcOf(package), levelChanges);
                else if (entries.Count > 0) throw new ArgumentException($"line {entries[0].Line}: {entries[0].File}.{entries[0].Collection} is a {copy.ClassName} in the level, not a light collection.");
            }
            var template = spec.StartPointTemplate is { } t ? (t.Package ?? throw new ArgumentException($"line {t.Line}: 'startPointTemplate' needs a 'package'."), t.Actor) : ((string, string)?)null;
            foreach (var start in spec.StartPoints) LevelStreams.SetStartPoint(game, pcc, level, start, template, levelChanges);
            if (spec.Squad is { } squad)
            {
                var sequence = pcc.FindExport(MainSequence) ?? throw new ArgumentException($"line {squad.Line}: the level has no {MainSequence}.");
                SquadSpawns.Set(game, pcc, sequence, squad.Package, streams, squad.Line, cache, levelChanges);
            }
            // No 'squad' (any more): the chain an earlier build linked stops firing; its Kismet stays, unlinked.
            else if (pcc.FindExport(MainSequence) is { } main) SquadSpawns.Remove(main, levelChanges, notes);
            // Read once, and only when a found NPC light needs its template's values back.
            var templateLook = new Lazy<PropertyCollection?>(() => TemplateLook(game, pcc, spec.LightTemplate ?? DefaultLightTemplate));
            var builtNpcs = spec.Npcs.Select(npc => BuildNpc(game, pcc, level, spec, npc, cache, templateLook, poses, missing, notes)).ToList();
            var builtUsables = spec.Usables.Select(u => BuildUsable(game, pcc, level, u, cache, notes)).ToList();
            var builtLights = spec.Lights.Select(l => BuildLight(game, pcc, level, spec, l, cache)).ToList();
            NoteOrphanLights(pcc, level, spec, notes);
            // Each prop is lit by prop lights or by its light environment, from the lights: entries (docs/design/in-game-editor.md §11).
            var looks = new Dictionary<(string?, string), (bool Static, StructProperty? Channels)>();
            var builtProps = spec.Props.Select(p => BuildProp(game, pcc, level, spec, p, cache, looks, notes)).ToList();
            // What StreamingLevels lists, not every entry: one off the list (a pair's other name) streams nothing.
            return (builtNpcs, builtUsables, builtProps, builtLights, LevelStreams.Streamed(pcc, level), left);
        }
        var (npcs, usables, props, lights, named, dropped) = Fill();

        // No backup: a fork is made again from the vanilla file. One that was on disk before (an earlier build's) is named as rebuilt,
        // not created. Each fork is recorded as soon as it is written, so a later one that fails still names it.
        var had = prepared.SelectMany(ForkBuilder.Targets).Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forks = new List<BuiltFork>();
        foreach (var fork in prepared.Select(p => ForkBuilder.Write(p, notes)))
        {
            forks.Add(fork);
            if (fork.Action == "written") written.Wrote(fork.ForkPath, null, had.Contains(fork.ForkPath));
            foreach (var copy in fork.Localized.Where(l => l.Action == "written")) written.Wrote(copy.Path, null, had.Contains(copy.Path));
        }
        prepared.Clear();

        if (create) Directory.CreateDirectory(Path.GetDirectoryName(pcc.FilePath)!);
        // With its path: a level created in memory has no file of its own to save to.
        var backup = PackageWriter.Save(pcc, backup: !create, path: pcc.FilePath);
        Wrote(pcc.FilePath, backup);
        if (create) notes.Add($"Created the level {Path.GetFileName(package)}.");

        // What describes the level right after it, before anything that may still fail: a GlobalTlk refused below leaves the
        // Sideloader config and levels.json describing the level on disk (MeleBridge measures NPC light offsets from its poses).
        var config = spec.Sideloader is { } prefixes ? EnsureSideloader(package, prefixes, Wrote, notes) : null;
        // No 'sideloader' (any more): the .m3cd files are left as they are, since a mod may keep its line by hand. The build only
        // says when the file it would have written for this level still lists it.
        if (spec.Sideloader is null) NoteSideloaderLeft(package, notes);
        var levelsFile = describe ? WriteLevelsFile(package, spec, levels, poses, Wrote) : null;

        // A file with no vanilla entry left, or that left streaming.levels, streams under its own name again: its fork goes now that
        // the saved map no longer names it.
        foreach (var vanilla in levels.Where(l => !forked.Contains(l)).Concat(dropped).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (named.Contains(ForkBuilder.ForkName(map, vanilla)))
            {
                notes.Add($"{ForkBuilder.ForkName(map, vanilla)} is kept: the level's StreamingLevels still lists it.");
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
                Wrote(edit.Path, edit.BackupPath);
                tlkFiles.Add($"{Path.GetFileName(globalPath)} ({language})");
            }
        }

        return new LevelBuildResult(package, create, levelChanges, npcs, usables, props, lights, spec.Strings.Count, tlkFiles, config, missing, notes, backups, forks, levelsFile);
    }

    /// <summary>The folders under mod_build out= before the build; null without out=.</summary>
    private static HashSet<string>? OutFolders(string? outRoot)
    {
        if (outRoot is null) return null;
        var into = Path.GetFullPath(GamePaths.ToWindowsPath(outRoot));
        return Directory.Exists(into) ? Directory.GetDirectories(into).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The package's DLC folder when <see cref="YamlFiles.Target"/> just copied it into out= (it is there now and was not in <paramref name="had"/>); else null.</summary>
    private static string? CopiedNow(HashSet<string>? had, string package)
    {
        if (had is null) return null;
        for (var folder = Path.GetDirectoryName(package); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (Path.GetFileName(folder).StartsWith("DLC_", StringComparison.OrdinalIgnoreCase)) return !had.Contains(folder) && Directory.Exists(folder) ? folder : null;
        }
        return null;
    }

    /// <summary>The vanilla: light entries of a characterLights copy: keyed by its source file and the copy's name, of a file the map does not stream.</summary>
    private static List<ForkBuilder.Entry> CopyEntries(Spec spec, string package, string copy) =>
        spec.Vanilla.Where(v => v.Light is not null && !Streams(spec, v.File) && v.File.Equals(FileOf(package), StringComparison.OrdinalIgnoreCase)
                                && v.Collection.Equals(copy, StringComparison.OrdinalIgnoreCase)).ToList();

    private static bool Streams(Spec spec, string file) => spec.Streaming?.Levels.Contains(file, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>A package as a file name without its extension: BIOA_FRE31_00_DSG.pcc → BIOA_FRE31_00_DSG.</summary>
    private static string FileOf(string package) => Path.GetFileNameWithoutExtension(GamePaths.ToWindowsPath(package));

    /// <summary>levels.json: %LOCALAPPDATA%\MeleBridge\LE1\levels.json, or the file <see cref="LevelsFileEnvVar"/> names (tests).</summary>
    internal static string LevelsFilePath()
    {
        var custom = Environment.GetEnvironmentVariable(LevelsFileEnvVar);
        return string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeleBridge", "LE1", "levels.json")
            : Path.GetFullPath(GamePaths.ToWindowsPath(custom));
    }

    /// <summary>The DLC folders levels.json says level_build built into (its 'dlc', and each level's); none without a readable file.</summary>
    internal static List<string> BuiltDlcFolders()
    {
        try
        {
            var path = LevelsFilePath();
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject doc) return [];
            var levels = doc["levels"] as JsonObject ?? [];
            return levels.Select(l => (l.Value as JsonObject)?["dlc"]).Prepend(doc["dlc"])
                .Select(d => d is JsonValue v && v.TryGetValue<string>(out var name) ? name : null)
                .OfType<string>().Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes this level's key of levels.json (docs/design/in-game-editor.md §11, §12), which MeleBridge reads on each map change and when the file changes:
    /// the vanilla files the level streams (by their vanilla names), the tags of its props that are not removed (the props it may
    /// switch to prop lights) and of its lights: entries (the lights it lists); every NPC of the YAML by her tag, with her YAML name,
    /// her pose as built and her lights' offsets (none too: the editor lists her NPC lights and edits them as offsets); the
    /// characterLights copies, by the collection's name in the level, with the file each is copied from; 'dlc', the DLC folder it lies
    /// in (at the top for MeleBridge, the last level built; in the level's key too, which catalog_index reads). Other keys stay. Unreadable, the file is kept aside (.bad-&lt;ticks&gt;) and a new one started. Written beside it, then moved over it.
    /// Backed up first, as the level is: putting a build's backups back gives the level and its description from before it together.
    /// </summary>
    private static string WriteLevelsFile(string package, Spec spec, IReadOnlyList<string> streams, IReadOnlyList<BuiltPose> poses, Action<string, string?> wrote)
    {
        var path = LevelsFilePath();
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
        var dlc = ForkBuilder.DlcOf(package);
        if (dlc is not null) doc["dlc"] = dlc;
        if (doc["levels"] is not JsonObject levels) doc["levels"] = levels = new JsonObject();
        var key = Path.GetFileNameWithoutExtension(package);
        foreach (var old in levels.Select(l => l.Key).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList()) levels.Remove(old);
        levels[key] = new JsonObject
        {
            ["dlc"] = dlc,
            ["streams"] = new JsonArray(streams.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["props"] = new JsonArray(spec.Props.Where(p => !p.Removed).Select(p => p.Tag).OfType<string>().Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            // Switched off ones too: the editor lists them, and switches them on again. NPC lights are under their NPC.
            ["lights"] = new JsonArray(spec.Lights.Select(l => l.Tag).OfType<string>().Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["npcs"] = new JsonObject(poses.Select(p => KeyValuePair.Create(p.Tag, (JsonNode?)new JsonObject
            {
                ["name"] = p.Npc.Name,
                ["location"] = Floats(p.Location.X, p.Location.Y, p.Location.Z),
                ["yaw"] = p.Yaw,
                ["lights"] = new JsonObject(p.Npc.Lights.Select(l => KeyValuePair.Create(l.Tag, (JsonNode?)Floats(l.Offset.X, l.Offset.Y, l.Offset.Z)))),
            }))),
            // The first copy of a name is the one the build keeps (LevelStreams.SetLight finds it again by its name).
            ["characterLights"] = new JsonObject(spec.CharacterLights.Where(c => c.Package is not null).DistinctBy(c => LevelStreams.CopyName(c.Actor), StringComparer.OrdinalIgnoreCase)
                .Select(c => KeyValuePair.Create(LevelStreams.CopyName(c.Actor), (JsonNode?)JsonValue.Create(FileOf(c.Package!))))),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var backup = File.Exists(path) ? PackageWriter.Backup(path) : null;
        AtomicFile.WriteAllText(path, json, new UTF8Encoding(false));
        wrote(path, backup);
        return path;
    }

    /// <summary>Floats as JSON numbers that read back as the same floats, written short: -36739.9, not -36739.8984375.</summary>
    private static JsonArray Floats(params float[] values) =>
        new(values.Select(f => (JsonNode?)JsonValue.Create(NumberText.Double(f.ToString("R", CultureInfo.InvariantCulture)))).ToArray());

    private static BuiltNpc BuildNpc(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, NpcSpec npc, PackageCache cache,
        Lazy<PropertyCollection?> templateLook, List<BuiltPose> poses, List<string> missing, List<string> notes)
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
        var was = props.GetProp<StructProperty>("Location") is { } before ? CommonStructs.GetVector3(before) : Vector3.Zero;
        var dirty = Changed(changes, "tag", Put(props, new NameProperty(tag, "Tag")) | Put(props, new NameProperty(tag, "UniqueTag")));
        if (npc.Location is { } location) dirty |= Changed(changes, "location", Put(props, CommonStructs.Vector3Prop(location, "Location")));
        var rotation = props.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : new Rotator(0, 0, 0);
        var wasYaw = rotation.Yaw;
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
        var pose = new NpcPose(origin, rotation.Yaw, was, wasYaw);
        poses.Add(new BuiltPose(npc, tag, origin, rotation.Yaw));
        foreach (var light in npc.Lights) SetLight(game, pcc, level, spec, light, pose, cache, templateLook, changes);
        var talk = npc.Conversation is { } conversation ? LinkConversation(pcc, pawn, conversation, npc.Line, cache, changes) : null;
        if (npc.Conversation is not null && talk is null)
        {
            // The conversation's own file creates it in this level; the level is built first so that it exists.
            missing.Add(npc.Conversation);
            notes.Add($"{npc.Name}: the level has no BioConversation '{npc.Conversation}' yet. Build it (conv_build; 'from' copies a template), then this file again to start it when Shepard uses her.");
        }

        // What the file no longer asks for stops firing (KismetPrefabs.Link only adds): the edges an earlier build linked for her
        // conversation or her idle are cut, and their objects stay in Main_Sequence, unlinked.
        var sequence = pcc.FindExport(MainSequence);
        if (npc.Conversation is null && sequence is not null && UnlinkConversations(sequence, pawn, null, changes) is { Count: > 0 } gone)
        {
            notes.Add($"{npc.Name}: the file has no conversation, so using her no longer starts {string.Join(", ", gone)}. Its Kismet stays in Main_Sequence, unlinked.");
        }
        if (npc.Idle is { } idle)
        {
            IdleStations.Set(game, pcc, sequence ?? throw new ArgumentException($"line {npc.Line}: the level has no {MainSequence}."), pawn, idle,
                talk?.Use, talk?.Start, cache, changes);
        }
        else if (sequence is not null) IdleStations.Remove(sequence, pawn, npc.Name, changes, notes);

        return new BuiltNpc(npc.Name, ExportInspector.Row(pawn), action, changes);
    }

    private static BuiltUsable BuildUsable(MEGame game, IMEPackage pcc, ExportEntry level, UsableSpec usable, PackageCache cache, List<string> notes)
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

        if (usable.GoTo is { } steps) LinkGoTo(pcc, actor, steps, usable.Line, cache, changes);
        // No 'goTo' (any more): the chain an earlier build linked stops firing; its Kismet stays, unlinked.
        else if (pcc.FindExport(MainSequence) is { } sequence && UnlinkGoTo(sequence, actor, changes))
        {
            notes.Add($"{usable.Name}: the file has no 'goTo', so using it no longer loads a map. Its Kismet stays in Main_Sequence, unlinked.");
        }
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

    // ---- Prop lighting (docs/design/in-game-editor.md §11) ----

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
    /// The switch of docs/design/in-game-editor.md §11: a prop that a lights: entry covers (enabled, on Gameplay_4 as both kinds are, falloff at its bounds
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
    private const string AreaTransition = "BioSeqAct_AreaTransition";
    private const string CheckConditional = "BioSeqAct_PMCheckConditional";

    /// <summary>
    /// The usable's Kismet in <c>Main_Sequence</c>: <c>BioSeqEvt_ArtPlaceableUsed</c> (Originator = the usable) → an area transition, or a
    /// chain of plot checks (<c>BioSeqAct_PMCheckConditional</c>), each with its transition on <c>True</c> and the next check on
    /// <c>False</c>, ending in the unconditional step's transition: vanilla's own pattern (the galaxy map's <c>To_LOS00</c>). A chain of
    /// the same shape gets its values written where they differ; any other (one more step, one less, a step that gained or lost its
    /// condition) is trashed and made again, so no old transition stays linked to fire beside the new ones.
    /// </summary>
    private static void LinkGoTo(IMEPackage pcc, ExportEntry usable, List<GoToStep> steps, int line, PackageCache cache, List<string> changes)
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

        var built = BuiltGoTo(used);
        var sameShape = built.Count == steps.Count
                        && built.Zip(steps).All(b => b.First.Transition is not null && (b.First.Check is null) == (b.Second.Conditional is null));
        if (!sameShape)
        {
            foreach (var (check, transition) in built)
            {
                if (check is not null) KismetHelper.RemoveFromSequence(check, trash: true);
                if (transition is not null) KismetHelper.RemoveFromSequence(transition, trash: true);
            }
            built = [];
            ExportEntry from = used;
            var output = "Out";
            foreach (var step in steps)
            {
                var transition = SequenceObjectCreator.CreateSequenceObject(sequence, AreaTransition, cache);
                ExportEntry? check = null;
                if (step.Conditional is not null)
                {
                    check = SequenceObjectCreator.CreateSequenceObject(sequence, CheckConditional, cache);
                    KismetHelper.CreateOutputLink(from, output, check, 0);
                    KismetHelper.CreateOutputLink(check, "True", transition, 0);
                    from = check;
                    output = "False";
                }
                else KismetHelper.CreateOutputLink(from, output, transition, 0);
                built.Add((check, transition));
            }
            changes.Add(steps.Count == 1 ? "Kismet: use → area transition" : $"Kismet: use → {steps.Count - 1} plot checks → {steps.Count} area transitions");
        }

        foreach (var ((check, transition), step) in built.Zip(steps))
        {
            var dirty = false;
            if (check is not null)
            {
                var checkProps = check.GetProperties();
                var checkDirty = Put(checkProps, new IntProperty(step.Conditional!.Value, "m_nIndex"));
                checkDirty |= step.Param == 0 ? checkProps.RemoveNamedProperty("Param") : Put(checkProps, new IntProperty(step.Param, "Param"));
                // What kismet_read and LEX's sequence editor show beside it, as vanilla's "Player was just at Noveria?".
                var comment = new ArrayProperty<StrProperty>([new StrProperty($"{step.Map}{(step.StartPoint is null ? "" : $" {step.StartPoint}")}?")], "m_aObjComment");
                checkDirty |= Put(checkProps, comment);
                if (checkDirty) check.WriteProperties(checkProps);
                dirty |= checkDirty;
            }
            var props = transition!.GetProperties();
            var transitionDirty = Put(props, new NameProperty(step.Map, "AreaName"));
            transitionDirty |= step.StartPoint is null ? props.RemoveNamedProperty("StartPoint") : Put(props, new NameProperty(step.StartPoint, "StartPoint"));
            if (transitionDirty) transition.WriteProperties(props);
            dirty |= transitionDirty;
            if (dirty) changes.Add(GoToText(step, steps.Count));
        }
    }

    /// <summary>The usable's chain as built: from its event's <c>Out</c>, each plot check with the transition on its <c>True</c>, along <c>False</c>, then the last transition.</summary>
    private static List<(ExportEntry? Check, ExportEntry? Transition)> BuiltGoTo(ExportEntry used)
    {
        var chain = new List<(ExportEntry? Check, ExportEntry? Transition)>();
        var seen = new HashSet<int>();
        var node = NextOfGoTo(used, "Out");
        while (node is not null && seen.Add(node.UIndex))
        {
            if (node.ClassName == AreaTransition)
            {
                chain.Add((null, node));
                break;
            }
            // A transition already in the chain (linked twice by hand) is no step's own: the shape differs, and it is trashed once.
            var transition = KismetPrefabs.Next(node, "True", AreaTransition);
            chain.Add((node, transition is not null && seen.Add(transition.UIndex) ? transition : null));
            node = NextOfGoTo(node, "False");
        }
        return chain;
    }

    private static ExportEntry? NextOfGoTo(ExportEntry from, string output) =>
        KismetPrefabs.Next(from, output, CheckConditional) ?? KismetPrefabs.Next(from, output, AreaTransition);

    /// <summary>
    /// A usable whose YAML has no 'goTo' (any more): cuts use → plot check and use → area transition on its use event
    /// (BioSeqEvt_ArtPlaceableUsed, Originator = the usable), the event <see cref="LinkGoTo"/> builds on and finds again. The checks
    /// and transitions stay in Main_Sequence, unlinked, and any other Kismet on the event stays linked. An event nobody ever
    /// linked has no outputs written: nothing to cut. Returns whether it cut a link.
    /// </summary>
    private static bool UnlinkGoTo(ExportEntry sequence, ExportEntry usable, List<string> changes)
    {
        var used = KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>()
            .FirstOrDefault(o => o.ClassName == "BioSeqEvt_ArtPlaceableUsed" && o.GetProperty<ObjectProperty>("Originator")?.Value == usable.UIndex);
        if (used is null || !KismetHelper.GetOutputLinkNames(used).Contains("Out")) return false;
        if (KismetPrefabs.Unlink(used, "Out", o => o.ClassName is CheckConditional or AreaTransition).Count == 0) return false;
        changes.Add("Kismet: use → go to unlinked");
        return true;
    }

    private static string GoToText(GoToStep step, int count)
    {
        var where = $"goes to {step.Map}{(step.StartPoint is null ? "" : $" {step.StartPoint}")}";
        if (step.Conditional is not { } id) return count == 1 ? where : $"otherwise {where}";
        return $"if conditional {id}{(step.Param == 0 ? "" : $" param {step.Param}")}: {where}";
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

    /// <summary>A fill light kept at <see cref="LightSpec.Offset"/> from the NPC, lighting only dynamic objects (see docs/le1-lighting.md §A.8–A.9).</summary>
    private static void SetLight(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, LightSpec light, NpcPose pose, PackageCache cache,
        Lazy<PropertyCollection?> templateLook, List<string> changes)
    {
        var at = NpcLightAt([pose.Origin.X, pose.Origin.Y, pose.Origin.Z], pose.Yaw, [light.Offset.X, light.Offset.Y, light.Offset.Z]);
        var settings = new LightSettings(light.Tag, new Vector3((float)at[0], (float)at[1], (float)at[2]), light.Line, light.Radius, light.Brightness, light.Enabled,
            Color: light.Color, Falloff: light.Falloff, Npc: (light.Offset, pose));
        var (_, action, changed) = ApplyLight(game, pcc, level, settings, spec.LightTemplate, cache, templateLook);
        // Among the NPC's own changes: each light on one line.
        if (action != "found") changes.Add($"light {light.Tag} added");
        else if (changed.Count > 0) changes.Add($"light {light.Tag}: {string.Join(", ", changed)}");
    }

    /// <summary>
    /// Where an NPC light stands: her location (the pawn's Location, its collision centre) plus the offset [forward, right, up] turned
    /// by her yaw (Unreal units, 65536 = 360°). MeleBridge places the lights so, and editor_import puts a light back from its offset.
    /// </summary>
    internal static double[] NpcLightAt(double[] origin, double yaw, double[] offset)
    {
        var angle = yaw * Math.PI * 2 / 65536;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        return [origin[0] + offset[0] * cos - offset[1] * sin, origin[1] + offset[0] * sin + offset[1] * cos, origin[2] + offset[2]];
    }

    /// <summary>An NPC light's offset [forward, right, up] from where it stands: <see cref="NpcLightAt"/> the other way round.</summary>
    internal static double[] NpcLightOffset(double[] origin, double yaw, double[] location)
    {
        var angle = yaw * Math.PI * 2 / 65536;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var (dx, dy) = (location[0] - origin[0], location[1] - origin[1]);
        return [dx * cos + dy * sin, -dx * sin + dy * cos, location[2] - origin[2]];
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
    /// matches a pawn in a Sideloader level, docs/le1-lighting.md §A.9), the radius and brightness given, CachedParentToWorld's WPlane, new
    /// GUIDs for a new copy, imported or cloned (lightmaps, shadow maps and a primitive's IrrelevantLights know a light by its
    /// LightGuid, so no two lights share one). A lights: entry (<see cref="LightSettings.Kind"/>) also gets the actor's bEnabled,
    /// its colour, falloff, no shadows, dynamic light, its kind's channels in full and no OtherLevelsToAffect (the template's level
    /// list, which the filter left off ignores); an NPC light keeps its template's (the medic's: Dynamic only), and gets the colour
    /// (alpha kept) and falloff its item gives, else its template's again (<paramref name="templateLook"/>), and the actor's bEnabled
    /// when off (or when an earlier build switched it off). A found NPC light still on a kind's channels in full was a lights: entry
    /// (its tag moved to an NPC): it takes its template's channels, shadows, dynamic light and level list back (<see cref="TakeTemplateKind"/>).
    /// An NPC light's receipt names a move as an offset change, measured from the pose its NPC had before this build; a move of its
    /// NPC alone stays a location change.
    /// </summary>
    /// <param name="templateLook">An NPC light's template's component values (<see cref="TemplateLook"/>); null for a lights: entry.</param>
    /// <returns>The light, found, imported or cloned, and what the build changed on a found one, each value old → new.</returns>
    private static (ExportEntry Actor, string Action, List<string> Changed) ApplyLight(MEGame game, IMEPackage pcc, ExportEntry level, LightSettings light, Source? template,
        PackageCache cache, Lazy<PropertyCollection?>? templateLook = null)
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
        var moved = Changed(changed, MoveOf(light, was), Put(props, CommonStructs.Vector3Prop(light.Location, "Location")));
        var dirty = Changed(changed, $"tag {props.GetProp<NameProperty>("Tag")?.Value.Instanced} → {light.Tag}", Put(props, new NameProperty(light.Tag, "Tag"))) | moved;
        // On or off: one receipt line for the actor's flag and the component's. An NPC light's actor has none of its own until it is off
        // (the medic's never had one): it gets one then, and keeps it.
        var switched = (full || !light.Enabled || props.GetProp<BoolProperty>("bEnabled") is not null) && Put(props, new BoolProperty(light.Enabled, "bEnabled"));
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
            edited |= PutNumber(c, "FalloffExponent", light.Falloff ?? 2, "falloff", changed);
            edited |= PutFlag(c, "CastShadows", false, changed);
            edited |= PutFlag(c, "bForceDynamicLight", true, changed);
            edited |= Changed(changed, KindChange(c.GetProp<StructProperty>("LightingChannels"), light.Kind!), PutChannels(c, Channels(KindChannels[light.Kind!])));
            if (c.GetProp<ArrayPropertyBase>("OtherLevelsToAffect") is { } others)
            {
                var levels = others.Properties.Select(p => p switch { NameProperty n => n.Value.Instanced, StrProperty t => t.Value, _ => p.ToString() });
                edited |= Changed(changed, $"OtherLevelsToAffect [{string.Join(", ", levels)}] cleared", c.RemoveNamedProperty("OtherLevelsToAffect"));
            }
        }
        else
        {
            // Its tag was a lights: entry's: on Gameplay_4 alone it would light props and not her. A new copy has the template's already.
            if (!isNew && KindOf(c.GetProp<StructProperty>("LightingChannels")) is { } wasKind && templateLook is { Value: { } own })
            {
                edited |= Changed(changed, $"kind {wasKind} → NPC light (the template's channels, shadows, dynamic light and level list)", TakeTemplateKind(c, own));
            }
            if (light.Color is { } rgb)
            {
                // The light's own alpha kept: LE1CP's template has no colour of its own, so the class default's 0.
                var alpha = c.GetProp<StructProperty>("LightColor")?.GetProp<ByteProperty>("A")?.Value ?? 0;
                var colour = CommonStructs.ColorProp(System.Drawing.Color.FromArgb(alpha, rgb[0], rgb[1], rgb[2]), "LightColor");
                edited |= Changed(changed, $"colour {Colour(c.GetProp<StructProperty>("LightColor"))} → {Colour(colour)}", Put(c, colour));
            }
            if (light.Falloff is { } falloff) edited |= PutNumber(c, "FalloffExponent", falloff, "falloff", changed);
            // A key the item leaves out is the template's: deleting it gives the template's back. A new copy has the template's already.
            if (!isNew && (light.Color is null || light.Falloff is null) && templateLook is { Value: { } look })
            {
                if (light.Color is null)
                {
                    var ownColour = look.GetProp<StructProperty>("LightColor");
                    edited |= Changed(changed, $"colour {Colour(c.GetProp<StructProperty>("LightColor"))} → {Colour(ownColour)} (the template's)", TakeOwn(c, "LightColor", ownColour));
                }
                if (light.Falloff is null)
                {
                    static string Falloff(FloatProperty? f) => f is null ? "default" : ForkBuilder.Number(f.Value);
                    var ownFalloff = look.GetProp<FloatProperty>("FalloffExponent");
                    edited |= Changed(changed, $"falloff {Falloff(c.GetProp<FloatProperty>("FalloffExponent"))} → {Falloff(ownFalloff)} (the template's)",
                        TakeOwn(c, "FalloffExponent", ownFalloff));
                }
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
        if (KindOf(old) is { } was) return $"kind {was} → {kind}";
        var on = old?.Properties.OfType<BoolProperty>().Where(b => b.Value && b.Name.Instanced != "bInitialized").Select(b => b.Name.Instanced).ToList();
        return $"kind {kind} (channels were {(on is null ? "the template's" : on.Count == 0 ? "none" : string.Join(", ", on))})";
    }

    /// <summary>
    /// The kind (props, room) whose channels these are, in full as a lights: entry writes them (<see cref="Channels"/>); null for any
    /// other channels, a template's as an NPC light keeps them.
    /// </summary>
    internal static string? KindOf(StructProperty? channels) =>
        channels is null ? null : KindChannels.Keys.FirstOrDefault(k => SameExactly(channels, Channels(KindChannels[k])));

    /// <summary>
    /// What a lights: entry writes on a light's component and an NPC light leaves to its template, back to the template's own
    /// (<paramref name="template"/>, its flattened component): its channels, shadows and dynamic light, none where it has none, and its
    /// level list when the light has none (the entry cleared it), as <see cref="TakeTemplateLight"/> does for a new clone. True when
    /// that changed the component.
    /// </summary>
    internal static bool TakeTemplateKind(PropertyCollection component, PropertyCollection template)
    {
        var taken = new[] { "LightingChannels", "CastShadows", "bForceDynamicLight" }.Aggregate(false, (any, name) => TakeOwn(component, name, template.GetProp<Property>(name)) | any);
        if (component.GetProp<ArrayPropertyBase>("OtherLevelsToAffect") is null && template.GetProp<ArrayPropertyBase>("OtherLevelsToAffect") is { } levels)
        {
            component.AddOrReplaceProp(levels);
            taken = true;
        }
        return taken;
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
        static bool Take(PropertyCollection to, PropertyCollection from, string name) => TakeOwn(to, name, from.GetProp<Property>(name));

        var templateProps = template.GetProperties();
        var props = copy.GetProperties();
        if (Take(props, templateProps, "bEnabled")) copy.WriteProperties(props);
        if (Object(template.FileRef, templateProps, "LightComponent") is not { } original || Object(pcc, props, "LightComponent") is not { } component) return;
        var originalProps = original.GetProperties();
        var c = component.GetProperties();
        var dirty = TemplateLightFields.Aggregate(false, (any, name) => Take(c, originalProps, name) | any);
        // The level list only when the copy has none (a lights: entry's clears it): an NPC light's own list, as the medic's, stays.
        if (c.GetProp<ArrayPropertyBase>("OtherLevelsToAffect") is null && originalProps.GetProp<ArrayPropertyBase>("OtherLevelsToAffect") is { } levels)
        {
            c.AddOrReplaceProp(levels);
            dirty = true;
        }
        if (dirty) component.WriteProperties(c);
    }

    /// <summary>A value back to the template's own <paramref name="value"/>: none when it has none.</summary>
    private static bool TakeOwn(PropertyCollection to, string name, Property? value)
    {
        if (value is null) return to.RemoveNamedProperty(name);
        // A struct field for field: Put compares only the fields the template's lists, so the clone's Gameplay_4 could stay.
        if (value is not StructProperty whole || to.GetProp<StructProperty>(name) is not { } old || SameExactly(old, whole)) return Put(to, value);
        to.AddOrReplaceProp(whole);
        return true;
    }

    /// <summary>
    /// The component of the NPC lights' template (the file's lightTemplate, else LE1CP's): its own values (flattened, as an import of
    /// it has them), null without one. What an NPC light goes back to: its colour and falloff when its item leaves them out (LE1CP's
    /// has neither, the class defaults), its channels, shadows, dynamic light and level list when it was a lights: entry.
    /// </summary>
    private static PropertyCollection? TemplateLook(MEGame game, IMEPackage pcc, Source template)
    {
        using var source = template.Package is null ? null : PackageInspector.Open(game, template.Package);
        var original = ResolveActor(source ?? pcc, template.Actor, template.Line);
        // As AddActor imports it: what a prefab's archetype gives its component is the copy's own (only changes the template's file in
        // memory). The level's own template stays as is: its clones keep their archetype, and the level being built is never flattened.
        if (source is not null) Flatten(original);
        return Object(original.FileRef, original.GetProperties(), "LightComponent")?.GetProperties();
    }

    /// <summary>
    /// A light's move as its receipt names it: an NPC light's as its offset from its NPC when that changed (measured from the pose the
    /// NPC had before the build), else as its location (its NPC moved, the light with it).
    /// </summary>
    private static string MoveOf(LightSettings light, Vector3 was)
    {
        if (light.Npc is { } npc)
        {
            var before = npc.Pose.Was;
            var old = NpcLightOffset([before.X, before.Y, before.Z], npc.Pose.WasYaw, [was.X, was.Y, was.Z]);
            var offset = npc.Offset;
            if (Math.Abs(old[0] - offset.X) > 0.02 || Math.Abs(old[1] - offset.Y) > 0.02 || Math.Abs(old[2] - offset.Z) > 0.02)
            {
                // Measured, a 0 may come out a hair under (-0.0000001): it reads 0, not -0.
                static float Axis(double d) => (float)(Math.Round(d, 2) + 0.0);
                return $"offset {ForkBuilder.Location(new Vector3(Axis(old[0]), Axis(old[1]), Axis(old[2])))} → {ForkBuilder.Location(offset)}";
            }
        }
        return $"location {ForkBuilder.Moved(was, light.Location)}";
    }

    /// <summary>
    /// A note for each tagged light of the level whose tag no entry names (a lights: entry or an NPC's light deleted by hand): the
    /// build never deletes a light, so it stays as built. A tag that is its class's name is no tag of ours.
    /// </summary>
    private static void NoteOrphanLights(IMEPackage pcc, ExportEntry level, Spec spec, List<string> notes)
    {
        var named = spec.Lights.Select(l => l.Tag).Concat(spec.Npcs.SelectMany(n => n.Lights.Select(l => l.Tag))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var light in pcc.Exports.Where(e => e.Parent == level && e.IsA("Light")))
        {
            if (light.GetProperty<NameProperty>("Tag")?.Value.Instanced is not { } tag || tag == light.ClassName || named.Contains(tag)) continue;
            notes.Add($"light {tag} is built but no entry names it: it stays as built. Switch a light off with enabled: false instead of deleting its line.");
        }
    }

    /// <summary>
    /// Main_Sequence: Shepard uses the pawn (BioSeqEvt_OnPlayerActivate) → BioSeqAct_StartConversation, Owner = the pawn.
    /// Returns the use event and the start action; null when the level has no conversation of that name. Her use no longer starts
    /// any other conversation an earlier build linked (a renamed one), also while the new one is not built yet.
    /// </summary>
    internal static (ExportEntry Use, ExportEntry Start)? LinkConversation(IMEPackage pcc, ExportEntry pawn, string name, int line, PackageCache cache, List<string> changes)
    {
        var sequence = pcc.FindExport(MainSequence) ?? throw new ArgumentException($"line {line}: the level has no {MainSequence}.");
        var conversation = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioConversation" && e.ObjectName.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase));
        var objects = KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().ToList();

        var start = conversation is null ? null : objects.FirstOrDefault(o => o.ClassName == "BioSeqAct_StartConversation"
                                                                             && o.GetProperty<ObjectProperty>("Conv")?.Value == conversation.UIndex && OwnedBy(o, pawn));
        // Link only adds: without this cut a renamed conversation would start beside the old one on use.
        UnlinkConversations(sequence, pawn, start, changes);
        if (conversation is null) return null;
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

    /// <summary>
    /// Cuts use → start on her use events (BioSeqEvt_OnPlayerActivate, Originator = the pawn) for each conversation start of hers
    /// (Owner = the pawn) but <paramref name="keep"/>: the objects <see cref="LinkConversation"/> builds and finds again, never other
    /// Kismet. The starts stay in Main_Sequence, unlinked. Returns the conversations her use no longer starts.
    /// </summary>
    internal static List<string> UnlinkConversations(ExportEntry sequence, ExportEntry pawn, ExportEntry? keep, List<string> changes)
    {
        var cut = new List<string>();
        foreach (var use in KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>()
                     .Where(o => o.ClassName == "BioSeqEvt_OnPlayerActivate" && o.GetProperty<ObjectProperty>("Originator")?.Value == pawn.UIndex))
        {
            foreach (var start in KismetPrefabs.Unlink(use, "Out", o => o != keep && o.ClassName == "BioSeqAct_StartConversation" && OwnedBy(o, pawn)))
            {
                var conversation = start.GetProperty<ObjectProperty>("Conv")?.ResolveToEntry(start.FileRef)?.ObjectName.Instanced ?? $"#{start.UIndex}";
                changes.Add($"Kismet: use → start {conversation} unlinked");
                cut.Add(conversation);
            }
        }
        return cut;
    }

    /// <summary>A conversation start whose Owner slot holds the pawn, as <see cref="LinkConversation"/> builds it.</summary>
    private static bool OwnedBy(ExportEntry start, ExportEntry pawn) =>
        KismetHelper.GetVariableLinksOfNode(start).Any(v => v.LinkDesc == "Owner" && v.LinkedNodes.Any(n =>
            n is ExportEntry { ClassName: "SeqVar_Object" } o && o.GetProperty<ObjectProperty>("ObjValue")?.Value == pawn.UIndex));

    /// <summary>ConfigDelta .m3cd next to the level: LE1CP's Sideloader loads it with the files whose names start with a prefix. M3 merges it at install.</summary>
    private static string EnsureSideloader(string package, List<string> prefixes, Action<string, string?> wrote, List<string> notes)
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
            var backup = PackageWriter.Backup(config);
            lines[index] = wanted;
            AtomicFile.WriteAllText(config, string.Join(newline, lines));
            wrote(config, backup);
            notes.Add($"Updated {Path.GetFileName(config)}: reinstall the mod with M3 (it merges ConfigDelta files into Coalesced at install).");
            return config;
        }
        var path = Path.Combine(dir, $"ConfigDelta-{name}.m3cd");
        AtomicFile.WriteAllText(path, $"[BioGame.ini SFXGame.SideloaderFramework]\n{wanted}\n");
        wrote(path, null);
        notes.Add($"Wrote {Path.GetFileName(path)}: install the mod with M3 (it merges ConfigDelta files into Coalesced at install).");
        return path;
    }

    /// <summary>
    /// A YAML with no 'sideloader' key: notes that ConfigDelta-&lt;level&gt;.m3cd, the file <see cref="EnsureSideloader"/> creates,
    /// still tells LE1CP's Sideloader to load the level. Nothing is changed: without the key the .m3cd files are the mod's own (a
    /// line kept by hand can carry fields the build does not write), and the build cannot tell its line from such a one. A line
    /// in an .m3cd of another name is not reported. A file that cannot be read is not reported either: a note must not fail a
    /// build whose level is already saved.
    /// </summary>
    private static void NoteSideloaderLeft(string package, List<string> notes)
    {
        var name = Path.GetFileNameWithoutExtension(package);
        var config = Path.Combine(Path.GetDirectoryName(package)!, $"ConfigDelta-{name}.m3cd");
        try
        {
            if (!File.Exists(config) || !File.ReadAllText(config).Contains($"LevelName=\"{name}\"", StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        notes.Add($"{Path.GetFileName(config)} still tells LE1CP's Sideloader to load {name}, and the file has no 'sideloader' key: the build leaves .m3cd files "
                  + "as they are without it. Delete that file if the level should no longer be sideloaded, or put 'sideloader' back.");
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
        (StrProperty x, StrProperty y) => x.Value == y.Value,
        (ArrayProperty<StrProperty> x, ArrayProperty<StrProperty> y) => x.Select(e => e.Value).SequenceEqual(y.Select(e => e.Value)),
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
        foreach (var (key, value) in TopLevel(Root(yaml), "package", "defaultLanguage", "globalTlk", "sideloader", "lightTemplate", "streaming", "postProcess",
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
                    if (value is not YamlMappingNode vanilla)
                    {
                        throw Fail(value, "'vanilla' maps <file>.<actor name> to what changes: {location, rotation or yaw, scale, scale3d, removed}, and <file>.<collection>.<light> "
                                          + "to {location, brightness, color, radius, falloff, enabled}.");
                    }
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
        // A vanilla actor ships in a fork, which the map streams instead of the file: only a file it streams can be forked. A light of a
        // light collection may also be a light of a characterLights copy, keyed by its source file: the build writes it into the map.
        var copied = spec.CharacterLights.Where(c => c.Package is not null).DistinctBy(c => LevelStreams.CopyName(c.Actor), StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{FileOf(c.Package!)}.{LevelStreams.CopyName(c.Actor)}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in spec.Vanilla.Where(v => !Streams(spec, v.File)))
        {
            if (entry.Light is null) throw new ArgumentException($"line {entry.Line}: {entry.File} is not in streaming.levels: a vanilla actor ships in a fork of a file the map streams.");
            if (!copied.Contains($"{entry.File}.{entry.Collection}"))
            {
                throw new ArgumentException($"line {entry.Line}: {entry.File}.{entry.Collection} is neither in streaming.levels nor a characterLights item");
            }
        }
        var again = spec.Vanilla.GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (again is not null) throw new ArgumentException($"line {again.Last().Line}: 'vanilla' has {again.Key} twice.");
        return spec;
    }

    /// <summary>
    /// BIOA_FRE31_00_LAY.StaticMeshActor_69: {location: [x, y, z], rotation: [pitch, yaw, roll] or yaw, scale, scale3d, removed}, as props
    /// take them; an effect (an Emitter_N) takes no scale. A light of a light collection: <see cref="VanillaLightOf"/>.
    /// </summary>
    private static ForkBuilder.Entry VanillaOf(YamlNode key, YamlNode body)
    {
        var name = Scalar(key);
        if (name.Split('.') is { Length: 3 } parts && parts.All(p => p.Length > 0)) return VanillaLightOf(key, parts, body);
        var dot = name.IndexOf('.');
        if (dot <= 0 || dot == name.Length - 1 || name.IndexOf('.', dot + 1) >= 0)
        {
            throw Fail(key, $"'{name}': a vanilla key is <file>.<actor name>, e.g. BIOA_FRE31_00_LAY.StaticMeshActor_69, or <file>.<collection>.<light> for a light of a light "
                            + "collection, e.g. BIOA_FRE31_00_LAY.StaticLightCollectionActor_127.PointLight_35_LC.");
        }
        if (body is not YamlMappingNode m) throw Fail(body, $"vanilla '{name}' must be a mapping, e.g. {{ location: [x, y, z] }} or {{ removed: true }}.");
        Vector3? location = null, scale3d = null;
        float? yaw = null, scale = null;
        float[]? rotation = null;
        var removed = false;
        YamlNode? turned = null, scaled = null;
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
                case "scale":
                    scale = Number(value);
                    scaled = value;
                    break;
                case "scale3d":
                    var s = Numbers(value, 3, 3);
                    scale3d = new Vector3(s[0], s[1], s[2]);
                    scaled = value;
                    break;
                case "removed": removed = Bool(value); break;
            }
        }
        // How its particles follow a scale is unchecked (the fork refuses one on any Emitter, whatever its name).
        if (scaled is not null && name[(dot + 1)..].StartsWith("Emitter_", StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(scaled, $"{name[(dot + 1)..]} is an effect: location, rotation (or yaw) and removed only");
        }
        if (yaw is not null && turned is not null)
        {
            throw Fail(turned, $"vanilla '{name}' has 'yaw' and 'rotation': give one ('rotation' is [pitch, yaw, roll] in degrees, 'yaw' keeps the file's pitch and roll).");
        }
        return new ForkBuilder.Entry(name[..dot], name[(dot + 1)..], (int)key.Start.Line, location, yaw, rotation, scale, scale3d, removed);
    }

    /// <summary>
    /// BIOA_FRE31_00_LAY.StaticLightCollectionActor_127.PointLight_35_LC: {location: [x, y, z], brightness, color: [r, g, b], radius, falloff,
    /// enabled} (docs/design/in-game-editor.md §12.4): a point or spot light of a light collection; a field left out keeps the file's value. It moves and keeps
    /// its channels: no rotation, scale, removed or kind. Sun and sky lights are not edited.
    /// </summary>
    private static ForkBuilder.Entry VanillaLightOf(YamlNode key, string[] parts, YamlNode body)
    {
        var light = parts[2];
        if (light.StartsWith("DirectionalLight", StringComparison.OrdinalIgnoreCase)) throw Fail(key, $"{light} is a sun light: not edited in Phase 5");
        if (light.StartsWith("SkyLight", StringComparison.OrdinalIgnoreCase)) throw Fail(key, $"{light} is a sky light: not edited in Phase 5");
        if (body is not YamlMappingNode m) throw Fail(body, $"vanilla '{string.Join('.', parts)}' must be a mapping, e.g. {{ brightness: 1.2 }} or {{ enabled: false }}.");
        foreach (var k in m.Children.Keys)
        {
            if (k is YamlScalarNode { Value: "rotation" or "yaw" or "scale" or "scale3d" or "removed" }) throw Fail(k, "lights move, they don't turn, scale or go: use enabled: false");
            if (k is YamlScalarNode { Value: "kind" }) throw Fail(k, "level lights keep their channels: a Room light (lights:) lights the walls");
        }
        Vector3? location = null;
        float? radius = null, brightness = null, falloff = null;
        byte[]? color = null;
        bool? enabled = null;
        foreach (var (k, value) in Entries(m, "location", "brightness", "color", "radius", "falloff", "enabled"))
        {
            switch (k)
            {
                case "location":
                    var xyz = Numbers(value, 3, 3);
                    location = new Vector3(xyz[0], xyz[1], xyz[2]);
                    break;
                case "brightness": brightness = Number(value) is >= 0 and var b ? b : throw Fail(value, "'brightness' can't be negative."); break;
                case "color": color = ColorOf(value); break;
                case "radius": radius = Number(value) is > 0 and var r ? r : throw Fail(value, "'radius' must be over 0."); break;
                case "falloff": falloff = FalloffOf(value); break;
                case "enabled": enabled = Bool(value); break;
            }
        }
        return new ForkBuilder.Entry(parts[0], $"{parts[1]}.{light}", (int)key.Start.Line, location, null, null, null, null, false,
            new SlcaLights.Edit(radius, brightness, color, falloff, enabled));
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

    /// <summary>
    /// <c>{map, startPoint}</c>, or a list of them, each but the last with <c>if: conditional &lt;id&gt; [param &lt;n&gt;]</c>: the first whose
    /// conditional is true, else the last.
    /// </summary>
    private static List<GoToStep> GoToOf(YamlNode value)
    {
        if (value is YamlMappingNode single) return [GoToStepOf(single, allowIf: false)];
        if (value is not YamlSequenceNode list)
            throw Fail(value, "'goTo' is {map: <level file name>, startPoint: <start point tag>}, or a list of them, each but the last with 'if: conditional <id>'.");
        var steps = list.Children.Select(item => GoToStepOf(item as YamlMappingNode ?? throw Fail(item, "a 'goTo' entry is {if, map, startPoint}."), allowIf: true)).ToList();
        if (steps.Count == 0) throw Fail(value, "'goTo' has no entry.");
        for (var i = 0; i < steps.Count; i++)
        {
            var last = i == steps.Count - 1;
            if (last && steps[i].Conditional is not null)
                throw new ArgumentException($"line {steps[i].Line}: the last 'goTo' entry needs no 'if': it is where the usable goes when no other applies.");
            if (!last && steps[i].Conditional is null)
                throw new ArgumentException($"line {steps[i].Line}: a 'goTo' entry without 'if' must be the last: the entries after it could never apply.");
        }
        return steps;
    }

    private static GoToStep GoToStepOf(YamlMappingNode m, bool allowIf)
    {
        string? map = null, startPoint = null;
        int? conditional = null;
        var param = 0;
        foreach (var (key, v) in allowIf ? Entries(m, "if", "map", "startPoint") : Entries(m, "map", "startPoint"))
        {
            switch (key)
            {
                case "map": map = Path.GetFileNameWithoutExtension(Scalar(v)); break;
                case "startPoint": startPoint = Scalar(v); break;
                case "if":
                    var match = ConversationBuilder.FunctionCondition().Match(Scalar(v).Trim());
                    if (!match.Success) throw Fail(v, "'if' is 'conditional <id>' or 'conditional <id> param <n>' (a plot conditional function).");
                    conditional = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
                    if (match.Groups["param"].Success) param = int.Parse(match.Groups["param"].Value, CultureInfo.InvariantCulture);
                    break;
            }
        }
        return new GoToStep(conditional, param, map ?? throw Fail(m, "'goTo' needs 'map'."), startPoint, (int)m.Start.Line);
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

    /// <summary>{type: MyNurse, strref: 74210001, fr: "...", en: "..."}.</summary>
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

    /// <summary>
    /// {tag, offset: [forward, right, up], radius, brightness, color: [r, g, b], falloff, enabled}: colour and falloff the template's
    /// when left out (white and 2 for LE1CP's), enabled true.
    /// </summary>
    private static LightSpec LightOf(YamlNode value)
    {
        if (value is not YamlMappingNode m) throw Fail(value, "a light is {tag, offset: [forward, right, up], radius, brightness}, and optionally color, falloff and enabled.");
        string? tag = null;
        var offset = Vector3.Zero;
        float? radius = null, brightness = null, falloff = null;
        byte[]? color = null;
        var enabled = true;
        foreach (var (key, v) in Entries(m, "tag", "offset", "radius", "brightness", "color", "falloff", "enabled"))
        {
            switch (key)
            {
                case "tag": tag = Scalar(v); break;
                case "offset":
                    var f = Numbers(v, 3, 3);
                    offset = new Vector3(f[0], f[1], f[2]);
                    break;
                case "radius": radius = Number(v) is > 0 and var r ? r : throw Fail(v, "'radius' must be over 0."); break;
                case "brightness": brightness = Number(v) is >= 0 and var b ? b : throw Fail(v, "'brightness' can't be negative."); break;
                case "color": color = ColorOf(v); break;
                case "falloff": falloff = FalloffOf(v); break;
                case "enabled": enabled = Bool(v); break;
            }
        }
        return new LightSpec(tag ?? throw Fail(value, "the light needs a 'tag'."), offset, radius, brightness, (int)m.Start.Line, color, falloff, enabled);
    }

    private static byte[] ColorOf(YamlNode value)
    {
        var rgb = Numbers(value, 3, 3);
        return rgb.All(c => c is >= 0 and <= 255 && c == MathF.Round(c)) ? rgb.Select(c => (byte)c).ToArray() : throw Fail(value, "'color' is [r, g, b], whole numbers from 0 to 255.");
    }

    private static float FalloffOf(YamlNode value) => Number(value) is > 0 and var f ? f : throw Fail(value, "'falloff' must be over 0 (2 by default).");

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
                case "color": light.Color = ColorOf(value); break;
                case "falloff": light.Falloff = FalloffOf(value); break;
                case "enabled": light.Enabled = Bool(value); break;
                case "template": light.Template = SourceOf(value); break;
            }
        }
        if (light.Tag is null) throw Fail(body, $"light '{name}' needs a 'tag' (how the build and the editor find it again).");
        if (light.Location is null || light.Radius is null || light.Brightness is null) throw Fail(body, $"light '{name}' needs 'location', 'radius' and 'brightness'.");
        return light;
    }
}
