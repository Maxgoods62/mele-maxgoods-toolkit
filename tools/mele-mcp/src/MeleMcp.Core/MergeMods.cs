using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorerCore.UnrealScript;
using LegendaryExplorerCore.UnrealScript.Compiling.Errors;

namespace MeleMcp.Core;

/// <param name="Name">Property name; a struct member as Offset.X, a static array item as Name[2].</param>
/// <param name="Type">Bool, Float, Int, Str, Name, Enum, Object, StringRef or Array Property; found from the export or its class when left out.</param>
/// <param name="Value">The value as text. Enum: EnumType.Value; Object: an entry's instanced path, or M3M_NULL; Array: the array literal.</param>
public sealed record MergeProperty(string Name, string? Type = null, string? Value = null);

/// <param name="Entry">UIndex, instanced full path, or a name unique in the file; written to the manifest as the instanced full path.</param>
/// <param name="Properties">A propertyupdates change.</param>
/// <param name="Script">Function text (scriptupdate), or one class member when Entry is a Class (addtoclassorreplace).</param>
/// <param name="ScriptFile">Text file holding Script.</param>
/// <param name="AssetFile">Package holding the replacement export (assetupdate).</param>
/// <param name="AssetEntry">Instanced full path of the export in AssetFile.</param>
/// <param name="CanMergeAsNew">assetupdate: the entry need not exist in the target yet.</param>
public sealed record MergeChange(string Entry, IReadOnlyList<MergeProperty>? Properties = null, string? Script = null, string? ScriptFile = null,
    string? AssetFile = null, string? AssetEntry = null, bool? CanMergeAsNew = null);

/// <param name="Entry">The entry's instanced full path.</param>
/// <param name="Kind">The manifest's change type.</param>
/// <param name="Detail">What the dry check saw.</param>
/// <param name="Errors">Empty when this change passed. Script errors have the 1-based line and column of the source given.</param>
public sealed record MergeCheck(string File, string Entry, string Kind, string Detail, IReadOnlyList<ScriptMessage> Errors);

/// <param name="Stage">check-failed: nothing written, M3 not involved. written: manifest written, compile not asked. compiled: .m3m written and listed in moddesc.ini.
/// m3-error: M3 reported a failure. m3-timeout: M3 said nothing in time. At written, m3-error and m3-timeout the .m3m is not listed in moddesc.ini: one an earlier
/// build listed is taken out (ModDescChanged), as this build saw no compile of its manifest and the .m3m on disk may be an earlier one.</param>
/// <param name="Manifest">The manifest, in the mod's MergeMods folder; null at check-failed.</param>
/// <param name="M3m">The compiled file; null unless Stage is compiled.</param>
/// <param name="ModDescChanged">compiled: the .m3m was added to moddesc.ini. Any other stage: an earlier build's listing was taken out.</param>
/// <param name="FeatureLevel">The mod's cmmver, passed to M3.</param>
/// <param name="MergeModVersion">The .m3m format version in the file M3 wrote.</param>
/// <param name="Log">M3 log lines of this compile.</param>
public sealed record MergeBuildResult(string Stage, string ModRoot, string Name, string? Manifest, IReadOnlyList<string> Sources, IReadOnlyList<MergeCheck> Checks,
    string? M3m, long? M3mBytes, int? MergeModVersion, string FeatureLevel, bool ModDescChanged, string? ModDescBackup, double Seconds, IReadOnlyList<string> Log,
    IReadOnlyList<string> Notes);

/// <summary>
/// M3 merge mods: a JSON manifest, its sources, and the .m3m M3 compiles from them. M3 is a GUI and any failure in its compiler opens a dialog that waits for a
/// click, so every check its serializer makes (and what its installer would throw on) is made here first, against a scratch copy of the target. The manifest
/// goes in the mod's MergeMods folder: M3 wants sources beside the manifest, writes the .m3m beside it, and looks one folder up for the moddesc.ini that says
/// which format to write; it ignores the feature level it is given and asks in a dialog when it finds no moddesc.ini.
/// </summary>
public static partial class MergeMods
{
    private const string FolderName = "MergeMods";
    private const string M3Process = "ME3TweaksModManager";

    // The lines MergeModLoader and MainWindow log. "Writing final result" comes just before the file is written.
    private const string LogBegin = "M3MCompiler: Serializing ";
    private const string LogEnd = "M3MCompiler: Writing final result to ";
    private const string LogFailed = "Error compiling m3m mod file";
    private const string Magic = "M3MM";

    private const string PropertyUpdates = "propertyupdates", ScriptUpdate = "scriptupdate", AddToClass = "addtoclassorreplace", AssetUpdate = "assetupdate",
        ConfigUpdate = "disableconfigupdate", SkipUpdate = "sequenceskipupdate", ClassUpdate = "classupdate";

    private static readonly string[] Kinds = [PropertyUpdates, ScriptUpdate, AddToClass, AssetUpdate, ConfigUpdate, SkipUpdate, ClassUpdate];

    private static readonly string[] PropertyTypes = ["ObjectProperty", "NameProperty", "BoolProperty", "IntProperty", "FloatProperty", "EnumProperty", "StrProperty", "StringRefProperty", "ArrayProperty"];

    // M3's v2 schema: additionalProperties is false at every level.
    private static readonly string[] TopKeys = ["game", "comment", "files"];
    private static readonly string[] FileKeys = ["filename", "applytoalllocalizations", "comment", "changes"];
    private static readonly string[] ChangeKeys = ["entryname", "comment", .. Kinds];
    private static readonly string[] PropertyKeys = ["propertyname", "propertytype", "propertyasset", "propertyvalue", "comment"];

    private static readonly Dictionary<string, string[]> KindKeys = new()
    {
        [ScriptUpdate] = ["scriptfilename", "comment"],
        [AddToClass] = ["scriptfilenames", "comment"],
        [AssetUpdate] = ["assetname", "entryname", "canmergeasnew", "comment"],
        [SkipUpdate] = ["entrymd5", "outboundlinknametouse", "comment"],
        [ClassUpdate] = ["assetname", "comment"],
    };

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"[^A-Za-z0-9_]+")]
    private static partial Regex UnsafeChars();

    private sealed record Plan(string Stem, JsonObject Manifest, Dictionary<string, byte[]> Sources, bool FromChanges, double CmmVer, List<string> Notes);

    /// <summary>
    /// A plan <see cref="CheckAll"/> went through, which is what <see cref="Write"/> takes: the check finishes the manifest (entry names
    /// normalized to instanced full paths, property types filled in, script text on a Class moved to addtoclassorreplace, array
    /// literals moved into sources), so a manifest written before it would not be the one checked. Only <see cref="Of"/> makes one.
    /// </summary>
    private sealed class CheckedPlan
    {
        public Plan Plan { get; }
        public List<MergeCheck> Checks { get; }
        public bool Passed => Checks.All(c => c.Errors.Count == 0);

        private CheckedPlan(Plan plan, List<MergeCheck> checks) => (Plan, Checks) = (plan, checks);

        public static CheckedPlan Of(MEGame game, Plan plan) => new(plan, CheckAll(game, plan));
    }

    /// <summary>
    /// M3 as <see cref="Build(MEGame, string, string?, string?, IReadOnlyList{MergeChange}?, string?, bool, bool, int, M3Route)"/> uses it:
    /// the lock that keeps other mele-mcp calls off M3, and the compile. <see cref="Real"/> everywhere but in tests, which pass a
    /// stand-in so that no test starts M3 or takes the real lock.
    /// </summary>
    internal sealed record M3Route(Func<IDisposable> Lock,
        Func<string, string, string, int, List<string>, (string Stage, double Seconds, IReadOnlyList<string> Log)> Compile)
    {
        public static readonly M3Route Real = new(GameLock.ForM3, RunM3);
    }

    public static MergeBuildResult Build(MEGame game, string modRoot, string? name, string? targetFile, IReadOnlyList<MergeChange>? changes, string? manifestPath,
        bool allLocalizations, bool compile, int timeoutSeconds) =>
        Build(game, modRoot, name, targetFile, changes, manifestPath, allLocalizations, compile, timeoutSeconds, M3Route.Real);

    internal static MergeBuildResult Build(MEGame game, string modRoot, string? name, string? targetFile, IReadOnlyList<MergeChange>? changes, string? manifestPath,
        bool allLocalizations, bool compile, int timeoutSeconds, M3Route m3)
    {
        LecHost.EnsureInitialized();
        if (!game.IsLEGame()) throw new ArgumentException("mergemod_build handles LE1, LE2 and LE3.");
        if (timeoutSeconds <= 0) throw new ArgumentException("timeoutSeconds must be positive.");
        if ((manifestPath is null) == changes is null or { Count: 0 }) throw new ArgumentException("Give either changes (with targetFile and name), or manifest.");

        var root = Root(modRoot);
        var modDesc = Path.Combine(root, "moddesc.ini");
        var cmmVer = ModDescVersion(game, modDesc);
        var cmm = NumberText.Double(cmmVer);
        if (cmm < 7) throw new ArgumentException($"{modDesc} has cmmver {cmmVer}: merge mods need 7.0 or higher.");

        var stem = name?.Trim() ?? (manifestPath is null ? null : Path.GetFileNameWithoutExtension(GamePaths.ToWindowsPath(manifestPath)));
        if (stem is null || !SafeName().IsMatch(stem)) throw new ArgumentException($"Merge mod name '{stem}': letters, digits, _ and - only.");

        var plan = manifestPath is null ? FromChanges(game, stem, targetFile, changes!, allLocalizations, cmm) : FromManifest(game, stem, manifestPath, cmm);
        var check = CheckedPlan.Of(game, plan);
        var checks = check.Checks;
        if (!check.Passed) return Result("check-failed");

        var folder = Path.Combine(root, FolderName);
        var manifest = Path.Combine(folder, stem + ".json");
        var m3m = Path.Combine(folder, stem + ".m3m");
        var listing = stem + ".m3m";
        // Once the new manifest is written, the .m3m on disk is an earlier manifest's compile until M3 writes this one's, so every
        // way out short of "compiled" takes it out of moddesc.ini: listed, the mod would install the old merge. A failure after a
        // write names each file it changed (BuildWrites).
        var writes = new BuildWrites();
        return writes.Guard(() =>
        {
            // M3 is one app with one log for all three games: from before this build's first write to the end of its compile, no
            // other mele-mcp call may install, boot or compile through it, or each would read the other's log lines. Taken first, so
            // a busy M3 refuses a build that has changed nothing. compile=false never touches M3 and takes no lock.
            using var m3Held = compile ? m3.Lock() : null;
            Write(folder, manifest, check, writes);
            if (!compile) return NotCompiled("written", "Not compiled (compile=false).");

            (string Stage, double Seconds, IReadOnlyList<string> Log) run;
            try
            {
                run = m3.Compile(manifest, m3m, cmmVer, timeoutSeconds, plan.Notes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Unlist();
                throw;
            }
            if (run.Stage != "compiled") return NotCompiled(run.Stage, "Not compiled.", run.Seconds, run.Log);
            var (backup, changed) = ListInModDesc(modDesc, listing);
            if (changed) writes.Wrote(modDesc, backup);
            return Result(run.Stage, manifest, m3m, changed, backup, run.Seconds, run.Log);
        });

        (string? Backup, bool Changed) Unlist()
        {
            var (backup, changed) = UnlistInModDesc(modDesc, listing);
            if (changed) writes.Wrote(modDesc, backup);
            return (backup, changed);
        }

        MergeBuildResult NotCompiled(string outcome, string why, double seconds = 0, IReadOnlyList<string>? log = null)
        {
            var (backup, changed) = Unlist();
            var onDisk = File.Exists(m3m);
            plan.Notes.Add(changed
                ? $"{why} {listing} is taken out of moddesc.ini [BASEGAME] mergemods (backup {backup}): "
                  + (onDisk ? $"this build saw no compile of this manifest, so the {listing} on disk may be an earlier one, which the mod would install." : "M3 refuses a mod that lists a missing .m3m.")
                  + " A build that compiles lists it again."
                : $"{why} {listing} is not listed in moddesc.ini"
                  + (onDisk ? $", so the mod does not install the {listing} on disk, which may be an earlier compile." : ": M3 refuses a mod that lists a missing .m3m.")
                  + " A build that compiles lists it.");
            return Result(outcome, manifest, modDescChanged: changed, modDescBackup: backup, seconds: seconds, log: log);
        }

        MergeBuildResult Result(string outcome, string? manifestFile = null, string? output = null, bool modDescChanged = false, string? modDescBackup = null, double seconds = 0, IReadOnlyList<string>? log = null)
        {
            var (bytes, version) = output is null ? ((long?)null, (int?)null) : ReadHeader(output);
            return new MergeBuildResult(outcome, root, stem, manifestFile, manifestFile is null ? [] : [.. plan.Sources.Keys], checks, output, bytes, version, cmmVer, modDescChanged, modDescBackup,
                seconds, log ?? [], plan.Notes);
        }
    }

    private static string Root(string modRoot)
    {
        var windows = GamePaths.ToWindowsPath(modRoot.Trim());
        if (!Path.IsPathFullyQualified(windows)) throw new ArgumentException($"'{modRoot}': give the mod's full path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windows));
        ConfigFiles.EnsureWritable(Path.Combine(root, "moddesc.ini"));
        return root;
    }

    /// <summary>The mod's cmmver: the mod has to exist first, as M3 finds the merge format in this file.</summary>
    private static string ModDescVersion(MEGame game, string modDesc)
    {
        if (!File.Exists(modDesc)) throw new FileNotFoundException($"No {modDesc}: start the mod with dlc_new (LE2, LE3) or M3's DLC Mod Starter Kit (LE1).");
        var ini = ModInstaller.ReadIni(modDesc);
        var modGame = ini.GetValueOrDefault("ModInfo")?.GetValueOrDefault("game");
        if (!string.Equals(modGame, game.ToString(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{modDesc} is a {modGame ?? "?"} mod, not {game}.");
        var version = ini.GetValueOrDefault("ModManager")?.GetValueOrDefault("cmmver");
        if (!NumberText.TryDouble(version, out _)) throw new ArgumentException($"{modDesc} has no usable cmmver ('{version}').");
        return version!;
    }

    private static Plan FromChanges(MEGame game, string stem, string? targetFile, IReadOnlyList<MergeChange> changes, bool allLocalizations, double cmm)
    {
        if (string.IsNullOrWhiteSpace(targetFile)) throw new ArgumentException("Give targetFile, the file the changes go in (SFXGame.pcc, Startup_INT.pcc...).");
        var sources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var nodes = new JsonArray();
        for (var i = 0; i < changes.Count; i++) nodes.Add(ChangeNode(stem, i + 1, changes[i], sources));

        var file = new JsonObject { ["filename"] = targetFile.Trim() };
        if (allLocalizations) file["applytoalllocalizations"] = true;
        file["changes"] = nodes;
        return new Plan(stem, new JsonObject { ["game"] = game.ToString(), ["files"] = new JsonArray(file) }, sources, true, cmm, []);
    }

    private static JsonObject ChangeNode(string stem, int index, MergeChange change, Dictionary<string, byte[]> sources)
    {
        var script = change.Script is not null || change.ScriptFile is not null;
        var asset = change.AssetFile is not null || change.AssetEntry is not null;
        var count = (change.Properties is { Count: > 0 } ? 1 : 0) + (script ? 1 : 0) + (asset ? 1 : 0);
        if (count != 1) throw new ArgumentException($"Change {index} ({change.Entry}): give exactly one of properties, script or scriptFile, assetFile.");

        var node = new JsonObject { ["entryname"] = change.Entry };
        if (script)
        {
            // Written as the file M3 reads, so the header export_decompile prints goes.
            var text = ScriptCompile.ReadSource(change.Script, change.ScriptFile);
            var file = $"{stem}_{index}_{UnsafeChars().Replace(change.Entry.Split('.')[^1], "_")}.uc";
            sources[file] = new UTF8Encoding(false).GetBytes(text);
            node[ScriptUpdate] = new JsonObject { ["scriptfilename"] = file };
        }
        else if (asset)
        {
            if (change.AssetFile is null || change.AssetEntry is null) throw new ArgumentException($"Change {index} ({change.Entry}): assetFile and assetEntry go together.");
            var path = GamePaths.ToWindowsPath(change.AssetFile);
            if (!File.Exists(path)) throw new FileNotFoundException($"No file {path}.");
            sources[Path.GetFileName(path)] = File.ReadAllBytes(path);
            var update = new JsonObject { ["assetname"] = Path.GetFileName(path), ["entryname"] = change.AssetEntry };
            if (change.CanMergeAsNew == true) update["canmergeasnew"] = true;
            node[AssetUpdate] = update;
        }
        else
        {
            var properties = new JsonArray();
            foreach (var p in change.Properties!)
            {
                var update = new JsonObject { ["propertyname"] = p.Name };
                if (p.Type is not null) update["propertytype"] = p.Type;
                update["propertyvalue"] = p.Value;
                properties.Add(update);
            }
            node[PropertyUpdates] = properties;
        }
        return node;
    }

    private static Plan FromManifest(MEGame game, string stem, string manifestPath, double cmm)
    {
        var path = Path.GetFullPath(GamePaths.ToWindowsPath(manifestPath));
        if (!File.Exists(path)) throw new FileNotFoundException($"No manifest {path}.");
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"{path}: not JSON: {e.Message}");
        }
        if (node is not JsonObject manifest) throw new ArgumentException($"{path}: the manifest is one JSON object.");

        var problems = new List<string>();
        Shape(manifest, cmm, problems);
        var inGame = manifest["game"]?.GetValue<string>();
        if (!string.Equals(inGame, game.ToString(), StringComparison.OrdinalIgnoreCase)) problems.Add($"The manifest is for {inGame ?? "?"}, not {game}.");
        if (problems.Count > 0) throw new ArgumentException($"{path}: {string.Join(" ", problems)}");

        var sources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Referenced(manifest))
        {
            if (Path.GetFileName(file) != file) throw new ArgumentException($"{file}: M3 wants every source in the manifest's own folder.");
            var source = Path.Combine(Path.GetDirectoryName(path)!, file);
            if (!File.Exists(source)) throw new FileNotFoundException($"The manifest names {file}, and {source} does not exist.");
            sources[file] = File.ReadAllBytes(source);
            if (sources[file].Length == 0) throw new ArgumentException($"{file} is empty: M3 refuses a zero-byte source.");
        }
        return new Plan(stem, manifest, sources, false, cmm, []);
    }

    /// <summary>What M3's schema check would reject, as it would open a dialog in M3: the v2 schema, plus the cmmver each feature needs.</summary>
    private static void Shape(JsonObject manifest, double cmm, List<string> problems)
    {
        void Keys(JsonObject o, string[] allowed, string where)
        {
            foreach (var (key, _) in o)
            {
                if (!allowed.Contains(key)) problems.Add($"{where}: unknown key '{key}'.");
                if (key == "comment" && cmm < 9) problems.Add($"{where}: a comment needs cmmver 9.0.");
            }
        }

        Keys(manifest, TopKeys, "manifest");
        if (manifest["game"] is null) problems.Add("manifest: no game.");
        if (manifest["files"] is not JsonArray { Count: > 0 } files)
        {
            problems.Add("manifest: no files.");
            return;
        }
        foreach (var f in files)
        {
            if (f is not JsonObject file)
            {
                problems.Add("files: an item is not an object.");
                continue;
            }
            var filename = file["filename"]?.GetValue<string>() ?? "?";
            Keys(file, FileKeys, filename);
            if (file["filename"] is null) problems.Add("files: an item has no filename.");
            if (file["changes"] is not JsonArray changes)
            {
                problems.Add($"{filename}: no changes.");
                continue;
            }
            foreach (var c in changes)
            {
                if (c is not JsonObject change)
                {
                    problems.Add($"{filename}: a change is not an object.");
                    continue;
                }
                var entry = change["entryname"]?.GetValue<string>();
                var where = $"{filename} {entry ?? "?"}";
                Keys(change, ChangeKeys, where);
                if (entry is null) problems.Add($"{filename}: a change has no entryname.");
                var kinds = Kinds.Where(change.ContainsKey).ToList();
                if (kinds.Count != 1) problems.Add($"{where}: a change holds exactly one of {string.Join(", ", Kinds)}; found {kinds.Count}.");
                foreach (var kind in kinds)
                {
                    if (kind == AddToClass && cmm < 8) problems.Add($"{where}: {kind} needs cmmver 8.0.");
                    if (kind is ClassUpdate or SkipUpdate && cmm < 9) problems.Add($"{where}: {kind} needs cmmver 9.0.");
                    if (KindKeys.TryGetValue(kind, out var allowed))
                    {
                        if (change[kind] is JsonObject update) Keys(update, allowed, $"{where} {kind}");
                        else problems.Add($"{where}: {kind} is not an object.");
                    }
                }
                if (change[PropertyUpdates] is JsonArray properties)
                {
                    foreach (var p in properties)
                    {
                        if (p is not JsonObject property)
                        {
                            problems.Add($"{where}: a property update is not an object.");
                            continue;
                        }
                        Keys(property, PropertyKeys, where);
                        var type = property["propertytype"]?.GetValue<string>();
                        if (property["propertyname"] is null || type is null) problems.Add($"{where}: a property update needs propertyname and propertytype.");
                        else if (!PropertyTypes.Contains(type)) problems.Add($"{where}: unknown propertytype '{type}'.");
                        if (property.ContainsKey("propertyvalue") == property.ContainsKey("propertyasset")) problems.Add($"{where}: a property update has one of propertyvalue, propertyasset.");
                        else if (type == "ArrayProperty" && !property.ContainsKey("propertyasset")) problems.Add($"{where}: an ArrayProperty update needs propertyasset.");
                    }
                }
            }
        }
    }

    /// <summary>The source files a manifest names.</summary>
    private static IEnumerable<string> Referenced(JsonObject manifest)
    {
        foreach (var change in manifest["files"]!.AsArray().SelectMany(f => f!["changes"]!.AsArray()))
        {
            if (change![ScriptUpdate]?["scriptfilename"] is { } script) yield return script.GetValue<string>();
            if (change[AssetUpdate]?["assetname"] is { } asset) yield return asset.GetValue<string>();
            if (change[ClassUpdate]?["assetname"] is { } classFile) yield return classFile.GetValue<string>();
            foreach (var file in change[AddToClass]?["scriptfilenames"]?.AsArray() ?? []) yield return file!.GetValue<string>();
            foreach (var property in change[PropertyUpdates]?.AsArray() ?? [])
            {
                if (property!["propertyasset"] is { } value) yield return value.GetValue<string>();
            }
        }
    }

    /// <summary>Works on a copy, as M3 will on the installed file: script_compile refuses the game's own files, and the changes pile up in memory in manifest order.</summary>
    private static List<MergeCheck> CheckAll(MEGame game, Plan plan)
    {
        var checks = new List<MergeCheck>();
        // The copy this install loads, through the one fresh-checked list every tool reads: a forced rescan here would change what
        // the other tools resolve after a merge build.
        var loaded = GamePaths.LoadedFiles(game);
        var scratch = Path.Combine(Path.GetTempPath(), $"mele-mcp-merge-{Guid.NewGuid():N}");
        try
        {
            foreach (var file in plan.Manifest["files"]!.AsArray().Select(f => f!.AsObject()))
            {
                var name = file["filename"]!.GetValue<string>();
                if (!Allowed(game, name))
                {
                    checks.Add(new MergeCheck(name, "", "file", "", [new ScriptMessage($"M3 refuses {name} as a merge target. {game}: {string.Join(", ", Targets(game))}, and their localized copies.", null, null)]));
                }
                else if (!loaded.TryGetValue(name, out var installed))
                {
                    checks.Add(new MergeCheck(name, "", "file", "", [new ScriptMessage($"{game} has no file {name}: M3 skips a merge target it cannot find, with only a log line.", null, null)]));
                }
                else
                {
                    Directory.CreateDirectory(scratch);
                    var copy = Path.Combine(scratch, Path.GetFileName(installed));
                    File.Copy(installed, copy, overwrite: true);
                    using (var pcc = MEPackageHandler.OpenMEPackage(copy, forceLoadFromDisk: true))
                    {
                        checks.AddRange(file["changes"]!.AsArray().Select(c => CheckChange(pcc, name, c!.AsObject(), plan)).ToList());
                    }
                    File.Delete(copy);
                    if (file["applytoalllocalizations"]?.GetValue<bool>() == true) plan.Notes.Add($"{name}: only this copy was checked, M3 applies the changes to every localized copy.");
                    if (!installed.StartsWith(MEDirectories.GetCookedPath(game), StringComparison.OrdinalIgnoreCase))
                    {
                        plan.Notes.Add($"{name}: checked against {installed}, the copy this install loads; M3 merges into that copy too.");
                    }
                }
            }
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
        return checks;
    }

    private static IEnumerable<string> Targets(MEGame game) => EntryImporter.FilesSafeToImportFrom(game).Append("EntryMenu.pcc");

    /// <summary>M3's MergeFile1.Validate for format 2: the name, its localization cut, starts with the stem of an allowed file.</summary>
    private static bool Allowed(MEGame game, string name)
    {
        var bare = name.StripUnrealLocalization();
        return Targets(game).Any(t => bare.StartsWith(Path.GetFileNameWithoutExtension(t.StripUnrealLocalization()), StringComparison.OrdinalIgnoreCase))
               && name.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase);
    }

    private static MergeCheck CheckChange(IMEPackage pcc, string file, JsonObject change, Plan plan)
    {
        var entry = change["entryname"]!.GetValue<string>();
        var kind = Kinds.First(change.ContainsKey);
        MergeCheck Fail(string message) => new(file, entry, kind, "", [new ScriptMessage(message, null, null)]);

        ExportEntry? target;
        try
        {
            target = pcc.FindExport(entry) ?? ExportInspector.Resolve(pcc, entry);
        }
        catch (ArgumentException e)
        {
            target = null;
            var maybeNew = kind == ClassUpdate || (kind == AssetUpdate && change[AssetUpdate]?["canmergeasnew"]?.GetValue<bool>() == true);
            if (!maybeNew) return Fail(e.Message);
        }
        if (target is not null)
        {
            entry = target.InstancedFullPath;
            change["entryname"] = entry;
        }

        // Script text given without a kind of its own: a Class takes it as one member.
        if (plan.FromChanges && kind == ScriptUpdate && target?.ClassName == "Class")
        {
            var script = change[ScriptUpdate]!["scriptfilename"]!.GetValue<string>();
            change.Remove(ScriptUpdate);
            change[AddToClass] = new JsonObject { ["scriptfilenames"] = new JsonArray(script) };
            kind = AddToClass;
            if (plan.CmmVer < 8) return Fail($"Adding a class member needs cmmver 8.0, and this mod has {plan.CmmVer.ToString(CultureInfo.InvariantCulture)}.");
        }

        switch (kind)
        {
            case ScriptUpdate:
                if (target!.ClassName != "Function") return Fail($"{entry} is a {target.ClassName}: a scriptupdate replaces a Function. A State or class member goes through its Class (addtoclassorreplace).");
                return CheckScripts(file, entry, kind, target, [change[ScriptUpdate]!["scriptfilename"]!.GetValue<string>()], plan);
            case AddToClass:
                if (target!.ClassName != "Class") return Fail($"{entry} is a {target.ClassName}: addtoclassorreplace takes a Class.");
                return CheckScripts(file, entry, kind, target, change[AddToClass]!["scriptfilenames"]!.AsArray().Select(s => s!.GetValue<string>()).ToList(), plan);
            case PropertyUpdates:
                return CheckProperties(file, entry, target!, change[PropertyUpdates]!.AsArray().Select(p => p!.AsObject()).ToList(), plan);
            case AssetUpdate:
                return CheckAsset(file, entry, change[AssetUpdate]!.AsObject(), plan);
            default:
                return new MergeCheck(file, entry, kind, $"Not checked here: {kind} is passed to M3 as it is.", []);
        }
    }

    private static MergeCheck CheckScripts(string file, string entry, string kind, ExportEntry target, IReadOnlyList<string> scripts, Plan plan)
    {
        var errors = new List<ScriptMessage>();
        var details = new List<string>();
        foreach (var script in scripts)
        {
            var text = Encoding.UTF8.GetString(plan.Sources[script]);
            if (kind == AddToClass && text.TrimStart().StartsWith("class ", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ScriptMessage($"{script} is a whole class: M3 refuses that for a vanilla class. Give one var, struct, enum, function or state.", null, null));
                continue;
            }
            var before = target.DataSize;
            var run = ScriptCompile.CompileInto(target, text);
            errors.AddRange(run.Errors);
            plan.Notes.AddRange(run.Warnings.Where(w => !w.Contains("is a core file", StringComparison.Ordinal)));
            details.Add(run.Compiled ? $"{script}: compiled as {run.How}, {before} -> {target.DataSize} bytes" : $"{script}: does not compile");
        }
        return new MergeCheck(file, entry, kind, string.Join("; ", details), errors);
    }

    private static MergeCheck CheckAsset(string file, string entry, JsonObject update, Plan plan)
    {
        var asset = update["assetname"]!.GetValue<string>();
        var inner = update["entryname"]!.GetValue<string>();
        using var package = MEPackageHandler.OpenMEPackageFromStream(new MemoryStream(plan.Sources[asset]), asset);
        return package.FindExport(inner) is { } found
            ? new MergeCheck(file, entry, AssetUpdate, $"{asset} holds {found.InstancedFullPath} ({found.ClassName}).", [])
            : new MergeCheck(file, entry, AssetUpdate, "", [new ScriptMessage($"{asset} has no export {inner}.", null, null)]);
    }

    private static MergeCheck CheckProperties(string file, string entry, ExportEntry target, List<JsonObject> updates, Plan plan)
    {
        var errors = new List<ScriptMessage>();
        var details = new List<string>();
        foreach (var update in updates)
        {
            var name = update["propertyname"]!.GetValue<string>();
            try
            {
                details.Add(CheckProperty(target, update, name, plan));
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
            {
                errors.Add(new ScriptMessage($"{name}: {e.Message}", null, null));
            }
        }
        return new MergeCheck(file, entry, PropertyUpdates, string.Join("; ", details), errors);
    }

    /// <summary>What M3's PropertyUpdate1.ApplyUpdate would throw on at install, and a type or name the game would not read: M3 writes those without a word.</summary>
    private static string CheckProperty(ExportEntry target, JsonObject update, string name, Plan plan)
    {
        var parts = name.Split('.');
        var props = target.GetProperties();
        var owner = target.ClassName;
        foreach (var part in parts[..^1])
        {
            var (structName, index) = ParseName(part);
            var inner = props.GetProp<StructProperty>(structName, index) ?? throw new ArgumentException($"{target.InstancedFullPath} holds no struct {part}: M3 can only reach into a struct the export already holds.");
            props = inner.Properties;
            owner = inner.StructType;
        }
        var (final, arrayIndex) = ParseName(parts[^1]);
        var current = props.GetProp<Property>(final, arrayIndex);
        var info = GlobalUnrealObjectInfo.GetPropertyInfo(target.Game, final, owner, containingExport: target);
        var actual = current is not null ? TypeName(current) : info is null ? null : TypeName(info);
        if (actual is null) throw new ArgumentException($"{owner} has no such property, so the game would not read it.");

        var type = update["propertytype"]?.GetValue<string>();
        if (type is null)
        {
            if (!PropertyTypes.Contains(actual)) throw new ArgumentException($"its type is {actual}, which merge mods cannot set.");
            update["propertytype"] = type = actual;
        }
        else if (type != actual) throw new ArgumentException($"its type is {actual}, not {type}.");

        var value = update["propertyvalue"]?.GetValue<string>();
        if (type == "ArrayProperty")
        {
            var text = value ?? (update["propertyasset"] is { } asset ? Encoding.UTF8.GetString(plan.Sources[asset.GetValue<string>()]) : throw new ArgumentException("no array literal given."));
            if (plan.FromChanges)
            {
                var file = ArrayAssetFile(plan.Sources, plan.Stem, target.ObjectName.Instanced, name, new UTF8Encoding(false).GetBytes(text));
                update.Remove("propertyvalue");
                update["propertyasset"] = file;
            }
            CheckArray(target, name, text);
        }
        else
        {
            if (value is null) throw new ArgumentException("no value given.");
            CheckValue(target, type, value);
        }
        return $"{name} ({type}): {Show(current)} -> {value ?? "array"}";
    }

    /// <summary>
    /// Stores an array literal in sources and returns its file name: &lt;stem&gt;_&lt;export&gt;_&lt;property&gt;.uc, with _2, _3... when
    /// another literal already has that name. Two exports that set an array of the same name each keep their own literal;
    /// the same literal twice (a check run again) reuses its file.
    /// </summary>
    internal static string ArrayAssetFile(Dictionary<string, byte[]> sources, string stem, string exportName, string property, byte[] bytes)
    {
        var stub = $"{stem}_{UnsafeChars().Replace($"{exportName}_{property}", "_")}";
        var file = stub + ".uc";
        for (var n = 2; sources.TryGetValue(file, out var had) && !had.AsSpan().SequenceEqual(bytes); n++) file = $"{stub}_{n}.uc";
        sources[file] = bytes;
        return file;
    }

    internal static void CheckValue(ExportEntry target, string type, string value)
    {
        switch (type)
        {
            case "FloatProperty":
                NumberText.Float(value);
                break;
            case "IntProperty":
                int.Parse(value, CultureInfo.InvariantCulture);
                break;
            case "BoolProperty":
                bool.Parse(value);
                break;
            case "StringRefProperty":
                int.Parse(value.TrimStart('$'), CultureInfo.InvariantCulture);
                break;
            case "NameProperty":
                // PropertyUpdate1.ApplyUpdate cuts the name at '|' and then reads the number from the cut text, which throws. Read in M3's source, not run.
                if (value.Contains('|')) throw new ArgumentException($"'{value}': M3's merge code cuts a name at '|' before it reads the instance number, so this would fail at install.");
                break;
            case "ObjectProperty":
                if (value != "M3M_NULL" && target.FileRef.FindEntry(value) is null) throw new ArgumentException($"{Path.GetFileName(target.FileRef.FilePath)} has no entry {value}: M3 only links existing entries.");
                break;
            case "EnumProperty":
                var enumParts = value.Split('.');
                if (enumParts.Length != 2) throw new ArgumentException($"'{value}': an enum value is EnumType.Value.");
                var values = GlobalUnrealObjectInfo.GetEnumValues(target.Game, enumParts[0]);
                if (values is null) throw new ArgumentException($"{target.Game} has no enum {enumParts[0]}.");
                if (!values.Any(v => v.Instanced.Equals(enumParts[1], StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"{enumParts[0]} has no value {enumParts[1]}.");
                break;
        }
    }

    private static void CheckArray(ExportEntry target, string name, string literal)
    {
        using var cache = new PackageCache();
        var usop = new UnrealScriptOptionsPackage { Cache = cache };
        using var lib = new FileLib(target.FileRef);
        if (!lib.Initialize(usop)) throw new InvalidOperationException("Script symbols failed to load: " + string.Join("; ", lib.InitializationLog.Content.Take(5)));
        var log = new MessageLog();
        var compiled = UnrealScriptCompiler.CompileProperty(name, literal, target, lib, log, usop);
        if (compiled is null || log.HasErrors) throw new ArgumentException($"the array literal does not compile: {string.Join("; ", log.AllErrors.Select(e => e.Message))}");
    }

    private static (NameReference Name, int Index) ParseName(string text)
    {
        var open = text.IndexOf('[');
        if (open < 0) return (NameReference.FromInstancedString(text), 0);
        if (text[^1] != ']') throw new ArgumentException($"'{text}': no closing ].");
        return (NameReference.FromInstancedString(text[..open]), int.Parse(text[(open + 1)..^1], CultureInfo.InvariantCulture));
    }

    private static string TypeName(Property property) => property switch
    {
        EnumProperty => "EnumProperty",
        ArrayPropertyBase => "ArrayProperty",
        _ => property.GetType().Name,
    };

    private static string TypeName(PropertyInfo info) => info.IsEnumProp() ? "EnumProperty" : info.Type.ToString();

    private static string Show(Property? property) => property switch
    {
        null => "not set",
        BoolProperty p => p.Value.ToString(),
        FloatProperty p => p.Value.ToString(CultureInfo.InvariantCulture),
        IntProperty p => p.Value.ToString(CultureInfo.InvariantCulture),
        StrProperty p => p.Value,
        NameProperty p => p.Value.Instanced,
        EnumProperty p => $"{p.EnumType.Instanced}.{p.Value.Instanced}",
        ObjectProperty p => p.Value.ToString(CultureInfo.InvariantCulture),
        ArrayPropertyBase p => $"{p.Count} items",
        _ => "set",
    };

    private static void Write(string folder, string manifest, CheckedPlan check, BuildWrites writes)
    {
        if (!check.Passed) throw new InvalidOperationException($"{manifest} failed its check: it is not written.");
        var plan = check.Plan;
        ConfigFiles.EnsureWritable(manifest);
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in plan.Sources)
        {
            var path = Path.Combine(folder, name);
            ConfigFiles.EnsureWritable(path);
            var exists = File.Exists(path);
            if (exists && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
            var backup = exists ? PackageWriter.Backup(path) : null;
            AtomicFile.WriteAllBytes(path, bytes);
            writes.Wrote(path, backup);
        }

        var manifestBackup = File.Exists(manifest) ? PackageWriter.Backup(manifest) : null;
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        AtomicFile.WriteAllText(manifest, plan.Manifest.ToJsonString(options), new UTF8Encoding(false));
        writes.Wrote(manifest, manifestBackup);
    }

    /// <summary>Runs M3 and waits on its log and the output file. A running M3 takes the arguments from this second launch; it is never closed.</summary>
    private static (string Stage, double Seconds, IReadOnlyList<string> Log) RunM3(string manifest, string m3m, string cmmVer, int timeoutSeconds, List<string> notes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("M3 runs on Windows.");
        var m3 = ModInstaller.M3Path();
        var running = Process.GetProcessesByName(M3Process).Length > 0;
        var before = File.Exists(m3m) ? File.GetLastWriteTimeUtc(m3m) : DateTime.MinValue;
        var offsets = ModInstaller.LogOffsets();
        var started = DateTime.UtcNow;
        ModInstaller.StartM3(m3, ["--compilemergemod", manifest, "--featurelevel", cmmVer]);
        if (!running) notes.Add("M3 was not running: it was started, and stays open.");

        var fileName = Path.GetFileName(m3m);
        var log = new StringBuilder();
        var stage = "m3-timeout";
        var finished = DateTime.MaxValue;
        var size = 0L;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(timeoutSeconds))
        {
            Thread.Sleep(500);
            log.Append(ModInstaller.ReadNew(offsets));
            var text = log.ToString();
            // One size twice running, and no handle left on the file: a big file can be seen while M3 writes it, and a write that
            // stalls between two polls shows one size twice.
            var now = File.Exists(m3m) && File.GetLastWriteTimeUtc(m3m) > before ? new FileInfo(m3m).Length : 0L;
            if (now > 0 && now == size && Closed(m3m))
            {
                stage = "compiled";
                break;
            }
            size = now;
            if (CompileFailed(text, manifest))
            {
                stage = "m3-error";
                break;
            }
            // The log line comes just before the file: allow it a moment, then say what happened.
            if (finished == DateTime.MaxValue && text.Contains(LogEnd, StringComparison.Ordinal) && text.Contains(fileName, StringComparison.OrdinalIgnoreCase)) finished = DateTime.UtcNow;
            if (DateTime.UtcNow - finished > TimeSpan.FromSeconds(10)) break;
        }

        log.Append(ModInstaller.ReadNew(offsets));
        var lines = log.ToString().Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Contains("M3MCompiler", StringComparison.Ordinal) || l.Contains(LogFailed, StringComparison.Ordinal)
                        || (l.Contains(fileName, StringComparison.OrdinalIgnoreCase) && (l.Contains("[ERR]") || l.Contains("[FTL]") || l.Contains("[WRN]"))))
            .ToList();
        if (stage == "m3-error") notes.Add($"M3 also shows this in a dialog that waits for OK. The manifest stays at {manifest}.");
        if (stage == "m3-timeout")
        {
            notes.Add($"No result from M3 in {timeoutSeconds} s. If its window shows a dialog, it waits for a click; nothing here clicks it. "
                      + $"The manifest stays at {manifest}: dropping that file on M3's window compiles it too.");
        }
        return (stage, Math.Round((DateTime.UtcNow - started).TotalSeconds, 1), lines);
    }

    /// <summary>
    /// Whether M3's log says the compile of <paramref name="manifest"/> failed. M3's failure line names no file, so it counts only
    /// after the line that begins this manifest's compile ("M3MCompiler: Serializing &lt;manifest&gt; to ..."): one that follows
    /// another manifest's begin line, or comes before this one's, belongs to another compile, and would take this build's good
    /// .m3m out of moddesc.ini.
    /// </summary>
    internal static bool CompileFailed(string log, string manifest)
    {
        var begun = log.LastIndexOf(LogBegin + manifest, StringComparison.OrdinalIgnoreCase);
        return begun >= 0 && log.IndexOf(LogFailed, begun, StringComparison.Ordinal) >= 0;
    }

    /// <summary>Whether nothing has the file open: opened with no sharing, which fails while M3 (or anything else) still holds it.</summary>
    private static bool Closed(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static (long Bytes, int? Version) ReadHeader(string m3m)
    {
        using var stream = File.OpenRead(m3m);
        var head = new byte[5];
        var read = stream.Read(head, 0, head.Length);
        var valid = read == head.Length && Encoding.ASCII.GetString(head, 0, 4) == Magic;
        return (stream.Length, valid ? head[4] : null);
    }

    /// <summary>
    /// Adds the file to [BASEGAME] mergemods= with the file's bytes otherwise untouched, read as Latin-1 for that, and only ASCII added. M3 needs moddir in that
    /// section, and '.' when the job has no other files. Returns the backup, and whether the file changed.
    /// </summary>
    internal static (string? Backup, bool Changed) ListInModDesc(string modDesc, string file)
    {
        // Bytes, not ReadAllText, which would follow a BOM and undo the point of Latin-1.
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(modDesc));
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var section = lines.FindIndex(l => l.Trim().Equals("[BASEGAME]", StringComparison.OrdinalIgnoreCase));
        if (section < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(["", "[BASEGAME]", "moddir = .", $"mergemods = {file}", ""]);
        }
        else
        {
            var end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            int Key(string key) => lines.FindIndex(section + 1, end - section - 1, l => l.Split('=')[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase) && l.Contains('='));
            var at = Key("mergemods");
            if (at >= 0)
            {
                var value = lines[at][(lines[at].IndexOf('=') + 1)..].Trim();
                var listed = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (listed.Contains(file, StringComparer.OrdinalIgnoreCase)) return (null, false);
                lines[at] = $"mergemods = {string.Join(';', listed.Append(file))}";
            }
            else
            {
                var insert = section + 1;
                if (Key("moddir") < 0) lines.Insert(insert++, "moddir = .");
                else insert = Key("moddir") + 1;
                lines.Insert(insert, $"mergemods = {file}");
            }
        }

        var backup = PackageWriter.Backup(modDesc);
        AtomicFile.WriteAllBytes(modDesc, Encoding.Latin1.GetBytes(string.Join(eol, lines)));
        return (backup, true);
    }

    /// <summary>
    /// Takes the file out of [BASEGAME] mergemods=, the other way from <see cref="ListInModDesc"/> and as byte-careful: the line goes
    /// when nothing is left on it, and the section when nothing but its moddir = . is left, as ListInModDesc writes both for a mod with
    /// no other base game job. Returns the backup, and whether the file changed.
    /// </summary>
    internal static (string? Backup, bool Changed) UnlistInModDesc(string modDesc, string file)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(modDesc));
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var section = lines.FindIndex(l => l.Trim().Equals("[BASEGAME]", StringComparison.OrdinalIgnoreCase));
        if (section < 0) return (null, false);
        var end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        var at = lines.FindIndex(section + 1, end - section - 1, l => l.Split('=')[0].Trim().Equals("mergemods", StringComparison.OrdinalIgnoreCase) && l.Contains('='));
        if (at < 0) return (null, false);
        var listed = lines[at][(lines[at].IndexOf('=') + 1)..].Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!listed.Contains(file, StringComparer.OrdinalIgnoreCase)) return (null, false);

        var rest = listed.Where(l => !l.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rest.Count > 0)
        {
            lines[at] = $"mergemods = {string.Join(';', rest)}";
        }
        else
        {
            lines.RemoveAt(at);
            end--;
            var body = lines.GetRange(section + 1, end - section - 1).Where(l => l.Trim().Length > 0).ToList();
            var onlyModDir = body.Count == 0 || (body.Count == 1 && body[0].Contains('=') && body[0].Split('=')[0].Trim().Equals("moddir", StringComparison.OrdinalIgnoreCase)
                                                  && body[0][(body[0].IndexOf('=') + 1)..].Trim() == ".");
            if (onlyModDir)
            {
                lines.RemoveRange(section, end - section);
                if (section == lines.Count)
                {
                    // The file's last section, where ListInModDesc appends it: the blank line before it goes too, and the file still
                    // ends with a line break.
                    while (section > 0 && lines[section - 1].Trim().Length == 0) lines.RemoveAt(--section);
                    if (lines.Count > 0) lines.Add("");
                }
            }
        }

        var backup = PackageWriter.Backup(modDesc);
        AtomicFile.WriteAllBytes(modDesc, Encoding.Latin1.GetBytes(string.Join(eol, lines)));
        return (backup, true);
    }
}
