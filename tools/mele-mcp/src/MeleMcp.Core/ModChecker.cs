using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MeleMcp.Core;

/// <param name="Severity">error (breaks in game), warning (likely wrong) or info.</param>
public sealed record ModFinding(string Severity, string Check, string File, string? Export, string Message);

public sealed record ModCheckResult(string ModDesc, int Files, IReadOnlyList<string> Checks, int Errors, int Warnings,
                                    IReadOnlyList<ModFinding> Findings, IReadOnlyList<string> Notes);

/// <summary>
/// Checks a mod's built files offline, before installing, for the mistakes that cost us game restarts: broken references,
/// stream states that leave pawns in stasis, pathless start points, strings without text, lines without lip sync, and ids
/// another installed mod also uses. Reads M3's library copy; changes nothing.
/// </summary>
public static class ModChecker
{
    public static readonly string[] AllChecks = ["refs", "streaming", "startpoints", "strings", "facefx", "ids"];

    private static readonly string[] IdKinds = ["transition", "consequence", "quest", "strref", "bool", "int", "float"];

    /// <param name="languages">Languages whose strings must exist, e.g. INT, FR.</param>
    public static ModCheckResult Check(MEGame game, string mod, IReadOnlyList<string> checks, IReadOnlyList<string> languages, int limit)
    {
        if (!game.IsGame1()) throw new ArgumentException("mod_check handles Game 1 (LE1).");
        var unknown = checks.Except(AllChecks, StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Unknown checks: {string.Join(", ", unknown)}. Use: {string.Join(", ", AllChecks)}.");
        if (checks.Count == 0) checks = AllChecks;
        bool Run(string check) => checks.Contains(check, StringComparer.OrdinalIgnoreCase);
        LecHost.EnsureInitialized();

        var (moddesc, folders) = ModInstaller.Locate(game, mod);
        var files = folders.SelectMany(f => Directory.EnumerateFiles(f, "*.pcc", SearchOption.AllDirectories)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var findings = new List<ModFinding>();
        var notes = new List<string>();
        var strings = Run("strings")
            ? languages.ToDictionary(l => l, l => GlobalStrings.Load(game, l, folders), StringComparer.OrdinalIgnoreCase)
            : [];
        // Lines the mod wrote itself: vanilla nodes kept from a template bring their own lip sync, or had none in vanilla either.
        var ownStrings = new Dictionary<int, string>();
        if (Run("facefx")) foreach (var folder in folders) foreach (var l in languages) GlobalStrings.ReadDlc(folder, l, ownStrings);

        foreach (var path in files)
        {
            using var pcc = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            var file = Path.GetFileName(path);
            var local = LocalStrings(pcc);
            if (Run("refs")) References(pcc, file, findings);
            if (Run("streaming")) Streaming(pcc, file, findings);
            if (Run("startpoints")) StartPoints(pcc, file, findings);
            if (Run("strings")) Strings(pcc, file, strings, local, findings);
            if (Run("facefx")) FaceFx(pcc, file, ownStrings, findings);
        }
        if (Run("ids")) Ids(game, folders, findings, notes);

        var ordered = findings.OrderBy(f => f.Severity switch { "error" => 0, "warning" => 1, _ => 2 }).ThenBy(f => f.File).ToList();
        return new ModCheckResult(moddesc, files.Count, checks.ToList(), findings.Count(f => f.Severity == "error"),
                                  findings.Count(f => f.Severity == "warning"), ordered.Take(Math.Max(1, limit)).ToList(), notes);
    }

    /// <summary>LEX's own reference check (Package Editor → Check references): indices outside the tables, wrong property types.</summary>
    private static void References(IMEPackage pcc, string file, List<ModFinding> findings)
    {
        var report = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(report, pcc, LECLocalizationShim.NonLocalizedStringConverter, file);
        findings.AddRange(report.GetBlockingErrors().Select(m => new ModFinding("error", "refs", file, m.Entry?.InstancedFullPath, m.Message)));
        findings.AddRange(report.GetSignificantIssues().Select(m => new ModFinding("warning", "refs", file, m.Entry?.InstancedFullPath, m.Message)));
    }

    /// <summary>A stream state that shows levels but names no InChunkName puts their pawns in stasis: no tick, no AI, no light update.</summary>
    private static void Streaming(IMEPackage pcc, string file, List<ModFinding> findings)
    {
        foreach (var volume in pcc.Exports.Where(e => e.ClassName == "BioTriggerStream"))
        {
            var states = volume.GetProperty<ArrayProperty<StructProperty>>("StreamingStates");
            for (var i = 0; i < (states?.Count ?? 0); i++)
            {
                var visible = states![i].GetProp<ArrayProperty<NameProperty>>("VisibleChunkNames");
                var chunk = states[i].GetProp<NameProperty>("InChunkName")?.Value.Instanced;
                if (visible is { Count: > 0 } && chunk is null or "None")
                {
                    findings.Add(new ModFinding("warning", "streaming", file, volume.InstancedFullPath,
                        $"StreamingStates[{i}] shows {string.Join(", ", visible.Select(v => v.Value.Instanced))} but has no InChunkName: pawns there "
                        + "stay in stasis (frozen, no AI, black). Set InChunkName to the first level (level_build does)."));
                }
            }
        }
    }

    /// <summary>A start point without paths is a dead end; henchmen spawned next to it take it as their anchor: "destination blocked".</summary>
    private static void StartPoints(IMEPackage pcc, string file, List<ModFinding> findings)
    {
        foreach (var point in pcc.Exports.Where(e => e.ClassName == "BioStartPoint"))
        {
            var paths = point.GetProperty<ArrayProperty<ObjectProperty>>("PathList");
            if (paths is { Count: > 0 } || point.GetProperty<BoolProperty>("bBlocked")?.Value == true) continue;
            var tag = point.GetProperty<NameProperty>("Tag")?.Value.Instanced ?? "?";
            findings.Add(new ModFinding("warning", "startpoints", file, point.InstancedFullPath,
                $"Start point '{tag}' has no paths and is not blocked: henchmen that spawn near it can't path out (\"destination blocked\"). Set bBlocked (level_build does)."));
        }
    }

    /// <summary>Every string reference in the package must have text in each language: global strings (installed plus this mod's) or the package's own TLK.</summary>
    private static void Strings(IMEPackage pcc, string file, Dictionary<string, Dictionary<int, string>> strings, HashSet<int> local, List<ModFinding> findings)
    {
        foreach (var exp in pcc.Exports.Where(e => e.ClassName != "BioTlkFile" && e.ClassName != "Package"))
        {
            var refs = new List<(string Path, int Id)>();
            foreach (var prop in exp.GetProperties()) CollectStringRefs(prop, ExportInspector.Label(prop), refs);
            foreach (var (path, id) in refs.Where(r => r.Id > 0 && !local.Contains(r.Id)).DistinctBy(r => r.Id))
            {
                var missing = strings.Where(kv => !kv.Value.ContainsKey(id)).Select(kv => kv.Key).ToList();
                if (missing.Count == 0) continue;
                findings.Add(new ModFinding("error", "strings", file, exp.InstancedFullPath,
                    $"{path} = ${id} has no text in {string.Join(", ", missing)}: it shows empty in game. Add it with tlk_set (or the YAML's strings)."));
            }
        }
    }

    private static void CollectStringRefs(Property prop, string path, List<(string, int)> refs)
    {
        switch (prop)
        {
            case StringRefProperty s:
                refs.Add((path, s.Value));
                break;
            case StructProperty st:
                foreach (var child in st.Properties) CollectStringRefs(child, $"{path}.{ExportInspector.Label(child)}", refs);
                break;
            case ArrayProperty<StructProperty> array:
                for (var i = 0; i < array.Count; i++) CollectStringRefs(array[i], $"{path}[{i}]", refs);
                break;
            case ArrayProperty<StringRefProperty> array:
                for (var i = 0; i < array.Count; i++) refs.Add(($"{path}[{i}]", array[i].Value));
                break;
        }
    }

    /// <summary>
    /// Each line the mod wrote (a string of its own GlobalTlk) needs FXA_&lt;strref&gt; (female set) and FXA_&lt;strref&gt;_M (male set)
    /// in its speaker's FaceFX sets, or the lips don't move.
    /// </summary>
    private static void FaceFx(IMEPackage pcc, string file, Dictionary<int, string> ownStrings, List<ModFinding> findings)
    {
        var lineNames = new Dictionary<ExportEntry, HashSet<string>>();
        HashSet<string> Lines(ExportEntry set) => lineNames.TryGetValue(set, out var names) ? names
            : lineNames[set] = ObjectBinary.From<FaceFXAnimSet>(set).Lines.Select(l => l.NameAsString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool HasText(int id) => ownStrings.TryGetValue(id, out var t) && t.Trim().Length > 0;

        foreach (var conv in pcc.Exports.Where(e => e.ClassName == "BioConversation"))
        {
            var props = conv.GetProperties();
            var nodes = ConversationReader.Structs(props, "m_EntryList").Select((e, i) => ($"E{i}", e, e.GetProp<IntProperty>("nSpeakerIndex")?.Value ?? -1))
                .Concat(ConversationReader.Structs(props, "m_ReplyList").Select((r, i) => ($"R{i}", r, -2)));
            foreach (var (node, data, speaker) in nodes)
            {
                var id = data.GetProp<StringRefProperty>("srText")?.Value ?? 0;
                if (id <= 0 || !HasText(id)) continue;
                foreach (var (list, suffix) in new[] { ("m_aFemaleFaceSets", ""), ("m_aMaleFaceSets", "_M") })
                {
                    var sets = props.GetProp<ArrayProperty<ObjectProperty>>(list);
                    if (sets is null || speaker + 2 < 0 || speaker + 2 >= sets.Count || sets[speaker + 2].ResolveToEntry(pcc) is not ExportEntry set) continue;
                    var line = $"FXA_{id}{suffix}";
                    if (Lines(set).Contains(line)) continue;
                    findings.Add(new ModFinding("warning", "facefx", file, conv.InstancedFullPath,
                        $"{node} (${id}) has no FaceFX line {line} in {set.ObjectName.Instanced}: the lips don't move. Give the node a 'strref:' in its conv YAML, or facefx_copy_lines."));
                }
            }
        }
    }

    /// <summary>
    /// Whether a source of <see cref="IdAllocator.Collect"/> is one of this mod's folders. Its owner comes first, as
    /// "&lt;folder&gt;: &lt;file&gt; ..." or "&lt;folder&gt; (&lt;path&gt;): ...", so DLC_MOD_XB's sources are not DLC_MOD_X's.
    /// </summary>
    internal static bool IsOwnSource(string source, IReadOnlyList<string> ownFolders) =>
        ownFolders.Any(o => source.StartsWith(o, StringComparison.OrdinalIgnoreCase)
                            && (source.AsSpan(o.Length).StartsWith(":") || source.AsSpan(o.Length).StartsWith(" (")));

    /// <summary>Ids this mod defines that another installed mod or the base game also defines.</summary>
    private static void Ids(MEGame game, IReadOnlyList<string> folders, List<ModFinding> findings, List<string> notes)
    {
        var own = folders.Select(f => Path.GetFileName(f.TrimEnd('\\', '/'))).ToList();
        bool IsOwn(string source) => IsOwnSource(source, own);
        foreach (var kind in IdKinds)
        {
            var sources = IdAllocator.Collect(game, kind, folders, [], notes);
            var ours = sources.Where(s => IsOwn(s.Name)).SelectMany(s => s.Ids).ToHashSet();
            var others = sources.Where(s => !IsOwn(s.Name)).ToList();
            // One finding per group of ids shared with the same sources.
            foreach (var group in ours.Order().Select(id => (Id: id, By: others.Where(o => o.Ids.Contains(id)).Select(o => o.Name).ToList()))
                                      .Where(x => x.By.Count > 0).GroupBy(x => string.Join(" + ", x.By)))
            {
                var ids = Ranges(group.Select(x => x.Id).ToList());
                var vanillaOnly = group.First().By.All(n => n.Contains("base game", StringComparison.OrdinalIgnoreCase));
                // A mod may write a vanilla plot variable on purpose (Paragon points); an id it defines itself (event, quest, string) must be its own.
                var (severity, message) = kind is "bool" or "int" or "float"
                    ? vanillaOnly
                        ? ("info", $"{kind} {ids}: vanilla variables this mod writes. Fine if meant (e.g. Paragon/Renegade points).")
                        : ("warning", $"{kind} {ids} also written by {group.Key}: both mods change them.")
                    : ("error", $"{kind} {ids} also defined by {group.Key}: one overrides the other. Pick free ids with id_alloc.");
                findings.Add(new ModFinding(severity, "ids", Path.GetFileName(folders[0]), null, message));
            }
        }
    }

    private static HashSet<int> LocalStrings(IMEPackage pcc) =>
        pcc.Exports.Where(e => e.ClassName == "BioTlkFile")
           .SelectMany(e => new LegendaryExplorerCore.TLK.ME1.ME1TalkFile(e).StringRefs.Select(s => s.StringID)).ToHashSet();

    private static string Ranges(List<int> ids)
    {
        var parts = new List<string>();
        for (var i = 0; i < ids.Count;)
        {
            var j = i;
            while (j + 1 < ids.Count && ids[j + 1] == ids[j] + 1) j++;
            parts.Add(i == j ? $"{ids[i]}" : $"{ids[i]}–{ids[j]}");
            i = j + 1;
        }
        return parts.Count <= 6 ? string.Join(", ", parts) : $"{string.Join(", ", parts.Take(6))} … ({ids.Count} ids)";
    }
}
