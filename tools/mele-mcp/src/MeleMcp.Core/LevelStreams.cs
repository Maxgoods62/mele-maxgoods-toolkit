using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MeleMcp.Core;

/// <summary>
/// What makes a level of its own a place to go: vanilla sub-levels it shows (a Normandy room, by its _ART and _LAY files),
/// its post-process, character lights, and start points. Game 1 shows sub-levels the BioWare way: BioWorldInfo.StreamingLevels lists them
/// (LevelStreamingKismet), and a BioTriggerStream volume makes them visible while the player is inside it. The volume is a
/// copy of the vanilla one around the same room, so it has the room's place and shape.
/// </summary>
internal static class LevelStreams
{
    private const string LevelPath = "TheWorld.PersistentLevel";

    /// <param name="Volume">The vanilla BioTriggerStream to copy, e.g. the cabin's on the Normandy.</param>
    /// <param name="Levels">Files shown inside the volume, by name without extension, e.g. BIOA_NOR10_09_LAY, or its fork MYM00_NOR10_09_LAY.</param>
    internal sealed record Spec(string Package, string Volume, List<string> Levels, int Line);

    /// <param name="Yaw">Degrees: 0 faces +X, 90 faces +Y.</param>
    internal sealed record StartPoint(string Tag, Vector3 Location, float? Yaw, int Line);

    /// <param name="others">For a file the level may have streamed under another name, that name: a vanilla file's fork, or a fork's
    /// vanilla file (docs/design/in-game-editor.md §11). Its streaming entry is renamed in place, since entries are only ever added: a second one would
    /// stay in StreamingLevels and stream both.</param>
    /// <returns>
    /// The vanilla files that left <see cref="Spec.Levels"/> while the level streamed their fork: their entries name the vanilla file
    /// again, so the caller deletes the fork once the level is saved.
    /// </returns>
    internal static List<string> SetStreaming(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, IReadOnlyDictionary<string, string> others, PackageCache cache, List<string> changes)
    {
        var world = pcc.FindExport("TheWorld") ?? throw new ArgumentException($"line {spec.Line}: the level has no TheWorld.");
        var worldInfo = WorldInfo(pcc, level);
        var listed = worldInfo.GetProperty<ArrayProperty<ObjectProperty>>("StreamingLevels") ?? new ArrayProperty<ObjectProperty>("StreamingLevels");
        var listDirty = false;
        ExportEntry? Streaming(string name) => pcc.Exports.FirstOrDefault(e => e.Parent == world && e.ClassName == "LevelStreamingKismet"
                                                                              && e.GetProperty<NameProperty>("PackageName")?.Value.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        // Read before the list changes: a volume an earlier build copied shows only names the level lists (a changed 'volume', below).
        var listedNames = Names(pcc, listed);
        var builtVolumes = pcc.Exports.Where(e => e.Parent == level && e.ClassName == "BioTriggerStream" && IsBuiltVolume(e, listedNames)).ToList();
        foreach (var name in spec.Levels)
        {
            var streaming = Streaming(name);
            if (others.TryGetValue(name, out var otherName) && Streaming(otherName) is { } other)
            {
                if (streaming is null)
                {
                    other.WriteProperty(new NameProperty(name, "PackageName"));
                    changes.Add($"streams {name} instead of {otherName}");
                    streaming = other;
                }
                else if (listed.Where(o => o.Value == other.UIndex).ToList() is { Count: > 0 } stale)
                {
                    foreach (var entry in stale) listed.Remove(entry);
                    listDirty = true;
                    changes.Add($"no longer streams {otherName} (streams {name})");
                }
            }
            if (streaming is null)
            {
                streaming = pcc.CreateExport("LevelStreamingKismet", "LevelStreamingKismet", world, cache: cache);
                streaming.WriteProperty(new NameProperty(name, "PackageName"));
                changes.Add($"streams {name}");
            }
            if (listed.Any(o => o.Value == streaming.UIndex)) continue;
            listed.Add(new ObjectProperty(streaming));
            listDirty = true;
        }

        var dropped = DropForks(game, pcc, listed, spec.Levels, Streaming, vanilla => $"{vanilla} left streaming.levels", changes, ref listDirty);
        if (listDirty) worldInfo.WriteProperty(listed);

        // Found again by its name: an import keeps the source's.
        var volumeName = spec.Volume.Contains('.') ? spec.Volume[(spec.Volume.LastIndexOf('.') + 1)..] : spec.Volume;
        var volume = pcc.FindExport($"{LevelPath}.{volumeName}");
        if (volume is null)
        {
            using var source = PackageInspector.Open(game, spec.Package);
            var original = Resolve(source, spec.Volume, spec.Line);
            if (original.ClassName != "BioTriggerStream") throw new ArgumentException($"line {spec.Line}: {original.InstancedFullPath} is a {original.ClassName}, not a BioTriggerStream.");
            volume = Import(pcc, level, original);
            changes.Add($"stream volume (copy of {spec.Package} {original.ObjectName.Instanced})");
            // A changed 'volume': the copy an earlier build made would go on showing its levels beside this one, in its own room.
            foreach (var old in builtVolumes)
            {
                changes.Add($"stream volume {old.ObjectName.Instanced} removed (an earlier build's copy: the file's volume is {volume.ObjectName.Instanced} now)");
                pcc.RemoveFromLevelActors(old);
                EntryPruner.TrashEntryAndDescendants(old);
            }
        }
        else if (volume.ClassName != "BioTriggerStream")
        {
            throw new ArgumentException($"line {spec.Line}: the level's {volume.InstancedFullPath} is a {volume.ClassName}, not a BioTriggerStream.");
        }

        // One state, the one a volume applies when the player enters it. Its InChunkName is the chunk (files by name prefix)
        // the volume is in, which every vanilla volume names: pawns that touch the volume go through its stasis check
        // (BioTriggerStream.DoStasis), and without it they stay in stasis, not ticking: no AI, no light update.
        var visible = new ArrayProperty<NameProperty>(spec.Levels.Select(l => new NameProperty(l)), "VisibleChunkNames");
        var states = new ArrayProperty<StructProperty>("StreamingStates")
        {
            new("BioStreamingState", [visible, new NameProperty("Default", "StateName"), new NameProperty(spec.Levels[0], "InChunkName")]),
        };
        var props = volume.GetProperties();
        props.AddOrReplaceProp(states);
        if (Write(volume, props)) changes.Add($"stream volume shows {string.Join(", ", spec.Levels)}");

        // The shown files bring their path network (PathNodes, ReachSpecs). Every vanilla persistent level says it has one,
        // even with no node of its own (BIOA_NOR00, BIOA_FRE31).
        var infoProps = worldInfo.GetProperties();
        infoProps.AddOrReplaceProp(new BoolProperty(true, "bPathsRebuilt"));
        infoProps.AddOrReplaceProp(new BoolProperty(true, "bHasPathNodes"));
        if (Write(worldInfo, infoProps)) changes.Add("has a path network (bPathsRebuilt, bHasPathNodes)");
        return dropped;
    }

    /// <summary>
    /// A YAML with no 'streaming' section (any more): every fork an earlier build streamed goes back to its vanilla name, as for a file
    /// that left streaming.levels (<see cref="SetStreaming"/>). The list and the volumes stay as they are. Returns those vanilla files,
    /// so the caller deletes their forks once the level is saved.
    /// </summary>
    internal static List<string> DropAllForks(MEGame game, IMEPackage pcc, ExportEntry level, List<string> changes)
    {
        if (pcc.FindExport("TheWorld") is not { } world
            || pcc.Exports.FirstOrDefault(e => e.Parent == level && e.ClassName == "BioWorldInfo") is not { } worldInfo
            || worldInfo.GetProperty<ArrayProperty<ObjectProperty>>("StreamingLevels") is not { Count: > 0 } listed) return [];
        ExportEntry? Streaming(string name) => pcc.Exports.FirstOrDefault(e => e.Parent == world && e.ClassName == "LevelStreamingKismet"
                                                                              && e.GetProperty<NameProperty>("PackageName")?.Value.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        var listDirty = false;
        var dropped = DropForks(game, pcc, listed, [], Streaming, _ => "the YAML has no streaming section", changes, ref listDirty);
        if (listDirty) worldInfo.WriteProperty(listed);
        return dropped;
    }

    /// <summary>
    /// A file off <paramref name="keep"/> while the level streamed its fork: its entry stays in StreamingLevels, as any file's that left
    /// (no volume shows it), under the vanilla name again, so the map no longer names the fork and the caller deletes it. Only a name
    /// that is this map's fork of a file the game loads counts: a sub-level of the mod's own is never taken for one.
    /// </summary>
    private static List<string> DropForks(MEGame game, IMEPackage pcc, ArrayProperty<ObjectProperty> listed, IReadOnlyCollection<string> keep,
        Func<string, ExportEntry?> streaming, Func<string, string> why, List<string> changes, ref bool listDirty)
    {
        var map = Path.GetFileNameWithoutExtension(pcc.FilePath);
        var dropped = new List<string>();
        foreach (var entry in listed.Select(o => o.ResolveToEntry(pcc)).OfType<ExportEntry>().Where(e => e.ClassName == "LevelStreamingKismet").Distinct().ToList())
        {
            if (entry.GetProperty<NameProperty>("PackageName")?.Value.Instanced is not { } name || keep.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var vanilla = ForkBuilder.Canonical(map, name);
            if (vanilla.Equals(name, StringComparison.OrdinalIgnoreCase) || !ForkBuilder.ForkName(map, vanilla).Equals(name, StringComparison.OrdinalIgnoreCase)
                || !LoadsVanilla(game, vanilla, pcc.FilePath)) continue;
            if (streaming(vanilla) is { } own)
            {
                // Both names have an entry: the fork's leaves the list, and the vanilla file's is on it once.
                foreach (var stale in listed.Where(o => o.Value == entry.UIndex).ToList()) listed.Remove(stale);
                if (!listed.Any(o => o.Value == own.UIndex)) listed.Add(new ObjectProperty(own));
                listDirty = true;
            }
            else entry.WriteProperty(new NameProperty(vanilla, "PackageName"));
            changes.Add($"no longer streams {name} ({why(vanilla)})");
            dropped.Add(vanilla);
        }
        return dropped;
    }

    /// <summary>
    /// The names the level streams: the PackageName of each LevelStreamingKismet its StreamingLevels lists. An entry off the list (a
    /// pair's other name that <see cref="SetStreaming"/> took off it) streams nothing, so it names no file the level needs.
    /// </summary>
    internal static HashSet<string> Streamed(IMEPackage pcc, ExportEntry level) =>
        pcc.Exports.FirstOrDefault(e => e.Parent == level && e.ClassName == "BioWorldInfo")?.GetProperty<ArrayProperty<ObjectProperty>>("StreamingLevels") is { } listed
            ? Names(pcc, listed)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Names(IMEPackage pcc, ArrayProperty<ObjectProperty> listed) =>
        listed.Select(o => o.ResolveToEntry(pcc)).OfType<ExportEntry>().Select(e => e.GetProperty<NameProperty>("PackageName")?.Value.Instanced).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A stream volume in the shape <see cref="SetStreaming"/> writes: one state, Default, with only its VisibleChunkNames and the first
    /// of them as InChunkName, each a name the level lists. Only that shape counts as an earlier build's copy, so a changed 'volume'
    /// never removes a volume the build did not write.
    /// </summary>
    internal static bool IsBuiltVolume(ExportEntry volume, IReadOnlySet<string> listed)
    {
        if (volume.GetProperty<ArrayProperty<StructProperty>>("StreamingStates") is not { Count: 1 } states) return false;
        var state = states[0];
        var visible = state.GetProp<ArrayProperty<NameProperty>>("VisibleChunkNames");
        return visible is { Count: > 0 } && state.Properties.Count(p => p is not NoneProperty) == 3
               && state.GetProp<NameProperty>("StateName")?.Value.Instanced == "Default"
               && state.GetProp<NameProperty>("InChunkName")?.Value.Instanced.Equals(visible[0].Value.Instanced, StringComparison.OrdinalIgnoreCase) == true
               && visible.All(v => listed.Contains(v.Value.Instanced));
    }

    /// <summary>A file the game loads outside the level's own DLC folder: the vanilla file a fork of it copies (<see cref="ForkBuilder.VanillaSource"/>).</summary>
    private static bool LoadsVanilla(MEGame game, string file, string package)
    {
        try
        {
            ForkBuilder.VanillaSource(game, file, ForkBuilder.DlcOf(package));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renames, in every SeqAct_WaitForLevelsVisible of the level, a name it no longer streams under (the other name of a pair:
    /// <paramref name="others"/>, as <see cref="SetStreaming"/> takes it) to the one it streams now. A wait looks its levels up by
    /// name: one the level no longer streams never shows, and the wait (the squad's, docs/design/in-game-editor.md §11) never finishes. The squad's gets
    /// its names from SquadSpawns too, but only while the YAML has 'squad'.
    /// </summary>
    internal static void RenameWaits(IMEPackage pcc, IReadOnlyDictionary<string, string> others, List<string> changes)
    {
        var streamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (now, other) in others) streamed.TryAdd(other, now);
        foreach (var wait in pcc.Exports.Where(e => e.ClassName == "SeqAct_WaitForLevelsVisible"))
        {
            if (wait.GetProperty<ArrayProperty<NameProperty>>("LevelNames") is not { Count: > 0 } names
                || !names.Any(n => streamed.ContainsKey(n.Value.Instanced))) continue;
            var renamed = names.Select(n => streamed.TryGetValue(n.Value.Instanced, out var now) ? new NameProperty(now) : n).ToList();
            wait.WriteProperty(new ArrayProperty<NameProperty>(renamed, "LevelNames"));
            changes.Add($"Kismet: {wait.ObjectName.Instanced} waits for {string.Join(", ", renamed.Select(n => n.Value.Instanced))}");
        }
    }

    /// <summary>
    /// A light actor copied from another level, e.g. the character lights a vanilla map keeps in its _DSG file: lights of
    /// the dynamic channels only, which light pawns and not the walls. A level that shows the map's _LAY file but not its
    /// _DSG (scripts, enemies) has none. Returns the copy, found or made (its lights' vanilla: entries go into it next).
    /// </summary>
    internal static ExportEntry SetLight(MEGame game, IMEPackage pcc, ExportEntry level, string package, string actor, int line, List<string> changes)
    {
        // Found again by its name: an import keeps the source's.
        var name = CopyName(actor);
        if (pcc.FindExport($"{LevelPath}.{name}") is { } existing)
        {
            if (!existing.ClassName.Contains("Light")) throw new ArgumentException($"line {line}: the level's {existing.InstancedFullPath} is a {existing.ClassName}, not a light.");
            return existing;
        }
        using var source = PackageInspector.Open(game, package);
        var original = Resolve(source, actor, line);
        if (!original.ClassName.Contains("Light")) throw new ArgumentException($"line {line}: {original.InstancedFullPath} is a {original.ClassName}, not a light.");
        var copy = Import(pcc, level, original);
        changes.Add($"lights (copy of {package} {original.ObjectName.Instanced})");
        return copy;
    }

    /// <summary>The name a light copied by <see cref="SetLight"/> has in the level: the source actor's own (the last part of its path).</summary>
    internal static string CopyName(string actor) => actor.Contains('.') ? actor[(actor.LastIndexOf('.') + 1)..] : actor;

    /// <summary>The DefaultPostProcessSettings of another level's BioWorldInfo: its color grading, bloom and depth of field.</summary>
    internal static void SetPostProcess(MEGame game, IMEPackage pcc, ExportEntry level, string package, int line, List<string> changes)
    {
        using var source = PackageInspector.Open(game, package);
        var sourceInfo = source.Exports.FirstOrDefault(e => e.ClassName == "BioWorldInfo") ?? throw new ArgumentException($"line {line}: {package} has no BioWorldInfo.");
        var settings = sourceInfo.GetProperty<StructProperty>("DefaultPostProcessSettings") ?? throw new ArgumentException($"line {line}: {package}'s BioWorldInfo has no DefaultPostProcessSettings.");
        var worldInfo = WorldInfo(pcc, level);
        var props = worldInfo.GetProperties();
        props.AddOrReplaceProp(settings);
        if (Write(worldInfo, props)) changes.Add($"post-process of {package}");
    }

    /// <summary>A start point found by tag, else imported from the template; on the level's navigation list, as cooked levels have them.</summary>
    internal static void SetStartPoint(MEGame game, IMEPackage pcc, ExportEntry level, StartPoint start, (string Package, string Actor)? template, List<string> changes)
    {
        var point = pcc.Exports.FirstOrDefault(e => e.Parent == level && e.ClassName == "BioStartPoint"
                                                    && e.GetProperty<NameProperty>("Tag")?.Value.Instanced.Equals(start.Tag, StringComparison.OrdinalIgnoreCase) == true);
        if (point is null)
        {
            var (package, actor) = template ?? throw new ArgumentException($"line {start.Line}: the level has no start point '{start.Tag}'; give a top-level 'startPointTemplate'.");
            using var source = PackageInspector.Open(game, package);
            var original = Resolve(source, actor, start.Line);
            if (original.ClassName != "BioStartPoint") throw new ArgumentException($"line {start.Line}: {original.InstancedFullPath} is a {original.ClassName}, not a BioStartPoint.");
            // Its navigation links are the vanilla level's (the next point of its list, its paths): cut them, or the import
            // would copy the whole list and the path network around it.
            var originalProps = original.GetProperties();
            originalProps.RemoveNamedProperty("nextNavigationPoint");
            originalProps.RemoveNamedProperty("PathList");
            originalProps.RemoveNamedProperty("bHasCrossLevelPaths");
            original.WriteProperties(originalProps);
            original.ObjectName = pcc.GetNextIndexedName("BioStartPoint");
            point = Import(pcc, level, original);
            changes.Add($"start point {start.Tag} added");
        }

        var props = point.GetProperties();
        props.AddOrReplaceProp(new NameProperty(start.Tag, "Tag"));
        // Without its paths it is a dead end, and the henchmen spawned next to it start their paths from it (their anchor,
        // the nearest usable navigation point): "destination blocked". Blocked, it is no anchor, and they start from the shown files' nodes.
        props.AddOrReplaceProp(new BoolProperty(true, "bBlocked"));
        props.AddOrReplaceProp(CommonStructs.Vector3Prop(start.Location, "Location"));
        if (start.Yaw is { } yaw) props.AddOrReplaceProp(CommonStructs.RotatorProp(new Rotator(0, (int)Math.Round(yaw * 65536 / 360), 0), "Rotation"));
        if (Write(point, props)) changes.Add($"start point {start.Tag}");

        if (AddToNavigationList(pcc, level, point)) changes.Add($"start point {start.Tag} on the navigation list");
    }

    /// <summary>Navigation points chain through nextNavigationPoint, from the level's NavListStart to its NavListEnd.</summary>
    private static bool AddToNavigationList(IMEPackage pcc, ExportEntry level, ExportEntry point)
    {
        var binary = ObjectBinary.From<Level>(level);
        var seen = new HashSet<int>();
        for (var at = binary.NavListStart; at != 0 && seen.Add(at); at = pcc.GetUExport(at).GetProperty<ObjectProperty>("nextNavigationPoint")?.Value ?? 0)
        {
            if (at == point.UIndex) return false;
        }
        if (binary.NavListEnd != 0) pcc.GetUExport(binary.NavListEnd).WriteProperty(new ObjectProperty(point, "nextNavigationPoint"));
        else binary.NavListStart = point.UIndex;
        binary.NavListEnd = point.UIndex;
        level.WriteBinary(binary);
        return true;
    }

    private static ExportEntry Import(IMEPackage pcc, ExportEntry level, ExportEntry original)
    {
        if (pcc.FindEntry($"{LevelPath}.{original.ObjectName.Instanced}") is { } clash)
        {
            throw new InvalidOperationException($"The level already has {clash.InstancedFullPath}, so an import would link to it instead of copying.");
        }
        var rop = new RelinkerOptionsPackage { Cache = new PackageCache() };
        EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, original, pcc, level, true, rop, out var entry);
        var copy = entry as ExportEntry ?? throw new InvalidOperationException("The import did not create an export.");
        pcc.AddToLevelActorsIfNotThere(copy);
        return copy;
    }

    private static ExportEntry WorldInfo(IMEPackage pcc, ExportEntry level) =>
        pcc.Exports.FirstOrDefault(e => e.Parent == level && e.ClassName == "BioWorldInfo") ?? throw new ArgumentException($"{Path.GetFileName(pcc.FilePath)} has no BioWorldInfo.");

    /// <summary>BioStartPoint_23, TheWorld.PersistentLevel.BioStartPoint_23 or #216.</summary>
    private static ExportEntry Resolve(IMEPackage pcc, string actor, int line)
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

    /// <summary>Writes the properties; true when that changed the export's bytes.</summary>
    private static bool Write(ExportEntry export, PropertyCollection props)
    {
        var before = export.Data;
        export.WriteProperties(props);
        return !export.DataReadOnly.SequenceEqual(before);
    }
}
