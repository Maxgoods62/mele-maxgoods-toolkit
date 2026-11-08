using System.Text;
using System.Text.Json;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.PlotDatabase.Databases;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using static LegendaryExplorerCore.Unreal.BinaryConverters.BioStateEventMap;

namespace MeleMcp.Core;

public sealed record IdUse(int Id, IReadOnlyList<string> Sources);

public sealed record IdSourceSummary(string Source, int Count, int MinId, int MaxId);

/// <param name="Free">Checked ids no source uses, or the first free ids at or above From.</param>
/// <param name="Used">Checked ids that a source uses, or the used ids skipped while searching from From.</param>
public sealed record IdReport(string Kind, IReadOnlyList<int> Free, IReadOnlyList<IdUse> Used, IReadOnlyList<IdSourceSummary> Sources, IReadOnlyList<string> Notes);

/// <summary>
/// Game 1 plot, quest and string ids already in use: LEX's base-game plot database, LEX mod plot databases, and what the base
/// game and every installed DLC wire in their Autoload.ini (plot and quest maps, conditionals, GlobalTlk), so new ids do not collide.
/// The ids it hands out are reserved (<see cref="IdReservations"/>) until a scan finds them installed.
/// </summary>
public static class IdAllocator
{
    public static readonly string[] Kinds = ["bool", "int", "float", "conditional", "transition", "consequence", "quest", "strref"];

    private const int SearchSpan = 1_000_000;

    /// <param name="Installed">
    /// Seen by every id_alloc call: LEX's plot databases, the base game, an installed DLC. A folder or package passed in is not:
    /// another call may not pass it, so its ids do not end a reservation (<see cref="IdReservations.Release"/>).
    /// </param>
    internal sealed record Source(string Name, HashSet<int> Ids, bool Installed = true);

    internal sealed record AutoloadEntry(string Key, string Package, string Export);

    /// <param name="owner">Who the ids are for (a mod's name): its reservations are free to it, and the ids returned are reserved for it.</param>
    public static IdReport Find(MEGame game, string kind, IReadOnlyList<int>? ids, int? from, int count,
                                IReadOnlyList<string>? extraDlcFolders, IReadOnlyList<string>? packages, string? owner = null)
    {
        if (!game.IsGame1()) throw new ArgumentException("id_alloc handles Game 1 (LE1).");
        kind = kind.Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) throw new ArgumentException($"Unknown kind '{kind}'. Use one of: {string.Join(", ", Kinds)}.");
        if (ids is not { Count: > 0 } && from is null) throw new ArgumentException("Give ids to check, or from (and count) to find free ids.");
        if (from < 0) throw new ArgumentException("from must be 0 or more: plot, quest and string ids are not negative.");
        if (count < 1) throw new ArgumentException("count must be at least 1.");
        LecHost.EnsureInitialized();

        var notes = new List<string>();
        var scanned = Collect(game, kind, extraDlcFolders ?? [], packages ?? [], notes);
        var (free, used, sources) = Choose(kind, ids, from, count, scanned, owner, IdReservations.FilePath(), DateTime.UtcNow, notes);

        notes.Add("Not scanned: a mod built into M3's library but not installed, unless its DLC folder is in extraDlcFolders (mod_check's ids "
                  + "check, after the build, is the only other check). The ids id_alloc hands out are reserved, so other calls skip them before they are built.");
        if (kind is "bool" or "int" or "float")
        {
            notes.Add("Mod plot variables are found where a plot map writes them; one a mod only reads (conditionals, conversation conditions) "
                      + "shows up only if the mod ships a plot database in LEX's ModPlotsLE1 folder.");
        }
        if (kind == "strref")
        {
            notes.Add("Conversation TLKs inside level and _LOC_ files are not scanned; pass them in packages to include them.");
        }

        var summaries = sources.Select(s => new IdSourceSummary(s.Name, s.Ids.Count, s.Ids.Min(), s.Ids.Max())).ToList();
        return new IdReport(kind, free, used, summaries, notes);
    }

    /// <summary>
    /// Find after the scan: the ids neither <paramref name="sources"/> nor another owner's reservation in <paramref name="ledger"/>
    /// uses, checked or searched from <paramref name="from"/>, then reserved for <paramref name="owner"/>. Returns the sources
    /// with those reservations added. Reservations of ids an installed source now has end first.
    /// </summary>
    internal static (List<int> Free, List<IdUse> Used, List<Source> Sources) Choose(string kind, IReadOnlyList<int>? ids, int? from, int count,
        IReadOnlyList<Source> sources, string? owner, string ledger, DateTime now, List<string> notes)
    {
        owner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();
        using var reservations = IdReservations.Open(ledger, now);
        var released = reservations.Release(kind, sources.Where(s => s.Installed).SelectMany(s => s.Ids).ToHashSet());
        if (released.Count > 0) notes.Add($"Reservations ended for {kind} {IdReservations.Text(released)}: a scan finds them installed now.");
        var all = sources.Concat(reservations.HeldByOthers(kind, owner)).ToList();
        bool IsUsed(int id) => all.Any(s => s.Ids.Contains(id));
        IdUse Use(int id) => new(id, all.Where(s => s.Ids.Contains(id)).Select(s => s.Name).ToList());

        List<int> free;
        List<IdUse> used;
        if (ids is { Count: > 0 })
        {
            var distinct = ids.Distinct().ToList();
            free = distinct.Where(id => !IsUsed(id)).ToList();
            used = distinct.Where(IsUsed).Select(Use).ToList();
        }
        else
        {
            free = [];
            used = [];
            // In long: from + SearchSpan passes int.MaxValue for a block near the top, and int.MaxValue itself is an id.
            var end = Math.Min((long)from!.Value + SearchSpan, (long)int.MaxValue + 1);
            for (long id = from.Value; free.Count < count && id < end; id++)
            {
                if (IsUsed((int)id)) used.Add(Use((int)id));
                else free.Add((int)id);
            }
            if (free.Count < count) notes.Add($"Only {free.Count} free ids between {from} and {end - 1}.");
        }

        // Ids given to check are reserved too when free: a check is how a block picked by hand is claimed.
        var reserved = free.Where(id => id >= 0).ToList();
        if (reserved.Count > 0)
        {
            var until = reservations.Reserve(kind, owner, reserved);
            notes.Add(owner is null
                ? $"Reserved {kind} {IdReservations.Text(reserved)} until {until:yyyy-MM-dd} in {ledger}, for no owner: every later id_alloc call skips them, "
                  + "this mod's too. Give owner (the mod's name) to get your own ids back. A reservation ends once a scan finds the ids installed, or after 30 days."
                : $"Reserved {kind} {IdReservations.Text(reserved)} for '{owner}' until {until:yyyy-MM-dd} in {ledger}: other id_alloc calls skip them, "
                  + $"and a call with owner '{owner}' gets them again. A reservation ends once a scan finds the ids installed, or after 30 days.");
        }
        reservations.Save();
        return (free, used, all);
    }

    internal static List<Source> Collect(MEGame game, string kind, IReadOnlyList<string> extraDlcFolders, IReadOnlyList<string> packages, List<string> notes)
    {
        var sources = new List<Source>();
        if (kind != "strref")
        {
            Add(sources, "LEX plot database (base game)", DatabaseIds(PlotDatabases.Le1PlotDatabase, kind));
            var modDbFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LegendaryExplorer", "ModPlotsLE1");
            foreach (var json in Directory.Exists(modDbFolder) ? Directory.EnumerateFiles(modDbFolder, "*.json") : [])
            {
                try
                {
                    var db = new ModPlotDatabase { Game = MEGame.LE1 };
                    db.LoadPlotsFromFile(json);
                    Add(sources, $"LEX mod plot database {db.ModRoot?.Label ?? Path.GetFileNameWithoutExtension(json)}", DatabaseIds(db, kind));
                }
                catch (Exception e)
                {
                    notes.Add($"Skipped {json}: {e.Message}");
                }
            }
        }

        // The base game's own maps and conditionals are loaded by the engine, not by Autoload.ini.
        var cooked = MEDirectories.GetCookedPath(game);
        switch (kind)
        {
            case "bool" or "int" or "float" or "transition" or "consequence":
                foreach (var map in new[] { "PlotManagerAuto.StateTransitionMap", "PlotManagerAuto.ConsequenceMap" }.Where(m => MapMatches(m, kind)))
                {
                    AddPackage(sources, notes, "base game", Path.Combine(cooked, "BIOC_Materials.pcc"), map, kind);
                }
                break;
            case "conditional":
                AddPackage(sources, notes, "base game", GamePaths.ResolvePackage(game, "PlotManager.pcc"), "BioAutoConditionals", kind);
                break;
            case "quest":
                AddPackage(sources, notes, "base game", Path.Combine(cooked, "BIOC_Materials.pcc"), "PlotManagerAuto.QuestMap", kind);
                break;
            case "strref":
                AddPackage(sources, notes, "base game", Path.Combine(cooked, "Startup_INT.pcc"), null, kind);
                break;
        }

        var folders = new List<(string Name, string Folder, string Ini, bool Installed)> { ("base game", cooked, Path.Combine(cooked, "Autoload.ini"), true) };
        folders.AddRange(MELoadedDLC.GetEnabledDLCFolders(game).Select(d => (Path.GetFileName(d), d, Path.Combine(d, "AutoLoad.ini"), true)));
        folders.AddRange(extraDlcFolders.Select(GamePaths.ToWindowsPath).Select(d => ($"{Path.GetFileName(d.TrimEnd('\\', '/'))} ({d})", d, Path.Combine(d, "AutoLoad.ini"), false)));

        foreach (var (name, folder, ini, installed) in folders)
        {
            if (!File.Exists(ini))
            {
                if (!Directory.Exists(folder)) notes.Add($"No folder {folder}.");
                continue;
            }
            foreach (var entry in ReadPackages(ini).Where(e => EntryMatches(e.Key, kind)))
            {
                var file = FindPackage(folder, entry.Package);
                if (file is null)
                {
                    // Startup packages (the base game's GlobalTlk) are inside Startup_<LANG>.pcc, scanned above.
                    if (name != "base game") notes.Add($"{name}: {entry.Key} names {entry.Package}, but no {entry.Package}.pcc is in {folder}.");
                    continue;
                }
                AddPackage(sources, notes, name, file, kind == "strref" ? null : entry.Export, kind, installed);
            }
        }

        foreach (var package in packages)
        {
            var path = GamePaths.ResolvePackage(game, package);
            AddPackage(sources, notes, "extra package", path, null, kind, installed: false);
        }
        return sources;
    }

    private static IEnumerable<int> DatabaseIds(PlotDatabaseBase db, string kind) => kind switch
    {
        "bool" => db.Bools.Keys,
        "int" => db.Ints.Keys,
        "float" => db.Floats.Keys,
        "conditional" => db.Conditionals.Keys,
        "transition" => db.Transitions.Keys,
        _ => [],
    };

    /// <summary>
    /// Adds the ids one package defines: plot map events or elements, conditional functions F&lt;id&gt; of a class,
    /// or the strings of its BioTlkFile exports. <paramref name="export"/> null means every export of the right class.
    /// </summary>
    private static void AddPackage(List<Source> sources, List<string> notes, string owner, string path, string? export, string kind, bool installed = true)
    {
        if (!File.Exists(path))
        {
            notes.Add($"{owner}: no file {path}.");
            return;
        }
        bool Wanted(ExportEntry e) => kind switch
        {
            "strref" => e.ClassName == "BioTlkFile",
            "conditional" => false,
            "quest" => e.ClassName == "BioQuestMap" && (export is null || e.InstancedFullPath.Equals(export, StringComparison.OrdinalIgnoreCase)),
            _ => e.ClassName == "BioStateEventMap" && (export is null ? MapMatches(e.ObjectName.Instanced, kind)
                                                                      : e.InstancedFullPath.Equals(export, StringComparison.OrdinalIgnoreCase)),
        };

        using var pcc = MEPackageHandler.UnsafePartialLoad(path, Wanted);
        var file = Path.GetFileName(path);
        if (kind == "conditional")
        {
            var classes = pcc.Exports.Where(e => e.ClassName == "Class"
                                                 && (export is null || e.InstancedFullPath.Equals(export, StringComparison.OrdinalIgnoreCase))).ToList();
            var functions = pcc.Exports.Where(e => e.ClassName == "Function" && classes.Any(c => c.UIndex == e.idxLink)).Select(e => e.ObjectName.Name);
            Add(sources, $"{owner}: {file} {export ?? "classes"}",
                functions.Where(n => n.Length > 1 && n[0] == 'F' && n[1..].All(char.IsAsciiDigit)).Select(n => int.Parse(n[1..])), installed);
            return;
        }

        var targets = pcc.Exports.Where(Wanted).ToList();
        if (targets.Count == 0)
        {
            notes.Add($"{owner}: {file} has no {(kind switch { "strref" => "BioTlkFile", "quest" => "BioQuestMap", _ => export ?? "plot map" })} export.");
            return;
        }
        if (kind == "strref")
        {
            // Male/female copies and every language of a TLK set share ids: one source per file.
            Add(sources, $"{owner}: {file} ({targets.Count} BioTlkFile)", targets.SelectMany(t => new ME1TalkFile(t).StringRefs.Select(r => r.StringID)), installed);
            return;
        }
        foreach (var target in targets)
        {
            Add(sources, $"{owner}: {file} {target.InstancedFullPath}", kind == "quest"
                ? ObjectBinary.From<BioQuestMap>(target).Quests.Select(q => q.ID)
                : MapIds(ObjectBinary.From<BioStateEventMap>(target), kind), installed);
        }
    }

    private static void Add(List<Source> sources, string name, IEnumerable<int> ids, bool installed = true)
    {
        var set = ids.Where(i => i >= 0).ToHashSet();
        if (set.Count > 0) sources.Add(new Source(name, set, installed));
    }

    private static IEnumerable<int> MapIds(BioStateEventMap map, string kind) => kind switch
    {
        "transition" or "consequence" => map.StateEvents.Select(e => e.ID),
        _ => map.StateEvents.SelectMany(e => e.Elements).SelectMany(x => ElementIds(x, kind)),
    };

    private static IEnumerable<int> ElementIds(BioStateEventElement element, string kind) => (kind, element) switch
    {
        ("bool", BioStateEventElementBool b) => [b.GlobalBool],
        ("bool", BioStateEventElementSubstate s) => [s.GlobalBool, s.ParentIndex, .. s.SiblingIndices],
        ("int", BioStateEventElementInt i) => [i.GlobalInt],
        ("float", BioStateEventElementFloat f) => [f.GlobalFloat],
        _ => [],
    };

    /// <summary>Transitions live in StateTransitionMaps, consequences in ConsequenceMaps; variables are written by both.</summary>
    private static bool MapMatches(string mapName, string kind) => kind switch
    {
        "transition" => mapName.EndsWith("StateTransitionMap", StringComparison.OrdinalIgnoreCase),
        "consequence" => mapName.EndsWith("ConsequenceMap", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static bool EntryMatches(string key, string kind) => kind switch
    {
        "transition" => key.StartsWith("PlotManagerStateTransitionMap", StringComparison.OrdinalIgnoreCase),
        "consequence" => key.StartsWith("PlotManagerConsequenceMap", StringComparison.OrdinalIgnoreCase),
        "bool" or "int" or "float" => key.StartsWith("PlotManagerStateTransitionMap", StringComparison.OrdinalIgnoreCase)
                                      || key.StartsWith("PlotManagerConsequenceMap", StringComparison.OrdinalIgnoreCase),
        "conditional" => key.StartsWith("PlotManagerConditionals", StringComparison.OrdinalIgnoreCase),
        "quest" => key.StartsWith("PlotManagerQuestMap", StringComparison.OrdinalIgnoreCase),
        "strref" => key.StartsWith("GlobalTalkTable", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>The [Packages] entries of an Autoload.ini whose value is Package.Export.</summary>
    internal static List<AutoloadEntry> ReadPackages(string ini)
    {
        var entries = new List<AutoloadEntry>();
        var inPackages = false;
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inPackages = line.Equals("[Packages]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (!inPackages || eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            var dot = value.IndexOf('.');
            if (dot <= 0) continue;
            entries.Add(new AutoloadEntry(line[..eq].Trim(), value[..dot], value[(dot + 1)..]));
        }
        return entries;
    }

    internal static string? FindPackage(string folder, string package) =>
        Directory.EnumerateFiles(folder, package + ".pcc", SearchOption.AllDirectories).FirstOrDefault();
}

/// <summary>
/// The ids id_alloc handed out that no scan sees yet. An id goes free, chosen (in a YAML or a tool argument), built, installed,
/// and <see cref="IdAllocator.Collect"/> sees only installed ids and the folders passed in: without this ledger, two calls
/// before an install hand out the same ids. One file per Windows user, shared by every session's server:
/// %LOCALAPPDATA%\mele-mcp\id-reservations.json, or the file <see cref="FileEnvVar"/> names (tests). It is read and written under
/// a lock file and written beside, then moved over, so two servers never lose each other's reservations.
/// </summary>
internal sealed class IdReservations : IDisposable
{
    internal const string FileEnvVar = "MELE_ID_RESERVATIONS";

    /// <summary>A reservation no scan has found installed by then ends: a mod given up on does not hold its ids for ever.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <param name="Owner">id_alloc's owner (a mod's name), compared ignoring case; null: none, held against every later call.</param>
    /// <param name="Reserved">UTC.</param>
    internal sealed record Entry(string Kind, string? Owner, int First, int Last, DateTime Reserved);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string path;
    private readonly FileStream gate;
    private readonly DateTime now;
    private List<Entry> entries;
    private bool changed;

    private IdReservations(string path, FileStream gate, DateTime now, List<Entry> entries, bool changed)
    {
        this.path = path;
        this.gate = gate;
        this.now = now;
        this.entries = entries;
        this.changed = changed;
    }

    internal IReadOnlyList<Entry> Entries => entries;

    internal static string FilePath()
    {
        var custom = Environment.GetEnvironmentVariable(FileEnvVar);
        return string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mele-mcp", "id-reservations.json")
            : GamePaths.ToWindowsPath(custom);
    }

    /// <summary>Takes the lock (until Dispose) and reads the file; reservations older than <see cref="Lifetime"/> are dropped.</summary>
    internal static IdReservations Open(string path, DateTime now)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var gate = Lock(full + ".lock");
        try
        {
            List<Entry> read = [];
            if (File.Exists(full))
            {
                try
                {
                    read = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(full), Json) ?? [];
                }
                catch (JsonException e)
                {
                    throw new InvalidDataException($"{full} is not an id reservation file ({e.Message}). Fix it, or delete it to drop every reservation.", e);
                }
            }
            var live = read.Where(e => e.Kind is not null && e.First <= e.Last && e.Reserved + Lifetime > now).ToList();
            return new IdReservations(full, gate, now, live, live.Count != read.Count);
        }
        catch (Exception)
        {
            gate.Dispose();
            throw;
        }
    }

    /// <summary>Ends the reservations of <paramref name="kind"/> ids an installed source now has; returns those ids.</summary>
    internal List<int> Release(string kind, HashSet<int> installed)
    {
        var released = new List<int>();
        var kept = new List<Entry>();
        foreach (var entry in entries)
        {
            if (entry.Kind != kind || !Ids(entry).Any(installed.Contains))
            {
                kept.Add(entry);
                continue;
            }
            released.AddRange(Ids(entry).Where(installed.Contains));
            kept.AddRange(Ranges(Ids(entry).Where(id => !installed.Contains(id))).Select(r => entry with { First = r.First, Last = r.Last }));
        }
        if (released.Count > 0)
        {
            entries = kept;
            changed = true;
        }
        return released;
    }

    /// <summary>The <paramref name="kind"/> ids reserved for anyone but <paramref name="owner"/>, a source per owner.</summary>
    internal List<IdAllocator.Source> HeldByOthers(string kind, string? owner) =>
        entries.Where(e => e.Kind == kind && !SameOwner(e.Owner, owner))
            .GroupBy(e => e.Owner ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new IdAllocator.Source(
                $"reserved by id_alloc {(g.Key.Length == 0 ? "for no owner" : $"for '{g.First().Owner}'")} until {g.Max(e => e.Reserved) + Lifetime:yyyy-MM-dd}",
                g.SelectMany(Ids).ToHashSet(), Installed: false))
            .ToList();

    /// <summary>Reserves <paramref name="ids"/> for <paramref name="owner"/> from now (an owner's own ids again start anew); returns when they end.</summary>
    internal DateTime Reserve(string kind, string? owner, IReadOnlyCollection<int> ids)
    {
        var set = ids.ToHashSet();
        var kept = new List<Entry>();
        foreach (var entry in entries)
        {
            if (entry.Kind != kind || !SameOwner(entry.Owner, owner) || !Ids(entry).Any(set.Contains))
            {
                kept.Add(entry);
                continue;
            }
            kept.AddRange(Ranges(Ids(entry).Where(id => !set.Contains(id))).Select(r => entry with { First = r.First, Last = r.Last }));
        }
        kept.AddRange(Ranges(set).Select(r => new Entry(kind, owner, r.First, r.Last, now)));
        entries = kept;
        changed = true;
        return now + Lifetime;
    }

    /// <summary>Writes the file when anything changed: beside it, then moved over it, so a reader never sees half a file.</summary>
    internal void Save()
    {
        if (!changed) return;
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(entries.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.First).ToList(), Json), new UTF8Encoding(false));
        changed = false;
    }

    public void Dispose() => gate.Dispose();

    /// <summary>8421-8425, 8430.</summary>
    internal static string Text(IEnumerable<int> ids)
    {
        var ranges = Ranges(ids).Select(r => r.First == r.Last ? $"{r.First}" : $"{r.First}-{r.Last}").ToList();
        return ranges.Count <= 10 ? string.Join(", ", ranges) : $"{string.Join(", ", ranges.Take(10))}, ... ({ranges.Count} ranges)";
    }

    private static List<(int First, int Last)> Ranges(IEnumerable<int> ids)
    {
        var ranges = new List<(int First, int Last)>();
        foreach (var id in ids.Distinct().Order())
        {
            if (ranges.Count > 0 && (long)ranges[^1].Last + 1 == id) ranges[^1] = (ranges[^1].First, id);
            else ranges.Add((id, id));
        }
        return ranges;
    }

    private static IEnumerable<int> Ids(Entry entry)
    {
        for (long id = entry.First; id <= entry.Last; id++) yield return (int)id;
    }

    private static bool SameOwner(string? a, string? b) => a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>One id_alloc call at a time in every server: the ledger is read, changed and written under it.</summary>
    private static FileStream Lock(string file)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException e)
            {
                throw new IOException($"Another id_alloc call has held {file} for 10 s: try again.", e);
            }
        }
    }
}
