using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using YamlDotNet.RepresentationModel;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <param name="Key">The actor's tag, or its object path when it has none.</param>
/// <param name="Action">updated, added, removed, unchanged, conflict (refused: nothing applied), skipped or failed.</param>
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
/// Merges what the in-game editor saved (MeleBridge's session file, one per map; docs/07 §3.7, §10) into a level YAML file, keyed by
/// tag, so a rebuild keeps it: moved, turned or scaled actors get their location, rotation and scale, field by field against the
/// state the YAML held when the editor first changed them (a value changed in the YAML since is kept, both changed is a conflict);
/// meshes spawned in the editor get a props entry, deleted props 'removed: true' (the build hides their actor). Vanilla actors are
/// skipped: moving or deleting them ships with forked files (docs/07 §4, phase 4).
/// </summary>
public static class EditorImport
{
    private const string PropsComment = "Meshes placed with the in-game editor (F8, Save, then editor_import).";

    /// <summary>Locations this close after rounding to 2 decimals are one; scales within <see cref="ScaleNear"/>.</summary>
    private const double Near = 0.01;
    private const double ScaleNear = 0.001;

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
    private sealed record Transform(double[]? Location, double[]? Rotation, double? Scale, double[]? Scale3D, bool? Deleted = null);

    /// <summary>One actor's latest state (version 1: location, rotation and deleted only).</summary>
    /// <param name="Rotation">Pitch, yaw, roll in Unreal units (65536 = 360°).</param>
    /// <param name="Base">What the YAML holds for the actor, for the three-way merge: its state before the editor first changed it, or
    /// what an import wrote. Null in version 1 and for a spawn never imported.</param>
    /// <param name="Imported">Present once an import applied the entry: MeleBridge re-applies it until the install shows it, then drops it.</param>
    private sealed record SessionActor(string? Tag, string? Path, string? Level, bool Spawned, SessionMesh? Mesh, double[]? Location, double[]? Rotation,
        double? Scale, double[]? Scale3D, bool Deleted, Transform? Base, JsonElement? Imported);

    /// <param name="File">The package the game loaded the mesh from (a name without .pcc), when the editor knows it.</param>
    private sealed record SessionMesh(string? Path, string? File);

    /// <summary>What an entry gives its actor (rotation and yaw in degrees); a value it leaves out is null.</summary>
    private sealed record YamlPlace(double[]? Location, double? Yaw, double[]? Rotation, double? Scale, double[]? Scale3D, bool Removed);

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

        // A spawned mesh's file as the YAML names it: the level by its own path (by name, the game finds the installed copy), others by name.
        var own = Path.Combine(Path.GetDirectoryName(file)!, GamePaths.ToWindowsPath(package));
        own = File.Exists(own) ? Path.GetFullPath(own) : GamePaths.ToWindowsPath(package);
        string FileOf(string name) => Path.GetFileNameWithoutExtension(name).Equals(level, StringComparison.OrdinalIgnoreCase) ? own : Pcc(name);
        // Where it may come from when the session does not say: the files the level streams, then the level itself, last because
        // it only has the mesh from an earlier build (a level built again from nothing would not).
        var streamed = Value(root, "streaming") is YamlMappingNode streaming && Value(streaming, "levels") is YamlSequenceNode levels
            ? levels.Children.Select(l => FileOf(Scalar(l)))
            : [];
        var fallbacks = streamed.Append(own).ToList();

        using var files = new Files(game);
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
                said.Add($"The session is of map {session.Map} and the file builds {level}: actors are imported when one of its entries has their tag"
                         + (spawnedHere ? ", and meshes spawned in the editor become props entries of this file (takeSpawned)." : "; meshes spawned in the editor are not (takeSpawned=true adds them here)."));
            }

            var actors = session.Actors ?? [];
            var results = new ImportedActor?[actors.Count];
            // In the YAML now (marked imported), or never to be (dropped): written into the session after the YAML.
            var updates = new List<SessionUpdate>();
            for (var i = 0; i < actors.Count; i++)
            {
                var actor = actors[i];
                var tag = actor.Tag is null or "" or "None" ? null : actor.Tag;
                var key = tag ?? actor.Path ?? $"actors[{i}]";
                if (actor.Path is null || actor.Location is not { Length: 3 } || actor.Rotation is not { Length: 3 })
                {
                    results[i] = new ImportedActor(key, "failed", null, [], "needs 'path', 'location' [x, y, z] and 'rotation' [pitch, yaw, roll].");
                    continue;
                }
                var actorLevel = actor.Level ?? actor.Path.Split('.')[0];
                var ours = actorLevel.Equals(level, StringComparison.OrdinalIgnoreCase);
                var found = tag is null ? null : YamlEdits.Tagged(Root(text), tag);
                // A spawn gets a base from an import only, and keeps it through a Save (which drops 'imported'): with one, an import
                // put it in a YAML file already.
                var imported = actor.Spawned && (actor.Base is not null || actor.Imported is { ValueKind: JsonValueKind.Object });
                if (found is { } f)
                {
                    var entry = $"{f.Section}.{Scalar(f.Entry.Key)}";
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
                    (results[i], var update) = Merge(ref text, f.Section, entry, tag!, key, place, actor, i, policy, said);
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
                            ? $"{elsewhere}. Deleted in the editor: its session entry stays, so importing the file that has it marks it removed: true."
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
                else if (actor.Spawned)
                {
                    var loaded = actor.Mesh?.File is { } loadedFrom && FileOf(loadedFrom) is var from && from != own ? [from] : Array.Empty<string>();
                    results[i] = AddMesh(game, files, ref text, actor, tag, key, loaded.Concat(fallbacks).Distinct(StringComparer.OrdinalIgnoreCase));
                    if (results[i]!.Action == "added")
                    {
                        updates.Add(new SessionUpdate(i, BaseOf(Place(YamlEdits.Tagged(Reread(text, $"props.{tag}"), tag!)!.Value.Entry.Value), actor, newEntry: true), null, []));
                    }
                }
                else
                {
                    results[i] = new ImportedActor(key, "skipped", null, [], ours
                        ? $"{actor.Path} (an actor of {level}) has no npcs, usables or props entry{(tag is null ? "" : $" with tag '{tag}'")}: the YAML does not place it that way. Edit it in the YAML by hand."
                        : tag is not null && !ownMap
                            ? $"{actor.Path}, an actor of {actorLevel}: no entry of this file has tag '{tag}'. Another level file of the map has it, or it is vanilla (phase 4, docs/07 §4)."
                            : $"a vanilla actor ({actor.Path}, in {actorLevel}): moving or deleting vanilla actors ships in phase 4 (docs/07 §4).");
                }
            }
            return new Merged(text, sessionText, results.Select(r => r!).ToList(), updates, said);
        }
    }

    /// <summary>
    /// Merges an actor's place into the entry with its tag, field by field (docs/07 §10): the editor's value when the YAML still has
    /// the base's, the YAML's when the editor still has it (the session entry takes it too, so importing again keeps it), and on a
    /// conflict what <paramref name="policy"/> says: refuse leaves the actor out. Without a base (version 1, or a spawn never
    /// imported), the editor's value wins. 'removed' merges the same way against the base's 'deleted'. Props take a full rotation
    /// and scale; npcs and usables a location and a yaw (the session entry takes back a pitch, roll or scale they don't keep).
    /// </summary>
    private static (ImportedActor Result, SessionUpdate? Update) Merge(ref string text, string section, string entry, string tag, string key, YamlPlace place,
        SessionActor actor, int index, string policy, List<string> notes)
    {
        var prop = section == "props";
        var b = actor.Base;
        var editor = actor.Rotation!;
        var baseRotation = Of(b?.Rotation, 3);
        var (keptPitch, keptRoll) = baseRotation is { } br ? (br[0], br[2]) : (0d, 0d);
        var fields = new List<Field> { new("location", actor.Location!, place.Location ?? Of(b?.Location, 3), Of(b?.Location, 3), SameLocation, WriteLocation) };
        var (tilted, scaled) = (false, false);
        if (prop)
        {
            fields.Add(new("rotation", editor, Turned(place, actor, newEntry: false) ?? baseRotation, baseRotation, Rotations.Same, YamlEdits.Rotation));
            // A value the YAML leaves out is the base's; with no base, the build's default.
            if (actor.Scale is { } scale) fields.Add(new("scale", [scale], Of(place.Scale) ?? Of(b?.Scale) ?? [1], Of(b?.Scale), SameScale, s => YamlEdits.Scale(s[0])));
            if (actor.Scale3D is { Length: 3 } scale3d) fields.Add(new("scale3d", scale3d, place.Scale3D ?? Of(b?.Scale3D, 3) ?? Unscaled, Of(b?.Scale3D, 3), SameScale, YamlEdits.Scale3D));
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
        // There again in the editor (an undone delete): the build shows it again. Unless the YAML removed it since the base.
        var stillRemoved = place.Removed && RemovedAfter(editor: false, yaml: true, b?.Deleted);
        if (place.Removed && !stillRemoved) keys.Add(("removed", "false"));
        var (edited, changes) = YamlEdits.SetKeys(text, YamlEdits.Tagged(Root(text), tag)!.Value.Entry.Value, keys, ["location", "yaw", "rotation", "scale"]);
        if (writes.Any(w => w.Name == "rotation"))
        {
            (edited, var turned) = YamlEdits.SetRotation(edited, YamlEdits.Tagged(Reread(edited, entry), tag)!.Value.Entry.Value, editor, keptPitch, keptRoll);
            changes.AddRange(turned);
        }
        var now = Place(YamlEdits.Tagged(Reread(edited, entry), tag)!.Value.Entry.Value);
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
        var update = new SessionUpdate(index, BaseOf(now, actor, newEntry: false), Shown(place, actor), state);

        var why = new List<string>();
        if (conflicts.Count > 0) why.Add($"the editor and the YAML both changed {string.Join(", ", conflicts.Select(c => c.Field))}: took the {(policy == "yaml" ? "YAML" : "editor")}'s (onConflict={policy})");
        var keptHere = kept.Where(k => conflicts.All(c => c.Field != k.Name)).Select(k => k.Name).Concat(stillRemoved ? ["removed"] : Array.Empty<string>()).ToList();
        if (keptHere.Count > 0) why.Add($"kept the YAML's {string.Join(", ", keptHere)}, changed there since the session's base");
        var reason = why.Count > 0 ? string.Join("; ", why) + "." : changes.Count > 0 ? null : "already where the editor left it.";
        return (new ImportedActor(key, changes.Count > 0 ? "updated" : "unchanged", entry, changes, reason, conflicts.Count > 0 ? conflicts : null), update);

        Side Pick(Field field) =>
            field.Yaml is null ? Side.Editor
            : field.Same(field.Editor, field.Yaml) ? Side.Same
            : field.Base is null || field.Same(field.Yaml, field.Base) ? Side.Editor
            : field.Same(field.Editor, field.Base) ? Side.Yaml
            : Side.Conflict;
    }

    /// <summary>
    /// The rotation an entry gives its actor, in Unreal units: its 'rotation', or its 'yaw' over the pitch and roll the build keeps
    /// (the base's; 0 for a new stand-in, which starts unturned; a version 1 editor's own, since it never changed them); null with neither.
    /// </summary>
    private static double[]? Turned(YamlPlace place, SessionActor actor, bool newEntry)
    {
        if (place.Rotation is { } rotation) return rotation.Select(Rotations.Units).ToArray();
        if (place.Yaw is not { } yaw) return null;
        var (pitch, roll) = newEntry || actor.Spawned && Of(actor.Base?.Rotation, 3) is null ? (0d, 0d)
            : Of(actor.Base?.Rotation, 3) is { } b ? (b[0], b[2])
            : (actor.Rotation![0], actor.Rotation[2]);
        return [pitch, Rotations.Units(yaw), roll];
    }

    /// <summary>
    /// What the YAML holds for the actor now, as the session's base: a value the entry leaves out is the old base's, else the editor's
    /// (scale: the default); deleted is its removed, for the next import's merge of it.
    /// </summary>
    private static Transform BaseOf(YamlPlace now, SessionActor actor, bool newEntry) => new(
        now.Location ?? Of(actor.Base?.Location, 3) ?? actor.Location,
        (Turned(now, actor, newEntry) ?? Of(actor.Base?.Rotation, 3) ?? actor.Rotation)!.Select(u => Math.Round(u)).ToArray(),
        now.Scale ?? actor.Base?.Scale ?? 1,
        now.Scale3D ?? Of(actor.Base?.Scale3D, 3) ?? Unscaled,
        now.Removed);

    /// <summary>
    /// What a level built from an entry shows (rotation in Unreal units; deleted: removed), for 'imported.before' when the session
    /// entry has no base. A value the entry leaves out is left out: the build keeps the actor's own, and MeleBridge then compares
    /// the actor's own (a stand-in of a template StaticMeshActor has that actor's scale, not 1).
    /// </summary>
    private static Transform Shown(YamlPlace place, SessionActor actor) =>
        new(place.Location, Turned(place, actor, newEntry: false)?.Select(u => Math.Round(u)).ToArray(), place.Scale, place.Scale3D, place.Removed);

    /// <summary>
    /// Whether the entry is removed after the merge, as the other fields merge: the editor's (deleted) when the YAML still has the
    /// base's, the YAML's when the editor still has it. Without the base's (version 1, or a base written before it had one), the editor's.
    /// Two values that differ can't both differ from a third: never a conflict.
    /// </summary>
    private static bool RemovedAfter(bool editor, bool yaml, bool? atBase) => editor == yaml || atBase is not { } was || yaml == was ? editor : yaml;

    /// <summary>Whether a session entry still has its base's state: nothing changed in the editor since the import that wrote the base.</summary>
    private static bool AsBase(SessionActor actor) =>
        actor.Base is { } b && Of(b.Location, 3) is { } location && SameLocation(actor.Location!, location) && Of(b.Rotation, 3) is { } rotation
        && Rotations.Same(actor.Rotation!, rotation) && SameScale([actor.Scale ?? 1], [b.Scale ?? 1]) && SameScale(Of(actor.Scale3D, 3) ?? Unscaled, Of(b.Scale3D, 3) ?? Unscaled);

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
            (edited, var standIn) = YamlEdits.AddPropTemplate(game, edited, source);
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

    private static bool SameLocation(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(Math.Round(p.First, 2) - Math.Round(p.Second, 2)) <= Near + 1e-6);

    private static bool SameScale(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= ScaleNear + 1e-9);

    private static string WriteLocation(double[] l) => YamlEdits.Location((float)Math.Round(l[0], 2), (float)Math.Round(l[1], 2), (float)Math.Round(l[2], 2));

    private static double[]? Of(double[]? values, int count) => values?.Length == count ? values : null;

    private static double[]? Of(double? value) => value is { } v ? [v] : null;

    private static JsonArray Doubles(double[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonArray Ints(double[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create((int)Math.Round(v))).ToArray());

    /// <summary>A true/false key of an entry; false when it is missing.</summary>
    private static bool Flag(YamlNode entry, string key) =>
        entry is YamlMappingNode body && Value(body, key) is YamlScalarNode { Value: { } v } && bool.TryParse(v, out var b) && b;

    private static YamlNode? Value(YamlMappingNode mapping, string key) => mapping.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: { } k } && k == key).Value;

    private static string Pcc(string package) => package.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase) ? package : package + ".pcc";

    /// <summary>Where MeleBridge saves a map's session: %LOCALAPPDATA%\MeleBridge\LE1\sessions\&lt;map&gt;.json.</summary>
    private static string DefaultSession(string map) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeleBridge", "LE1", "sessions", map + ".json");

    /// <summary>
    /// The session of the map the level is in: &lt;level&gt;.json for a map of its own. A level the game loads into another map
    /// (sideloaded, as on the Normandy) is in that map's session: the newest one with an actor of the level, by its level or by
    /// a tag one of the file's entries has (a mesh spawned in the editor counts by its tag only: it stands in the persistent map,
    /// and an import put it in this file). With none, the error names the sessions with spawned meshes: which map loads the
    /// level is not for this tool to guess (another map's spawns would end up in this file).
    /// </summary>
    private static string FindSession(string level, YamlMappingNode root, List<string> notes)
    {
        var own = DefaultSession(level);
        if (File.Exists(own)) return own;
        var folder = Path.GetDirectoryName(own)!;
        var tags = YamlEdits.Tags(root);
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
            if (actors.Any(a => (!a.Spawned && string.Equals(a.Level ?? a.Path?.Split('.')[0], level, StringComparison.OrdinalIgnoreCase)) || (a.Tag is { } t && tags.Contains(t))))
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
    /// Marks the entries this import applied (docs/07 §10): 'base' = what the YAML holds now (and 'deleted': whether it removes the
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
            notes.Add($"Dropped {dropped.Count} session entr{(dropped.Count == 1 ? "y" : "ies")} of meshes spawned in the editor that no YAML builds (deleted before any import, "
                      + $"or imported and no longer in the YAML file: removed by hand, or the file reverted alone), kept in {dropFile}. To add one again, copy its 'entry' into "
                      + "the session's 'actors' with \"base\": null and without 'imported' (game closed), then import again: it becomes a new props entry. To undo an "
                      + "import, restore the session file together with the YAML (or try options with keepSession=true).");
        }
        // Marked entries have a base: a version 2 file.
        if (marked > 0) doc["version"] = 2;

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
                       + "To add a spawn again, copy its 'entry' into the session's 'actors' with \"base\": null and without 'imported' (game closed), then import again: "
                       + "it becomes a new props entry.";
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
        if (session.Version is not (1 or 2))
        {
            throw new ArgumentException($"{path} is a version {session.Version} session; editor_import reads versions 1 and 2. Deploy mele-mcp and MeleBridge from the same commit.");
        }
        return (session, text);
    }

    /// <summary>Packages opened once per import: the meshes of a map mostly come from the same few files.</summary>
    private sealed class Files(MEGame game) : IDisposable
    {
        private readonly Dictionary<string, IMEPackage?> open = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A file as level_build opens a mesh's package (a path, or a file the game loads); null when there is none.</summary>
        public IMEPackage? Get(string package)
        {
            if (open.TryGetValue(package, out var pcc)) return pcc;
            try
            {
                // A missing path is not looked up by its name: that finds the installed copy, not the file the YAML names.
                pcc = Path.IsPathRooted(package) && !File.Exists(package) ? null : PackageInspector.Open(game, package);
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
}
