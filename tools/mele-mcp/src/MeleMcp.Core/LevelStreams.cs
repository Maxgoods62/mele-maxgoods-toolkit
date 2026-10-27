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
    /// <param name="Levels">Files shown inside the volume, by name without extension, e.g. BIOA_NOR10_09_LAY, or its fork CLD00_NOR10_09_LAY.</param>
    internal sealed record Spec(string Package, string Volume, List<string> Levels, int Line);

    /// <param name="Yaw">Degrees: 0 faces +X, 90 faces +Y.</param>
    internal sealed record StartPoint(string Tag, Vector3 Location, float? Yaw, int Line);

    /// <param name="others">For a file the level may have streamed under another name, that name: a vanilla file's fork, or a fork's
    /// vanilla file (docs/07 §11). Its streaming entry is renamed in place, since entries are only ever added: a second one would
    /// stay in StreamingLevels and stream both.</param>
    internal static void SetStreaming(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, IReadOnlyDictionary<string, string> others, PackageCache cache, List<string> changes)
    {
        var world = pcc.FindExport("TheWorld") ?? throw new ArgumentException($"line {spec.Line}: the level has no TheWorld.");
        var worldInfo = WorldInfo(pcc, level);
        var listed = worldInfo.GetProperty<ArrayProperty<ObjectProperty>>("StreamingLevels") ?? new ArrayProperty<ObjectProperty>("StreamingLevels");
        var listDirty = false;
        ExportEntry? Streaming(string name) => pcc.Exports.FirstOrDefault(e => e.Parent == world && e.ClassName == "LevelStreamingKismet"
                                                                              && e.GetProperty<NameProperty>("PackageName")?.Value.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
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
    }

    /// <summary>
    /// Renames, in every SeqAct_WaitForLevelsVisible of the level, a name it no longer streams under (the other name of a pair:
    /// <paramref name="others"/>, as <see cref="SetStreaming"/> takes it) to the one it streams now. A wait looks its levels up by
    /// name: one the level no longer streams never shows, and the wait (the squad's, docs/07 §11) never finishes. The squad's gets
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
    /// _DSG (scripts, enemies) has none.
    /// </summary>
    internal static void SetLight(MEGame game, IMEPackage pcc, ExportEntry level, string package, string actor, int line, List<string> changes)
    {
        // Found again by its name: an import keeps the source's.
        var name = actor.Contains('.') ? actor[(actor.LastIndexOf('.') + 1)..] : actor;
        if (pcc.FindExport($"{LevelPath}.{name}") is { } existing)
        {
            if (!existing.ClassName.Contains("Light")) throw new ArgumentException($"line {line}: the level's {existing.InstancedFullPath} is a {existing.ClassName}, not a light.");
            return;
        }
        using var source = PackageInspector.Open(game, package);
        var original = Resolve(source, actor, line);
        if (!original.ClassName.Contains("Light")) throw new ArgumentException($"line {line}: {original.InstancedFullPath} is a {original.ClassName}, not a light.");
        Import(pcc, level, original);
        changes.Add($"lights (copy of {package} {original.ObjectName.Instanced})");
    }

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
