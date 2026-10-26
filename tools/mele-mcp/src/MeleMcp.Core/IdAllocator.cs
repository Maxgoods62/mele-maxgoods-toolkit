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
/// Game 1 plot and string ids already in use: LEX's base-game plot database, LEX mod plot databases, and what the base
/// game and every installed DLC wire in their Autoload.ini (plot maps, conditionals, GlobalTlk), so new ids do not collide.
/// </summary>
public static class IdAllocator
{
    public static readonly string[] Kinds = ["bool", "int", "float", "conditional", "transition", "consequence", "strref"];

    private const int SearchSpan = 1_000_000;

    private sealed record Source(string Name, HashSet<int> Ids);

    private sealed record AutoloadEntry(string Key, string Package, string Export);

    public static IdReport Find(MEGame game, string kind, IReadOnlyList<int>? ids, int? from, int count,
                                IReadOnlyList<string>? extraDlcFolders, IReadOnlyList<string>? packages)
    {
        if (!game.IsGame1()) throw new ArgumentException("id_alloc handles Game 1 (LE1).");
        kind = kind.Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) throw new ArgumentException($"Unknown kind '{kind}'. Use one of: {string.Join(", ", Kinds)}.");
        if (ids is not { Count: > 0 } && from is null) throw new ArgumentException("Give ids to check, or from (and count) to find free ids.");
        if (count < 1) throw new ArgumentException("count must be at least 1.");
        LecHost.EnsureInitialized();

        var notes = new List<string>();
        var sources = Collect(game, kind, extraDlcFolders ?? [], packages ?? [], notes);
        bool IsUsed(int id) => sources.Any(s => s.Ids.Contains(id));
        IdUse Use(int id) => new(id, sources.Where(s => s.Ids.Contains(id)).Select(s => s.Name).ToList());

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
            for (var id = from!.Value; free.Count < count && id < from.Value + SearchSpan; id++)
            {
                if (IsUsed(id)) used.Add(Use(id));
                else free.Add(id);
            }
            if (free.Count < count) notes.Add($"Only {free.Count} free ids between {from} and {from + SearchSpan - 1}.");
        }

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

    private static List<Source> Collect(MEGame game, string kind, IReadOnlyList<string> extraDlcFolders, IReadOnlyList<string> packages, List<string> notes)
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
            case "strref":
                AddPackage(sources, notes, "base game", Path.Combine(cooked, "Startup_INT.pcc"), null, kind);
                break;
        }

        var folders = new List<(string Name, string Folder, string Ini)> { ("base game", cooked, Path.Combine(cooked, "Autoload.ini")) };
        folders.AddRange(MELoadedDLC.GetEnabledDLCFolders(game).Select(d => (Path.GetFileName(d), d, Path.Combine(d, "AutoLoad.ini"))));
        folders.AddRange(extraDlcFolders.Select(GamePaths.ToWindowsPath).Select(d => ($"{Path.GetFileName(d.TrimEnd('\\', '/'))} ({d})", d, Path.Combine(d, "AutoLoad.ini"))));

        foreach (var (name, folder, ini) in folders)
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
                AddPackage(sources, notes, name, file, kind == "strref" ? null : entry.Export, kind);
            }
        }

        foreach (var package in packages)
        {
            var path = GamePaths.ResolvePackage(game, package);
            AddPackage(sources, notes, "extra package", path, null, kind);
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
    private static void AddPackage(List<Source> sources, List<string> notes, string owner, string path, string? export, string kind)
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
                functions.Where(n => n.Length > 1 && n[0] == 'F' && n[1..].All(char.IsAsciiDigit)).Select(n => int.Parse(n[1..])));
            return;
        }

        var targets = pcc.Exports.Where(Wanted).ToList();
        if (targets.Count == 0)
        {
            notes.Add($"{owner}: {file} has no {(kind == "strref" ? "BioTlkFile" : export ?? "plot map")} export.");
            return;
        }
        if (kind == "strref")
        {
            // Male/female copies and every language of a TLK set share ids: one source per file.
            Add(sources, $"{owner}: {file} ({targets.Count} BioTlkFile)", targets.SelectMany(t => new ME1TalkFile(t).StringRefs.Select(r => r.StringID)));
            return;
        }
        foreach (var target in targets)
        {
            Add(sources, $"{owner}: {file} {target.InstancedFullPath}", MapIds(ObjectBinary.From<BioStateEventMap>(target), kind));
        }
    }

    private static void Add(List<Source> sources, string name, IEnumerable<int> ids)
    {
        var set = ids.Where(i => i >= 0).ToHashSet();
        if (set.Count > 0) sources.Add(new Source(name, set));
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
        "strref" => key.StartsWith("GlobalTalkTable", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>The [Packages] entries of an Autoload.ini whose value is Package.Export.</summary>
    private static List<AutoloadEntry> ReadPackages(string ini)
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

    private static string? FindPackage(string folder, string package) =>
        Directory.EnumerateFiles(folder, package + ".pcc", SearchOption.AllDirectories).FirstOrDefault();
}
