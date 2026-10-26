using System.Numerics;
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

/// <param name="LevelChanges">What this build changed in the level itself (streamed files, post-process, start points).</param>
/// <param name="MissingConversations">Conversations the NPCs name that the level does not have yet: build them, then the level again.</param>
public sealed record LevelBuildResult(string Path, bool CreatedLevel, IReadOnlyList<string> LevelChanges, IReadOnlyList<BuiltNpc> Npcs, IReadOnlyList<BuiltUsable> Usables, int StringCount, IReadOnlyList<string> TlkFiles,
    string? SideloaderConfig, IReadOnlyList<string> MissingConversations, IReadOnlyList<string> Notes, IReadOnlyList<string> BackupPaths);

/// <summary>
/// Builds a Game 1 level from a YAML file. The level itself: vanilla sub-levels it shows and their stream volume, its
/// post-process, start points (see <see cref="LevelStreams"/>). Its NPCs: the pawn (found by tag, else imported from a
/// template), position, name (own actor type), skin, hair and eye colors, fill lights that follow her, and the Kismet that
/// starts her conversation when Shepard uses her. Its usables: placeables Shepard uses to go to another map. A missing level is
/// created, and the LE1CP Sideloader config that loads it is written.
/// </summary>
public static class LevelBuilder
{
    private const string LevelPath = "TheWorld.PersistentLevel";
    private const string MainSequence = "TheWorld.PersistentLevel.Main_Sequence";

    private sealed record Source(string? Package, string Actor, int Line);

    private sealed record NameSpec(string Type, int StrRef, int Line);

    /// <param name="Offset">Forward, right, up from the NPC's location, turned by her yaw.</param>
    private sealed record LightSpec(string Tag, Vector3 Offset, float? Radius, float? Brightness, int Line);

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

    private sealed class Spec
    {
        public string? Package;
        public string DefaultLanguage = "INT";
        public List<string> GlobalTlk = [];
        public List<string>? Sideloader;
        public Source? LightTemplate;
        public LevelStreams.Spec? Streaming;
        public (string Package, int Line)? PostProcess;
        public Source? StartPointTemplate;
        public List<LevelStreams.StartPoint> StartPoints = [];
        public List<NpcSpec> Npcs = [];
        public List<UsableSpec> Usables = [];
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

        var backups = new List<string>();
        var missing = new List<string>();
        var levelChanges = new List<string>();
        var ((npcs, usables), backup) = PackageWriter.Edit(game, package, pcc =>
        {
            var level = pcc.FindExport(LevelPath) ?? throw new ArgumentException($"{Path.GetFileName(pcc.FilePath)} has no {LevelPath}; is it a level?");
            using var cache = new PackageCache();
            if (spec.Streaming is { } streaming) LevelStreams.SetStreaming(game, pcc, level, streaming, cache, levelChanges);
            if (spec.PostProcess is { } post) LevelStreams.SetPostProcess(game, pcc, level, post.Package, post.Line, levelChanges);
            var template = spec.StartPointTemplate is { } t ? (t.Package ?? throw new ArgumentException($"line {t.Line}: 'startPointTemplate' needs a 'package'."), t.Actor) : ((string, string)?)null;
            foreach (var start in spec.StartPoints) LevelStreams.SetStartPoint(game, pcc, level, start, template, levelChanges);
            var builtNpcs = spec.Npcs.Select(npc => BuildNpc(game, pcc, level, spec, npc, cache, missing, notes)).ToList();
            return (builtNpcs, spec.Usables.Select(u => BuildUsable(game, pcc, level, u, cache)).ToList());
        });
        backups.Add(backup);

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
        return new LevelBuildResult(package, create, levelChanges, npcs, usables, spec.Strings.Count, tlkFiles, config, missing, notes, backups);
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
        foreach (var light in npc.Lights) SetLight(game, pcc, level, spec, npc, light, origin, rotation.Yaw, cache, changes);
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
        if (usable.Location is { } location) dirty |= Changed(changes, "location", Put(props, CommonStructs.Vector3Prop(location, "Location")));
        if (usable.Yaw is { } yaw)
        {
            var rotation = props.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : new Rotator(0, 0, 0);
            dirty |= Changed(changes, "yaw", Put(props, CommonStructs.RotatorProp(new Rotator(rotation.Pitch, (int)Math.Round(yaw * 65536 / 360), rotation.Roll), "Rotation")));
        }
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
    private static ExportEntry AddActor(MEGame game, IMEPackage pcc, ExportEntry level, Source template, PackageCache cache)
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
            if (pcc.FindEntry($"{LevelPath}.{actor.ObjectName.Instanced}") is { } clash)
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
    private static void SetLight(MEGame game, IMEPackage pcc, ExportEntry level, Spec spec, NpcSpec npc, LightSpec light, Vector3 origin, int yaw,
                                 PackageCache cache, List<string> changes)
    {
        var actor = FindActor(pcc, level, light.Tag, e => e.IsA("Light"));
        var isNew = actor is null;
        if (actor is null)
        {
            // Copy a light of this level, else the file's light template.
            var sibling = pcc.Exports.FirstOrDefault(e => e.Parent == level && e.IsA("Light"));
            var source = sibling is not null ? new Source(null, sibling.InstancedFullPath, light.Line)
                         : spec.LightTemplate ?? throw new ArgumentException($"line {light.Line}: the level has no light to copy; give a top-level 'lightTemplate'.");
            actor = AddActor(game, pcc, level, source, cache);
        }

        var angle = yaw * Math.PI * 2 / 65536;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var at = new Vector3(
            (float)(origin.X + light.Offset.X * cos - light.Offset.Y * sin),
            (float)(origin.Y + light.Offset.X * sin + light.Offset.Y * cos),
            (float)(origin.Z + light.Offset.Z));
        var props = actor.GetProperties();
        var moved = Put(props, CommonStructs.Vector3Prop(at, "Location"));
        if (Put(props, new NameProperty(light.Tag, "Tag")) | moved) actor.WriteProperties(props);

        var component = Object(pcc, props, "LightComponent") ?? throw new ArgumentException($"line {light.Line}: {actor.InstancedFullPath} has no LightComponent.");
        var c = component.GetProperties();
        var dirty = Put(c, new BoolProperty(true, "bEnabled"));
        // The level filter never matches a pawn in a Sideloader level (§A.9): keep it off, and the radius small.
        dirty |= Put(c, new BoolProperty(false, "bOnlyAffectSameAndSpecifiedLevels"));
        if (light.Radius is { } radius) dirty |= Put(c, new FloatProperty(radius, "Radius"));
        if (light.Brightness is { } brightness) dirty |= Put(c, new FloatProperty(brightness, "Brightness"));
        if (c.GetProp<StructProperty>("CachedParentToWorld")?.GetProp<StructProperty>("WPlane") is { } plane)
        {
            dirty |= Put(plane.Properties, new FloatProperty(at.X, "X")) | Put(plane.Properties, new FloatProperty(at.Y, "Y")) | Put(plane.Properties, new FloatProperty(at.Z, "Z"));
        }
        if (isNew)
        {
            c.AddOrReplaceProp(CommonStructs.GuidProp(Guid.NewGuid(), "LightGuid"));
            c.AddOrReplaceProp(CommonStructs.GuidProp(Guid.NewGuid(), "LightmapGuid"));
        }
        if (dirty || isNew) component.WriteProperties(c);

        if (isNew) changes.Add($"light {light.Tag} added");
        else if (dirty || moved) changes.Add($"light {light.Tag}");
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
                     "startPointTemplate", "startPoints", "npcs", "usables"))
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
                case "startPointTemplate": spec.StartPointTemplate = SourceOf(value); break;
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
            }
        }
        var twice = spec.Npcs.Where(n => n.Tag is not null).GroupBy(n => n.Tag!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null) throw new ArgumentException($"Two NPCs have tag '{twice.Key}'.");
        var usableTwice = spec.Usables.Where(u => u.Tag is not null).GroupBy(u => u.Tag!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (usableTwice is not null) throw new ArgumentException($"Two usables have tag '{usableTwice.Key}'.");
        return spec;
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
}
