using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using YamlDotNet.RepresentationModel;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <param name="Key">The actor's tag, or its object path when it has none.</param>
/// <param name="Action">updated, added, removed, unchanged, conflict (refused: nothing applied), refused (a vanilla actor the build may not
/// change, an NPC light whose item is another NPC's), skipped or failed.</param>
/// <param name="Entry">The YAML entry, as section.name.</param>
/// <param name="Changes">What changed in the YAML.</param>
/// <param name="Reason">Why it was left unchanged, refused, skipped or failed; which of the YAML's values were kept over the editor's.</param>
/// <param name="Conflicts">The fields the editor and the YAML both changed since the session's base; null when none did.</param>
public sealed record ImportedActor(string Key, string Action, string? Entry, IReadOnlyList<string> Changes, string? Reason, IReadOnlyList<FieldConflict>? Conflicts = null);

/// <param name="Field">location, rotation ([pitch, yaw, roll] in degrees), yaw, scale or scale3d.</param>
/// <param name="Base">What the YAML held when the editor first changed the actor, or when an import last wrote it.</param>
/// <param name="Taken">editor or yaml, as onConflict chose; null when the actor was refused.</param>
public sealed record FieldConflict(string Field, string Base, string Yaml, string Editor, string? Taken);

/// <param name="Written">Whether the YAML file changed; importing the same session again changes nothing.</param>
/// <param name="Marked">Session entries marked imported: their base is now what the YAML holds, and MeleBridge drops them once the installed level shows them.</param>
public sealed record EditorImportResult(string File, string Session, bool Written, IReadOnlyList<ImportedActor> Actors, int Marked, LevelBuildResult? Build, IReadOnlyList<string> Notes);

/// <summary>
/// Merges what the in-game editor saved (MeleBridge's session file, one per map; docs/design/in-game-editor.md §3.7, §10) into a level YAML file, keyed by
/// tag, so a rebuild keeps it: moved, turned or scaled actors get their location, rotation and scale, field by field against the
/// state the YAML held when the editor first changed them (a value changed in the YAML since is kept, both changed is a conflict);
/// meshes spawned in the editor get a props entry, deleted props 'removed: true' (the build hides their actor). Vanilla actors of the
/// files the level streams (meshes and effects) get a vanilla entry, keyed by file and actor name, which the build ships in a fork of the
/// file (docs/design/in-game-editor.md §11); those of other levels stay dev-only. NPC lights go into their NPC's lights list as offsets from the NPC. Lights of
/// light collections (docs/design/in-game-editor.md §12.4) get a vanilla entry too, keyed by file, collection and light: shipped in a fork, or for a light of
/// the map's characterLights copy, keyed by the copy's source file and written into the map.
/// </summary>
public static class EditorImport
{
    private const string PropsComment = "Meshes placed with the in-game editor (F8, Save, then editor_import).";

    /// <summary>Above a lights section the import adds.</summary>
    private const string LightsComment =
        "Lights placed with the in-game editor (F8, Save, then editor_import). kind props: lights only props (the Gameplay_4\n"
        + "channel); room: walls, vanilla meshes, pawns and props. A light is switched off (enabled: false), never removed.";

    /// <summary>Above a vanilla section the import adds; {0} names the fork of the first entry's file (", here CLD01_FRE31_00_LAY"), or nothing.</summary>
    private const string VanillaComment =
        "Vanilla actors and lights the map's streamed files change (shipped in a fork), and lights of the map's characterLights\n"
        + "copies (written into the map). A light is <file>.<collection>.<light> (a copy's: keyed by its source file): location,\n"
        + "brightness, color, radius, falloff, enabled; light types: B baked, C characters only, U special channel, D dynamic.\n"
        + "A fork is a renamed copy of the file, named after the map and the file{0}, that the map streams instead. A field\n"
        + "left out keeps the file's value: delete a field, or the whole entry, to give it its vanilla value back.";

    /// <summary>Above an NPC's lights list the import adds.</summary>
    private const string NpcLightsComment =
        "Lights that go with this NPC: offset [forward, right, up] from the NPC's centre (the pawn's Location), turned by the NPC's yaw.\n"
        + "Optional: color (default white), falloff (default 2), enabled (default true). A light is switched off, never removed.";

    /// <summary>Locations this close after rounding to 2 decimals are one; scales within <see cref="ScaleNear"/>.</summary>
    private const double Near = 0.01;
    private const double ScaleNear = 0.001;

    /// <summary>NPC light offsets this close on each axis are one: written to 2 decimals, MeleBridge's float32 round trip stays under it.</summary>
    private const double OffsetNear = 0.02;

    /// <summary>Light radii this close are one; brightness and falloff within <see cref="LightNear"/> (MeleBridge compares them so too).</summary>
    private const double RadiusNear = 0.01;
    private const double LightNear = 0.001;

    private static readonly double[] White = [255, 255, 255];

    /// <summary>An NPC light's offset when its item gives none: the NPC's centre.</summary>
    private static readonly double[] Origin = [0, 0, 0];

    private static readonly double[] Unscaled = [1, 1, 1];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    /// <summary>For a file people read: quotes and apostrophes as they are, not \u0027.</summary>
    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Session(int Version, string? Map, List<SessionActor>? Actors);

    /// <param name="Rotation">Pitch, yaw, roll in Unreal units (65536 = 360°).</param>
    /// <param name="Scale">DrawScale.</param>
    /// <param name="Scale3D">DrawScale3D.</param>
    /// <param name="Deleted">In a base: whether the YAML's entry has removed: true (editor_import writes it, MeleBridge's Save keeps it; null
    /// in a base without it, which merges 'removed' two-way). In imported.before: whether the installed level hides the actor.</param>
    /// <param name="Light">A light's block (docs/design/in-game-editor.md §11); null for any other actor.</param>
    /// <param name="Npc">An NPC light's NPC and offset; null for any other actor.</param>
    private sealed record Transform(double[]? Location, double[]? Rotation, double? Scale, double[]? Scale3D, bool? Deleted = null, LightBlock? Light = null, SessionNpc? Npc = null);

    /// <summary>An NPC light's NPC (its tag) and its offset [forward, right, up] from the NPC's pose (<see cref="LevelBuilder.NpcLightAt"/>).</summary>
    private sealed record SessionNpc(string? Tag, double[]? Offset);

    /// <summary>
    /// A light's state in a session entry (docs/design/in-game-editor.md §11): kind by name (props or room; npc for an NPC light, vanilla for a light of a light
    /// collection), radius, brightness, colour [r, g, b] (0-255), falloff exponent, and whether it is on. A spawned light the editor deleted
    /// keeps its 'enabled' and is 'deleted'; it shows enabled and not deleted. A vanilla light has its light type (B, C, U or D).
    /// </summary>
    private sealed record LightBlock(string? Kind, double? Radius, double? Brightness, double[]? Color, double? Falloff, bool? Enabled, string? Type = null);

    /// <summary>A light block with every field, as the import compares and writes it.</summary>
    private sealed record LightState(string Kind, double Radius, double Brightness, double[] Color, double Falloff, bool Enabled, string? Type = null)
    {
        public static LightState? Of(LightBlock? block) =>
            block is { Kind: "props" or "room" or "npc" or "vanilla", Radius: { } radius, Brightness: { } brightness, Color: { Length: 3 } color, Falloff: { } falloff,
                Enabled: { } enabled }
                ? new LightState(block.Kind, radius, brightness, color, falloff, enabled, block.Type)
                : null;

        public JsonObject Json()
        {
            var json = new JsonObject
            {
                ["kind"] = Kind, ["radius"] = Radius, ["brightness"] = Brightness, ["color"] = Ints(Color), ["falloff"] = Falloff, ["enabled"] = Enabled,
            };
            if (Type is not null) json["type"] = Type;
            return json;
        }
    }

    /// <summary>One actor's latest state (version 1: location, rotation and deleted only).</summary>
    /// <param name="Rotation">Pitch, yaw, roll in Unreal units (65536 = 360°).</param>
    /// <param name="Base">What the YAML holds for the actor, for the three-way merge: its state before the editor first changed it, or
    /// what an import wrote. Null in version 1 and for a spawn never imported.</param>
    /// <param name="Imported">Present once an import applied the entry: MeleBridge re-applies it until the install shows it, then drops it.</param>
    /// <param name="Installed">What an install may still show, kept by MeleBridge's Save from earlier imports (written by MeleBridge only).</param>
    /// <param name="Light">A light's block (docs/design/in-game-editor.md §11): a light the editor spawned, a light of a lights section or an NPC light; null for any other actor.</param>
    /// <param name="Npc">An NPC light's NPC and offset (its location is the NPC's pose plus the offset); null for any other actor.</param>
    private sealed record SessionActor(string? Tag, string? Path, string? Level, bool Spawned, SessionMesh? Mesh, double[]? Location, double[]? Rotation,
        double? Scale, double[]? Scale3D, bool Deleted, Transform? Base, JsonElement? Imported, string? Class = null, JsonElement? Installed = null, LightBlock? Light = null,
        SessionNpc? Npc = null)
    {
        public bool IsNpcLight => Npc is not null || Light?.Kind == "npc";
    }

    /// <summary>Where an NPC stands, for its lights: location and yaw (Unreal units).</summary>
    private sealed record NpcPose(double[] Location, double Yaw);

    /// <param name="File">The package the game loaded the mesh from (a name without .pcc), when the editor knows it.</param>
    private sealed record SessionMesh(string? Path, string? File);

    /// <summary>What an entry gives its actor (rotation and yaw in degrees); a value it leaves out is null.</summary>
    private sealed record YamlPlace(double[]? Location, double? Yaw, double[]? Rotation, double? Scale, double[]? Scale3D, bool Removed);

    /// <summary>What a lights entry gives its light; a value it leaves out is null (the build's: kind props, colour white, falloff 2, on).</summary>
    private sealed record YamlLight(double[]? Location, string? Kind, double? Radius, double? Brightness, double[]? Color, double? Falloff, bool? Enabled);

    /// <summary>One value of a transform: the editor's, the YAML's (the base's when the entry has none) and the base's; as the YAML writes it.</summary>
    private sealed record Field(string Name, double[] Editor, double[]? Yaml, double[]? Base, Func<double[], double[], bool> Same, Func<double[], string> Write);

    /// <summary>
    /// What the import does to a session entry: marks it with the YAML's values as its base and the YAML's values it kept over the
    /// editor's as its state; a null base drops it (kept in the dropped file, <see cref="DroppedFile"/>, with <paramref name="Why"/>).
    /// <paramref name="Before"/>: what the YAML's entry gave the actor before the import (<see cref="Shown"/>), null for a props entry
    /// the import added.
    /// </summary>
    private sealed record SessionUpdate(int Index, Transform? Base, Transform? Before, IReadOnlyList<(string Field, JsonNode Value)> State, string? Why = null);

    /// <summary>One merge of the session into the YAML: the text, a result per session entry, and what to write into the session.</summary>
    private sealed record Merged(string Text, string SessionText, IReadOnlyList<ImportedActor> Results, List<SessionUpdate> Updates, List<string> Notes);

    private enum Side { Same, Editor, Yaml, Conflict }

    /// <param name="takeSpawned">Whether meshes spawned in the editor become props entries of this file; by default, when the
    /// session is of the map this file builds. A level the game loads into another map (sideloaded) has to be asked, or two
    /// files imported from one session would both build the prop.</param>
    /// <param name="keepSession">Leave the session file as it is, instead of marking the entries this import applied.</param>
    /// <param name="onConflict">refuse (the actor is left out), editor or yaml: which value a field takes when both changed it since the base.</param>
    public static EditorImportResult Run(MEGame game, string yamlPath, string? sessionPath, bool build, bool? takeSpawned = null, bool keepSession = false,
        string onConflict = "refuse")
    {
        if (!game.IsGame1()) throw new ArgumentException("editor_import handles Game 1 (LE1) levels.");
        var policy = onConflict.Trim().ToLowerInvariant();
        if (policy is not ("refuse" or "editor" or "yaml")) throw new ArgumentException($"onConflict is refuse, editor or yaml, not '{onConflict}'.");
        var file = Path.GetFullPath(GamePaths.ToWindowsPath(yamlPath));
        if (!File.Exists(file)) throw new FileNotFoundException($"No file {file}.");
        var original = File.ReadAllText(file);
        var root = Root(original);
        var package = Value(root, "package") is { } pkg ? Scalar(pkg) : throw new ArgumentException("The file has no 'package'; is it a level_build file?");
        var level = Path.GetFileNameWithoutExtension(GamePaths.ToWindowsPath(package));
        var notes = new List<string>();
        var sessionFile = sessionPath is null ? FindSession(level, root, notes) : Path.GetFullPath(GamePaths.ToWindowsPath(sessionPath));
        var dropFile = DroppedFile(sessionFile);

        // A spawned mesh's file as the YAML names it: the level as its 'package' gives it, read from where level_build finds it (beside the
        // YAML or under its mod root; by name, the game finds the installed copy), so a relative one stays relative; others by name, a fork
        // of this map by its vanilla file's (docs/design/in-game-editor.md §11: the fork is a build output, not a source).
        var ownFile = Existing(game, package, Path.GetDirectoryName(file)!);
        var own = ownFile is not null && !Path.IsPathRooted(GamePaths.ToWindowsPath(package)) ? package : ownFile ?? GamePaths.ToWindowsPath(package);
        string FileOf(string name) => ForkBuilder.Canonical(level, Path.GetFileNameWithoutExtension(name)) is var vanilla && vanilla.Equals(level, StringComparison.OrdinalIgnoreCase)
            ? own
            : Pcc(vanilla);
        // Where it may come from when the session does not say: the files the level streams, then the level itself, last because
        // it only has the mesh from an earlier build (a level built again from nothing would not).
        var streams = Value(root, "streaming") is YamlMappingNode streaming && Value(streaming, "levels") is YamlSequenceNode levels
            ? levels.Children.Select(l => Path.GetFileNameWithoutExtension(Scalar(l))).ToList()
            : [];
        var fallbacks = streams.Select(FileOf).Append(own).ToList();

        // The characterLights copies by their name in the map, the first of a name (the one the build keeps), with their source.
        var copies = Value(root, "characterLights") is YamlSequenceNode copied
            ? copied.Children.OfType<YamlMappingNode>().Select(c => (Package: Value(c, "package") is { } p ? Scalar(p) : null, Actor: Value(c, "actor") is { } a ? Scalar(a) : null))
                .Where(c => c.Package is not null && c.Actor is not null)
                .Select(c => (File: Path.GetFileNameWithoutExtension(GamePaths.ToWindowsPath(c.Package!)), Collection: LevelStreams.CopyName(c.Actor!), Package: c.Package!, Actor: c.Actor!))
                .DistinctBy(c => c.Collection, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        using var files = new Files(game, own, ownFile);
        var vanillaFiles = new VanillaFiles(game, (ownFile ?? (Path.IsPathRooted(own) ? own : null)) is { } ownPath ? ForkBuilder.DlcOf(ownPath) : null, files);
        // A light's archetypes end in Engine's class default objects.
        using var cache = new PackageCache();
        // MeleBridge may save, or a re-apply pass drop entries, while this runs (the game may be running). The YAML written and the
        // entries marked must come from one session, or the next import sees three different values (a false conflict): when the
        // session changed before it is marked, the merge is done again from the file as it was (replacing this import's own write),
        // up to 3 tries.
        Merged? merged = null, wrote = null;
        int? marked = null;
        for (var attempt = 1; marked is null; attempt++)
        {
            merged = MergeSession(Read(sessionFile));
            if (File.ReadAllText(sessionFile) == merged.SessionText)
            {
                if (merged.Text != (wrote?.Text ?? original)) File.WriteAllText(file, merged.Text, new UTF8Encoding(false));
                wrote = merged;
                marked = keepSession ? 0 : Mark(sessionFile, merged.SessionText, merged.Updates, merged.Notes, file, dropFile);
            }
            if (marked is null && attempt == 3)
            {
                merged = wrote ?? throw new IOException($"{sessionFile} changed during each of {attempt} tries (the editor saving?): nothing was written. Import again.");
                merged.Notes.Add($"{Path.GetFileName(sessionFile)} changed during each of {attempt} tries (the editor saving?): the YAML has the last merge written, the session "
                                 + "is left as it is, and the next import merges its entries again.");
                marked = 0;
            }
        }
        notes.AddRange(merged!.Notes);

        var written = merged.Text != original;
        var refused = merged.Results.Where(r => r.Action == "conflict").Select(r => r.Key).ToList();
        if (refused.Count > 0)
        {
            notes.Add($"Refused on a conflict (the editor and the YAML both changed them): {string.Join(", ", refused)}. Their session entries are left as they are; "
                      + "import again with onConflict=editor or yaml to pick a side.");
        }
        var built = build ? LevelBuilder.Build(game, file, null) : null;
        if (built is not null) notes.Add("Install the level (game closed: mod_install) to see it after a reload.");
        return new EditorImportResult(file, sessionFile, written, merged.Results, marked.Value, built, notes);

        Merged MergeSession((Session Session, string Text) read)
        {
            var (session, sessionText) = read;
            var text = original;
            var said = new List<string>();
            var ownMap = session.Map is null || session.Map.Equals(level, StringComparison.OrdinalIgnoreCase);
            var spawnedHere = takeSpawned ?? ownMap;
            if (!ownMap)
            {
                said.Add($"The session is of map {session.Map} and the file builds {level}: actors are imported when one of its entries has their tag (vanilla "
                         + $"actors are not: the YAML of {session.Map} ships them, else they stay dev-only)"
                         + (spawnedHere ? ", and meshes spawned in the editor become props entries of this file (takeSpawned)." : "; meshes spawned in the editor are not (takeSpawned=true adds them here)."));
            }

            var actors = session.Actors ?? [];
            var results = new ImportedActor?[actors.Count];
            // In the YAML now (marked imported), or never to be (dropped): written into the session after the YAML.
            var updates = new List<SessionUpdate>();
            var vanillaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var npcLights = YamlEdits.NpcLightTags(Root(text));
            // Two passes: NPC lights after the other entries, since their NPC's entry may move the NPC they are placed from.
            foreach (var i in Enumerable.Range(0, actors.Count).OrderBy(i => actors[i].IsNpcLight))
            {
                var actor = actors[i];
                // A level actor tagged as its class (every vanilla mesh is 'StaticMeshActor') has no tag of its own.
                var tag = actor.Tag is null or "" or "None" || !actor.Spawned && actor.Tag.Equals(actor.Class, StringComparison.OrdinalIgnoreCase) ? null : actor.Tag;
                var key = tag ?? actor.Path ?? $"actors[{i}]";
                if (actor.IsNpcLight)
                {
                    (results[i], var lit) = NpcLight(actor, i, tag, key);
                    if (lit is not null) updates.Add(lit);
                    continue;
                }
                if (tag is not null && npcLights.TryGetValue(tag, out var npcLight))
                {
                    // Without its npc block (a MeleBridge older than NPC lights), it would be applied as a light of a lights section, on every load.
                    results[i] = new ImportedActor(key, "skipped", npcLight.Where, [], keepSession
                        ? $"an NPC light ({npcLight.Where}) saved without its npc block (by an older MeleBridge): not imported. Its session entry would be dropped (keepSession: left as it is)."
                        : $"an NPC light ({npcLight.Where}) saved without its npc block (by an older MeleBridge): not imported. Its session entry is dropped, so the editor "
                          + $"stops applying it, and kept in {Path.GetFileName(dropFile)}.");
                    updates.Add(new SessionUpdate(i, null, null, [], $"an NPC light's tag ({npcLight.Where}) without its npc block (an older MeleBridge's)"));
                    continue;
                }
                if (actor.Path is null || actor.Location is not { Length: 3 } || actor.Rotation is not { Length: 3 })
                {
                    results[i] = new ImportedActor(key, "failed", null, [], "needs 'path', 'location' [x, y, z] and 'rotation' [pitch, yaw, roll].");
                    continue;
                }
                var actorLevel = actor.Level ?? actor.Path.Split('.')[0];
                var ours = actorLevel.Equals(level, StringComparison.OrdinalIgnoreCase);
                if (actor.Light?.Kind == "vanilla")
                {
                    // A light of a light collection: by its path (a component has no tag).
                    if (!ours && !ownMap)
                    {
                        results[i] = new ImportedActor(key, "skipped", null, [], $"{actor.Path} is a light of {actorLevel} edited on map {session.Map}, and this file builds {level}: "
                                                                                  + $"the YAML of {session.Map} ships it, else it stays dev-only there (MeleBridge re-applies it).");
                    }
                    else if (MergeVanillaLight(ref text, actor, i, key, level, streams, copies, files, vanillaFiles, cache, vanillaKeys, policy, keepSession, dropFile) is { } light)
                    {
                        results[i] = light.Result;
                        if (light.Update is not null) updates.Add(light.Update);
                    }
                    else
                    {
                        results[i] = new ImportedActor(key, "skipped", null, [], $"dev-only: {actor.Path} is a light of {actorLevel}, which {level} does not stream (streaming.levels), "
                                                                                  + "so no fork of it ships: the editor re-applies it from the session.");
                    }
                    continue;
                }
                var found = tag is null ? null : YamlEdits.Tagged(Root(text), tag);
                // A spawn gets a base from an import only, and keeps it through a Save (which drops 'imported'): with one, an import
                // put it in a YAML file already.
                var imported = actor.Spawned && (actor.Base is not null || actor.Imported is { ValueKind: JsonValueKind.Object });
                if (found is { } f)
                {
                    var entry = $"{f.Section}.{Scalar(f.Entry.Key)}";
                    if ((f.Section == "lights") != (actor.Light is not null))
                    {
                        results[i] = new ImportedActor(key, "skipped", entry, [], actor.Light is null
                            ? $"{entry} is a light, and the session entry has no light block (saved by a MeleBridge older than lights?): edit {entry} in the YAML by hand."
                            : $"a light, and {entry} is not a lights entry: the tag is taken (give the light another one in the editor, or the entry another one).");
                        continue;
                    }
                    if (f.Section == "lights")
                    {
                        (results[i], var lit) = MergeLight(ref text, entry, r => YamlEdits.Tagged(r, tag!)!.Value.Entry.Value, key, actor, i, policy);
                        if (lit is not null) updates.Add(lit);
                        continue;
                    }
                    var place = Place(f.Entry.Value);
                    if (actor.Deleted)
                    {
                        if (f.Section != "props")
                        {
                            results[i] = new ImportedActor(key, "skipped", entry, [], $"deleted in the editor, but import removes props only ({f.Section} carry Kismet and text): remove {entry} by hand if you mean it.");
                        }
                        else if (place.Removed)
                        {
                            results[i] = new ImportedActor(key, "unchanged", entry, [], "already removed: true.");
                            updates.Add(new SessionUpdate(i, BaseOf(place, actor, newEntry: false), Shown(place, actor), []));
                        }
                        else if (!RemovedAfter(editor: true, yaml: false, actor.Base?.Deleted))
                        {
                            // Removed by an import, then shown again in the YAML by hand: the YAML's stays, and the session entry shows it too.
                            results[i] = new ImportedActor(key, "unchanged", entry, [], "kept the YAML's removed: false, changed there since the session's base: the session entry shows the actor again.");
                            updates.Add(new SessionUpdate(i, BaseOf(place, actor, newEntry: false), Shown(place, actor), [("deleted", JsonValue.Create(false))]));
                        }
                        else
                        {
                            // The entry stays, so the build finds the actor it made and hides it; its place stays as it was.
                            (text, var flagged) = YamlEdits.SetKeys(text, f.Entry.Value, [("removed", "true")]);
                            results[i] = new ImportedActor(key, "removed", entry, flagged, null);
                            updates.Add(new SessionUpdate(i, BaseOf(Place(YamlEdits.Tagged(Reread(text, entry), tag!)!.Value.Entry.Value), actor, newEntry: false), Shown(place, actor), []));
                        }
                        continue;
                    }
                    if (!actor.Spawned && !ours) said.Add($"{key}: matched {entry} by tag, but it is {actor.Path}, an actor of {actorLevel}, not {level}. Check it is the actor the entry builds.");
                    (results[i], var update) = Merge(ref text, f.Section, entry, r => YamlEdits.Tagged(r, tag!)!.Value.Entry.Value, key, place, actor, i, policy, said);
                    if (update is not null) updates.Add(update);
                }
                else if (imported)
                {
                    // In a YAML file, not this one: removed from it by hand since, or imported into another level file of the map
                    // (takeSpawned there). Added here, it would come back, or be built by two files.
                    const string elsewhere = "imported before, and this file has no entry with its tag: removed from the YAML by hand, or in another level file of the map";
                    // The map's own file is the only one that takes spawns unasked: here, removed by hand most likely.
                    var onlyTaker = ownMap && takeSpawned is null;
                    if (!actor.Deleted && onlyTaker && AsBase(actor))
                    {
                        // Nothing changed in the editor since the import, and MeleBridge would spawn it on every load: what the entry has
                        // is in a YAML file already, if anywhere. A YAML file reverted alone (git) reads the same, and then the entry is the
                        // only copy of the mesh and its place: the dropped file keeps it.
                        results[i] = new ImportedActor(key, "skipped", null, [], keepSession
                            ? $"{elsewhere}. Unchanged since the import: its session entry would be dropped, so the game stops spawning it (keepSession: left as it is)."
                            : $"{elsewhere}. Unchanged since the import: its session entry is dropped, so the game stops spawning it, and kept in {Path.GetFileName(dropFile)} "
                              + "(the notes say how to add it again).");
                        updates.Add(new SessionUpdate(i, null, null, [], "imported, then this file had no entry with its tag (removed by hand, or the file reverted alone), "
                                                                         + "unchanged in the editor since"));
                    }
                    else
                    {
                        results[i] = new ImportedActor(key, "skipped", null, [], actor.Deleted
                            ? $"{elsewhere}. Deleted in the editor: its session entry stays, so importing the file that has it "
                              + (actor.Light is null ? "marks it removed: true." : "switches it off (enabled: false).")
                            : $"{elsewhere}. Not added again; its session entry stays ({(onlyTaker ? "the editor changed it since the import" : "another level file may have it")}).");
                    }
                }
                else if (actor.Spawned && actor.Deleted)
                {
                    // Saved, then deleted: an import before the delete made it a props entry (found above, or with a base); without one there is nothing.
                    results[i] = new ImportedActor(key, "skipped", null, [], "spawned and deleted in the editor, never imported: nothing to do.");
                    if (spawnedHere) updates.Add(new SessionUpdate(i, null, null, [], "spawned and deleted in the editor, never imported"));
                }
                else if (actor.Spawned && !spawnedHere)
                {
                    results[i] = new ImportedActor(key, "skipped", null, [], $"spawned on map {session.Map}, and this file builds {level}: the file of that map takes it, or this one with takeSpawned=true.");
                }
                else if (actor.Spawned && actor.Light is not null)
                {
                    results[i] = AddLight(ref text, actor, tag, key);
                    if (results[i]!.Action == "added")
                    {
                        updates.Add(new SessionUpdate(i, LightBase(LightOf(YamlEdits.Tagged(Reread(text, $"lights.{tag}"), tag!)!.Value.Entry.Value), actor), null, []));
                    }
                }
                else if (actor.Light is not null)
                {
                    // A light the editor did not spawn: a built light of a lights section, whose entry is gone.
                    results[i] = new ImportedActor(key, "skipped", null, [], ours
                        ? $"{actor.Path} is a light of {level} that no lights entry has{(tag is null ? "" : $" (tag '{tag}')")}: its entry was removed by hand? A light is switched off "
                          + "(enabled: false), never removed: add the entry back to edit it."
                        : $"{actor.Path} is a light of {actorLevel}, not of a lights section of {level}: lights of other files are not imported (MeleBridge re-applies it).");
                }
                else if (actor.Spawned)
                {
                    var loaded = actor.Mesh?.File is { } loadedFrom && FileOf(loadedFrom) is var from && from != own ? [from] : Array.Empty<string>();
                    results[i] = AddMesh(game, files, ref text, actor, tag, key, loaded.Concat(fallbacks).Distinct(StringComparer.OrdinalIgnoreCase));
                    if (results[i]!.Action == "added")
                    {
                        updates.Add(new SessionUpdate(i, BaseOf(Place(YamlEdits.Tagged(Reread(text, $"props.{tag}"), tag!)!.Value.Entry.Value), actor, newEntry: true), null, []));
                    }
                }
                else if (!ours && !actor.Spawned && !ownMap)
                {
                    // Edited on another map: shipped by that map's own fork (its YAML), if any. Here it would ship in this map's instead.
                    results[i] = new ImportedActor(key, "skipped", null, [], $"{actor.Path} is an actor of {actorLevel} edited on map {session.Map}, and this file builds "
                                                                              + $"{level}: the YAML of {session.Map} ships it, else it stays dev-only there (MeleBridge re-applies it).");
                }
                else if (!ours && !actor.Spawned
                         && MergeVanilla(ref text, actor, i, key, level, streams, vanillaFiles, vanillaKeys, policy, keepSession, dropFile, said) is { } vanilla)
                {
                    results[i] = vanilla.Result;
                    if (vanilla.Update is not null) updates.Add(vanilla.Update);
                }
                else
                {
                    results[i] = new ImportedActor(key, "skipped", null, [], ours
                        ? $"{actor.Path} (an actor of {level}) has no npcs, usables or props entry{(tag is null ? "" : $" with tag '{tag}'")}: the YAML does not place it that way. Edit it in the YAML by hand."
                        : $"dev-only: {actor.Path} is an actor of {actorLevel}, which {level} does not stream (streaming.levels), so no fork of it ships: the editor "
                          + "re-applies it from the session."
                          + (tag is not null && !ownMap ? $" If it is an actor of another level file of the map, that file has its entry (none here has tag '{tag}')." : ""));
                }
            }
            return new Merged(text, sessionText, results.Select(r => r!).ToList(), updates, said);

            // An NPC light goes to the file whose npcs has its NPC, whatever takeSpawned says: on the Normandy the editor spawns it in BIOA_NOR00.
            (ImportedActor Result, SessionUpdate? Update) NpcLight(SessionActor actor, int i, string? tag, string key)
            {
                if (tag is null) return (new ImportedActor(key, "failed", null, [], "an NPC light without a tag: its item would have nothing to find it by."), null);
                var root = Root(text);
                var owner = actor.Npc?.Tag;
                if (YamlEdits.NpcLightTags(root).TryGetValue(tag, out var item))
                {
                    if (owner is not null && !owner.Equals(item.NpcTag, StringComparison.OrdinalIgnoreCase))
                    {
                        return (new ImportedActor(key, "refused", item.Where, [], $"{tag} belongs to {item.Where[..^".lights".Length]} in the YAML, not {owner}: not imported"), null);
                    }
                    return MergeNpcLight(ref text, item.Where, r => YamlEdits.NpcLightTags(r)[tag].Item, key, actor, i, policy, Reference(item.NpcTag), Built(item.NpcTag),
                        item.NpcTag is null ? null : YamlPose(Root(original), item.NpcTag));
                }
                if (YamlEdits.Tagged(root, tag) is { } taken)
                {
                    return (new ImportedActor(key, "skipped", $"{taken.Section}.{Scalar(taken.Entry.Key)}", [], $"an NPC light, and {taken.Section}.{Scalar(taken.Entry.Key)} has its "
                                                                                                                + "tag: the tag is taken (give the light another one in the editor, or the entry another one)."), null);
                }
                if (owner is null || YamlEdits.Npc(root, owner) is not { } npc)
                {
                    return (new ImportedActor(key, "skipped", null, [], $"an NPC light of {owner ?? "no NPC"}, which no npcs entry of this file has: the file that has the NPC imports it."), null);
                }
                var where = $"npcs.{npc.Name}.lights";
                var imported = actor.Base is not null || actor.Imported is { ValueKind: JsonValueKind.Object };
                if (actor.Spawned && !imported)
                {
                    if (actor.Deleted)
                    {
                        return (new ImportedActor(key, "skipped", null, [], "spawned and deleted in the editor, never imported: nothing to do."),
                            new SessionUpdate(i, null, null, [], "an NPC light spawned and deleted in the editor, never imported"));
                    }
                    var added = AddNpcLight(ref text, npc.Name, owner, actor, tag, key);
                    return (added, added.Action == "added"
                        ? new SessionUpdate(i, NpcLightBase(LightOf(YamlEdits.NpcLightTags(Reread(text, where))[tag].Item, "offset"), actor, Built(owner)), null, [])
                        : null);
                }
                // Built from an item, or put in one by an import, and the item is gone since: never added back, as a lights entry.
                var gone = $"an NPC light whose item of {where} is gone: removed by hand (a light is switched off with enabled: false, never removed), or the file reverted alone";
                if (AsBase(actor) && actor.Deleted == (actor.Base?.Deleted ?? false))
                {
                    return (new ImportedActor(key, "skipped", where, [], keepSession
                            ? $"{gone}. Unchanged since: its session entry would be dropped, so the editor stops applying it (keepSession: left as it is)."
                            : $"{gone}. Unchanged since: its session entry is dropped, so the editor stops applying it, and kept in {Path.GetFileName(dropFile)}."),
                        new SessionUpdate(i, null, null, [], "an NPC light whose item is gone from the file (removed by hand, or the file reverted alone), unchanged in the editor since"));
                }
                return (new ImportedActor(key, "skipped", where, [], $"{gone}. Not added again: the editor changed it since, so its session entry stays. Add the item back by hand "
                                                                     + "to keep the edit, or undo it in the editor."), null);
            }

            // Where the editor measured an NPC light's offset from: its NPC's own session entry as this import leaves it, else the YAML's pose.
            NpcPose? Reference(string? npcTag)
            {
                if (npcTag is null) return null;
                var j = Enumerable.Range(0, actors.Count).FirstOrDefault(k => !actors[k].IsNpcLight && npcTag.Equals(actors[k].Tag, StringComparison.OrdinalIgnoreCase)
                                                                              && actors[k].Location is { Length: 3 } && actors[k].Rotation is { Length: 3 }, -1);
                if (j < 0) return Built(npcTag);
                var state = updates.FirstOrDefault(u => u.Index == j)?.State ?? [];
                var location = state.FirstOrDefault(s => s.Field == "location").Value is JsonArray l ? l.Select(Num).ToArray() : actors[j].Location!;
                return new NpcPose(location, state.FirstOrDefault(s => s.Field == "rotation").Value is JsonArray r ? Num(r[1]) : actors[j].Rotation![1]);
            }

            // Where the next build puts the NPC: the YAML's pose, as this import leaves it.
            NpcPose? Built(string? npcTag) => npcTag is null ? null : YamlPose(Root(text), npcTag);
        }
    }

    /// <summary>
    /// Merges an actor's place into its entry (found in a text by <paramref name="locate"/>), field by field (docs/design/in-game-editor.md §10): the editor's
    /// value when the YAML still has the base's, the YAML's when the editor still has it (the session entry takes it too, so importing
    /// again keeps it), and on a conflict what <paramref name="policy"/> says: refuse leaves the actor out. Without a base (version 1, or
    /// a spawn never imported), the editor's value wins. 'removed' merges the same way against the base's 'deleted'. Props and vanilla
    /// entries take a full rotation and scale; npcs and usables a location and a yaw (the session entry takes back a pitch, roll or
    /// scale they don't keep). A value a props entry leaves out is the base's; a vanilla entry's is the file's (<paramref name="file"/>),
    /// whose pitch and roll a 'yaw' keeps. A vanilla entry shown again loses 'removed' (the file's value), and the whole entry when
    /// that was all it had.
    /// </summary>
    private static (ImportedActor Result, SessionUpdate? Update) Merge(ref string text, string section, string entry, Func<YamlMappingNode, YamlNode> locate, string key,
        YamlPlace place, SessionActor actor, int index, string policy, List<string> notes, Transform? file = null)
    {
        var prop = section is "props" or "vanilla";
        var b = actor.Base;
        var editor = actor.Rotation!;
        var baseRotation = Of(b?.Rotation, 3);
        var keep = Of(file?.Rotation, 3);
        var (keptPitch, keptRoll) = (keep ?? baseRotation) is { } br ? (br[0], br[2]) : (0d, 0d);
        var fields = new List<Field> { new("location", actor.Location!, place.Location ?? Of(file?.Location, 3) ?? Of(b?.Location, 3), Of(b?.Location, 3), SameLocation, WriteLocation) };
        var (tilted, scaled) = (false, false);
        if (prop)
        {
            fields.Add(new("rotation", editor, Turned(place, actor, newEntry: false, keep) ?? keep ?? baseRotation, baseRotation, Rotations.Same, YamlEdits.Rotation));
            // A value the YAML leaves out is the file's, else the base's; with neither, the build's default.
            if (actor.Scale is { } scale) fields.Add(new("scale", [scale], Of(place.Scale) ?? Of(file?.Scale) ?? Of(b?.Scale) ?? [1], Of(b?.Scale), SameScale, s => YamlEdits.Scale(s[0])));
            if (actor.Scale3D is { Length: 3 } scale3d)
            {
                fields.Add(new("scale3d", scale3d, place.Scale3D ?? Of(file?.Scale3D, 3) ?? Of(b?.Scale3D, 3) ?? Unscaled, Of(b?.Scale3D, 3), SameScale, YamlEdits.Scale3D));
            }
        }
        else
        {
            double[]? YawOf(double[]? units) => units is null ? null : [Rotations.Degrees(units[1])];
            fields.Add(new("yaw", YawOf(editor)!, Of(place.Yaw) ?? YawOf(baseRotation), YawOf(baseRotation), (x, y) => Rotations.SameYaw(x[0], y[0]),
                y => YamlEdits.Degrees((float)Rotations.Angle(y[0]))));
            tilted = !Rotations.Same(editor, [keptPitch, editor[1], keptRoll]);
            scaled = actor.Scale is { } s && !SameScale([s], Of(b?.Scale) ?? [1]) || actor.Scale3D is { Length: 3 } s3 && !SameScale(s3, Of(b?.Scale3D, 3) ?? Unscaled);
            if (tilted || scaled)
            {
                notes.Add($"{key}: {(tilted ? scaled ? "the pitch, roll and scale" : "the pitch and roll" : "the scale")} given in the editor are not written: {section} keep a location and a yaw"
                          + (b is null ? "." : " (the session entry takes the YAML's back)."));
            }
        }

        var sides = fields.Select(field => (Field: field, Side: Pick(field))).ToList();
        var conflicts = sides.Where(s => s.Side == Side.Conflict)
            .Select(s => new FieldConflict(s.Field.Name, s.Field.Write(s.Field.Base!), s.Field.Write(s.Field.Yaml!), s.Field.Write(s.Field.Editor), policy == "refuse" ? null : policy))
            .ToList();
        if (conflicts.Count > 0 && policy == "refuse")
        {
            return (new ImportedActor(key, "conflict", entry, [], $"the editor and the YAML both changed {string.Join(", ", conflicts.Select(c => c.Field))} since the session's base: "
                                                                  + "nothing applied (onConflict=editor or yaml picks a side).", conflicts), null);
        }
        var taken = sides.Select(s => (s.Field, Side: s.Side != Side.Conflict ? s.Side : policy == "editor" ? Side.Editor : Side.Yaml)).ToList();
        var writes = taken.Where(t => t.Side == Side.Editor).Select(t => t.Field).ToList();
        var kept = taken.Where(t => t.Side == Side.Yaml).Select(t => t.Field).ToList();

        var keys = writes.Where(w => w.Name != "rotation").Select(w => (w.Name, w.Write(w.Editor))).ToList();
        // There again in the editor (an undone delete): the build shows it again. Unless the YAML removed it since the base. A
        // vanilla entry shows it by leaving 'removed' out (the file's value): 'removed: false' would keep a fork of nothing alive.
        var stillRemoved = place.Removed && RemovedAfter(editor: false, yaml: true, b?.Deleted);
        var shown = place.Removed && !stillRemoved;
        if (shown && section != "vanilla") keys.Add(("removed", "false"));
        var (edited, changes) = YamlEdits.SetKeys(text, locate(Root(text)), keys, ["location", "yaw", "rotation", "scale"]);
        if (writes.Any(w => w.Name == "rotation"))
        {
            (edited, var turned) = YamlEdits.SetRotation(edited, locate(Reread(edited, entry)), editor, keptPitch, keptRoll);
            changes.AddRange(turned);
        }
        YamlPlace now;
        var root = Reread(edited, entry);
        if (shown && section == "vanilla" && locate(root) is YamlMappingNode { Children.Count: 1 } only)
        {
            // Nothing else left to change: the entry goes, and the actor follows the file again (with no entry left, the file
            // streams again and its fork goes).
            edited = YamlEdits.RemoveEntry(edited, root, section, only);
            Reread(edited, entry);
            changes.Add($"{entry}: removed: true (the entry removed: nothing left to change, the actor takes the file's values)");
            now = new YamlPlace(null, null, null, null, null, false);
        }
        else
        {
            if (shown && section == "vanilla")
            {
                (edited, var unflagged) = YamlEdits.RemoveKeys(edited, locate(root), ["removed"]);
                changes.AddRange(unflagged);
            }
            now = Place(locate(Reread(edited, entry)));
        }
        text = edited;

        // The YAML's values the merge kept become the editor's too: imported again, the editor then has the base's.
        var state = kept.Where(k => k.Name != "yaw").Select(k => k.Name switch
        {
            "location" => ("location", (JsonNode)Doubles(k.Yaml!)),
            "rotation" => ("rotation", Ints(k.Yaml!)),
            "scale" => ("scale", JsonValue.Create(k.Yaml![0])),
            _ => (k.Name, (JsonNode)Doubles(k.Yaml!)),
        }).ToList();
        if (stillRemoved) state.Add(("deleted", JsonValue.Create(true)));
        if (!prop)
        {
            // A pitch, roll or scale npcs and usables don't keep goes back to the base's too: else the installed level never shows the
            // entry's state, and MeleBridge applies it on every load (a tilt that never ships).
            var yaw = kept.FirstOrDefault(k => k.Name == "yaw");
            if (yaw is not null || tilted && baseRotation is not null)
            {
                var (pitch, roll) = baseRotation is not null ? (keptPitch, keptRoll) : (editor[0], editor[2]);
                state.Add(("rotation", Ints([pitch, yaw is not null ? Rotations.Units(yaw.Yaml![0]) : editor[1], roll])));
            }
            if (scaled && b is not null)
            {
                if (actor.Scale is not null) state.Add(("scale", JsonValue.Create(b.Scale ?? 1)));
                if (actor.Scale3D is { Length: 3 }) state.Add(("scale3d", Doubles(Of(b.Scale3D, 3) ?? Unscaled)));
            }
        }
        var update = new SessionUpdate(index, BaseOf(now, actor, newEntry: false, file), Shown(place, actor, file), state);

        var why = new List<string>();
        if (conflicts.Count > 0) why.Add($"the editor and the YAML both changed {string.Join(", ", conflicts.Select(c => c.Field))}: took the {(policy == "yaml" ? "YAML" : "editor")}'s (onConflict={policy})");
        var keptHere = kept.Where(k => conflicts.All(c => c.Field != k.Name)).Select(k => k.Name).Concat(stillRemoved ? ["removed"] : Array.Empty<string>()).ToList();
        if (keptHere.Count > 0) why.Add($"kept the YAML's {string.Join(", ", keptHere)}, changed there since the session's base");
        var reason = why.Count > 0 ? string.Join("; ", why) + "." : changes.Count > 0 ? null : "already where the editor left it.";
        return (new ImportedActor(key, changes.Count > 0 ? "updated" : "unchanged", entry, changes, reason, conflicts.Count > 0 ? conflicts : null), update);
    }

    /// <summary>Which side a field takes: the editor's when the YAML still has the base's (or there is no base), the YAML's when the editor still has it.</summary>
    private static Side Pick(Field field) =>
        field.Yaml is null ? Side.Editor
        : field.Same(field.Editor, field.Yaml) ? Side.Same
        : field.Base is null || field.Same(field.Yaml, field.Base) ? Side.Editor
        : field.Same(field.Editor, field.Base) ? Side.Yaml
        : Side.Conflict;

    /// <summary>
    /// A vanilla actor's edit (docs/design/in-game-editor.md §11), merged into vanilla.&lt;file&gt;.&lt;actor&gt;, which the build ships in a fork of the file. The
    /// file is the actor's level when the YAML streams it, else the vanilla file its level stands for as a fork of this map; null when
    /// it is neither (dev-only). Merged field by field as a props entry, a value the entry leaves out being the file's (through its
    /// archetypes). A new entry gets the fields the editor changed from the file's (deleted: removed: true), and a comment on its key
    /// line naming the mesh (an effect: its class and template) and those vanilla values; against the session's base too when that is
    /// not the file's (an entry deleted by hand since). Refused: what the build would refuse (a delete included), and a room shell (not
    /// an effect). A marked entry whose line is gone was
    /// removed by hand and is never added back: dropped from the session when unchanged since, else skipped. Not one marked where the
    /// file has it: it never had a line.
    /// </summary>
    private static (ImportedActor Result, SessionUpdate? Update)? MergeVanilla(ref string text, SessionActor actor, int index, string key, string map,
        IReadOnlyList<string> streams, VanillaFiles vanilla, HashSet<string> seen, string policy, bool keepSession, string dropFile, List<string> notes)
    {
        var level = actor.Level ?? actor.Path!.Split('.')[0];
        var file = streams.FirstOrDefault(s => s.Equals(level, StringComparison.OrdinalIgnoreCase))
                   ?? streams.FirstOrDefault(s => s.Equals(ForkBuilder.Canonical(map, level), StringComparison.OrdinalIgnoreCase));
        if (file is null) return null;
        var parts = actor.Path!.Split('.');
        if (parts.Length != 4 || parts[1] != "TheWorld" || parts[2] != "PersistentLevel")
        {
            return (new ImportedActor(key, "failed", null, [], $"{actor.Path} is not a level actor's path (<file>.TheWorld.PersistentLevel.<actor>)."), null);
        }
        var name = $"{file}.{parts[3]}";
        var entry = $"vanilla.{name}";
        // A session saved before forks were keyed by their vanilla path may have the actor twice: by the fork's path and by the file's.
        if (!seen.Add(name)) return (new ImportedActor(key, "skipped", entry, [], $"an earlier session entry is the same actor ({name}): that one was merged."), null);

        ExportEntry export;
        IReadOnlyDictionary<int, string> held;
        try
        {
            (export, held) = vanilla.Actor(file, parts[3]);
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        {
            return (new ImportedActor(key, "failed", entry, [], e.Message), null);
        }
        var pose = ForkBuilder.PoseOf(export);
        // An effect has no mesh to measure, whatever its particles span.
        var refused = ForkBuilder.Refusal(export, held, actor.Deleted)
                      ?? (!export.IsA("Emitter") && ForkBuilder.Extent(export.FileRef, export, pose) is var extent and > ForkBuilder.MaxPropExtent
                          ? $"a room shell (half-extent {extent:0}): moving or hiding it opens the room"
                          : null);
        if (refused is not null) return (new ImportedActor(key, "refused", entry, [], $"{refused}. Its session entry stays (the editor re-applies it); nothing ships it."), null);

        Transform ofFile = new([pose.Location.X, pose.Location.Y, pose.Location.Z], [pose.Rotation.Pitch, pose.Rotation.Yaw, pose.Rotation.Roll], pose.Scale,
            [pose.Scale3D.X, pose.Scale3D.Y, pose.Scale3D.Z], false);
        YamlNode Locate(YamlMappingNode root) => ((YamlMappingNode)Value(root, "vanilla")!).Children.First(c => Scalar(c.Key).Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var found = Value(Root(text), "vanilla") is YamlMappingNode section
            ? section.Children.FirstOrDefault(c => Scalar(c.Key).Equals(name, StringComparison.OrdinalIgnoreCase))
            : default;
        if (found.Key is not null)
        {
            var place = Place(found.Value);
            if (!actor.Deleted) return Merge(ref text, "vanilla", entry, Locate, key, place, actor, index, policy, notes, ofFile);
            if (place.Removed)
            {
                return (new ImportedActor(key, "unchanged", entry, [], "already removed: true."), new SessionUpdate(index, BaseOf(place, actor, false, ofFile), Shown(place, actor, ofFile), []));
            }
            if (!RemovedAfter(editor: true, yaml: false, actor.Base?.Deleted))
            {
                return (new ImportedActor(key, "unchanged", entry, [], "kept the YAML's removed: false, changed there since the session's base: the session entry shows the actor again."),
                    new SessionUpdate(index, BaseOf(place, actor, false, ofFile), Shown(place, actor, ofFile), [("deleted", JsonValue.Create(false))]));
            }
            (text, var flagged) = YamlEdits.SetKeys(text, found.Value, [("removed", "true")]);
            return (new ImportedActor(key, "removed", entry, flagged, null),
                new SessionUpdate(index, BaseOf(Place(Locate(Reread(text, entry))), actor, false, ofFile), Shown(place, actor, ofFile), []));
        }

        // Marked where the file has it (an import found nothing to write), the YAML had no entry then either: none is gone.
        var atFile = actor.Base is { Deleted: not true } at && Of(at.Location, 3) is { } placed && SameLocation(placed, ofFile.Location!)
                     && Of(at.Rotation, 3) is { } turned && Rotations.Same(turned, ofFile.Rotation!) && SameScale([at.Scale ?? 1], [pose.Scale])
                     && SameScale(Of(at.Scale3D, 3) ?? Unscaled, ofFile.Scale3D!);
        if (!atFile && (actor.Imported is { ValueKind: JsonValueKind.Object } || actor.Installed is { ValueKind: JsonValueKind.Array } installed && installed.GetArrayLength() > 0))
        {
            // Imported, and its line is gone since: removed by hand (or the file reverted alone). Added again, it would come back on every import.
            const string gone = "imported before, and this file has no vanilla entry for it now: removed by hand, or the file reverted alone";
            if (AsBase(actor) && actor.Deleted == (actor.Base?.Deleted ?? false))
            {
                return (new ImportedActor(key, "skipped", entry, [], keepSession
                        ? $"{gone}. Unchanged since the import: its session entry would be dropped, so the editor stops applying it (keepSession: left as it is)."
                        : $"{gone}. Unchanged since the import: its session entry is dropped, so the editor stops applying it, and kept in {Path.GetFileName(dropFile)}."),
                    new SessionUpdate(index, null, null, [], "a vanilla entry imported, then removed from the file (by hand, or the file reverted alone), unchanged in the editor since"));
            }
            return (new ImportedActor(key, "skipped", entry, [], $"{gone}. Not added again: the editor changed it since the import, so its session entry stays. Add the entry back "
                                                                 + "by hand to ship it, or undo the edit in the editor."), null);
        }

        // New: the fields the editor changed from the file's, so a field left out keeps following the file. Merged three-way like an
        // entry when the session's base is not the file's (the YAML had an entry when the editor first changed the actor, deleted by
        // hand since to give it its vanilla values back): the YAML's side is then the file's, so only what the editor changed since its
        // base is written, and a field the YAML gave up stays the file's (in the session entry too).
        var b = atFile ? null : actor.Base;
        var keys = new List<(string Key, string Value)>();
        var was = new List<string>();
        var state = new List<(string Field, JsonNode Value)>();
        var why = new List<string>();
        var conflicts = new List<FieldConflict>();
        if (actor.Deleted)
        {
            if (RemovedAfter(editor: true, yaml: false, b?.Deleted)) keys.Add(("removed", "true"));
            else
            {
                state.Add(("deleted", JsonValue.Create(false)));
                why.Add("kept the YAML's shown actor: its entry with removed: true, which the session's base had, is gone since (deleted by hand): the session entry shows it again");
            }
        }
        else
        {
            var fields = new List<Field>
            {
                new("location", actor.Location!, ofFile.Location, Of(b?.Location, 3), SameLocation, WriteLocation),
                new("rotation", actor.Rotation!, ofFile.Rotation, Of(b?.Rotation, 3), Rotations.Same, YamlEdits.Rotation),
            };
            if (actor.Scale is { } scale) fields.Add(new("scale", [scale], [pose.Scale], Of(b?.Scale), SameScale, s => YamlEdits.Scale(s[0])));
            if (actor.Scale3D is { Length: 3 } scale3d) fields.Add(new("scale3d", scale3d, ofFile.Scale3D, Of(b?.Scale3D, 3), SameScale, YamlEdits.Scale3D));
            var sides = fields.Select(f => (Field: f, Side: Pick(f))).ToList();
            conflicts = sides.Where(s => s.Side == Side.Conflict)
                .Select(s => new FieldConflict(s.Field.Name, s.Field.Write(s.Field.Base!), s.Field.Write(s.Field.Yaml!), s.Field.Write(s.Field.Editor), policy == "refuse" ? null : policy))
                .ToList();
            if (conflicts.Count > 0 && policy == "refuse")
            {
                return (new ImportedActor(key, "conflict", entry, [], $"the editor changed {string.Join(", ", conflicts.Select(c => c.Field))} since the session's base, and the YAML "
                                                                      + "has no entry for it since (the file's values): nothing applied (onConflict=editor or yaml picks a side).", conflicts), null);
            }
            foreach (var (field, side) in sides.Select(s => (s.Field, Side: s.Side != Side.Conflict ? s.Side : policy == "editor" ? Side.Editor : Side.Yaml)))
            {
                if (side == Side.Yaml)
                {
                    state.Add(field.Name switch
                    {
                        "rotation" => ("rotation", Ints(field.Yaml!)),
                        "scale" => ("scale", JsonValue.Create(field.Yaml![0])),
                        _ => (field.Name, (JsonNode)Doubles(field.Yaml!)),
                    });
                    continue;
                }
                if (side != Side.Editor) continue;
                switch (field.Name)
                {
                    case "location":
                        keys.Add(("location", WriteLocation(actor.Location!)));
                        was.Add($"location {ForkBuilder.Location(pose.Location)}");
                        break;
                    case "rotation":
                        // 'yaw' keeps the file's pitch and roll.
                        keys.Add(Rotations.YawWith(actor.Rotation!, ofFile.Rotation![0], ofFile.Rotation[2]) is { } yaw ? ("yaw", YamlEdits.Degrees((float)yaw)) : ("rotation", YamlEdits.Rotation(actor.Rotation!)));
                        was.Add($"rotation {ForkBuilder.Rotation(pose.Rotation)}");
                        break;
                    case "scale":
                        keys.Add(("scale", YamlEdits.Scale(actor.Scale!.Value)));
                        was.Add($"scale {ForkBuilder.Number(pose.Scale)}");
                        break;
                    case "scale3d":
                        keys.Add(("scale3d", YamlEdits.Scale3D(actor.Scale3D!)));
                        was.Add($"scale3d {ForkBuilder.Scale3D(pose.Scale3D)}");
                        break;
                }
            }
            if (conflicts.Count > 0) why.Add($"the editor changed {string.Join(", ", conflicts.Select(c => c.Field))} since the session's base, and the YAML has no entry for it since: took the {(policy == "yaml" ? "file" : "editor")}'s (onConflict={policy})");
            var keptHere = state.Select(s => s.Field).Where(f => conflicts.All(c => c.Field != f)).ToList();
            if (keptHere.Count > 0) why.Add($"kept the file's {string.Join(", ", keptHere)}: the YAML's entry for it, which had the session's base, is gone since (deleted by hand)");
        }
        var reason = why.Count > 0 ? string.Join("; ", why) + "." : null;
        // What the installed level shows until the next install: the file's own, or the session's base when it had an entry then
        // (Mark keeps that one as 'before').
        if (keys.Count == 0)
        {
            return (new ImportedActor(key, "unchanged", null, [], reason ?? "where the file has it: nothing to write.", conflicts.Count > 0 ? conflicts : null),
                new SessionUpdate(index, ofFile, ofFile, state));
        }
        // An effect by its class and template: "Emitter FX_ME1_dust_card_METR".
        var mesh = export.IsA("Emitter") ? $"{export.ClassName} {ForkBuilder.MeshOf(export)?.ObjectName.Instanced ?? "(no template)"}"
            : ForkBuilder.MeshOf(export)?.ObjectName.Instanced ?? export.ClassName;
        try
        {
            var edited = YamlEdits.AddEntry(text, Root(text), "vanilla", YamlEdits.Quote(name), keys,
                string.Format(CultureInfo.InvariantCulture, VanillaComment, $", here {ForkBuilder.ForkName(map, file)}"), was.Count > 0 ? $"{mesh}; vanilla {string.Join(", ", was)}" : mesh);
            var now = Place(Locate(Reread(edited, entry)));
            text = edited;
            return (new ImportedActor(key, "added", entry, keys.Select(k => $"{k.Key}: {k.Value}").ToList(), reason, conflicts.Count > 0 ? conflicts : null),
                new SessionUpdate(index, BaseOf(now, actor, true, ofFile), ofFile, state));
        }
        catch (ArgumentException e)
        {
            return (new ImportedActor(key, "failed", entry, [], e.Message), null);
        }
    }

    /// <summary>
    /// A light of a light collection (docs/design/in-game-editor.md §12.4: a session entry of kind vanilla), merged into vanilla.&lt;file&gt;.&lt;collection&gt;.&lt;light&gt;:
    /// a light of a file the YAML streams (a light of a fork counts as its vanilla file's), which the build ships in the file's fork, or a
    /// light of the map's own characterLights copy, keyed by the copy's source file (the build writes it into the map). Merged field by
    /// field as a lights entry is (<see cref="MergeLight"/>: location, radius, brightness, color, falloff, enabled), a value the entry
    /// leaves out being the file's (through its archetypes: <see cref="SlcaLights.Read"/>); deleted in the editor, it is switched off.
    /// Rotation and scale are not merged (a light only moves). A new entry holds the fields the editor changed from the file's (against
    /// the session's base too when that is not the file's: an entry deleted by hand since), with the light type and those vanilla values
    /// in a comment on its key line. One imported before whose entry is gone was removed by hand, as for an actor (<see cref="MergeVanilla"/>).
    /// Null when it is neither (dev-only).
    /// </summary>
    private static (ImportedActor Result, SessionUpdate? Update)? MergeVanillaLight(ref string text, SessionActor actor, int index, string key, string map,
        IReadOnlyList<string> streams, IReadOnlyList<(string File, string Collection, string Package, string Actor)> copies, Files files, VanillaFiles vanilla,
        PackageCache cache, HashSet<string> seen, string policy, bool keepSession, string dropFile)
    {
        var parts = actor.Path!.Split('.');
        if (parts.Length != 5 || parts[1] != "TheWorld" || parts[2] != "PersistentLevel")
        {
            return (new ImportedActor(key, "failed", null, [], $"{actor.Path} is not the path of a light of a light collection (<file>.TheWorld.PersistentLevel.<collection>.<light>)."), null);
        }
        var level = actor.Level ?? parts[0];
        var (collection, component) = (parts[3], parts[4]);
        string file;
        ExportEntry light;
        IReadOnlyDictionary<int, string> held = new Dictionary<int, string>();
        Func<IEnumerable<IMEPackage>> shown;
        try
        {
            if (level.Equals(map, StringComparison.OrdinalIgnoreCase))
            {
                // The map's own: a characterLights copy, keyed by the file it is copied from, whose values the build gives back.
                var copy = copies.FirstOrDefault(c => c.Collection.Equals(collection, StringComparison.OrdinalIgnoreCase));
                if (copy.File is null)
                {
                    return (new ImportedActor(key, "skipped", null, [], $"{actor.Path} is a light of {collection} of {map}, which no characterLights item copies: only the lights of "
                                                                          + "a copy are imported (keyed by its source file). The editor re-applies it from the session."), null);
                }
                file = copy.File;
                var source = files.Get(copy.Package) ?? throw new FileNotFoundException($"{copy.Package}, the source of the characterLights copy {collection}, could not be read.");
                var owner = ExportInspector.Resolve(source, copy.Actor.Contains('.') || copy.Actor.StartsWith('#') ? copy.Actor : $"TheWorld.PersistentLevel.{copy.Actor}");
                light = source.FindExport($"{owner.InstancedFullPath}.{component}") ?? throw new ArgumentException($"{copy.Package} has no light {component} in {owner.ObjectName.Instanced}.");
                shown = () => streams.Select(vanilla.Package).OfType<IMEPackage>();
            }
            else
            {
                var streamed = streams.FirstOrDefault(s => s.Equals(level, StringComparison.OrdinalIgnoreCase))
                               ?? streams.FirstOrDefault(s => s.Equals(ForkBuilder.Canonical(map, level), StringComparison.OrdinalIgnoreCase));
                if (streamed is null) return null;
                file = streamed;
                (light, held) = vanilla.Actor(file, $"{collection}.{component}");
                var pcc = light.FileRef;
                shown = () => [pcc];
            }
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        {
            return (new ImportedActor(key, "failed", null, [], e.Message), null);
        }
        var name = $"{file}.{collection}.{component}";
        var entry = $"vanilla.{name}";
        if (!seen.Add(name)) return (new ImportedActor(key, "skipped", entry, [], $"an earlier session entry is the same light ({name}): that one was merged."), null);
        if (ForkBuilder.Refusal(light, held, removed: false) is { } refused)
        {
            return (new ImportedActor(key, "refused", entry, [], $"{refused}. Its session entry stays (the editor re-applies it); nothing ships it."), null);
        }
        if (SlcaLights.Read(light, cache) is not { } ofFile) return (new ImportedActor(key, "failed", entry, [], $"{collection} of {file} has no matrix for {component}."), null);
        if (LightState.Of(actor.Light) is not { Kind: "vanilla" } editor)
        {
            return (new ImportedActor(key, "failed", entry, [], "the session entry's light block needs kind vanilla, radius, brightness, color [r, g, b], falloff and enabled."), null);
        }

        // What the build leaves the light with: the entry's values, the file's where it has none.
        double[] fileLocation = [ofFile.Location.X, ofFile.Location.Y, ofFile.Location.Z];
        var fileColor = ofFile.Color.Select(c => (double)c).ToArray();
        YamlLight Filled(YamlLight? y) => new(y?.Location ?? fileLocation, null, y?.Radius ?? ofFile.Radius, y?.Brightness ?? ofFile.Brightness, y?.Color ?? fileColor,
            y?.Falloff ?? ofFile.Falloff, y?.Enabled ?? ofFile.Enabled);
        Transform StateOf(YamlLight y) => new(y.Location, actor.Rotation!.Select(u => Math.Round(u)).ToArray(), actor.Scale ?? 1, Of(actor.Scale3D, 3) ?? Unscaled, false,
            new LightBlock("vanilla", y.Radius, y.Brightness, y.Color, y.Falloff, y.Enabled, ofFile.Type.ToString()));
        YamlNode Locate(YamlMappingNode root) => ((YamlMappingNode)Value(root, "vanilla")!).Children.First(c => Scalar(c.Key).Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var found = Value(Root(text), "vanilla") is YamlMappingNode section
            ? section.Children.FirstOrDefault(c => Scalar(c.Key).Equals(name, StringComparison.OrdinalIgnoreCase))
            : default;
        var yaml = Filled(found.Key is null ? null : LightOf(found.Value));

        // Marked where the file has it (an import found nothing to write), the YAML had no entry then either: none is gone.
        var atFile = actor.Base is { } at && Of(at.Location, 3) is { } placed && SameLocation(placed, fileLocation) && LightState.Of(at.Light) is { } was
                     && SameRadius([was.Radius], [ofFile.Radius]) && SameLight([was.Brightness], [ofFile.Brightness]) && SameColor(was.Color, fileColor)
                     && SameLight([was.Falloff], [ofFile.Falloff]) && was.Enabled == ofFile.Enabled;
        if (found.Key is null && !atFile
            && (actor.Imported is { ValueKind: JsonValueKind.Object } || actor.Installed is { ValueKind: JsonValueKind.Array } installed && installed.GetArrayLength() > 0))
        {
            // Imported, and its line is gone since: removed by hand (or the file reverted alone). Added again, it would come back on every import.
            const string gone = "imported before, and this file has no vanilla entry for it now: removed by hand, or the file reverted alone";
            if (AsBase(actor))
            {
                return (new ImportedActor(key, "skipped", entry, [], keepSession
                        ? $"{gone}. Unchanged since the import: its session entry would be dropped, so the editor stops applying it (keepSession: left as it is)."
                        : $"{gone}. Unchanged since the import: its session entry is dropped, so the editor stops applying it, and kept in {Path.GetFileName(dropFile)}."),
                    new SessionUpdate(index, null, null, [], "a vanilla light entry imported, then removed from the file (by hand, or the file reverted alone), unchanged in the editor since"));
            }
            return (new ImportedActor(key, "skipped", entry, [], $"{gone}. Not added again: the editor changed it since the import, so its session entry stays. Add the entry back "
                                                                 + "by hand to ship it, or undo the edit in the editor."), null);
        }

        // New: three-way against the file's values when the session's base is not the file's (an entry deleted by hand since).
        var b = found.Key is null && atFile ? null : actor.Base;
        var lit = b?.Light;
        static double[] Flag(bool on) => [on ? 1 : 0];
        var fields = new List<Field>
        {
            new("location", actor.Location!, yaml.Location, Of(b?.Location, 3), SameLocation, WriteLocation),
            new("radius", [editor.Radius], [yaml.Radius!.Value], Of(lit?.Radius), SameRadius, v => YamlEdits.Radius(v[0])),
            new("brightness", [editor.Brightness], [yaml.Brightness!.Value], Of(lit?.Brightness), SameLight, v => YamlEdits.Fraction(v[0])),
            new("color", editor.Color, yaml.Color!, Of(lit?.Color, 3), SameColor, YamlEdits.Color),
            new("falloff", [editor.Falloff], [yaml.Falloff!.Value], Of(lit?.Falloff), SameLight, v => YamlEdits.Fraction(v[0])),
            // What the light shows: deleted in the editor, it is switched off.
            new("enabled", Flag(editor.Enabled && !actor.Deleted), Flag(yaml.Enabled!.Value), lit?.Enabled is { } on ? Flag(on) : null, SameExactly, v => v[0] == 1 ? "true" : "false"),
        };
        var sides = fields.Select(field => (Field: field, Side: Pick(field))).ToList();
        var conflicts = sides.Where(s => s.Side == Side.Conflict)
            .Select(s => new FieldConflict(s.Field.Name, s.Field.Write(s.Field.Base!), s.Field.Write(s.Field.Yaml!), s.Field.Write(s.Field.Editor), policy == "refuse" ? null : policy))
            .ToList();
        var since = found.Key is null ? " since the session's base, and the YAML has no entry for it since (the file's values)" : " since the session's base";
        if (conflicts.Count > 0 && policy == "refuse")
        {
            return (new ImportedActor(key, "conflict", entry, [], $"the editor {(found.Key is null ? "" : "and the YAML both ")}changed {string.Join(", ", conflicts.Select(c => c.Field))}"
                                                                  + $"{since}: nothing applied (onConflict=editor or yaml picks a side).", conflicts), null);
        }
        var taken = sides.Select(s => (s.Field, Side: s.Side != Side.Conflict ? s.Side : policy == "editor" ? Side.Editor : Side.Yaml)).ToList();
        var writes = taken.Where(t => t.Side == Side.Editor).Select(t => (t.Field.Name, t.Field.Write(t.Field.Editor))).ToList();
        var kept = taken.Where(t => t.Side == Side.Yaml).Select(t => t.Field).ToList();

        var edited = text;
        List<string> changes = [];
        if (found.Key is not null) (edited, changes) = YamlEdits.SetKeys(text, Locate(Root(text)), writes, ["location", "radius", "brightness", "color", "falloff"]);
        else if (writes.Count > 0)
        {
            // The vanilla values of what it changes, as the build's receipt names them; on or off goes in with the light type.
            var vanillaValues = writes.Select(w => w.Name switch
            {
                "location" => $"location {ForkBuilder.Location(ofFile.Location)}",
                "radius" => $"radius {ForkBuilder.Number(ofFile.Radius)}",
                "brightness" => $"brightness {ForkBuilder.Number(ofFile.Brightness)}",
                "color" => $"color {SlcaLights.Colour(ofFile.Color)}",
                "falloff" => $"falloff {ForkBuilder.Number(ofFile.Falloff)}",
                _ => null,
            }).OfType<string>().ToList();
            var comment = SlcaLights.Comment(ofFile, editor.Enabled && !actor.Deleted, vanillaValues, shown);
            var fork = streams.Contains(file, StringComparer.OrdinalIgnoreCase) ? $", here {ForkBuilder.ForkName(map, file)}" : "";
            try
            {
                edited = YamlEdits.AddEntry(text, Root(text), "vanilla", YamlEdits.Quote(name), writes, string.Format(CultureInfo.InvariantCulture, VanillaComment, fork), comment);
            }
            catch (ArgumentException e)
            {
                return (new ImportedActor(key, "failed", entry, [], e.Message), null);
            }
            changes = writes.Select(k => $"{k.Name}: {k.Item2}").ToList();
        }
        var now = edited == text && found.Key is null ? yaml : Filled(LightOf(Locate(Reread(edited, entry))));
        text = edited;

        // The YAML's values the merge kept become the editor's too: imported again, the editor then has the base's (see MergeLight).
        var state = new List<(string Field, JsonNode Value)>();
        if (kept.FirstOrDefault(k => k.Name == "location") is { } location) state.Add(("location", Doubles(location.Yaml!)));
        var block = editor.Json();
        foreach (var field in kept.Where(k => k.Name != "location"))
        {
            block[field.Name] = field.Name switch
            {
                "color" => Ints(field.Yaml!),
                "enabled" => field.Yaml![0] == 1,
                _ => field.Yaml![0],
            };
        }
        if (actor.Deleted && now.Enabled == true)
        {
            block["enabled"] = true;
            state.Add(("deleted", JsonValue.Create(false)));
        }
        if (block.ToJsonString() != editor.Json().ToJsonString()) state.Add(("light", block));
        var update = new SessionUpdate(index, StateOf(now), StateOf(yaml), state);

        var why = new List<string>();
        var side = policy == "yaml" ? found.Key is null ? "file" : "YAML" : "editor";
        if (conflicts.Count > 0) why.Add($"the editor {(found.Key is null ? "" : "and the YAML both ")}changed {string.Join(", ", conflicts.Select(c => c.Field))}{since}: took the {side}'s (onConflict={policy})");
        var keptHere = kept.Where(k => conflicts.All(c => c.Field != k.Name)).Select(k => k.Name).ToList();
        if (keptHere.Count > 0)
        {
            why.Add(found.Key is null
                ? $"kept the file's {string.Join(", ", keptHere)}: the YAML's entry for it, which had the session's base, is gone since (deleted by hand)"
                : $"kept the YAML's {string.Join(", ", keptHere)}, changed there since the session's base");
        }
        var reason = why.Count > 0 ? string.Join("; ", why) + "." : changes.Count > 0 ? null : found.Key is null ? "where the file has it: nothing to write." : "already as the editor left it.";
        var action = changes.Count == 0 ? "unchanged" : found.Key is null ? "added" : "updated";
        return (new ImportedActor(key, action, found.Key is null && changes.Count == 0 ? null : entry, changes, reason, conflicts.Count > 0 ? conflicts : null), update);
    }

    /// <summary>
    /// A light's edit (docs/design/in-game-editor.md §11), merged into its lights entry field by field as props are (<see cref="Merge"/>): its location and each
    /// light field, with MeleBridge's tolerances (location 0.01, radius 0.01, brightness and falloff 0.001; kind, colour and enabled
    /// exact). A value the entry leaves out is the build's (kind props, colour white, falloff 2, on); a radius or brightness, the base's.
    /// A spawned light the editor deleted is off: 'enabled: false' in the YAML and the base; its session block keeps its own 'enabled'
    /// with 'deleted' (the YAML switching it on since shows it again). The YAML's values the merge keeps go into the session entry, its
    /// block included. Its rotation and scale are the actor's: not merged.
    /// </summary>
    private static (ImportedActor Result, SessionUpdate? Update) MergeLight(ref string text, string entry, Func<YamlMappingNode, YamlNode> locate, string key,
        SessionActor actor, int index, string policy)
    {
        if (LightState.Of(actor.Light) is not { } editor)
        {
            return (new ImportedActor(key, "failed", entry, [], "the session entry's light block needs kind (props or room), radius, brightness, color [r, g, b], falloff and enabled."), null);
        }
        var before = LightOf(locate(Root(text)));
        var b = actor.Base;
        var at = b?.Light;
        static double[] Flag(bool on) => [on ? 1 : 0];
        static double[] Kind(string kind) => [kind == "room" ? 1 : 0];
        var fields = new List<Field>
        {
            new("location", actor.Location!, before.Location ?? Of(b?.Location, 3), Of(b?.Location, 3), SameLocation, WriteLocation),
            new("kind", Kind(editor.Kind), Kind(before.Kind ?? "props"), at?.Kind is { } k ? Kind(k) : null, SameExactly, v => v[0] == 1 ? "room" : "props"),
            new("radius", [editor.Radius], Of(before.Radius) ?? Of(at?.Radius), Of(at?.Radius), SameRadius, v => YamlEdits.Radius(v[0])),
            new("brightness", [editor.Brightness], Of(before.Brightness) ?? Of(at?.Brightness), Of(at?.Brightness), SameLight, v => YamlEdits.Fraction(v[0])),
            new("color", editor.Color, before.Color ?? White, Of(at?.Color, 3), SameColor, YamlEdits.Color),
            new("falloff", [editor.Falloff], [before.Falloff ?? 2], Of(at?.Falloff), SameLight, v => YamlEdits.Fraction(v[0])),
            // What the light shows: a deleted one is off.
            new("enabled", Flag(editor.Enabled && !actor.Deleted), Flag(before.Enabled ?? true), at?.Enabled is { } on ? Flag(on) : null, SameExactly, v => v[0] == 1 ? "true" : "false"),
        };
        var sides = fields.Select(field => (Field: field, Side: Pick(field))).ToList();
        var conflicts = sides.Where(s => s.Side == Side.Conflict)
            .Select(s => new FieldConflict(s.Field.Name, s.Field.Write(s.Field.Base!), s.Field.Write(s.Field.Yaml!), s.Field.Write(s.Field.Editor), policy == "refuse" ? null : policy))
            .ToList();
        if (conflicts.Count > 0 && policy == "refuse")
        {
            return (new ImportedActor(key, "conflict", entry, [], $"the editor and the YAML both changed {string.Join(", ", conflicts.Select(c => c.Field))} since the session's base: "
                                                                  + "nothing applied (onConflict=editor or yaml picks a side).", conflicts), null);
        }
        var taken = sides.Select(s => (s.Field, Side: s.Side != Side.Conflict ? s.Side : policy == "editor" ? Side.Editor : Side.Yaml)).ToList();
        var writes = taken.Where(t => t.Side == Side.Editor).Select(t => (t.Field.Name, t.Field.Write(t.Field.Editor))).ToList();
        var kept = taken.Where(t => t.Side == Side.Yaml).Select(t => t.Field).ToList();
        var (edited, changes) = YamlEdits.SetKeys(text, locate(Root(text)), writes, ["tag", "kind", "location", "radius", "brightness", "color", "falloff"]);
        var now = LightOf(locate(Reread(edited, entry)));
        text = edited;

        // The YAML's values the merge kept become the editor's too: imported again, the editor then has the base's.
        var state = new List<(string Field, JsonNode Value)>();
        if (kept.FirstOrDefault(k => k.Name == "location") is { } location) state.Add(("location", Doubles(location.Yaml!)));
        var block = editor.Json();
        foreach (var field in kept.Where(k => k.Name != "location"))
        {
            block[field.Name] = field.Name switch
            {
                "kind" => field.Yaml![0] == 1 ? "room" : "props",
                "color" => Ints(field.Yaml!),
                "enabled" => field.Yaml![0] == 1,
                _ => field.Yaml![0],
            };
        }
        // Deleted and off in the YAML, its block keeps the editor's enabled: 'deleted' already shows it off, and a delete undone in the
        // game then shows it as it was. Switched on in the YAML since: shown again.
        if (actor.Deleted && (now.Enabled ?? true))
        {
            block["enabled"] = true;
            state.Add(("deleted", JsonValue.Create(false)));
        }
        if (block.ToJsonString() != editor.Json().ToJsonString()) state.Add(("light", block));
        var update = new SessionUpdate(index, LightBase(now, actor), LightShown(before, actor), state);

        var why = new List<string>();
        if (conflicts.Count > 0) why.Add($"the editor and the YAML both changed {string.Join(", ", conflicts.Select(c => c.Field))}: took the {(policy == "yaml" ? "YAML" : "editor")}'s (onConflict={policy})");
        var keptHere = kept.Where(k => conflicts.All(c => c.Field != k.Name)).Select(k => k.Name).ToList();
        if (keptHere.Count > 0) why.Add($"kept the YAML's {string.Join(", ", keptHere)}, changed there since the session's base");
        var reason = why.Count > 0 ? string.Join("; ", why) + "." : changes.Count > 0 ? null : "already as the editor left it.";
        return (new ImportedActor(key, changes.Count > 0 ? "updated" : "unchanged", entry, changes, reason, conflicts.Count > 0 ? conflicts : null), update);
    }

    /// <summary>
    /// What a lights entry holds now, as the session's base: its location (else the old base's), its light block with the build's
    /// defaults for what it leaves out, deleted false (a light is switched off, never removed); the rotation and scale the actor's.
    /// </summary>
    private static Transform LightBase(YamlLight now, SessionActor actor) => new(
        now.Location ?? Of(actor.Base?.Location, 3) ?? actor.Location,
        (Of(actor.Base?.Rotation, 3) ?? actor.Rotation)!.Select(u => Math.Round(u)).ToArray(),
        actor.Base?.Scale ?? actor.Scale ?? 1,
        Of(actor.Base?.Scale3D, 3) ?? Of(actor.Scale3D, 3) ?? Unscaled,
        false,
        BlockOf(now, actor.Base?.Light ?? actor.Light));

    /// <summary>What a level built from a lights entry shows, for 'imported.before' when the session entry has no base.</summary>
    private static Transform LightShown(YamlLight place, SessionActor actor) =>
        new(place.Location, actor.Rotation!.Select(u => Math.Round(u)).ToArray(), actor.Scale ?? 1, Of(actor.Scale3D, 3) ?? Unscaled, false, BlockOf(place, null));

    /// <summary>A lights entry's values as a session block: the build's defaults for what it leaves out; a radius or brightness, <paramref name="otherwise"/>'s.</summary>
    private static LightBlock BlockOf(YamlLight place, LightBlock? otherwise) =>
        new(place.Kind ?? "props", place.Radius ?? otherwise?.Radius, place.Brightness ?? otherwise?.Brightness, place.Color ?? White, place.Falloff ?? 2, place.Enabled ?? true);

    /// <summary>A lights entry for a light spawned in the editor, named by its tag: every field, colour and falloff included, 'enabled' only when off.</summary>
    private static ImportedActor AddLight(ref string text, SessionActor actor, string? tag, string key)
    {
        if (tag is null) return new ImportedActor(key, "failed", null, [], "spawned without a tag: the YAML entry would have nothing to find it by.");
        if (LightState.Of(actor.Light) is not { } light)
        {
            return new ImportedActor(key, "failed", null, [], "the session entry's light block needs kind (props or room), radius, brightness, color [r, g, b], falloff and enabled.");
        }
        var keys = new List<(string Key, string Value)>
        {
            ("tag", YamlEdits.Quote(tag)),
            ("kind", light.Kind),
            ("location", WriteLocation(actor.Location!)),
            ("radius", YamlEdits.Radius(light.Radius)),
            ("brightness", YamlEdits.Fraction(light.Brightness)),
            ("color", YamlEdits.Color(light.Color)),
            ("falloff", YamlEdits.Fraction(light.Falloff)),
        };
        if (!light.Enabled) keys.Add(("enabled", "false"));
        try
        {
            var edited = YamlEdits.AddEntry(text, Root(text), "lights", YamlEdits.Quote(tag), keys, LightsComment);
            Reread(edited, $"lights.{tag}");
            text = edited;
            return new ImportedActor(key, "added", $"lights.{tag}", keys.Select(k => $"{k.Key}: {k.Value}").ToList(), null);
        }
        catch (ArgumentException e)
        {
            return new ImportedActor(key, "failed", $"lights.{tag}", [], e.Message);
        }
    }

    private const string NpcLightNeeds = "the session entry of an NPC light needs a light block (kind npc, radius, brightness, color [r, g, b], falloff and enabled) "
                                         + "and npc { tag, offset [forward, right, up] }.";

    /// <summary>
    /// An NPC light's edit (docs/design/in-game-editor.md §11), merged into its item of npcs.&lt;name&gt;.lights field by field as a lights entry is
    /// (<see cref="MergeLight"/>): its offset [forward, right, up] from the NPC (each axis within 0.02, written to 2 decimals) in place of a
    /// location, and no kind (it keeps its template's channels). A value the item leaves out is the template's (colour white, falloff 2,
    /// on); a radius or brightness, the base's. The session entry's location follows the offset it ends with, from <paramref name="pose"/>
    /// (where the editor measures from). Its base is the item at the YAML's pose of the NPC (<paramref name="built"/>: what the next build
    /// makes); without a base, 'imported.before' is the item as it was at the pose the YAML had (<paramref name="shown"/>).
    /// </summary>
    private static (ImportedActor Result, SessionUpdate? Update) MergeNpcLight(ref string text, string entry, Func<YamlMappingNode, YamlNode> locate, string key,
        SessionActor actor, int index, string policy, NpcPose? pose, NpcPose? built, NpcPose? shown)
    {
        if (LightState.Of(actor.Light) is not { Kind: "npc" } editor || actor.Npc is not { Offset: { Length: 3 } offset })
        {
            return (new ImportedActor(key, "failed", entry, [], NpcLightNeeds), null);
        }
        var before = LightOf(locate(Root(text)), "offset");
        var b = actor.Base;
        var at = b?.Light;
        static double[] Flag(bool on) => [on ? 1 : 0];
        var fields = new List<Field>
        {
            // An item without one is built at the NPC's centre.
            new("offset", offset, before.Location ?? Origin, Of(b?.Npc?.Offset, 3), SameOffset, YamlEdits.Offset),
            new("radius", [editor.Radius], Of(before.Radius) ?? Of(at?.Radius), Of(at?.Radius), SameRadius, v => YamlEdits.Radius(v[0])),
            new("brightness", [editor.Brightness], Of(before.Brightness) ?? Of(at?.Brightness), Of(at?.Brightness), SameLight, v => YamlEdits.Fraction(v[0])),
            new("color", editor.Color, before.Color ?? White, Of(at?.Color, 3), SameColor, YamlEdits.Color),
            new("falloff", [editor.Falloff], [before.Falloff ?? 2], Of(at?.Falloff), SameLight, v => YamlEdits.Fraction(v[0])),
            // What the light shows: a deleted one is off.
            new("enabled", Flag(editor.Enabled && !actor.Deleted), Flag(before.Enabled ?? true), at?.Enabled is { } on ? Flag(on) : null, SameExactly, v => v[0] == 1 ? "true" : "false"),
        };
        var sides = fields.Select(field => (Field: field, Side: Pick(field))).ToList();
        var conflicts = sides.Where(s => s.Side == Side.Conflict)
            .Select(s => new FieldConflict(s.Field.Name, s.Field.Write(s.Field.Base!), s.Field.Write(s.Field.Yaml!), s.Field.Write(s.Field.Editor), policy == "refuse" ? null : policy))
            .ToList();
        if (conflicts.Count > 0 && policy == "refuse")
        {
            return (new ImportedActor(key, "conflict", entry, [], $"the editor and the YAML both changed {string.Join(", ", conflicts.Select(c => c.Field))} since the session's base: "
                                                                  + "nothing applied (onConflict=editor or yaml picks a side).", conflicts), null);
        }
        var taken = sides.Select(s => (s.Field, Side: s.Side != Side.Conflict ? s.Side : policy == "editor" ? Side.Editor : Side.Yaml)).ToList();
        var writes = taken.Where(t => t.Side == Side.Editor).Select(t => (t.Field.Name, t.Field.Write(t.Field.Editor))).ToList();
        var kept = taken.Where(t => t.Side == Side.Yaml).Select(t => t.Field).ToList();
        var (edited, changes) = YamlEdits.SetKeys(text, locate(Root(text)), writes, ["tag", "offset", "radius", "brightness", "color", "falloff"]);
        var now = LightOf(locate(Reread(edited, entry)), "offset");
        text = edited;

        // The YAML's values the merge kept become the editor's too: imported again, the editor then has the base's. A kept offset
        // takes the location that goes with it; so does a pose the NPC's own entry took back from the YAML.
        var state = new List<(string Field, JsonNode Value)>();
        var keptOffset = kept.FirstOrDefault(k => k.Name == "offset")?.Yaml;
        var ends = keptOffset ?? offset;
        if (keptOffset is not null) state.Add(("npc", new JsonObject { ["tag"] = actor.Npc.Tag, ["offset"] = Doubles(keptOffset) }));
        if (pose is not null && LevelBuilder.NpcLightAt(pose.Location, pose.Yaw, ends) is var place && !SameLocation(place, actor.Location!)) state.Add(("location", Doubles(place)));
        var block = editor.Json();
        foreach (var field in kept.Where(k => k.Name != "offset"))
        {
            block[field.Name] = field.Name switch
            {
                "color" => Ints(field.Yaml!),
                "enabled" => field.Yaml![0] == 1,
                _ => field.Yaml![0],
            };
        }
        // Deleted and off in the YAML, its block keeps the editor's enabled (see MergeLight). Switched on in the YAML since: shown again.
        if (actor.Deleted && (now.Enabled ?? true))
        {
            block["enabled"] = true;
            state.Add(("deleted", JsonValue.Create(false)));
        }
        if (block.ToJsonString() != editor.Json().ToJsonString()) state.Add(("light", block));
        var update = new SessionUpdate(index, NpcLightBase(now, actor, built), NpcLightShown(before, actor, shown), state);

        var why = new List<string>();
        if (conflicts.Count > 0) why.Add($"the editor and the YAML both changed {string.Join(", ", conflicts.Select(c => c.Field))}: took the {(policy == "yaml" ? "YAML" : "editor")}'s (onConflict={policy})");
        var keptHere = kept.Where(k => conflicts.All(c => c.Field != k.Name)).Select(k => k.Name).ToList();
        if (keptHere.Count > 0) why.Add($"kept the YAML's {string.Join(", ", keptHere)}, changed there since the session's base");
        var reason = why.Count > 0 ? string.Join("; ", why) + "." : changes.Count > 0 ? null : "already as the editor left it.";
        return (new ImportedActor(key, changes.Count > 0 ? "updated" : "unchanged", entry, changes, reason, conflicts.Count > 0 ? conflicts : null), update);
    }

    /// <summary>
    /// What an NPC light's item holds now, as the session's base: the light at its NPC's pose in the YAML (<paramref name="built"/>) plus
    /// its offset (else the old base's location, else where it is), its block with the template's values for what it leaves out (a
    /// radius or brightness, the old base's), deleted false; the rotation and scale the actor's.
    /// </summary>
    private static Transform NpcLightBase(YamlLight now, SessionActor actor, NpcPose? built)
    {
        var offset = now.Location ?? Origin;
        return new(built is null ? Of(actor.Base?.Location, 3) ?? actor.Location : LevelBuilder.NpcLightAt(built.Location, built.Yaw, offset),
            (Of(actor.Base?.Rotation, 3) ?? actor.Rotation)!.Select(u => Math.Round(u)).ToArray(),
            actor.Base?.Scale ?? actor.Scale ?? 1,
            Of(actor.Base?.Scale3D, 3) ?? Of(actor.Scale3D, 3) ?? Unscaled,
            false,
            BlockOf(now, actor.Base?.Light ?? actor.Light) with { Kind = "npc" },
            new SessionNpc(actor.Npc?.Tag, offset));
    }

    /// <summary>What a level built from an NPC light's item shows at the NPC's pose then (<paramref name="shown"/>), for 'imported.before' when the session entry has no base.</summary>
    private static Transform NpcLightShown(YamlLight place, SessionActor actor, NpcPose? shown) =>
        new(shown is null ? null : LevelBuilder.NpcLightAt(shown.Location, shown.Yaw, place.Location ?? Origin), actor.Rotation!.Select(u => Math.Round(u)).ToArray(),
            actor.Scale ?? 1, Of(actor.Scale3D, 3) ?? Unscaled, false, BlockOf(place, null) with { Kind = "npc" }, new SessionNpc(actor.Npc?.Tag, place.Location ?? Origin));

    /// <summary>
    /// An item of npcs.&lt;name&gt;.lights for an NPC light spawned in the editor: every field, colour and falloff included, 'enabled'
    /// only when off; the list, with its comment, when the NPC has none.
    /// </summary>
    private static ImportedActor AddNpcLight(ref string text, string name, string npcTag, SessionActor actor, string tag, string key)
    {
        var where = $"npcs.{name}.lights";
        if (LightState.Of(actor.Light) is not { } light || actor.Npc?.Offset is not { Length: 3 } offset) return new ImportedActor(key, "failed", where, [], NpcLightNeeds);
        var keys = new List<(string Key, string Value)>
        {
            ("tag", YamlEdits.Quote(tag)),
            ("offset", YamlEdits.Offset(offset)),
            ("radius", YamlEdits.Radius(light.Radius)),
            ("brightness", YamlEdits.Fraction(light.Brightness)),
            ("color", YamlEdits.Color(light.Color)),
            ("falloff", YamlEdits.Fraction(light.Falloff)),
        };
        if (!light.Enabled) keys.Add(("enabled", "false"));
        try
        {
            var edited = YamlEdits.AppendFlowItem(text, YamlEdits.Npc(Root(text), npcTag)!.Value.Body, "lights", keys, NpcLightsComment);
            Reread(edited, where);
            text = edited;
            return new ImportedActor(key, "added", where, keys.Select(k => $"{k.Key}: {k.Value}").ToList(), null);
        }
        catch (ArgumentException e)
        {
            return new ImportedActor(key, "failed", where, [], e.Message);
        }
    }

    /// <summary>Where the YAML puts the NPC with this tag (its location, its yaw in Unreal units as the build rounds it); null without both.</summary>
    private static NpcPose? YamlPose(YamlMappingNode root, string npcTag)
    {
        if (YamlEdits.Npc(root, npcTag) is not { } npc || Value(npc.Body, "location") is not { } l || Value(npc.Body, "yaw") is not { } yaw) return null;
        var location = Sequence(l).Select(Number).ToArray();
        return location.Length == 3 ? new NpcPose(location, LevelBuilder.Units((float)Number(yaw))) : null;
    }

    /// <summary>What a lights entry gives its light, as level_build reads it; a value it leaves out is null. An NPC light's item has its offset in place of the location (<paramref name="place"/>).</summary>
    private static YamlLight LightOf(YamlNode entry, string place = "location")
    {
        if (entry is not YamlMappingNode body) throw Fail(entry, "the entry must be a mapping.");
        double[]? Three(string key) => Value(body, key) is { } v
            ? Sequence(v).Select(Number).ToArray() is { Length: 3 } xyz ? xyz : throw Fail(v, $"'{key}' needs 3 numbers.")
            : null;
        double? One(string key) => Value(body, key) is { } v ? Number(v) : null;
        bool? enabled = Value(body, "enabled") is { } e ? bool.TryParse(Scalar(e), out var on) ? on : throw Fail(e, "'enabled' is true or false.") : null;
        return new YamlLight(Three(place), Value(body, "kind") is { } kind ? Scalar(kind) : null, One("radius"), One("brightness"), Three("color"), One("falloff"), enabled);
    }

    /// <summary>
    /// The rotation an entry gives its actor, in Unreal units: its 'rotation', or its 'yaw' over the pitch and roll the build keeps
    /// (<paramref name="keep"/>'s for a vanilla entry, the file's; else the base's; 0 for a new stand-in, which starts unturned; a
    /// version 1 editor's own, since it never changed them); null with neither.
    /// </summary>
    private static double[]? Turned(YamlPlace place, SessionActor actor, bool newEntry, double[]? keep = null)
    {
        if (place.Rotation is { } rotation) return rotation.Select(Rotations.Units).ToArray();
        if (place.Yaw is not { } yaw) return null;
        var (pitch, roll) = keep is { Length: 3 } k ? (k[0], k[2])
            : newEntry || actor.Spawned && Of(actor.Base?.Rotation, 3) is null ? (0d, 0d)
            : Of(actor.Base?.Rotation, 3) is { } b ? (b[0], b[2])
            : (actor.Rotation![0], actor.Rotation[2]);
        return [pitch, Rotations.Units(yaw), roll];
    }

    /// <summary>
    /// What the YAML holds for the actor now, as the session's base: a value the entry leaves out is the file's for a vanilla entry
    /// (<paramref name="file"/>), else the old base's, else the editor's (scale: the default); deleted is its removed, for the next
    /// import's merge of it.
    /// </summary>
    private static Transform BaseOf(YamlPlace now, SessionActor actor, bool newEntry, Transform? file = null) => new(
        now.Location ?? Of(file?.Location, 3) ?? Of(actor.Base?.Location, 3) ?? actor.Location,
        (Turned(now, actor, newEntry, Of(file?.Rotation, 3)) ?? Of(file?.Rotation, 3) ?? Of(actor.Base?.Rotation, 3) ?? actor.Rotation)!.Select(u => Math.Round(u)).ToArray(),
        now.Scale ?? file?.Scale ?? actor.Base?.Scale ?? 1,
        now.Scale3D ?? Of(file?.Scale3D, 3) ?? Of(actor.Base?.Scale3D, 3) ?? Unscaled,
        now.Removed);

    /// <summary>
    /// What a level built from an entry shows (rotation in Unreal units; deleted: removed), for 'imported.before' when the session
    /// entry has no base. A value the entry leaves out is left out: the build keeps the actor's own, and MeleBridge then compares
    /// the actor's own (a stand-in of a template StaticMeshActor has that actor's scale, not 1). A vanilla entry's 'yaw' keeps the
    /// file's pitch and roll.
    /// </summary>
    private static Transform Shown(YamlPlace place, SessionActor actor, Transform? file = null) =>
        new(place.Location, Turned(place, actor, newEntry: false, Of(file?.Rotation, 3))?.Select(u => Math.Round(u)).ToArray(), place.Scale, place.Scale3D, place.Removed);

    /// <summary>
    /// Whether the entry is removed after the merge, as the other fields merge: the editor's (deleted) when the YAML still has the
    /// base's, the YAML's when the editor still has it. Without the base's (version 1, or a base written before it had one), the editor's.
    /// Two values that differ can't both differ from a third: never a conflict.
    /// </summary>
    private static bool RemovedAfter(bool editor, bool yaml, bool? atBase) => editor == yaml || atBase is not { } was || yaml == was ? editor : yaml;

    /// <summary>
    /// Whether a session entry still has its base's state: nothing changed in the editor since the import that wrote the base. A light's
    /// block too, as it shows (a deleted light is off); an NPC light's offset, not its location (which follows its NPC).
    /// </summary>
    private static bool AsBase(SessionActor actor) =>
        actor.Base is { } b
        && (actor.IsNpcLight
            ? Of(actor.Npc?.Offset, 3) is { } offset && Of(b.Npc?.Offset, 3) is { } baseOffset && SameOffset(offset, baseOffset)
            : Of(b.Location, 3) is { } location && SameLocation(actor.Location!, location))
        && Of(b.Rotation, 3) is { } rotation
        && Rotations.Same(actor.Rotation!, rotation) && SameScale([actor.Scale ?? 1], [b.Scale ?? 1]) && SameScale(Of(actor.Scale3D, 3) ?? Unscaled, Of(b.Scale3D, 3) ?? Unscaled)
        && (actor.Light is null || LightState.Of(actor.Light) is { } light && LightState.Of(b.Light) is { } was && light.Kind == was.Kind && SameRadius([light.Radius], [was.Radius])
            && SameLight([light.Brightness], [was.Brightness]) && SameColor(light.Color, was.Color) && SameLight([light.Falloff], [was.Falloff])
            && (light.Enabled && !actor.Deleted) == was.Enabled);

    /// <summary>
    /// The YAML after an edit of <paramref name="entry"/>. An edit that leaves it unreadable is this tool's fault, said as such: the
    /// parser's line is one of the edited text, not of the file.
    /// </summary>
    private static YamlMappingNode Reread(string edited, string entry)
    {
        try
        {
            return Root(edited);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // YamlDotNet throws InvalidOperationException for some (an unclosed flow mapping at the end).
            throw new InvalidOperationException($"editor_import's edit of {entry} left the YAML unreadable, a fault of the tool and not of the file (the parser, on the edited "
                                                + $"text: {e.Message}). Nothing was written: edit {entry} by hand, then import again.");
        }
    }

    /// <summary>
    /// A props entry for a mesh spawned in the editor, named by its tag, copying the mesh from the first of these files that has it
    /// as a StaticMesh, placed, turned and scaled as the editor left it. Adds 'propTemplate' when the file has none.
    /// </summary>
    private static ImportedActor AddMesh(MEGame game, Files files, ref string text, SessionActor actor, string? tag, string key, IEnumerable<string> candidates)
    {
        if (tag is null) return new ImportedActor(key, "failed", null, [], "spawned without a tag: the YAML entry would have nothing to find it by.");
        if (actor.Mesh?.Path is not { } meshPath) return new ImportedActor(key, "failed", null, [], "spawned without a mesh: only meshes spawned in the editor become props entries.");

        var misses = new List<string>();
        string? source = null;
        foreach (var candidate in candidates)
        {
            var name = Path.GetFileName(candidate);
            var pcc = files.Get(candidate);
            var export = pcc?.Exports.FirstOrDefault(e => e.InstancedFullPath.Equals(meshPath, StringComparison.OrdinalIgnoreCase));
            if (export is { ClassName: "StaticMesh" })
            {
                source = candidate;
                break;
            }
            misses.Add(pcc is null ? $"{name} not found" : export is null ? $"not in {name}" : $"a {export.ClassName} in {name}");
        }
        if (source is null)
        {
            return new ImportedActor(key, "failed", null, [], $"no StaticMesh {meshPath} to copy ({string.Join("; ", misses)}). pkg_find_name finds the files that have it: "
                                                               + $"add the entry by hand with mesh: {{ package: <file>, export: {meshPath} }}.");
        }

        var keys = new List<(string Key, string Value)>
        {
            ("tag", YamlEdits.Quote(tag)),
            ("mesh", $"{{ package: {YamlEdits.Quote(source)}, export: {YamlEdits.Quote(meshPath)} }}"),
            ("location", WriteLocation(actor.Location!)),
            YamlEdits.RotationKey(actor.Rotation!),
        };
        // A stand-in is built at scale 1: only a scale the editor gave is written.
        if (actor.Scale is { } scale && !SameScale([scale], [1])) keys.Add(("scale", YamlEdits.Scale(scale)));
        if (actor.Scale3D is { Length: 3 } scale3d && !SameScale(scale3d, Unscaled)) keys.Add(("scale3d", YamlEdits.Scale3D(scale3d)));
        try
        {
            var edited = YamlEdits.AddEntry(text, Root(text), "props", YamlEdits.Quote(tag), keys, PropsComment);
            Reread(edited, $"props.{tag}");
            var changes = keys.Select(k => $"{k.Key}: {k.Value}").ToList();
            (edited, var standIn) = YamlEdits.AddPropTemplate(game, edited, source, files.PathOf(source));
            if (standIn is not null) changes.Insert(0, standIn);
            text = edited;
            return new ImportedActor(key, "added", $"props.{tag}", changes, null);
        }
        catch (ArgumentException e)
        {
            return new ImportedActor(key, "failed", $"props.{tag}", [], e.Message);
        }
    }

    /// <summary>What an entry gives its actor. 'yaw' and 'rotation' together are an error, as in level_build.</summary>
    private static YamlPlace Place(YamlNode entry)
    {
        if (entry is not YamlMappingNode body) throw Fail(entry, "the entry must be a mapping.");
        double[]? Three(string key) => Value(body, key) is { } v
            ? Sequence(v).Select(Number).ToArray() is { Length: 3 } xyz ? xyz : throw Fail(v, $"'{key}' needs 3 numbers.")
            : null;
        double? One(string key) => Value(body, key) is { } v ? Number(v) : null;
        if (Value(body, "yaw") is not null && Value(body, "rotation") is { } both)
        {
            throw Fail(both, "'yaw' and 'rotation' together: give one ('rotation' is [pitch, yaw, roll] in degrees, 'yaw' keeps the actor's pitch and roll).");
        }
        return new YamlPlace(Three("location"), One("yaw"), Three("rotation"), One("scale"), Three("scale3d"), Flag(entry, "removed"));
    }

    /// <summary>A number of the YAML as a double: a float would turn 1257.1 into 1257.0999.</summary>
    private static double Number(YamlNode value) =>
        double.TryParse(Scalar(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw Fail(value, "expected a number.");

    private static bool SameOffset(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= OffsetNear + 1e-9);

    private static bool SameLocation(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(Math.Round(p.First, 2) - Math.Round(p.Second, 2)) <= Near + 1e-6);

    private static bool SameScale(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= ScaleNear + 1e-9);

    private static bool SameRadius(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= RadiusNear + 1e-9);

    /// <summary>Brightness and falloff.</summary>
    private static bool SameLight(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= LightNear + 1e-9);

    /// <summary>Colours as whole numbers 0-255.</summary>
    private static bool SameColor(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Round(p.First) == Math.Round(p.Second));

    private static bool SameExactly(double[] a, double[] b) => a.SequenceEqual(b);

    private static string WriteLocation(double[] l) => YamlEdits.Location((float)Math.Round(l[0], 2), (float)Math.Round(l[1], 2), (float)Math.Round(l[2], 2));

    private static double[]? Of(double[]? values, int count) => values?.Length == count ? values : null;

    private static double[]? Of(double? value) => value is { } v ? [v] : null;

    private static JsonArray Doubles(double[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>A number of a session's JSON, whichever way it was written.</summary>
    private static double Num(JsonNode? value) => double.Parse(value!.ToJsonString(), CultureInfo.InvariantCulture);

    private static JsonArray Ints(double[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create((int)Math.Round(v))).ToArray());

    /// <summary>A true/false key of an entry; false when it is missing.</summary>
    private static bool Flag(YamlNode entry, string key) =>
        entry is YamlMappingNode body && Value(body, key) is YamlScalarNode { Value: { } v } && bool.TryParse(v, out var b) && b;

    private static YamlNode? Value(YamlMappingNode mapping, string key) => mapping.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: { } k } && k == key).Value;

    private static string Pcc(string package) => package.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase) ? package : package + ".pcc";

    /// <summary>
    /// Where MeleBridge saves a map's session: %LOCALAPPDATA%\MeleBridge\LE1\sessions\&lt;map&gt;.json, beside levels.json (so a test's
    /// MELE_LEVELS_JSON has its own sessions too).
    /// </summary>
    private static string DefaultSession(string map) => Path.Combine(SessionsFolder(), map + ".json");

    private static string SessionsFolder() => Path.Combine(Path.GetDirectoryName(LevelBuilder.LevelsFilePath())!, "sessions");

    /// <summary>
    /// The session of the map the level is in: &lt;level&gt;.json for a map of its own. A level the game loads into another map
    /// (sideloaded, as on the Normandy) is in that map's session: the newest one with an actor of the level, by its level or by
    /// a tag one of the file's entries has (a mesh spawned in the editor counts by its tag only: it stands in the persistent map,
    /// and an import put it in this file), an NPC light's included, or by an NPC light's NPC (a light spawned on the map is its NPC's,
    /// imported or not). With none, the error names the sessions with spawned meshes: which map loads the
    /// level is not for this tool to guess (another map's spawns would end up in this file).
    /// </summary>
    private static string FindSession(string level, YamlMappingNode root, List<string> notes)
    {
        var own = DefaultSession(level);
        if (File.Exists(own)) return own;
        var folder = Path.GetDirectoryName(own)!;
        var tags = YamlEdits.Tags(root);
        tags.UnionWith(YamlEdits.NpcLightTags(root).Keys);
        var matches = new List<(string File, DateTime Written)>();
        var spawning = new List<string>();
        foreach (var candidate in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json") : [])
        {
            if (candidate.EndsWith(DroppedSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            Session? other;
            try
            {
                other = JsonSerializer.Deserialize<Session>(File.ReadAllText(candidate), Json);
            }
            catch (JsonException)
            {
                continue;
            }
            var actors = other?.Actors ?? [];
            if (actors.Any(a => (!a.Spawned && string.Equals(a.Level ?? a.Path?.Split('.')[0], level, StringComparison.OrdinalIgnoreCase)) || (a.Tag is { } t && tags.Contains(t))
                                || (a.Npc?.Tag is { } npc && tags.Contains(npc))))
            {
                matches.Add((candidate, File.GetLastWriteTimeUtc(candidate)));
            }
            else if (actors.Any(a => a.Spawned)) spawning.Add(Path.GetFileName(candidate));
        }
        if (matches.Count == 0)
        {
            throw new FileNotFoundException($"No session file for {level}: no {own}, and no session in {folder} has an actor of {level}. "
                                            + (spawning.Count > 0
                                                ? $"Sessions with meshes spawned in the editor: {string.Join(", ", spawning)}. Give session=<the one of the map that loads {level}> "
                                                  + "with takeSpawned=true to add them to this file."
                                                : "Press Save in the in-game editor (F8) on that map first, or give 'session' (a level loaded into another map is saved in that map's file)."));
        }
        var newest = matches.MaxBy(m => m.Written);
        notes.Add(matches.Count == 1
            ? $"{level} has no session of its own (the game loads it into another map): took {Path.GetFileName(newest.File)}, which has actors of it."
            : $"Several sessions have actors of {level}: took the newest, {Path.GetFileName(newest.File)} (also {string.Join(", ", matches.Where(m => m != newest).Select(m => Path.GetFileName(m.File)))}). Give 'session' for another.");
        return newest.File;
    }

    /// <summary>
    /// The maps whose session (read only) holds an edit no import has applied yet ('imported' missing) of the NPC with this tag or of its
    /// lights (<paramref name="lightTags"/>, and any light the editor gave it): npc_place moving the NPC would conflict with it.
    /// </summary>
    internal static List<string> UnimportedEdits(string npcTag, IReadOnlyCollection<string> lightTags)
    {
        var folder = SessionsFolder();
        var maps = new List<string>();
        foreach (var file in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json") : [])
        {
            if (file.EndsWith(DroppedSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            Session? session;
            try
            {
                session = JsonSerializer.Deserialize<Session>(File.ReadAllText(file), Json);
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                continue;
            }
            if (session is not { Version: 1 or 2 or 3 }) continue;
            bool Ours(SessionActor a) => a.Tag is { } t && (t.Equals(npcTag, StringComparison.OrdinalIgnoreCase) || lightTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                                         || npcTag.Equals(a.Npc?.Tag, StringComparison.OrdinalIgnoreCase);
            if ((session.Actors ?? []).Any(a => Ours(a) && a.Imported is not { ValueKind: JsonValueKind.Object })) maps.Add(session.Map ?? Path.GetFileNameWithoutExtension(file));
        }
        return maps;
    }

    /// <summary>
    /// Marks the entries this import applied (docs/design/in-game-editor.md §10): 'base' = what the YAML holds now (and 'deleted': whether it removes the
    /// actor), so the next merge starts from it, and 'imported' = { before: the base they had, plus whether the YAML removed them },
    /// the state the installed level still shows: MeleBridge re-applies an entry until the install shows it, then drops it. An entry
    /// without a base (version 1) gets the YAML's entry before this import as 'before'; a props entry this import added, none (no
    /// level has it yet). An entry imported again before an install keeps its 'before'. A mesh spawned and deleted before any import,
    /// or imported and removed from the YAML by hand since, is dropped: nothing ships it. Dropped entries go to the dropped file first
    /// (<see cref="KeepDropped"/>). Left as it is, and null, when the file changed since it was read (the editor saved meanwhile); not
    /// rewritten when nothing changed.
    /// </summary>
    private static int? Mark(string sessionFile, string read, List<SessionUpdate> updates, List<string> notes, string yamlFile, string dropFile)
    {
        if (updates.Count == 0) return 0;
        if (File.ReadAllText(sessionFile) != read) return null;
        var doc = JsonNode.Parse(read)!.AsObject();
        var list = doc["actors"]!.AsArray();
        var marked = 0;
        foreach (var update in updates.Where(u => u.Base is not null))
        {
            var entry = list[update.Index]!.AsObject();
            foreach (var (field, value) in update.State) entry[field] = value;
            if (entry["imported"] is not JsonObject)
            {
                var before = entry["base"] is JsonObject old ? (JsonObject)old.DeepClone() : update.Before is { } shown ? Pose(shown) : null;
                if (before is not null) before["deleted"] = update.Before?.Deleted ?? false;
                entry["imported"] = new JsonObject { ["before"] = before };
            }
            entry["base"] = Pose(update.Base!);
            marked++;
        }
        var dropped = updates.Where(u => u.Base is null).OrderBy(u => u.Index).Select(u => (u.Why, Entry: list[u.Index]!.DeepClone())).ToList();
        foreach (var index in updates.Where(u => u.Base is null).Select(u => u.Index).OrderDescending()) list.RemoveAt(index);
        if (dropped.Count > 0)
        {
            notes.Add($"Dropped {dropped.Count} session entr{(dropped.Count == 1 ? "y" : "ies")} that no YAML builds (a mesh spawned in the editor and deleted before any "
                      + $"import, or an entry imported and no longer in the YAML file: removed by hand, or the file reverted alone), kept in {dropFile}. To add one again, copy "
                      + "its 'entry' into the session's 'actors' with \"base\": null and without 'imported' or 'installed' (game closed), then import again: it becomes a new "
                      + "props or vanilla entry. To undo an import, restore the session file together with the YAML (or try options with keepSession=true).");
        }
        // Marked entries have a base: a version 2 file. An NPC or SLCA light makes it 3 (a MeleBridge without them would apply its entry as a
        // light of a lights section); the file's own version is never lowered.
        var version = doc["version"] is { } v ? (int)Num(v) : 1;
        if (marked > 0) version = Math.Max(version, 2);
        if (list.Any(NewKindOfLight)) version = 3;
        doc["version"] = version;

        var json = doc.ToJsonString(Indented);
        if (json == JsonNode.Parse(read)!.ToJsonString(Indented)) return marked;
        // Before the session loses them: a crash between the two writes leaves an entry in both, never in neither.
        if (dropped.Count > 0) KeepDropped(dropFile, sessionFile, yamlFile, dropped);
        // Written beside it, then moved over it: a crash mid-write leaves the old file whole. Not MeleBridge's .tmp name.
        var temporary = sessionFile + ".import.tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, sessionFile, overwrite: true);
        return marked;
    }

    /// <summary>Whether a session entry, its base, imported.before or an installed state holds a light of the kinds of version 3: npc or vanilla.</summary>
    private static bool NewKindOfLight(JsonNode? entry)
    {
        static bool Kind(JsonNode? state) => state?["light"]?["kind"] is JsonValue kind && kind.GetValueKind() == JsonValueKind.String && kind.GetValue<string>() is "npc" or "vanilla";
        return Kind(entry) || Kind(entry?["base"]) || Kind(entry?["imported"]?["before"]) || entry?["installed"] is JsonArray installed && installed.Any(Kind);
    }

    /// <summary>Where the entries an import drops from a session go: &lt;session&gt;.dropped.json beside it.</summary>
    private static string DroppedFile(string sessionFile) =>
        Path.Combine(Path.GetDirectoryName(sessionFile)!, Path.GetFileNameWithoutExtension(sessionFile) + DroppedSuffix);

    private const string DroppedSuffix = ".dropped.json";

    /// <summary>
    /// Adds the entries an import drops from a session to its dropped file, after those kept before, so none is lost: an entry that
    /// left the YAML file reads as removed by hand, and when the file was reverted alone (git), the entry was the only copy of the
    /// spawn's mesh and place. An unreadable dropped file is kept aside (.bad-&lt;ticks&gt;) and a new one started.
    /// </summary>
    private static void KeepDropped(string dropFile, string sessionFile, string yamlFile, IEnumerable<(string? Why, JsonNode Entry)> entries)
    {
        JsonObject doc;
        try
        {
            doc = File.Exists(dropFile) ? JsonNode.Parse(File.ReadAllText(dropFile)) as JsonObject ?? throw new JsonException("not a JSON object") : new JsonObject();
        }
        catch (JsonException)
        {
            File.Move(dropFile, $"{dropFile}.bad-{DateTime.UtcNow.Ticks}");
            doc = new JsonObject();
        }
        doc["about"] = "Session entries editor_import dropped from the in-game editor's session, with when, from which session, importing which YAML file, and why. "
                       + "To add one again, copy its 'entry' into the session's 'actors' with \"base\": null and without 'imported' or 'installed' (game closed), then "
                       + "import again: a spawn becomes a new props entry, a vanilla actor a new vanilla entry.";
        if (doc["dropped"] is not JsonArray list) doc["dropped"] = list = new JsonArray();
        var at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (var (why, entry) in entries)
        {
            list.Add(new JsonObject { ["at"] = at, ["session"] = sessionFile, ["file"] = yamlFile, ["why"] = why, ["entry"] = entry });
        }
        var temporary = dropFile + ".import.tmp";
        File.WriteAllText(temporary, doc.ToJsonString(Readable), new UTF8Encoding(false));
        File.Move(temporary, dropFile, overwrite: true);
    }

    /// <summary>A transform as a session entry's base or imported.before has it; a value it lacks is left out (MeleBridge takes the actor's).</summary>
    private static JsonObject Pose(Transform transform)
    {
        var pose = new JsonObject();
        if (transform.Location is { } location) pose["location"] = Doubles(location);
        if (transform.Rotation is { } rotation) pose["rotation"] = Ints(rotation);
        if (transform.Scale is { } scale) pose["scale"] = scale;
        if (transform.Scale3D is { } scale3d) pose["scale3d"] = Doubles(scale3d);
        if (transform.Deleted is { } deleted) pose["deleted"] = deleted;
        if (transform.Light is { } light)
        {
            var block = new JsonObject { ["kind"] = light.Kind };
            if (light.Radius is { } radius) block["radius"] = radius;
            if (light.Brightness is { } brightness) block["brightness"] = brightness;
            if (light.Color is { } color) block["color"] = Ints(color);
            if (light.Falloff is { } falloff) block["falloff"] = falloff;
            if (light.Enabled is { } enabled) block["enabled"] = enabled;
            if (light.Type is { } type) block["type"] = type;
            pose["light"] = block;
        }
        if (transform.Npc is { } npc) pose["npc"] = new JsonObject { ["tag"] = npc.Tag, ["offset"] = npc.Offset is { } offset ? Doubles(offset) : null };
        return pose;
    }

    private static (Session Session, string Text) Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"No session file {path}: press Save in the in-game editor (F8) on that map first, or give 'session'.");
        Session? session;
        var text = File.ReadAllText(path);
        try
        {
            session = JsonSerializer.Deserialize<Session>(text, Json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"{path} is not a session file of the in-game editor: {e.Message}");
        }
        if (session is null) throw new ArgumentException($"{path} is empty.");
        if (session.Version is not (1 or 2 or 3))
        {
            throw new ArgumentException($"{path} is a version {session.Version} session; editor_import reads versions 1 to 3. Deploy mele-mcp and MeleBridge from the same commit.");
        }
        return (session, text);
    }

    /// <summary>
    /// Packages opened once per import: the meshes of a map mostly come from the same few files. <paramref name="ownName"/>, the
    /// level as the YAML names it, is read from <paramref name="ownPath"/>, where level_build finds it.
    /// </summary>
    private sealed class Files(MEGame game, string ownName, string? ownPath) : IDisposable
    {
        private readonly Dictionary<string, IMEPackage?> open = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The file read for a package named so.</summary>
        public string PathOf(string package) => ownPath is not null && package == ownName ? ownPath : package;

        /// <summary>A file as level_build opens a mesh's package (a path, or a file the game loads); null when there is none.</summary>
        public IMEPackage? Get(string package)
        {
            if (open.TryGetValue(package, out var pcc)) return pcc;
            try
            {
                // A missing path is not looked up by its name: that finds the installed copy, not the file the YAML names.
                var path = PathOf(package);
                pcc = Path.IsPathRooted(path) && !File.Exists(path) ? null : PackageInspector.Open(game, path);
            }
            catch (FileNotFoundException)
            {
                pcc = null;
            }
            return open[package] = pcc;
        }

        public void Dispose()
        {
            foreach (var pcc in open.Values) pcc?.Dispose();
        }
    }

    /// <summary>The vanilla files forks start from (<see cref="ForkBuilder.VanillaSource"/>), each read once per import with the exports its Kismet holds.</summary>
    private sealed class VanillaFiles(MEGame game, string? ownDlc, Files files)
    {
        private readonly Dictionary<string, (IMEPackage Pcc, IReadOnlyDictionary<int, string> Held)> open = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The file's actor of that name (a light of a light collection: &lt;collection&gt;.&lt;light&gt;); throws when the file or the actor is missing.</summary>
        public (ExportEntry Actor, IReadOnlyDictionary<int, string> Held) Actor(string file, string name)
        {
            var vanilla = Open(file);
            return (vanilla.Pcc.FindExport($"TheWorld.PersistentLevel.{name}") ?? throw new ArgumentException($"{file} has no {(name.Contains('.') ? "light" : "actor")} {name}."),
                vanilla.Held);
        }

        /// <summary>The file's vanilla copy; null when there is none.</summary>
        public IMEPackage? Package(string file)
        {
            try
            {
                return Open(file).Pcc;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private (IMEPackage Pcc, IReadOnlyDictionary<int, string> Held) Open(string file)
        {
            if (open.TryGetValue(file, out var vanilla)) return vanilla;
            var source = ForkBuilder.VanillaSource(game, file, ownDlc);
            var pcc = files.Get(source) ?? throw new FileNotFoundException($"{source} could not be read.");
            return open[file] = (pcc, ForkBuilder.KismetHeld(pcc));
        }
    }
}
