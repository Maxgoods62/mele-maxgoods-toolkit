using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using YamlDotNet.RepresentationModel;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <param name="Key">The actor's tag, or its object path when it has none.</param>
/// <param name="Action">updated, added, removed, unchanged, skipped or failed.</param>
/// <param name="Entry">The YAML entry, as section.name.</param>
/// <param name="Changes">What changed in the YAML.</param>
/// <param name="Reason">Why it was left unchanged, skipped or failed.</param>
public sealed record ImportedActor(string Key, string Action, string? Entry, IReadOnlyList<string> Changes, string? Reason);

/// <param name="Written">Whether the YAML file changed; importing the same session again changes nothing.</param>
/// <param name="Retired">Session entries dropped because the YAML now has them, so a later import can't put them back over newer YAML edits.</param>
public sealed record EditorImportResult(string File, string Session, bool Written, IReadOnlyList<ImportedActor> Actors, int Retired, LevelBuildResult? Build, IReadOnlyList<string> Notes);

/// <summary>
/// Merges what the in-game editor saved (MeleBridge's session file, one per map; docs/07 §3.7) into a level YAML file, keyed by
/// tag, so a rebuild keeps it: moved actors get their location and yaw, meshes spawned in the editor a props entry, deleted
/// props 'removed: true' (the build hides their actor). Vanilla actors are skipped: moving or deleting them ships with forked files (docs/07 §4, phase 4).
/// </summary>
public static class EditorImport
{
    private const int SessionVersion = 1;
    private const string PropsComment = "Meshes placed with the in-game editor (F8, Save, then editor_import).";
    private const float Near = 0.01f;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Session(int Version, string? Map, List<SessionActor>? Actors);

    /// <param name="Rotation">Pitch, yaw, roll in Unreal units (65536 = 360°).</param>
    private sealed record SessionActor(string? Tag, string? Path, string? Level, bool Spawned, SessionMesh? Mesh, float[]? Location, float[]? Rotation, bool Deleted);

    /// <param name="File">The package the game loaded the mesh from (a name without .pcc), when the editor knows it.</param>
    private sealed record SessionMesh(string? Path, string? File);

    /// <param name="takeSpawned">Whether meshes spawned in the editor become props entries of this file; by default, when the
    /// session is of the map this file builds. A level the game loads into another map (sideloaded) has to be asked, or two
    /// files imported from one session would both build the prop.</param>
    /// <param name="keepSession">Leave the session file as it is, instead of dropping the entries this import applied.</param>
    public static EditorImportResult Run(MEGame game, string yamlPath, string? sessionPath, bool build, bool? takeSpawned = null, bool keepSession = false)
    {
        if (!game.IsGame1()) throw new ArgumentException("editor_import handles Game 1 (LE1) levels.");
        var file = Path.GetFullPath(GamePaths.ToWindowsPath(yamlPath));
        if (!File.Exists(file)) throw new FileNotFoundException($"No file {file}.");
        var original = File.ReadAllText(file);
        var text = original;
        var root = Root(text);
        var package = Value(root, "package") is { } p ? Scalar(p) : throw new ArgumentException("The file has no 'package'; is it a level_build file?");
        var level = Path.GetFileNameWithoutExtension(GamePaths.ToWindowsPath(package));
        var notes = new List<string>();
        var sessionFile = sessionPath is null ? FindSession(level, root, notes) : Path.GetFullPath(GamePaths.ToWindowsPath(sessionPath));
        var (session, sessionText) = Read(sessionFile);
        var ownMap = session.Map is null || session.Map.Equals(level, StringComparison.OrdinalIgnoreCase);
        var spawnedHere = takeSpawned ?? ownMap;
        if (!ownMap)
        {
            notes.Add($"The session is of map {session.Map} and the file builds {level}: actors are imported when one of its entries has their tag"
                      + (spawnedHere ? ", and meshes spawned in the editor become props entries of this file (takeSpawned)." : "; meshes spawned in the editor are not (takeSpawned=true adds them here)."));
        }

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

        var actors = session.Actors ?? [];
        var results = new ImportedActor?[actors.Count];
        // In the YAML now, or never to be: dropped from the session after the YAML is written.
        var applied = new bool[actors.Count];
        using var files = new Files(game);
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
            var location = actor.Location.Select(v => MathF.Round(v, 2)).ToArray();
            var yaw = Yaw(actor.Rotation[1]);
            root = Root(text);
            var found = tag is null ? null : YamlEdits.Tagged(root, tag);
            if (found is { } f)
            {
                var name = Scalar(f.Entry.Key);
                var entry = $"{f.Section}.{name}";
                var removed = Flag(f.Entry.Value, "removed");
                if (actor.Deleted)
                {
                    if (f.Section != "props")
                    {
                        results[i] = new ImportedActor(key, "skipped", entry, [], $"deleted in the editor, but import removes props only ({f.Section} carry Kismet and text): remove {entry} by hand if you mean it.");
                    }
                    else if (removed)
                    {
                        results[i] = new ImportedActor(key, "unchanged", entry, [], "already removed: true.");
                        applied[i] = true;
                    }
                    else
                    {
                        // The entry stays, so the build finds the actor it made and hides it; location and yaw stay as they were.
                        (text, var flagged) = YamlEdits.SetKeys(text, f.Entry.Value, [("removed", "true")]);
                        results[i] = new ImportedActor(key, "removed", entry, flagged, null);
                        applied[i] = true;
                    }
                    continue;
                }
                if (!actor.Spawned && !ours) notes.Add($"{key}: matched {entry} by tag, but it is {actor.Path}, an actor of {actorLevel}, not {level}. Check it is the actor the entry builds.");
                var keys = Moved(f.Entry.Value, location, yaw);
                // There again in the editor (an undone delete): the build shows it again.
                if (removed) keys.Add(("removed", "false"));
                var (edited, changes) = YamlEdits.SetKeys(text, f.Entry.Value, keys);
                text = edited;
                results[i] = new ImportedActor(key, changes.Count > 0 ? "updated" : "unchanged", entry, changes, changes.Count > 0 ? null : "already where the editor left it.");
                applied[i] = true;
            }
            else if (actor.Spawned && actor.Deleted)
            {
                // Saved, then deleted: an import before the delete made it a props entry (found above); without one there is nothing.
                results[i] = new ImportedActor(key, "skipped", null, [], "spawned and deleted in the editor, never imported: nothing to do.");
                applied[i] = spawnedHere;
                continue;
            }
            else if (actor.Spawned && !spawnedHere)
            {
                results[i] = new ImportedActor(key, "skipped", null, [], $"spawned on map {session.Map}, and this file builds {level}: the file of that map takes it, or this one with takeSpawned=true.");
                continue;
            }
            else if (actor.Spawned)
            {
                var loaded = actor.Mesh?.File is { } loadedFrom && FileOf(loadedFrom) is var from && from != own ? [from] : Array.Empty<string>();
                results[i] = AddMesh(game, files, ref text, actor, tag, key, location, yaw, loaded.Concat(fallbacks).Distinct(StringComparer.OrdinalIgnoreCase));
                applied[i] = results[i]!.Action == "added";
            }
            else
            {
                results[i] = new ImportedActor(key, "skipped", null, [], ours
                    ? $"{actor.Path} (an actor of {level}) has no npcs, usables or props entry{(tag is null ? "" : $" with tag '{tag}'")}: the YAML does not place it that way. Edit it in the YAML by hand."
                    : tag is not null && !ownMap
                        ? $"{actor.Path}, an actor of {actorLevel}: no entry of this file has tag '{tag}'. Another level file of the map has it, or it is vanilla (phase 4, docs/07 §4)."
                        : $"a vanilla actor ({actor.Path}, in {actorLevel}): moving or deleting vanilla actors ships in phase 4 (docs/07 §4).");
                continue;
            }

            if (results[i]!.Action is "updated" or "added" && (Units(actor.Rotation[0]) != 0 || Units(actor.Rotation[2]) != 0))
            {
                notes.Add($"{key}: pitch {actor.Rotation[0]} and roll {actor.Rotation[2]} (Unreal units) are not kept: the YAML has yaw only.");
            }
        }

        var written = text != original;
        if (written) File.WriteAllText(file, text, new UTF8Encoding(false));
        var retired = keepSession ? 0 : Retire(sessionFile, sessionText, applied, notes);
        var built = build ? LevelBuilder.Build(game, file, null) : null;
        if (built is not null) notes.Add("Install the level (game closed: mod_install) to see it after a reload.");
        return new EditorImportResult(file, sessionFile, written, results.Select(r => r!).ToList(), retired, built, notes);
    }

    /// <summary>
    /// A props entry for a mesh spawned in the editor, named by its tag, copying the mesh from the first of these files that has it
    /// as a StaticMesh. Adds 'propTemplate' when the file has none.
    /// </summary>
    private static ImportedActor AddMesh(MEGame game, Files files, ref string text, SessionActor actor, string? tag, string key, float[] location, float yaw,
        IEnumerable<string> candidates)
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
            ("location", YamlEdits.Location(location[0], location[1], location[2])),
            ("yaw", YamlEdits.Degrees(yaw)),
        };
        try
        {
            var edited = YamlEdits.AddEntry(text, Root(text), "props", YamlEdits.Quote(tag), keys, PropsComment);
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

    /// <summary>The location and yaw that differ from the entry's; yaw compared as an angle (200 and -160 are one), so an unmoved actor changes nothing.</summary>
    private static List<(string Key, string Value)> Moved(YamlNode entry, float[] location, float yaw)
    {
        var keys = new List<(string, string)>();
        var body = entry as YamlMappingNode;
        var old = body is null ? null : Numbers(body, "location");
        if (old is not { Length: 3 } || old.Zip(location).Any(p => MathF.Abs(p.First - p.Second) > Near))
        {
            keys.Add(("location", YamlEdits.Location(location[0], location[1], location[2])));
        }
        var oldYaw = body is null ? null : Numbers(body, "yaw");
        if (oldYaw is not { Length: 1 } || MathF.Abs(Angle(oldYaw[0] - yaw)) > Near) keys.Add(("yaw", YamlEdits.Degrees(yaw)));
        return keys;
    }

    /// <summary>A true/false key of an entry; false when it is missing.</summary>
    private static bool Flag(YamlNode entry, string key) =>
        entry is YamlMappingNode body && Value(body, key) is YamlScalarNode { Value: { } v } && bool.TryParse(v, out var b) && b;

    /// <summary>A value's numbers (one scalar or a list); null when it is missing or not numbers.</summary>
    private static float[]? Numbers(YamlMappingNode body, string key)
    {
        var nodes = Value(body, key) switch
        {
            YamlScalarNode s => [s],
            YamlSequenceNode l => l.Children.ToList(),
            _ => null,
        };
        var numbers = nodes?.Select(n => n is YamlScalarNode { Value: { } v } && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : (float?)null).ToList();
        return numbers is null || numbers.Any(n => n is null) ? null : numbers.Select(n => n!.Value).ToArray();
    }

    private static YamlNode? Value(YamlMappingNode mapping, string key) => mapping.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: { } k } && k == key).Value;

    /// <summary>Unreal units to degrees in (-180, 180], to 2 decimals.</summary>
    private static float Yaw(float units)
    {
        var degrees = Angle(MathF.Round(Angle(units * 360f / 65536), 2));
        return degrees == 0 ? 0 : degrees;
    }

    private static float Angle(float degrees)
    {
        var d = degrees % 360;
        return d > 180 ? d - 360 : d <= -180 ? d + 360 : d;
    }

    private static int Units(float units) => (int)MathF.Round(units) % 65536;

    private static string Pcc(string package) => package.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase) ? package : package + ".pcc";

    /// <summary>Where MeleBridge saves a map's session: %LOCALAPPDATA%\MeleBridge\LE1\sessions\&lt;map&gt;.json.</summary>
    private static string DefaultSession(string map) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeleBridge", "LE1", "sessions", map + ".json");

    /// <summary>
    /// The session of the map the level is in: &lt;level&gt;.json for a map of its own. A level the game loads into another map
    /// (sideloaded, as on the Normandy) is in that map's session: the newest one with an actor of the level, by its level or by
    /// a tag one of the file's entries has.
    /// </summary>
    private static string FindSession(string level, YamlMappingNode root, List<string> notes)
    {
        var own = DefaultSession(level);
        if (File.Exists(own)) return own;
        var folder = Path.GetDirectoryName(own)!;
        var tags = YamlEdits.Tags(root);
        var matches = new List<(string File, DateTime Written)>();
        foreach (var candidate in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json") : [])
        {
            Session? other;
            try
            {
                other = JsonSerializer.Deserialize<Session>(File.ReadAllText(candidate), Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (other?.Actors?.Any(a => !a.Spawned && (string.Equals(a.Level ?? a.Path?.Split('.')[0], level, StringComparison.OrdinalIgnoreCase)
                                                       || a.Tag is { } t && tags.Contains(t))) == true)
            {
                matches.Add((candidate, File.GetLastWriteTimeUtc(candidate)));
            }
        }
        if (matches.Count == 0)
        {
            throw new FileNotFoundException($"No session file for {level}: no {own}, and no session in {folder} has an actor of {level}. Press Save in the in-game editor (F8) "
                                            + "on that map first, or give 'session' (a level loaded into another map is saved in that map's file).");
        }
        var newest = matches.MaxBy(m => m.Written);
        notes.Add(matches.Count == 1
            ? $"{level} has no session of its own (the game loads it into another map): took {Path.GetFileName(newest.File)}, which has actors of it."
            : $"Several sessions have actors of {level}: took the newest, {Path.GetFileName(newest.File)} (also {string.Join(", ", matches.Where(m => m != newest).Select(m => Path.GetFileName(m.File)))}). Give 'session' for another.");
        return newest.File;
    }

    /// <summary>
    /// Drops the entries this import applied from the session file: the YAML has them now, and a later import must not put an
    /// old position back over a newer edit of the YAML. MeleBridge merges its next Save into what is left. Kept when the file
    /// changed since it was read (the editor saved meanwhile).
    /// </summary>
    private static int Retire(string sessionFile, string read, bool[] applied, List<string> notes)
    {
        var count = applied.Count(a => a);
        if (count == 0) return 0;
        if (File.ReadAllText(sessionFile) != read)
        {
            notes.Add($"{Path.GetFileName(sessionFile)} changed during the import (the editor saved): its entries are kept, and the next import applies them again.");
            return 0;
        }
        var doc = JsonNode.Parse(read)!.AsObject();
        var list = doc["actors"]!.AsArray();
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (applied[i]) list.RemoveAt(i);
        }
        // Written beside it, then moved over it: a crash mid-write leaves the old file whole. Not MeleBridge's .tmp name.
        var temporary = sessionFile + ".import.tmp";
        File.WriteAllText(temporary, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, sessionFile, overwrite: true);
        return count;
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
        if (session.Version != SessionVersion)
        {
            throw new ArgumentException($"{path} is a version {session.Version} session; editor_import reads version {SessionVersion}. Deploy mele-mcp and MeleBridge from the same commit.");
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
