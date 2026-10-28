using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core;

/// <param name="Key">The YAML's key: &lt;file&gt;.&lt;actor name&gt;, or &lt;file&gt;.&lt;collection&gt;.&lt;light&gt; for a light of a light collection.</param>
/// <param name="Class">The actor's class, or the light component's (PointLightComponent, SpotLightComponent).</param>
/// <param name="Mesh">The StaticMesh it shows, or an effect's ParticleSystem, by its path in the file; null when it shows none (a light).</param>
/// <param name="Changes">Each value the fork changes, vanilla → fork. A light's is one line with its light type: "PointLight_35_LC (U door light):
/// brightness 0.6 → 1.2, location (…) → (…)".</param>
public sealed record ForkedActor(string Key, string Class, string? Mesh, IReadOnlyList<string> Changes);

/// <param name="Path">The copy next to the fork: &lt;fork&gt;_LOC_&lt;language&gt;.pcc.</param>
/// <param name="Action">written, unchanged or deleted, as the fork's.</param>
/// <param name="Source">The file's own localized package it copies byte for byte (the highest-mounted one outside our own DLC folder); null when deleted.</param>
public sealed record ForkLocalized(string Path, string Action, string? Source, string? Md5);

/// <param name="File">The vanilla file, as streaming.levels names it.</param>
/// <param name="Fork">The fork's name: the file's, prefixed with the map's (<see cref="ForkBuilder.ForkName"/>).</param>
/// <param name="Action">written (new or changed), unchanged (the file next to the map has the same bytes) or deleted (no vanilla entry left:
/// removed after the map was saved, which streams the vanilla file again).</param>
/// <param name="Source">The vanilla copy the fork is made from: the highest-mounted one outside our own DLC folder.</param>
/// <param name="Localized">The file's localized packages (&lt;file&gt;_LOC_&lt;language&gt;: its dialogue, voice and sounds), copied under the fork's name,
/// since the game loads them by the name of the package it streams.</param>
public sealed record BuiltFork(string File, string Fork, string ForkPath, string Action, string? Source, long? SourceSize, string? SourceMd5, long? ForkSize, string? ForkMd5,
    IReadOnlyList<ForkedActor> Actors, IReadOnlyList<ForkLocalized> Localized);

/// <summary>
/// Forks of vanilla level files (docs/design/in-game-editor.md §11): a renamed copy of a file the map streams, in which vanilla actors are moved, turned,
/// scaled or hidden, and the lights of its light collections changed (docs/design/in-game-editor.md §12.4, <see cref="SlcaLights"/>), and which the map
/// streams instead of the file. The copy keeps everything else of the file; only its self
/// Package export (a forced export named as the file) is renamed, so the game loads it beside, not over, the vanilla one. The file's
/// localized packages (&lt;file&gt;_LOC_&lt;language&gt;), which the game loads by the streamed package's name, are copied under the fork's. Fork
/// names follow one rule, shared with editor_import and mirrored in MeleBridge, so nothing has to store the pair.
/// </summary>
public static class ForkBuilder
{
    private const string LevelPath = "TheWorld.PersistentLevel";

    /// <summary>A half-extent over this is a room shell (110 m), not a prop: hiding it opens the room.</summary>
    internal const float MaxPropExtent = 3000;

    /// <summary>A cover link or slot this close to a changed actor still says there is cover where it stood.</summary>
    private const float CoverReach = 100;

    /// <summary>Total scales this close are one (the cooked collision maps key on them).</summary>
    private const float ScaleNear = 1e-4f;

    /// <summary>
    /// The classes a vanilla entry may change: placed meshes, and effects (moved, turned or hidden, not scaled). Doors and usables carry
    /// Kismet; nodes, volumes and light actors must stay. A light collection's lights are changed one by one, by their component.
    /// </summary>
    private static readonly string[] AllowedClasses = ["StaticMeshActor", "BioInert", "Emitter"];

    /// <summary>
    /// A vanilla: entry: the file's actor and what it gives it (degrees for yaw and rotation); a value left out keeps the file's. A light
    /// of a light collection has <see cref="Light"/>, and its <see cref="Actor"/> is &lt;collection&gt;.&lt;light&gt;: a location and its fields.
    /// </summary>
    internal sealed record Entry(string File, string Actor, int Line, Vector3? Location, float? Yaw, float[]? Rotation, float? Scale, Vector3? Scale3D, bool Removed,
        SlcaLights.Edit? Light = null)
    {
        public string Key => $"{File}.{Actor}";

        /// <summary>A light's collection (StaticLightCollectionActor_127); the actor itself for any other entry.</summary>
        public string Collection => Light is null ? Actor : Actor[..Actor.IndexOf('.')];

        /// <summary>A light's component (PointLight_35_LC); the actor itself for any other entry.</summary>
        public string Component => Light is null ? Actor : Actor[(Actor.IndexOf('.') + 1)..];
    }

    /// <summary>An actor's transform as its file gives it, through its archetypes (rotation in Unreal units).</summary>
    internal sealed record Pose(Vector3 Location, Rotator Rotation, float Scale, Vector3 Scale3D);

    /// <summary>
    /// A fork saved in memory and not written yet (<see cref="Prepare"/>), with what <see cref="Write"/> needs: the build prepares every
    /// file's first, so a refusal in any of them writes none.
    /// </summary>
    /// <param name="Refusals">Each entry the build won't apply, with its YAML line; the build writes nothing when any file has one.</param>
    /// <param name="Localized">Each localized package of the file: the language (INT, FR...) and the copy the game would load.</param>
    internal sealed record PreparedFork(string File, string Fork, string ForkPath, string Source, long SourceSize, string SourceMd5, byte[] Bytes, string Md5,
        IReadOnlyList<ForkedActor> Actors, IReadOnlyList<string> Warnings, IReadOnlyList<string> Refusals, IReadOnlyList<(string Language, string Source)> Localized);

    /// <summary>The fork of a file in a map: BIOA_CLD01 + BIOA_FRE31_00_LAY → CLD01_FRE31_00_LAY.</summary>
    public static string ForkName(string map, string file) => $"{Bare(map)}_{Bare(file)}";

    /// <summary>
    /// The vanilla file a level of this map stands for: a fork's (&lt;map minus BIOA_&gt;_&lt;rest&gt; → BIOA_&lt;rest&gt;, or &lt;rest&gt;
    /// when it starts with BIO), else the name itself.
    /// </summary>
    public static string Canonical(string map, string level)
    {
        var prefix = Bare(map) + "_";
        if (level.Length <= prefix.Length || !level.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return level;
        var rest = level[prefix.Length..];
        return rest.StartsWith("BIO", StringComparison.OrdinalIgnoreCase) ? rest : "BIOA_" + rest;
    }

    private static string Bare(string name) => name.StartsWith("BIOA_", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;

    /// <summary>The DLC folder a file lies in (DLC_MOD_MyMod); null outside one.</summary>
    internal static string? DlcOf(string path)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Path.GetFileName(dir).StartsWith("DLC_", StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(dir);
        }
        return null;
    }

    /// <summary>
    /// The copy of a vanilla file a fork starts from: the highest-mounted one outside our own DLC folder (base game or another mod,
    /// e.g. LE1CP's), so a copy our DLC might ship is never forked again.
    /// </summary>
    internal static string VanillaSource(MEGame game, string file, string? ownDlc)
    {
        LecHost.EnsureInitialized();
        var name = file + ".pcc";
        bool Ours(string folder) => ownDlc is not null && Path.GetFileName(folder).Equals(ownDlc, StringComparison.OrdinalIgnoreCase);
        if (MELoadedFiles.GetFilesLoadedInGame(game).TryGetValue(name, out var found) && !(DlcOf(found) is { } dlc && Ours(dlc))) return found;
        // Our DLC has a copy (it mounts above the others): the next one down, in mount order as the game loads them.
        string? below = null;
        var folders = MELoadedDLC.GetEnabledDLCFolders(game).OrderBy(d => MELoadedDLC.GetMountPriority(d, game)).Prepend(MEDirectories.GetBioGamePath(game));
        foreach (var folder in folders.Where(f => !Ours(f)))
        {
            below = MELoadedFiles.GetCookedFiles(game, folder).LastOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? below;
        }
        return below ?? throw new FileNotFoundException($"{game} loads no {name} outside {ownDlc ?? "our DLC"}: the vanilla file to fork.");
    }

    /// <summary>
    /// The localized packages of a file, &lt;file&gt;_LOC_&lt;language&gt;.pcc, each the copy the game would load (the highest-mounted one outside
    /// our own DLC folder): its dialogue, voice and sounds, in forced packages the file's own imports name (BIOA_NOR10_13_LAY imports
    /// nor10_backgrounds_D.nor10_backgrounds_dlg from its LOC). The game loads them by the name of the package it streams, so a fork
    /// without copies under its own name loses them.
    /// </summary>
    internal static List<(string Language, string Source)> Localized(MEGame game, string file, string? ownDlc)
    {
        LecHost.EnsureInitialized();
        var prefix = file + "_LOC_";
        var found = new List<(string Language, string Source)>();
        var names = MELoadedFiles.GetFilesLoadedInGame(game).Keys.Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(n => n.Length > prefix.Length && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            try
            {
                found.Add((name[prefix.Length..], VanillaSource(game, name, ownDlc)));
            }
            catch (FileNotFoundException)
            {
                // Only our DLC has it: not the file's own.
            }
        }
        return found;
    }

    /// <summary>
    /// Makes the fork of <paramref name="file"/> in memory: the vanilla copy, renamed, with each entry applied in full effective values
    /// (Location, Rotation, DrawScale, DrawScale3D on the actor, whatever its archetypes give), saved to bytes. Writes nothing:
    /// refusals (with their YAML lines) and warnings come back with it, for the build to throw on before any fork is written.
    /// </summary>
    internal static PreparedFork Prepare(MEGame game, string map, string file, IReadOnlyList<Entry> entries)
    {
        var fork = ForkName(Path.GetFileNameWithoutExtension(map), file);
        var forkPath = Path.Combine(Path.GetDirectoryName(map)!, fork + ".pcc");
        PackageWriter.EnsureWritable(game, forkPath);
        var source = VanillaSource(game, file, DlcOf(map));
        var localized = Localized(game, file, DlcOf(map));
        foreach (var (language, _) in localized) PackageWriter.EnsureWritable(game, LocalizedPath(forkPath, fork, language));
        var sourceBytes = File.ReadAllBytes(source);

        // In memory: the vanilla file is never saved.
        using var stream = new MemoryStream(sourceBytes);
        using var pcc = MEPackageHandler.OpenMEPackageFromStream(stream, source);
        var self = pcc.Exports.FirstOrDefault(e => e.Parent is null && e.ClassName == "Package" && e.ObjectName.Instanced.Equals(file, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"{source} has no Package export named {file}, so it can't be forked: is it a level file?");
        self.ObjectName = NameReference.FromInstancedString(fork);
        // Its own GUID, the same on every build: two packages with one GUID are one package to the engine.
        var guid = GuidOf(fork);
        self.PackageGUID = guid;
        pcc.PackageGuid = guid;

        var held = KismetHeld(pcc);
        var refusals = new List<string>();
        var warnings = new List<string>();
        Level? levelBinary = null;
        Level LevelBinary() => levelBinary ??= ObjectBinary.From<Level>(pcc.FindExport(LevelPath) ?? throw new ArgumentException($"{source} has no {LevelPath}."));
        // A light's archetypes end in Engine's class default objects.
        using var cache = new PackageCache();
        var actors = entries.Select(e => e.Light is not null ? ApplyLight(pcc, e, held, cache, refusals) : Apply(pcc, e, held, LevelBinary, refusals, warnings))
            .OfType<ForkedActor>().ToList();
        var bytes = refusals.Count > 0 ? [] : pcc.SaveToStream(compress: true).ToArray();
        return new PreparedFork(file, fork, forkPath, source, sourceBytes.LongLength, Md5(sourceBytes), bytes, Md5(bytes), actors, warnings, refusals, localized);
    }

    /// <summary>
    /// Writes a prepared fork next to the map, only when its bytes differ from the file there, and a byte copy of each of the vanilla
    /// file's localized packages under the fork's name (they have no self export to rename), likewise; a copy of a language the file
    /// no longer has goes. Its warnings go to the notes.
    /// </summary>
    internal static BuiltFork Write(PreparedFork prepared, List<string> notes)
    {
        if (prepared.Refusals.Count > 0) throw new InvalidOperationException($"{prepared.Fork} has refusals: it is not written.");
        notes.AddRange(prepared.Warnings);
        var action = WriteIfChanged(prepared.ForkPath, prepared.Bytes, prepared.Md5);
        var localized = new List<ForkLocalized>();
        foreach (var (language, source) in prepared.Localized)
        {
            var bytes = File.ReadAllBytes(source);
            var (path, md5) = (LocalizedPath(prepared.ForkPath, prepared.Fork, language), Md5(bytes));
            localized.Add(new ForkLocalized(path, WriteIfChanged(path, bytes, md5), source, md5));
        }
        var copied = localized.Select(l => l.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in LocalizedCopies(prepared.ForkPath, prepared.Fork).Where(p => !copied.Contains(p)))
        {
            File.Delete(stale);
            localized.Add(new ForkLocalized(stale, "deleted", null, null));
        }
        return new BuiltFork(prepared.File, prepared.Fork, prepared.ForkPath, action, prepared.Source, prepared.SourceSize, prepared.SourceMd5, prepared.Bytes.LongLength,
            prepared.Md5, prepared.Actors, localized);
    }

    /// <summary>Written beside it, then moved over it: a crash mid-write leaves the old file whole. No backup: a fork can always be rebuilt.</summary>
    private static string WriteIfChanged(string path, byte[] bytes, string md5)
    {
        if (File.Exists(path) && Md5(File.ReadAllBytes(path)) == md5) return "unchanged";
        var temporary = path + ".build.tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
        return "written";
    }

    private static string LocalizedPath(string forkPath, string fork, string language) => Path.Combine(Path.GetDirectoryName(forkPath)!, $"{fork}_LOC_{language}.pcc");

    /// <summary>The localized copies of a fork next to it (&lt;fork&gt;_LOC_*.pcc).</summary>
    private static IEnumerable<string> LocalizedCopies(string forkPath, string fork) =>
        Directory.Exists(Path.GetDirectoryName(forkPath))
            ? Directory.EnumerateFiles(Path.GetDirectoryName(forkPath)!, $"{fork}_LOC_*.pcc").Where(p => p.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

    /// <summary>
    /// Deletes the fork of a file that has no vanilla entry left, and its localized copies, once the map streams the vanilla file again
    /// (the caller saved it). Null when there is none.
    /// </summary>
    internal static BuiltFork? Delete(MEGame game, string map, string file)
    {
        var fork = ForkName(Path.GetFileNameWithoutExtension(map), file);
        var forkPath = Path.Combine(Path.GetDirectoryName(map)!, fork + ".pcc");
        var copies = LocalizedCopies(forkPath, fork).ToList();
        if (!File.Exists(forkPath) && copies.Count == 0) return null;
        foreach (var path in copies.Prepend(forkPath))
        {
            PackageWriter.EnsureWritable(game, path);
            File.Delete(path);
        }
        return new BuiltFork(file, fork, forkPath, "deleted", null, null, null, null, null, [], copies.Select(c => new ForkLocalized(c, "deleted", null, null)).ToList());
    }

    /// <summary>Applies one entry to its actor; null (with the reason in <paramref name="refusals"/>) when it may not change.</summary>
    private static ForkedActor? Apply(IMEPackage pcc, Entry entry, IReadOnlyDictionary<int, string> held, Func<Level> level, List<string> refusals, List<string> warnings)
    {
        var where = $"line {entry.Line}: {entry.Key}";
        var actor = pcc.FindExport($"{LevelPath}.{entry.Actor}");
        if (actor is null)
        {
            refusals.Add($"{where}: {entry.File} has no actor {entry.Actor}");
            return null;
        }
        if (Refusal(actor, held, entry.Removed) is { } why)
        {
            refusals.Add($"{where}: {why}");
            return null;
        }
        // How its particles follow a scale is unchecked.
        var effect = actor.IsA("Emitter");
        if (effect && (entry.Scale is not null || entry.Scale3D is not null))
        {
            refusals.Add($"line {entry.Line}: {entry.Actor} is an effect: location, rotation (or yaw) and removed only");
            return null;
        }

        var vanilla = PoseOf(actor);
        var rotation = entry.Rotation is { } pyr ? new Rotator(LevelBuilder.Units(pyr[0]), LevelBuilder.Units(pyr[1]), LevelBuilder.Units(pyr[2]))
            : entry.Yaw is { } yaw ? new Rotator(vanilla.Rotation.Pitch, LevelBuilder.Units(yaw), vanilla.Rotation.Roll)
            : vanilla.Rotation;
        var forked = new Pose(entry.Location ?? vanilla.Location, rotation, entry.Scale ?? vanilla.Scale, entry.Scale3D ?? vanilla.Scale3D);

        // In full: 499 of the freighter file's 850 meshes take their rotation from a prefab archetype only.
        var props = actor.GetProperties();
        props.AddOrReplaceProp(CommonStructs.Vector3Prop(forked.Location, "Location"));
        props.AddOrReplaceProp(CommonStructs.RotatorProp(forked.Rotation, "Rotation"));
        props.AddOrReplaceProp(new FloatProperty(forked.Scale, "DrawScale"));
        props.AddOrReplaceProp(CommonStructs.Vector3Prop(forked.Scale3D, "DrawScale3D"));
        var changes = new List<string>();
        if (forked.Location != vanilla.Location) changes.Add($"location {Moved(vanilla.Location, forked.Location)}");
        if (forked.Rotation != vanilla.Rotation) changes.Add($"rotation {Rotation(vanilla.Rotation)} → {Rotation(forked.Rotation)}");
        if (forked.Scale != vanilla.Scale) changes.Add($"scale {Number(vanilla.Scale)} → {Number(forked.Scale)}");
        if (forked.Scale3D != vanilla.Scale3D) changes.Add($"scale3d {Scale3D(vanilla.Scale3D)} → {Scale3D(forked.Scale3D)}");
        if (effect && forked.Location != vanilla.Location)
        {
            // Where its particles were last tick: in UE3 it only feeds their system's velocity, so set where the effect now is.
            foreach (var component in actor.GetChildren().OfType<ExportEntry>().Where(c => c.IsA("ParticleSystemComponent")))
            {
                var c = component.GetProperties();
                c.AddOrReplaceProp(CommonStructs.Vector3Prop(forked.Location, "OldPosition"));
                component.WriteProperties(c);
            }
        }
        if (entry.Removed)
        {
            // As removed: true hides a prop. Its physics body is made at level start from the component's BlockRigidBody (on the actor it is transient).
            props.AddOrReplaceProp(new BoolProperty(true, "bHidden"));
            props.AddOrReplaceProp(new BoolProperty(false, "bCollideActors"));
            props.AddOrReplaceProp(new BoolProperty(false, "bBlockActors"));
            foreach (var component in actor.GetChildren().OfType<ExportEntry>().Where(c => c.IsA("StaticMeshComponent")))
            {
                var c = component.GetProperties();
                c.AddOrReplaceProp(new BoolProperty(false, "BlockRigidBody"));
                component.WriteProperties(c);
            }
            // An effect has nothing to collide with: hidden, it makes no particles.
            changes.Add(effect ? "shown → hidden" : "shown → hidden, no collision (BlockRigidBody false)");
        }
        actor.WriteProperties(props);

        // An effect stands on no cover and opens no room.
        if (changes.Count > 0 && !effect) Warn(pcc, actor, entry, vanilla, forked, level, warnings);
        return new ForkedActor(entry.Key, actor.ClassName, MeshOf(actor)?.InstancedFullPath, changes);
    }

    /// <summary>
    /// Applies a light entry to its component of a light collection (docs/design/in-game-editor.md §12.4): Brightness, LightColor, Radius, FalloffExponent and
    /// bEnabled as given, and a location as Translation through the collection's binary matrix (<see cref="SlcaLights.Write"/>); null
    /// (with the reason in <paramref name="refusals"/>) when it may not change. Its receipt is one line, with its light type.
    /// </summary>
    private static ForkedActor? ApplyLight(IMEPackage pcc, Entry entry, IReadOnlyDictionary<int, string> held, PackageCache cache, List<string> refusals)
    {
        var where = $"line {entry.Line}: {entry.Key}";
        var light = pcc.FindExport($"{LevelPath}.{entry.Actor}");
        if (light is null)
        {
            refusals.Add($"{where}: {entry.File} has no light {entry.Component} in {entry.Collection}");
            return null;
        }
        if (Refusal(light, held, removed: false) is { } why)
        {
            refusals.Add($"{where}: {why}");
            return null;
        }
        if (SlcaLights.Read(light, cache) is not { } before)
        {
            refusals.Add($"{where}: {entry.Collection} has no matrix for it (it is not in its LightComponents)");
            return null;
        }
        if (SlcaLights.Write(light, before, entry.Light!, entry.Location) is { } refused)
        {
            refusals.Add($"{where}: {refused}");
            return null;
        }
        var receipt = SlcaLights.Receipt(entry.Component, before, SlcaLights.Read(light, cache)!, () => [pcc]);
        return new ForkedActor(entry.Key, light.ClassName, null, receipt is null ? [] : [receipt]);
    }

    /// <summary>What a changed actor leaves behind that the fork does not fix: cover, a room shell, a non-uniform scale with no collision cooked for it.</summary>
    private static void Warn(IMEPackage pcc, ExportEntry actor, Entry entry, Pose vanilla, Pose forked, Func<Level> level, List<string> warnings)
    {
        var where = $"line {entry.Line}: {entry.Key}";
        // The link first: in the freighter every prefab-placed mesh is the cover prefab's (FRE10_Cover01_PREFAB), cover or not.
        if (CoverNear(pcc, actor, vanilla) is { } cover) warnings.Add($"{where}: {cover.ObjectName.Instanced} is within {CoverReach} units: its cover stays where the actor stood");
        else if (Archetypes(actor).FirstOrDefault(a => a.ObjectName.Instanced.Contains("Cover", StringComparison.OrdinalIgnoreCase)) is { } prefab)
        {
            warnings.Add($"{where}: a cover prefab's ({prefab.ObjectName.Instanced}): cover nodes it had stay where it stood");
        }
        if (Extent(pcc, actor, vanilla) is var extent and > MaxPropExtent) warnings.Add($"{where}: a room shell (half-extent {extent:0}): moving or hiding it opens the room");

        // The level cooks collision per mesh and total scale; at a scale with none cooked the game makes it at load. Seen in game for a
        // uniform scale (a cargo container at 0.57 blocked Shepard at its scaled face); a non-uniform one is untested.
        foreach (var component in Primitives(actor).Where(c => c.IsA("StaticMeshComponent")))
        {
            if (Mesh(pcc, component) is not { } mesh) continue;
            var (before, after) = (TotalScale(component, vanilla), TotalScale(component, forked));
            if (Near(before, after) || Near(after, new Vector3(after.X))) continue;
            bool Cooked(Vector3 scale) => level().CachedPhysSMDataMap.Concat(level().CachedPhysPerTriSMDataMap).Any(p => p.Key == mesh.UIndex && Near(p.Value.Scale3D, scale));
            if (Cooked(before) && !Cooked(after))
            {
                warnings.Add($"{where}: {mesh.ObjectName.Instanced} has collision cooked at {Scale3D(before)}, none at the non-uniform {Scale3D(after)}: the game makes it at "
                             + "load, which was checked in game for a uniform scale only: bump it in game");
            }
        }
    }

    /// <summary>
    /// Why a vanilla actor may not change, or null: only StaticMeshActors, BioInerts and Emitters (by class chain) that no Kismet variable
    /// of the file holds; hidden (<paramref name="removed"/>), none with a primitive that is drawn whatever its owner (bIgnoreOwnerHidden).
    /// A component: only a point or spot light of a light collection (<see cref="SlcaLights.Refusal"/>). The build and editor_import ask the same.
    /// </summary>
    internal static string? Refusal(ExportEntry actor, IReadOnlyDictionary<int, string> held, bool removed)
    {
        if (actor.IsA("ActorComponent"))
        {
            return SlcaLights.Refusal(actor) ?? (held.TryGetValue(actor.UIndex, out var holder) ? $"Kismet holds it ({holder}): its scripts would find it changed" : null);
        }
        if (!AllowedClasses.Any(c => actor.IsA(c)))
        {
            var why = actor.IsA("BioDoor") ? "doors carry Kismet and door volumes"
                : actor.IsA("BioUseable") ? "usables carry Kismet"
                : actor.IsA("Pawn") || actor.IsA("Controller") ? "pawns are placed by their own entries"
                : actor.IsA("NavigationPoint") ? "path nodes and cover links must stay where the paths were built"
                : actor.IsA("Volume") ? "volumes must stay"
                : actor.IsA("StaticLightCollectionActor") ? "its lights change one by one (<file>.<collection>.<light>)"
                : actor.IsA("Light") ? "lights are baked into the level"
                : "only StaticMeshActors, BioInerts and Emitters may change";
            return $"a {actor.ClassName}: {why}";
        }
        if (held.TryGetValue(actor.UIndex, out var variable)) return $"Kismet holds it ({variable}): its scripts would find it changed";
        return removed && Primitives(actor).FirstOrDefault(c => LevelBuilder.Effective(c).GetProp<BoolProperty>("bIgnoreOwnerHidden")?.Value == true) is { } drawn
            ? $"removed, but its {drawn.ObjectName.Instanced} has bIgnoreOwnerHidden, so it would stay drawn"
            : null;
    }

    /// <summary>The exports a SeqVar_Object or SeqVar_ObjectList of the file holds, with the first variable that does: its Kismet finds them.</summary>
    internal static Dictionary<int, string> KismetHeld(IMEPackage pcc)
    {
        var held = new Dictionary<int, string>();
        foreach (var variable in pcc.Exports.Where(e => e.ClassName.Contains("SeqVar") && (e.IsA("SeqVar_Object") || e.IsA("SeqVar_ObjectList"))))
        {
            var props = variable.GetProperties();
            var objects = (props.GetProp<ArrayProperty<ObjectProperty>>("ObjList")?.Select(o => o.Value) ?? []).Append(props.GetProp<ObjectProperty>("ObjValue")?.Value ?? 0);
            foreach (var uIndex in objects.Where(o => o > 0)) held.TryAdd(uIndex, variable.InstancedFullPath);
        }
        return held;
    }

    /// <summary>
    /// The largest half-extent of the actor's meshes at this pose: their world boxes, scaled and turned, as the engine's
    /// Bounds.BoxExtent the bridge measures in game; 0 with none.
    /// </summary>
    internal static float Extent(IMEPackage pcc, ExportEntry actor, Pose pose) =>
        Primitives(actor).Select(c => (Component: c, Bounds: Bounds(pcc, c)))
            .Where(b => b.Bounds is not null)
            .Select(b => WorldBox(b.Bounds!.Value, TotalScale(b.Component, pose), pose).Half)
            .Select(e => MathF.Max(e.X, MathF.Max(e.Y, e.Z)))
            .DefaultIfEmpty(0).Max();

    internal static Pose PoseOf(ExportEntry actor)
    {
        var props = LevelBuilder.Effective(actor);
        return new Pose(
            props.GetProp<StructProperty>("Location") is { } l ? CommonStructs.GetVector3(l) : Vector3.Zero,
            props.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : new Rotator(0, 0, 0),
            props.GetProp<FloatProperty>("DrawScale")?.Value ?? 1,
            props.GetProp<StructProperty>("DrawScale3D") is { } s ? CommonStructs.GetVector3(s) : Vector3.One);
    }

    /// <summary>The StaticMesh the actor shows (a StaticMeshActor's, a BioInert's placeable mesh), or an emitter's ParticleSystem (its template); null when none.</summary>
    internal static IEntry? MeshOf(ExportEntry actor) =>
        Primitives(actor).Select(c => c.IsA("StaticMeshComponent") ? LevelBuilder.Effective(c).GetProp<ObjectProperty>("StaticMesh")?.ResolveToEntry(actor.FileRef)
                : c.IsA("ParticleSystemComponent") ? LevelBuilder.Effective(c).GetProp<ObjectProperty>("Template")?.ResolveToEntry(actor.FileRef)
                : null)
            .FirstOrDefault(m => m is not null);

    /// <summary>The actor's components that draw or collide (its subobjects).</summary>
    private static IEnumerable<ExportEntry> Primitives(ExportEntry actor) => actor.GetChildren().OfType<ExportEntry>().Where(c => c.IsA("PrimitiveComponent"));

    private static IEnumerable<ExportEntry> Archetypes(ExportEntry export)
    {
        for (var a = export.Archetype as ExportEntry; a is not null; a = a.Archetype as ExportEntry) yield return a;
    }

    private static ExportEntry? Mesh(IMEPackage pcc, ExportEntry component) =>
        LevelBuilder.Effective(component).GetProp<ObjectProperty>(component.IsA("SkeletalMeshComponent") ? "SkeletalMesh" : "StaticMesh")?.ResolveToEntry(pcc) as ExportEntry;

    /// <summary>The component's mesh's bounds (local, unscaled); null when the mesh is not in the file.</summary>
    private static BoxSphereBounds? Bounds(IMEPackage pcc, ExportEntry component) => Mesh(pcc, component) switch
    {
        { ClassName: "StaticMesh" } m => ObjectBinary.From<StaticMesh>(m).Bounds,
        { ClassName: "SkeletalMesh" } m => ObjectBinary.From<SkeletalMesh>(m).Bounds,
        _ => null,
    };

    /// <summary>DrawScale × DrawScale3D × the component's Scale × Scale3D (through its archetypes): the scale its collision is cooked for.</summary>
    internal static Vector3 TotalScale(ExportEntry component, Pose pose)
    {
        var props = LevelBuilder.Effective(component);
        var scale3d = props.GetProp<StructProperty>("Scale3D") is { } s ? CommonStructs.GetVector3(s) : Vector3.One;
        return pose.Scale3D * pose.Scale * (props.GetProp<FloatProperty>("Scale")?.Value ?? 1) * scale3d;
    }

    /// <summary>A cover link of the file whose place or one of whose slots is within <see cref="CoverReach"/> of the actor's box where it stands in vanilla.</summary>
    private static ExportEntry? CoverNear(IMEPackage pcc, ExportEntry actor, Pose pose)
    {
        var boxes = Primitives(actor).Select(c => (Component: c, Bounds: Bounds(pcc, c))).Where(b => b.Bounds is not null)
            .Select(b => WorldBox(b.Bounds!.Value, TotalScale(b.Component, pose), pose)).ToList();
        if (boxes.Count == 0) boxes.Add((pose.Location, Vector3.Zero));
        var level = pcc.FindExport(LevelPath);
        foreach (var link in pcc.Exports.Where(e => e.Parent == level && e.IsA("CoverLink")))
        {
            var props = LevelBuilder.Effective(link);
            if (props.GetProp<StructProperty>("Location") is not { } l) continue;
            var at = CommonStructs.GetVector3(l);
            var turned = props.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : new Rotator(0, 0, 0);
            var slots = props.GetProp<ArrayProperty<StructProperty>>("Slots")?.Select(s => s.GetProp<StructProperty>("LocationOffset")).OfType<StructProperty>() ?? [];
            var points = slots.Select(o => at + Rotate(turned, CommonStructs.GetVector3(o))).Prepend(at);
            if (points.Any(p => boxes.Any(b => Distance(p, b) <= CoverReach))) return link;
        }
        return null;
    }

    /// <summary>A mesh's bounds scaled, turned and placed: the world box around it (centre, half-extent).</summary>
    internal static (Vector3 Centre, Vector3 Half) WorldBox(BoxSphereBounds bounds, Vector3 scale, Pose pose)
    {
        var (x, y, z) = Axes(pose.Rotation);
        var e = bounds.BoxExtent * scale;
        var half = Vector3.Abs(x) * MathF.Abs(e.X) + Vector3.Abs(y) * MathF.Abs(e.Y) + Vector3.Abs(z) * MathF.Abs(e.Z);
        return (pose.Location + Rotate(pose.Rotation, bounds.Origin * scale), half);
    }

    private static float Distance(Vector3 point, (Vector3 Centre, Vector3 Half) box) =>
        Vector3.Max(Vector3.Abs(point - box.Centre) - box.Half, Vector3.Zero).Length();

    private static Vector3 Rotate(Rotator r, Vector3 v)
    {
        var (x, y, z) = Axes(r);
        return x * v.X + y * v.Y + z * v.Z;
    }

    private static (Vector3 X, Vector3 Y, Vector3 Z) Axes(Rotator r)
    {
        var axes = Rotations.Axes([r.Pitch, r.Yaw, r.Roll]);
        static Vector3 V((double X, double Y, double Z) a) => new((float)a.X, (float)a.Y, (float)a.Z);
        return (V(axes.X), V(axes.Y), V(axes.Z));
    }

    private static bool Near(Vector3 a, Vector3 b) => MathF.Abs(a.X - b.X) <= ScaleNear && MathF.Abs(a.Y - b.Y) <= ScaleNear && MathF.Abs(a.Z - b.Z) <= ScaleNear;

    /// <summary>A GUID from the MD5 of the fork's name (upper case: names are not case-sensitive).</summary>
    private static Guid GuidOf(string fork) => new(MD5.HashData(Encoding.UTF8.GetBytes(fork.ToUpperInvariant())));

    private static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    // ---- How values read in a receipt or a comment ----

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static string Number(double value) => value.ToString("0.####", Inv);

    internal static string Location(Vector3 v) => $"({string.Join(", ", new[] { v.X, v.Y, v.Z }.Select(f => Math.Round(f, 2).ToString("0.##", Inv)))})";

    /// <summary>"(old) → (new)" to 2 decimals, or in full when those read the same: a move under 0.005 units still shows what moved.</summary>
    internal static string Moved(Vector3 from, Vector3 to)
    {
        static string Exact(Vector3 v) => $"({string.Join(", ", new[] { v.X, v.Y, v.Z }.Select(f => f.ToString("R", Inv)))})";
        var (a, b) = (Location(from), Location(to));
        return a == b && from != to ? $"{Exact(from)} → {Exact(to)}" : $"{a} → {b}";
    }

    internal static string Rotation(Rotator r) => $"({string.Join(", ", new[] { r.Pitch, r.Yaw, r.Roll }.Select(u => Rotations.Angle(Rotations.Degrees(u)).ToString("0.##", Inv)))})";

    internal static string Scale3D(Vector3 v) => $"({Number(v.X)}, {Number(v.Y)}, {Number(v.Z)})";
}
