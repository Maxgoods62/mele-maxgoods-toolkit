using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core;

/// <summary>
/// The point and spot lights of a level's light collections (StaticLightCollectionActor, SLCA; docs/design/in-game-editor.md §12.4): their fields as the
/// game loads them (through their archetypes: a prefab's, then the class defaults), their light type, where they stand, and what a
/// fork of their file or the map's characterLights copy writes into them. A light stands at its Translation taken through the
/// SLCA's binary matrix at its index in LightComponents, the parent UE3 gives it at load; LE1 applies a written Translation on top
/// of that matrix (spike S3), so a move never touches the binary. Only brightness, colour, radius, falloff, on/off and Translation
/// are ever written: never the GUIDs (lightmaps name lights by LightmapGuid), the built flags, UseDirectLightMap, channels,
/// bForceDynamicLight, shadows or the level filter.
/// </summary>
internal static class SlcaLights
{
    /// <summary>A parent matrix whose row lengths are off 1 by more than this scales the light's Translation: no move is written under it.</summary>
    private const float ScaleNear = 1e-4f;

    /// <summary>Channels a light of type C or D may have; any other one (Unnamed_*, Gameplay_*...) makes it a U.</summary>
    private static readonly string[] BaseChannels = ["BSP", "Static", "Dynamic", "CompositeDynamic"];

    /// <summary>What a vanilla: light entry gives its light (colour [r, g, b]); a field left out keeps the file's value.</summary>
    internal sealed record Edit(float? Radius, float? Brightness, byte[]? Color, float? Falloff, bool? Enabled)
    {
        public static readonly Edit None = new(null, null, null, null, null);
    }

    /// <summary>
    /// A light as the game loads it: where it stands, its fields (colour [r, g, b] and its alpha apart), its light type (B baked, C
    /// characters only, U special channel, D dynamic), the channels that make it a U, and its parent: the SLCA's binary matrix.
    /// </summary>
    internal sealed record State(Vector3 Location, float Radius, float Brightness, byte[] Color, byte Alpha, float Falloff, bool Enabled, char Type,
        IReadOnlyList<string> Special, Matrix4x4 Parent);

    /// <summary>Why a component may not change, or null: only point and spot lights (spots are point lights) of a StaticLightCollectionActor.</summary>
    internal static string? Refusal(ExportEntry component) =>
        component.IsA("DirectionalLightComponent") ? $"a {component.ClassName}: a sun light, not edited in Phase 5"
        : component.IsA("SkyLightComponent") ? $"a {component.ClassName}: a sky light, not edited in Phase 5"
        : !component.IsA("PointLightComponent") ? $"a {component.ClassName}: only point and spot lights of a light collection may change"
        : component.Parent is not ExportEntry { ClassName: "StaticLightCollectionActor" } ? $"a {component.ClassName} of no light collection (StaticLightCollectionActor)"
        : null;

    /// <summary>The light as its file gives it, through archetypes resolved in <paramref name="cache"/>'s files; null when its collection has no matrix for it.</summary>
    internal static State? Read(ExportEntry light, PackageCache cache)
    {
        if (!StaticCollectionActor.TryGetStaticCollectionActorAndIndex(light, out var collection, out var index)) return null;
        var parent = collection.LocalToWorldTransforms[index];
        var props = Resolved(light, cache);
        var translation = props.GetProp<StructProperty>("Translation") is { } t ? CommonStructs.GetVector3(t) : Vector3.Zero;
        var color = props.GetProp<StructProperty>("LightColor");
        byte Channel(string name) => color?.GetProp<ByteProperty>(name)?.Value ?? 255;
        var channels = Channels(props);
        return new State(Vector3.Transform(translation, parent), props.GetProp<FloatProperty>("Radius")?.Value ?? 1024, props.GetProp<FloatProperty>("Brightness")?.Value ?? 1,
            [Channel("R"), Channel("G"), Channel("B")], color is null ? (byte)0 : Channel("A"), props.GetProp<FloatProperty>("FalloffExponent")?.Value ?? 2,
            props.GetProp<BoolProperty>("bEnabled")?.Value ?? true, TypeOf(props, channels), channels.Where(c => !BaseChannels.Contains(c)).ToList(), parent);
    }

    /// <summary>
    /// The light type (docs/design/in-game-editor.md §12.4), as MeleBridge reads it from the live light, in this order: B when built into a lightmap and
    /// UseDirectLightMap; else D with bForceDynamicLight or the Dynamic channel; else U with any channel outside BSP, Static and
    /// CompositeDynamic; else C.
    /// </summary>
    private static char TypeOf(PropertyCollection props, IReadOnlyList<string> channels)
    {
        bool Flag(string name) => props.GetProp<BoolProperty>(name)?.Value == true;
        if (Flag("bHasLightEverBeenBuiltIntoLightMap") && Flag("UseDirectLightMap")) return 'B';
        if (Flag("bForceDynamicLight") || channels.Contains("Dynamic")) return 'D';
        return channels.Any(c => !BaseChannels.Contains(c)) ? 'U' : 'C';
    }

    /// <summary>
    /// The channels a light is on (bInitialized is no channel): a field LightingChannels leaves out is the class default's
    /// (LightComponent: BSP, Static, Dynamic and CompositeDynamic on), which the archetype chain of a file never reaches.
    /// </summary>
    private static List<string> Channels(PropertyCollection props)
    {
        var on = new List<string>(BaseChannels);
        foreach (var field in props.GetProp<StructProperty>("LightingChannels")?.Properties.OfType<BoolProperty>() ?? [])
        {
            if (field.Name.Instanced == "bInitialized") continue;
            on.Remove(field.Name.Instanced);
            if (field.Value) on.Add(field.Name.Instanced);
        }
        return on;
    }

    /// <summary>
    /// An export's properties as the engine loads them: its own over its archetypes' (a prefab's, then the class default objects',
    /// whose imports resolve in <paramref name="cache"/>'s files), a struct field by field (a field a struct leaves out is its
    /// archetype's: LightingChannels { Unnamed_6 } keeps the others). Without a cache, archetypes of the file only.
    /// </summary>
    internal static PropertyCollection Resolved(ExportEntry export, PackageCache? cache)
    {
        var chain = new List<ExportEntry>();
        for (IEntry? at = export; at is not null && chain.Count < 32;)
        {
            var next = at as ExportEntry ?? (cache is not null && at is ImportEntry import ? EntryImporter.ResolveImport(import, cache) : null);
            if (next is null) break;
            chain.Add(next);
            at = next.Archetype;
        }
        var props = new PropertyCollection();
        foreach (var layer in Enumerable.Reverse(chain)) Over(props, layer.GetProperties());
        return props;
    }

    private static void Over(PropertyCollection under, PropertyCollection over)
    {
        foreach (var p in over.Where(p => p is not NoneProperty))
        {
            if (p is StructProperty s && under.GetProp<StructProperty>(p.Name) is { } old && old.StructType == s.StructType)
            {
                var fields = new PropertyCollection();
                Over(fields, old.Properties);
                Over(fields, s.Properties);
                under.AddOrReplaceProp(new StructProperty(s.StructType, fields, s.Name, s.IsImmutable));
            }
            else under.AddOrReplaceProp(p);
        }
    }

    /// <summary>Its parent's row lengths when one is off 1 (a scaled parent scales Translation); null when it is not scaled.</summary>
    private static float[]? Scaled(Matrix4x4 m)
    {
        float[] rows = [new Vector3(m.M11, m.M12, m.M13).Length(), new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()];
        return rows.Any(r => MathF.Abs(r - 1) > ScaleNear) ? rows : null;
    }

    /// <summary>The Translation that puts a light at <paramref name="world"/>: M⁻¹·(world − origin), the full 3×3 inverse of its parent.</summary>
    private static Vector3 TranslationAt(Matrix4x4 parent, Vector3 world)
    {
        var turn = parent with { M41 = 0, M42 = 0, M43 = 0 };
        if (!Matrix4x4.Invert(turn, out var inverse)) throw new InvalidOperationException("the light's parent matrix has no inverse.");
        return Vector3.TransformNormal(world - parent.Translation, inverse);
    }

    /// <summary>
    /// Writes an entry's fields into the light, each only when given: Radius, Brightness, LightColor (RGB; the light's alpha kept),
    /// FalloffExponent, bEnabled, and a location as Translation (<see cref="TranslationAt"/>). With <paramref name="source"/> (a
    /// characterLights copy's source light), a field the entry leaves out gets the source's own value again, or none when it has
    /// none (the class default's). Returns why it can't (a location under a scaled parent), or null.
    /// </summary>
    internal static string? Write(ExportEntry light, State state, Edit edit, Vector3? location, PropertyCollection? source = null)
    {
        Vector3? translation = null;
        if (location is { } at)
        {
            if (Scaled(state.Parent) is { } rows) return $"not moved: its light collection scales it (row lengths {string.Join(", ", rows.Select(r => ForkBuilder.Number(r)))})";
            translation = TranslationAt(state.Parent, at);
        }
        var props = light.GetProperties();
        Put(props, "Radius", edit.Radius is { } radius ? new FloatProperty(radius, "Radius") : null, source);
        Put(props, "Brightness", edit.Brightness is { } brightness ? new FloatProperty(brightness, "Brightness") : null, source);
        Put(props, "LightColor", edit.Color is { } c ? CommonStructs.ColorProp(System.Drawing.Color.FromArgb(state.Alpha, c[0], c[1], c[2]), "LightColor") : null, source);
        Put(props, "FalloffExponent", edit.Falloff is { } falloff ? new FloatProperty(falloff, "FalloffExponent") : null, source);
        Put(props, "bEnabled", edit.Enabled is { } enabled ? new BoolProperty(enabled, "bEnabled") : null, source);
        Put(props, "Translation", translation is { } t ? CommonStructs.Vector3Prop(t, "Translation") : null, source);
        light.WriteProperties(props);
        return null;
    }

    private static void Put(PropertyCollection props, string name, Property? value, PropertyCollection? source)
    {
        if (value is not null) props.AddOrReplaceProp(value);
        else if (source?.GetProp<Property>(name) is { } own) props.AddOrReplaceProp(own);
        else if (source is not null) props.RemoveNamedProperty(name);
    }

    /// <summary>
    /// The map's characterLights copy of a light collection (docs/design/in-game-editor.md §12.4): each point and spot light of it gets each field its
    /// vanilla: entry gives (keyed by the source file: <paramref name="entries"/>), else the source file's own value again, so a
    /// deleted entry gives the source's back. A location is written through the copy's own binary matrix. A receipt line per light
    /// changed goes to <paramref name="changes"/>; a U light's names what it lights in the files the map streams (<paramref name="streams"/>,
    /// their vanilla copies, read only for it).
    /// </summary>
    internal static void SetCopy(MEGame game, ExportEntry copy, string package, string actor, IReadOnlyList<ForkBuilder.Entry> entries, PackageCache cache,
        IReadOnlyList<string> streams, string? ownDlc, List<string> changes)
    {
        var open = new List<IMEPackage>();
        try
        {
            SetCopy(game, copy, package, actor, entries, cache, changes, () =>
            {
                if (open.Count > 0) return open;
                foreach (var file in streams)
                {
                    try
                    {
                        open.Add(PackageInspector.Open(game, ForkBuilder.VanillaSource(game, file, ownDlc)));
                    }
                    catch (FileNotFoundException)
                    {
                        // Not a vanilla file: nothing of it to name.
                    }
                }
                return open;
            });
        }
        finally
        {
            foreach (var pcc in open) pcc.Dispose();
        }
    }

    private static void SetCopy(MEGame game, ExportEntry copy, string package, string actor, IReadOnlyList<ForkBuilder.Entry> entries, PackageCache cache, List<string> changes,
        Func<IEnumerable<IMEPackage>> shown)
    {
        using var source = PackageInspector.Open(game, package);
        var original = ExportInspector.Resolve(source, actor.Contains('.') || actor.StartsWith('#') ? actor : $"TheWorld.PersistentLevel.{actor}");
        var lights = (copy.GetProperty<ArrayProperty<ObjectProperty>>("LightComponents")?.Select(o => o.ResolveToEntry(copy.FileRef)).OfType<ExportEntry>() ?? []).ToList();
        foreach (var entry in entries)
        {
            var light = lights.FirstOrDefault(l => l.ObjectName.Instanced.Equals(entry.Component, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"line {entry.Line}: {entry.Key}: {copy.ObjectName.Instanced} (the characterLights copy of {package}) has no light {entry.Component}.");
            if (Refusal(light) is { } why) throw new ArgumentException($"line {entry.Line}: {entry.Key}: {why}.");
        }
        foreach (var light in lights.Where(l => Refusal(l) is null))
        {
            var name = light.ObjectName.Instanced;
            if (source.FindExport($"{original.InstancedFullPath}.{name}") is not { } from || Read(light, cache) is not { } before) continue;
            var entry = entries.FirstOrDefault(e => e.Component.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (Write(light, before, entry?.Light ?? Edit.None, entry?.Location, from.GetProperties()) is { } why) throw new ArgumentException($"line {entry!.Line}: {entry.Key}: {why}.");
            if (Receipt(name, before, Read(light, cache)!, shown) is { } receipt) changes.Add(receipt);
        }
    }

    /// <summary>
    /// What a light is, as a receipt or a comment names it: "B baked", "C character light", "U door light" (its channel lights only
    /// doors of <paramref name="shown"/>), "U special-channel light", "D dynamic light".
    /// </summary>
    internal static string Label(State light, Func<IEnumerable<IMEPackage>> shown) => light.Type switch
    {
        'B' => "B baked",
        'C' => "C character light",
        'U' => Reach(shown(), light.Special).Doors ? "U door light" : "U special-channel light",
        _ => "D dynamic light",
    };

    /// <summary>
    /// The receipt of a light the build changed, or null when it didn't: "PointLight_35_LC (U door light): brightness 0.6 → 1.2, location
    /// (352.55, -11923.24, -222.2) → (552.55, -11923.24, -222.2)"; a baked light switched off "switched off, characters and props only
    /// (the walls stay lit)".
    /// </summary>
    internal static string? Receipt(string name, State before, State after, Func<IEnumerable<IMEPackage>> shown)
    {
        var changed = new List<string>();
        var baked = after.Type == 'B';
        if (before.Enabled != after.Enabled)
        {
            changed.Add(after.Enabled ? baked ? "switched on, characters and props only (the walls keep their bake)" : "switched on"
                : baked ? "switched off, characters and props only (the walls stay lit)" : "switched off");
        }
        if (before.Radius != after.Radius) changed.Add($"radius {ForkBuilder.Number(before.Radius)} → {ForkBuilder.Number(after.Radius)}");
        if (before.Brightness != after.Brightness) changed.Add($"brightness {ForkBuilder.Number(before.Brightness)} → {ForkBuilder.Number(after.Brightness)}");
        if (!before.Color.SequenceEqual(after.Color)) changed.Add($"colour {Colour(before.Color)} → {Colour(after.Color)}");
        if (before.Falloff != after.Falloff) changed.Add($"falloff {ForkBuilder.Number(before.Falloff)} → {ForkBuilder.Number(after.Falloff)}");
        if (before.Location != after.Location) changed.Add($"location {ForkBuilder.Moved(before.Location, after.Location)}");
        return changed.Count == 0 ? null : $"{name} ({Label(after, shown)}): {string.Join(", ", changed)}";
    }

    /// <summary>
    /// The comment on a new vanilla: light entry's key line: its light type, what it lights (a U light), whether it is now off (a
    /// baked light for characters and props only), and the vanilla values of the fields it changes (<paramref name="was"/>):
    /// "U door light (lights the 8 doors); vanilla location (352.55, -11923.24, -222.2), brightness 0.6",
    /// "B baked: off for characters and props only, the walls stay lit".
    /// </summary>
    internal static string Comment(State light, bool enabled, IReadOnlyList<string> was, Func<IEnumerable<IMEPackage>> shown)
    {
        var text = Label(light, shown);
        if (light.Type == 'U')
        {
            var (count, doors) = Reach(shown(), light.Special);
            text += doors ? count == 1 ? " (lights the door)" : $" (lights the {count} doors)"
                : count == 0 ? $" (no mesh is on {string.Join(", ", light.Special)})" : $" (lights {count} meshes on {string.Join(", ", light.Special)})";
        }
        if (enabled != light.Enabled) text += !enabled ? light.Type == 'B' ? ": off for characters and props only, the walls stay lit" : ": off" : ": on";
        return was.Count > 0 ? $"{text}; vanilla {string.Join(", ", was)}" : text;
    }

    /// <summary>A colour as a receipt or a comment shows it: (r, g, b).</summary>
    internal static string Colour(IReadOnlyList<byte> rgb) => $"({rgb[0]}, {rgb[1]}, {rgb[2]})";

    /// <summary>
    /// The level actors of these files with a drawn component on one of <paramref name="channels"/> (through the file's archetypes), and
    /// whether they are all doors: what a U light lights (the freighter's door lights, on Unnamed_6, light its 8 BioDoors).
    /// </summary>
    private static (int Count, bool Doors) Reach(IEnumerable<IMEPackage> files, IReadOnlyList<string> channels)
    {
        var lit = new HashSet<ExportEntry>();
        foreach (var pcc in files)
        {
            var level = pcc.FindExport("TheWorld.PersistentLevel");
            foreach (var component in pcc.Exports.Where(e => e.ClassName.EndsWith("Component") && e.Parent is ExportEntry owner && owner.Parent == level && e.IsA("PrimitiveComponent")))
            {
                var owner = (ExportEntry)component.Parent!;
                if (!lit.Contains(owner) && Resolved(component, null).GetProp<StructProperty>("LightingChannels") is { } on
                    && on.Properties.OfType<BoolProperty>().Any(b => b.Value && channels.Contains(b.Name.Instanced))) lit.Add(owner);
            }
        }
        return (lit.Count, lit.Count > 0 && lit.All(a => a.IsA("BioDoor")));
    }
}
