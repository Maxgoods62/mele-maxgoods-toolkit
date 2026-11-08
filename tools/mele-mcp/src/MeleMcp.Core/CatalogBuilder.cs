using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using Microsoft.Win32;
using static LegendaryExplorerCore.Unreal.UnrealFlags;

namespace MeleMcp.Core;

/// <param name="Mesh">The mesh's path (InstancedFullPath), or what was asked for.</param>
public sealed record CatalogSkip(string Mesh, string Reason);

/// <param name="Path">The built package in the mod's folder in M3's library.</param>
/// <param name="ImportFiles">The files its imports resolve to: they must be loaded wherever a mesh is spawned (SFXGame, BIOC_Materials are, from startup).</param>
/// <param name="UnresolvedImports">Imports no file the game loads has: the mesh would miss a material or texture in game.</param>
/// <param name="RelinkIssues">LEC's porting report lines, all meshes together.</param>
/// <param name="Backup">The previous package, copied before it was replaced.</param>
public sealed record CatalogPackage(string Path, long Bytes, int Exports, int Imports, IReadOnlyList<string> ImportFiles, IReadOnlyList<string> UnresolvedImports,
    int RelinkIssues, IReadOnlyList<string> RelinkSamples, string? Backup);

/// <param name="Entries">Meshes in catalog.json; ByCategory counts them per menu category.</param>
/// <param name="Skipped">Meshes asked for and left out (not found, unreadable, a material with no shader in the game).</param>
/// <param name="WithoutThumbnail">Meshes in the catalog with 'thumb: null', and why.</param>
/// <param name="SourceFiles">Files the meshes were read from (each opened once).</param>
/// <param name="Package">The built package; null with package=false.</param>
/// <param name="Seconds">Time per step: index, open (source files), thumbnails, port, check (shaders), save, install (imports check, move, backup), write.</param>
public sealed record CatalogBuildResult(int Entries, IReadOnlyDictionary<string, int> ByCategory, int Thumbnails, IReadOnlyList<CatalogSkip> Skipped,
    IReadOnlyList<CatalogSkip> WithoutThumbnail, int SourceFiles, CatalogPackage? Package, string Catalog, string Thumbs, string? ModFolder,
    IReadOnlyList<string> Written, IReadOnlyDictionary<string, double> Seconds, IReadOnlyList<string> Notes);

/// <summary>
/// Builds the in-game editor's prop catalog (docs/design/in-game-editor.md §3.5, §3.6): MeleCatalog_Core.pcc, a package of the chosen meshes that the
/// bridge loads one mesh at a time with DynamicLoadObject, in a dev mod of its own in M3's library; and, for the bridge's menu,
/// catalog.json and its thumbnails under %LOCALAPPDATA%\MeleBridge\LE1\catalog. Nothing in the game install is written: the
/// package reaches the game when the mod is installed (mod_install, game closed).
/// </summary>
public static class CatalogBuilder
{
    public const string PackageName = "MeleCatalog_Core";
    public const string ModName = "Mele Catalog";
    public const string DlcName = "DLC_MOD_MeleCatalog";
    private const int ModMount = 7420;
    private const int FormatVersion = 1;

    /// <summary>Files LEC imports from (EntryImporter's LE1 list) that the game does not load on every map.</summary>
    private static readonly string[] NotAlwaysLoaded = ["SFXWorldResources.pcc", "SFXVehicleResources.pcc"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="Curated">Start from the default list (the curation rules' apl, apl-large and levelprop meshes).</param>
    /// <param name="Extra">More meshes by path (InstancedFullPath, as catalog_index lists them), whatever the rules made of them.</param>
    /// <param name="Thumbs">Render thumbnails; without, the ones already rendered for the same entry are kept.</param>
    /// <param name="Package">Build MeleCatalog_Core.pcc; without, catalog.json's 'load' paths are what a build gives.</param>
    /// <param name="ModFolder">The dev mod's folder; default M3's library, mods\LE1\Mele Catalog.</param>
    /// <param name="OutDir">Where catalog.json and thumbs\ go; default %LOCALAPPDATA%\MeleBridge\LE1\catalog.</param>
    public sealed record Options(bool Curated, IReadOnlyList<string> Extra, bool Thumbs, bool Package, string? ModFolder, string? OutDir);

    /// <summary>The bridge reads the catalog here.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeleBridge", "LE1", "catalog");

    /// <summary>One mesh of the catalog: where it comes from, the group and name it gets in the package, its thumbnail.</summary>
    private sealed class Entry(CatalogMesh mesh, string category, string group, string name)
    {
        public CatalogMesh Mesh { get; } = mesh;
        public string Category { get; } = category;
        public string Group { get; } = group;
        public string Name { get; } = name;
        public string Load => $"{PackageName}.{Group}.{Name}";
        public string ThumbFile => Regex.Replace($"{Group}_{Name}", @"[^A-Za-z0-9_.-]", "_") + ".png";
        public string? Skip { get; set; }
        public bool Thumbnail { get; set; }
        public string? ThumbSkip { get; set; }
        public ExportEntry? Ported { get; set; }
    }

    public static CatalogBuildResult Build(MEGame game, Options options)
    {
        if (!game.IsGame1()) throw new ArgumentException("The prop catalog handles Game 1 (LE1).");
        var seconds = new Dictionary<string, double>();
        var notes = new List<string>();
        var outDir = options.OutDir is null ? DefaultFolder : Path.GetFullPath(GamePaths.ToWindowsPath(options.OutDir));
        EnsureOutsideInstall(game, Path.Combine(outDir, "catalog.json"));
        var modFolder = options.Package || options.ModFolder is not null
            ? options.ModFolder is null ? Path.Combine(Library(), game.ToString(), ModName) : Path.GetFullPath(GamePaths.ToWindowsPath(options.ModFolder))
            : null;
        if (modFolder is not null) EnsureOutsideInstall(game, Path.Combine(modFolder, "moddesc.ini"));

        var sw = Stopwatch.StartNew();
        var index = CatalogIndex.Load(game);
        seconds["index"] = Round(sw);
        var skipped = new List<CatalogSkip>();
        var entries = Choose(index, options, skipped);
        if (entries.Count == 0) throw new ArgumentException("Nothing to build: no curated mesh and no extra mesh found. catalog_index search=<text> lists mesh paths.");

        Directory.CreateDirectory(outDir);
        var thumbsDir = Path.Combine(outDir, "thumbs");
        var newThumbs = Path.Combine(outDir, "thumbs.new");
        if (options.Thumbs)
        {
            if (Directory.Exists(newThumbs)) Directory.Delete(newThumbs, true);
            Directory.CreateDirectory(newThumbs);
        }

        var times = new Dictionary<string, Stopwatch> { ["open"] = new(), ["thumbnails"] = new(), ["port"] = new(), ["check"] = new(), ["save"] = new() };
        var relink = new List<string>();
        var sources = entries.Select(e => e.Mesh.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        CatalogPackage? package = null;
        var written = new List<string>();
        using (var cache = new PackageCache())
        {
            string? built = null;
            if (options.Package)
            {
                built = Pass(game, entries, true, options.Thumbs ? newThumbs : null, modFolder!, cache, times, relink, out var broken);
                if (broken.Count > 0)
                {
                    // A material whose shaders are neither in the package nor in the game's RefShaderCache renders as the default
                    // material in game: its meshes are left out, and the package is built again without them.
                    foreach (var (entry, material) in broken)
                    {
                        entry.Skip = $"material {material} has no shader in the game's shader cache (ShaderCacheManipulator.GetBrokenMaterials)";
                    }
                    built = Pass(game, entries, true, null, modFolder!, cache, times, relink, out broken);
                    if (broken.Count > 0) throw new InvalidOperationException($"Materials without shaders after leaving their meshes out: {string.Join(", ", broken.Select(b => b.Material).Distinct())}.");
                }
            }
            else if (options.Thumbs)
            {
                Pass(game, entries, false, newThumbs, null, cache, times, relink, out _);
            }
            foreach (var (step, watch) in times) seconds[step] = Math.Round(watch.Elapsed.TotalSeconds, 2);

            sw.Restart();
            if (built is not null)
            {
                package = Inspect(built, cache, relink, notes);
                package = Install(game, built, modFolder!, package, written, notes);
                seconds["install"] = Round(sw);
            }
        }

        sw.Restart();
        skipped.AddRange(entries.Where(e => e.Skip is not null).Select(e => new CatalogSkip(e.Mesh.Path, e.Skip!)));
        var kept = entries.Where(e => e.Skip is null).ToList();
        if (options.Thumbs)
        {
            foreach (var e in entries.Where(e => e.Skip is not null && e.Thumbnail)) File.Delete(Path.Combine(newThumbs, e.ThumbFile));
            Swap(newThumbs, thumbsDir);
            written.Add(thumbsDir);
        }
        else
        {
            foreach (var e in kept) e.Thumbnail = File.Exists(Path.Combine(thumbsDir, e.ThumbFile));
            notes.Add("thumbs=false: entries keep the thumbnails already rendered for them; the others have 'thumb: null'.");
        }
        if (modFolder is not null) WriteMod(modFolder, written, notes);
        if (!options.Package) notes.Add($"package=false: {PackageName}.pcc was not rebuilt, so the 'load' paths are those a build gives; build the package before installing it.");

        var catalog = Path.Combine(outDir, "catalog.json");
        var json = new JsonCatalog(FormatVersion, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), PackageName, CatalogRules.Categories,
            kept.Select(e => new JsonEntry(e.Mesh.Name, e.Category, e.Mesh.Path, e.Mesh.Source, e.Load, e.Thumbnail ? $"thumbs/{e.ThumbFile}" : null,
                e.Mesh.Size, e.Mesh.Tris, e.Mesh.Placed, e.Mesh.Large)).ToList());
        // Written beside it, then moved over it: the bridge never reads half a file.
        AtomicFile.WriteAllText(catalog, JsonSerializer.Serialize(json, Json));
        written.Add(catalog);
        seconds["write"] = Round(sw);
        if (package is not null) notes.Add($"Install the mod (game closed: mod_install mod='{ModName}') so the game finds {PackageName} by name.");

        return new CatalogBuildResult(kept.Count, CatalogRules.Categories.ToDictionary(c => c, c => kept.Count(e => e.Category == c)), kept.Count(e => e.Thumbnail),
            skipped, kept.Where(e => !e.Thumbnail).Select(e => new CatalogSkip(e.Mesh.Path, e.ThumbSkip ?? (options.Thumbs ? "not rendered" : "no thumbnail rendered yet"))).ToList(),
            sources, package, catalog, thumbsDir, modFolder, written, seconds, notes);
    }

    /// <summary>
    /// The curated meshes and the extra ones, in menu order (category, then the most placed), each with its group (the category
    /// in one word: SmallItems) and a name unique in it (a second mesh of the same name gets _2, _3...).
    /// </summary>
    private static List<Entry> Choose(CatalogIndex index, Options options, List<CatalogSkip> skipped)
    {
        var chosen = new Dictionary<string, (CatalogMesh Mesh, string Category)>(StringComparer.OrdinalIgnoreCase);
        if (options.Curated)
        {
            foreach (var m in index.Meshes.Where(m => m.Curated)) chosen[m.Path] = (m, m.Category ?? "Other");
        }
        foreach (var path in options.Extra)
        {
            if (index.Find(path) is not { } m) skipped.Add(new CatalogSkip(path, "no StaticMesh with this path in the files the game loads (catalog_index search=<text> lists paths)."));
            else chosen.TryAdd(m.Path, (m, m.Category ?? "Other"));
        }

        var order = CatalogRules.Categories.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var names = new Dictionary<string, HashSet<string>>();
        var entries = new List<Entry>();
        foreach (var (mesh, category) in chosen.Values.OrderBy(c => order[c.Category]).ThenByDescending(c => c.Mesh.Placed).ThenBy(c => c.Mesh.Path, StringComparer.OrdinalIgnoreCase))
        {
            var group = string.Concat(Regex.Split(category, "[^A-Za-z0-9]+").Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
            var taken = names.TryGetValue(group, out var set) ? set : names[group] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var name = mesh.Name;
            for (var k = 2; !taken.Add(name); k++) name = $"{mesh.Name}_{k}";
            entries.Add(new Entry(mesh, category, group, name));
        }
        return entries;
    }

    /// <summary>
    /// One pass over the source files, each opened once: renders the thumbnails (when thumbsDir is given) and ports the meshes
    /// into a new package (when port is set), each moved under its category's group, not forced, so the game finds it in memory
    /// as MeleCatalog_Core.&lt;Group&gt;.&lt;Name&gt;. Materials and textures stay forced exports at their vanilla paths, so a copy a level
    /// has already loaded is the one used. Returns the saved package's path (in the mod folder's .build, outside what M3 installs);
    /// null, unsaved, when materials have no shader (broken lists the meshes that use them).
    /// </summary>
    private static string? Pass(MEGame game, List<Entry> entries, bool port, string? thumbsDir, string? modFolder, PackageCache cache,
        Dictionary<string, Stopwatch> times, List<string> relink, out List<(Entry Entry, string Material)> broken)
    {
        broken = [];
        var todo = entries.Where(e => e.Skip is null).ToList();
        IMEPackage? catalog = null;
        string? path = null;
        var groups = new Dictionary<string, ExportEntry>();
        if (port)
        {
            var build = Path.Combine(modFolder!, ".build");
            Directory.CreateDirectory(build);
            path = Path.Combine(build, PackageName + ".pcc");
            if (File.Exists(path)) File.Delete(path);
            PackageWriter.Create(path, game);
            catalog = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            foreach (var group in todo.Select(e => e.Group).Distinct()) groups[group] = ExportCreator.CreatePackageExport(catalog, group, null, forcedExport: false);
            relink.Clear();
        }
        var renderer = new ThumbnailRenderer(cache);
        try
        {
            foreach (var source in todo.GroupBy(e => e.Mesh.SourcePath, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                IMEPackage pcc;
                times["open"].Start();
                try
                {
                    pcc = MEPackageHandler.OpenMEPackage(source.Key, forceLoadFromDisk: true);
                }
                catch (Exception ex)
                {
                    foreach (var e in source) e.Skip = $"{Path.GetFileName(source.Key)} unreadable: {ex.GetType().Name}: {ex.Message}";
                    continue;
                }
                finally
                {
                    times["open"].Stop();
                }
                using (pcc)
                {
                    foreach (var e in source)
                    {
                        var export = pcc.Exports.FirstOrDefault(x => x.ClassName == "StaticMesh" && x.InstancedFullPath.Equals(e.Mesh.Path, StringComparison.OrdinalIgnoreCase));
                        if (export is null)
                        {
                            e.Skip = $"no StaticMesh {e.Mesh.Path} in {pcc.FilePath} any more: run catalog_build again (the index is rescanned when files change).";
                            continue;
                        }
                        if (thumbsDir is not null) Render(renderer, export, e, thumbsDir, times["thumbnails"]);
                        if (catalog is not null) Port(export, e, catalog, groups[e.Group], cache, relink, times["port"]);
                    }
                }
            }
            if (catalog is null) return null;

            times["check"].Start();
            var bad = ShaderCacheManipulator.GetBrokenMaterials(catalog);
            times["check"].Stop();
            if (bad.Count > 0)
            {
                var paths = bad.Select(b => b.InstancedFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var e in todo.Where(e => e.Ported is not null))
                {
                    var uses = EntryImporter.GetAllReferencesOfExport(e.Ported!).FirstOrDefault(r => paths.Contains(r.InstancedFullPath));
                    if (uses is not null) broken.Add((e, uses.InstancedFullPath));
                }
                if (broken.Count == 0) throw new InvalidOperationException($"Materials without shaders that no mesh uses: {string.Join(", ", paths)}.");
                return null;
            }
            times["save"].Start();
            PackageWriter.Save(catalog, backup: false);
            times["save"].Stop();
            return path;
        }
        finally
        {
            catalog?.Dispose();
        }
    }

    private static void Render(ThumbnailRenderer renderer, ExportEntry export, Entry e, string thumbsDir, Stopwatch time)
    {
        time.Start();
        try
        {
            var thumbnail = renderer.Render(export);
            if (thumbnail.Png is null)
            {
                e.ThumbSkip = $"{thumbnail.Skip} ({string.Join("; ", thumbnail.Elements)})";
                return;
            }
            AtomicFile.WriteAllBytes(Path.Combine(thumbsDir, e.ThumbFile), thumbnail.Png);
            e.Thumbnail = true;
        }
        catch (Exception ex)
        {
            e.ThumbSkip = $"render failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }
        finally
        {
            time.Stop();
        }
    }

    /// <summary>The mesh and what it needs copied into the catalog (as level_build copies a prop's mesh), then moved under its group.</summary>
    private static void Port(ExportEntry export, Entry e, IMEPackage catalog, ExportEntry group, PackageCache cache, List<string> relink, Stopwatch time)
    {
        time.Start();
        try
        {
            var report = EntryExporter.ExportExportToPackage(export, catalog, out var ported, cache);
            relink.AddRange(report.Select(r => $"{e.Mesh.Path}: {r}"));
            if (ported is not ExportEntry { ClassName: "StaticMesh" } mesh)
            {
                e.Skip = $"porting gave {ported?.ClassName ?? "nothing"}, not a StaticMesh export.";
                return;
            }
            mesh.Parent = group;
            if (mesh.ObjectName.Instanced != e.Name) mesh.ObjectName = NameReference.FromInstancedString(e.Name);
            // Not forced: a forced export lives in memory at its path without the file name, which DynamicLoadObject can't open.
            foreach (var x in mesh.GetAllDescendants().OfType<ExportEntry>().Prepend(mesh)) x.ExportFlags &= ~EExportFlags.ForcedExport;
            e.Ported = mesh;
        }
        catch (Exception ex)
        {
            e.Skip = $"porting failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }
        finally
        {
            time.Stop();
        }
    }

    /// <summary>
    /// Size, counts, and where each import resolves (the files the game loads; startup ones are SFXGame and BIOC_Materials). An
    /// import from a file the game does not always load (LEC: SFXWorldResources and SFXVehicleResources are not loaded on STA
    /// maps) is noted with the meshes that use it.
    /// </summary>
    private static CatalogPackage Inspect(string path, PackageCache cache, List<string> relink, List<string> notes)
    {
        using var pcc = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        var notAlways = new Dictionary<ImportEntry, string>();
        foreach (var import in pcc.Imports.Where(i => i.ClassName is not ("Class" or "Package")))
        {
            var resolved = EntryImporter.ResolveImport(import, cache);
            if (resolved is null)
            {
                unresolved.Add($"{import.InstancedFullPath} ({import.ClassName})");
                continue;
            }
            var file = Path.GetFileName(resolved.FileRef.FilePath);
            files.Add(file);
            if (NotAlwaysLoaded.Contains(file, StringComparer.OrdinalIgnoreCase)) notAlways[import] = file;
        }
        if (notAlways.Count > 0)
        {
            foreach (var mesh in pcc.Exports.Where(e => e.ClassName == "StaticMesh"))
            {
                var uses = EntryImporter.GetAllReferencesOfExport(mesh).OfType<ImportEntry>().Where(notAlways.ContainsKey).ToList();
                if (uses.Count == 0) continue;
                notes.Add($"{mesh.MemoryFullPath} imports {string.Join(", ", uses.Select(u => $"{u.ObjectName.Instanced} from {notAlways[u]}"))}: "
                          + "LEC says that file is not loaded on STA (Citadel) maps, where the mesh may show without that texture.");
            }
        }
        return new CatalogPackage(path, new FileInfo(path).Length, pcc.ExportCount, pcc.ImportCount, files.ToList(), unresolved, relink.Count, relink.Take(10).ToList(), null);
    }

    /// <summary>The built package moved over the mod's copy (backed up first), in DLC_MOD_MeleCatalog\CookedPCConsole.</summary>
    private static CatalogPackage Install(MEGame game, string built, string modFolder, CatalogPackage package, List<string> written, List<string> notes)
    {
        var target = Path.Combine(modFolder, DlcName, "CookedPCConsole", PackageName + ".pcc");
        EnsureOutsideInstall(game, target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var backup = File.Exists(target) ? PackageWriter.Backup(target) : null;
        File.Move(built, target, overwrite: true);
        Directory.Delete(Path.GetDirectoryName(built)!, true);
        written.Add(target);
        GamePaths.RefreshLoadedFiles();
        if (package.UnresolvedImports.Count > 0) notes.Add($"{package.UnresolvedImports.Count} imports resolve to no file the game loads: the meshes that use them miss a material or texture in game.");
        return package with { Path = target, Backup = backup };
    }

    /// <summary>
    /// PackageWriter's check covers Game\ME1 only; the catalog's folders are free paths, so the whole Legendary Edition install
    /// (its root, Game\ME2, Game\ME3, the launcher) is refused too.
    /// </summary>
    private static void EnsureOutsideInstall(MEGame game, string path)
    {
        PackageWriter.EnsureWritable(game, path);
        var le = LegendaryExplorerCoreLibSettings.Instance.LEDirectory;
        if (string.IsNullOrWhiteSpace(le)) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(le)) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to write inside the game install ({path}): give a folder outside {root}.");
        }
    }

    /// <summary>moddesc.ini and AutoLoad.ini of the dev mod, written when missing (an existing one is left as it is).</summary>
    private static void WriteMod(string modFolder, List<string> written, List<string> notes)
    {
        var moddesc = Path.Combine(modFolder, "moddesc.ini");
        var autoload = Path.Combine(modFolder, DlcName, "AutoLoad.ini");
        var files = new (string Path, string Text)[]
        {
            (moddesc, $"""
                [ModManager]
                cmmver=9.2

                [ModInfo]
                game=LE1
                modname={ModName}
                moddev=Maxgoods
                moddesc=Prop meshes the in-game editor (MeleBridge, F8) spawns from its catalog. Built by mele-mcp catalog_build.
                modver=1
                modsite=

                [CUSTOMDLC]
                sourcedirs={DlcName}
                destdirs={DlcName}

                """),
            (autoload, $"""
                [ME1DLCMOUNT]
                ModName={ModName}
                ModMount={ModMount}

                """),
        };
        foreach (var (path, text) in files)
        {
            if (File.Exists(path))
            {
                notes.Add($"{path} exists: left as it is.");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, text.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
            written.Add(path);
        }
    }

    /// <summary>
    /// The new thumbnails folder put in place of the old one: renamed aside, the new one renamed in, the old one deleted. When a
    /// rename is refused (a file held open), the files are moved over one by one and the stale ones deleted. Deleting the old
    /// folder or a stale file is best effort (Defender, the indexer or the bridge may hold a PNG for a moment): the next build
    /// removes what is left.
    /// </summary>
    private static void Swap(string fresh, string current)
    {
        var old = current + ".old";
        TryDelete(old);
        try
        {
            if (Directory.Exists(current)) Directory.Move(current, old);
            Directory.Move(fresh, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(current) && Directory.Exists(old)) Directory.Move(old, current);
        }
        if (!Directory.Exists(fresh))
        {
            TryDelete(old);
            return;
        }
        Directory.CreateDirectory(current);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(fresh))
        {
            var name = Path.GetFileName(file);
            keep.Add(name);
            File.Move(file, Path.Combine(current, name), overwrite: true);
        }
        foreach (var stale in Directory.EnumerateFiles(current).Where(f => !keep.Contains(Path.GetFileName(f)))) TryDelete(stale);
        Directory.Delete(fresh, true);
    }

    /// <summary>A folder or file deleted if it can be; one still held is left (catalog.json no longer names it) for the next build.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next build.
        }
    }

    /// <summary>M3's mod library: settings.ini's LibraryPath, else the 'mods' folder next to ME3TweaksModManager.exe (as mod_install finds it).</summary>
    internal static string Library()
    {
        const string settings = @"C:\ProgramData\ME3TweaksModManager\settings.ini";
        var section = "";
        foreach (var raw in File.Exists(settings) ? File.ReadLines(settings) : [])
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && section.Equals("ModLibrary", StringComparison.OrdinalIgnoreCase) && line[..eq].Trim().Equals("LibraryPath", StringComparison.OrdinalIgnoreCase)
                && line[(eq + 1)..].Trim() is { Length: > 0 } library)
            {
                return library;
            }
        }
        var m3 = OperatingSystem.IsWindows() ? Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\ME3Tweaks", "ExecutableLocation", null) as string : null;
        if (m3 is null) throw new FileNotFoundException("M3's mod library not found (settings.ini LibraryPath, HKCU\\SOFTWARE\\ME3Tweaks ExecutableLocation): give modFolder.");
        return Path.Combine(Path.GetDirectoryName(m3)!, "mods");
    }

    private static double Round(Stopwatch sw) => Math.Round(sw.Elapsed.TotalSeconds, 2);

    /// <summary>catalog.json, the contract with MeleBridge (version 1).</summary>
    private sealed record JsonCatalog(int Version, string Built, string Package, string[] Categories, List<JsonEntry> Entries);

    /// <param name="Mesh">The vanilla InstancedFullPath: the bridge uses a mesh already in memory at this path; the session file records it.</param>
    /// <param name="Source">A file the game loads with the mesh as a StaticMesh export, without .pcc: editor_import and level_build copy it from there.</param>
    /// <param name="Load">The path DynamicLoadObject loads it by, from the catalog package.</param>
    /// <param name="Thumb">Relative to the catalog folder; null when there is none.</param>
    private sealed record JsonEntry(string Name, string Category, string Mesh, string Source, string Load, string? Thumb, int[] Size, int Tris, int Placed, bool Large);
}
