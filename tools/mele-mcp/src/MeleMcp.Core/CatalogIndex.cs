using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MeleMcp.Core;

/// <param name="Path">InstancedFullPath: for a mesh inside a level file (a forced export) also its path in memory once a level has it.</param>
/// <param name="Source">The smallest file the game loads that has it (ties by name), without .pcc: level_build and editor_import open it by name.</param>
/// <param name="Copies">Files the game loads that have it.</param>
/// <param name="Size">Bounds box size on X, Y, Z (2 × extent), in Unreal units.</param>
/// <param name="Placed">StaticMeshComponents under TheWorld in the files the game loads: how often its levels show it.</param>
/// <param name="Forced">A forced export (in its source file); one that is not has the file name in front of its path in memory.</param>
/// <param name="Curation">What <see cref="CatalogRules.Classify"/> made of it: apl, apl-large, levelprop (the default list), unknown, arch-*, excluded-*.</param>
/// <param name="Large">Over <see cref="CatalogRules.LargeSize"/>: only APL props that big are in the default list, with this badge.</param>
public sealed record CatalogMesh(string Path, string Name, string Source, string SourcePath, long SourceBytes, int Copies, int[] Size, int Tris, int Placed,
    bool Forced, string Curation, string? Category, bool Curated, bool Large);

/// <param name="FilesSkipped">Our own mods' files: they only copy vanilla meshes, and the catalog's own package must not list itself.</param>
/// <param name="Cached">The index of an earlier call, reused: no loaded file changed since (size and time).</param>
/// <param name="Matches">Meshes the search matched; Meshes holds up to 'limit' of them.</param>
public sealed record CatalogIndexResult(int FilesScanned, int FilesWithMeshes, IReadOnlyList<string> FilesSkipped, int MeshExports, int UniqueMeshes, int Curated,
    IReadOnlyDictionary<string, int> ByCuration, IReadOnlyDictionary<string, int> CuratedByCategory, double ScanSeconds, bool Cached,
    int Errors, IReadOnlyList<string> ErrorSamples, string? Search, int Matches, IReadOnlyList<CatalogMesh> Meshes);

/// <summary>
/// Every StaticMesh of the files the game loads (LE1CP's copy of a file wins over the vanilla one), one row per InstancedFullPath:
/// the file to copy it from, its size, triangles, how often levels place it, and its category in the editor's prop menu
/// (docs/07 §3.5). Reads export tables and the meshes' and StaticMeshComponents' data only, on every core: about 3 s warm.
/// </summary>
public sealed class CatalogIndex
{
    /// <summary>DLC folders of our own mods, left out: the catalog lists vanilla meshes, never its own copies.</summary>
    public static readonly string[] OwnDlc = ["DLC_MOD_ClaudeChakwas", "DLC_MOD_MeleCatalog"];

    private static readonly object Gate = new();
    private static (MEGame Game, long Signature, CatalogIndex Index)? last;

    public required IReadOnlyList<CatalogMesh> Meshes { get; init; }
    public required int FilesScanned { get; init; }
    public required int FilesWithMeshes { get; init; }
    public required IReadOnlyList<string> FilesSkipped { get; init; }
    public required int MeshExports { get; init; }
    public required double ScanSeconds { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public bool Cached { get; private init; }

    private Dictionary<string, CatalogMesh> byPath = new(StringComparer.OrdinalIgnoreCase);

    public CatalogMesh? Find(string path) => byPath.GetValueOrDefault(path.Trim());

    /// <summary>The index, scanned again only when a file the game loads was added, removed or changed since the last scan.</summary>
    public static CatalogIndex Load(MEGame game)
    {
        if (!game.IsGame1()) throw new ArgumentException("The prop catalog handles Game 1 (LE1).");
        LecHost.EnsureInitialized();
        var files = MELoadedFiles.GetFilesLoadedInGame(game)
            .Where(kv => kv.Key.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var signature = Signature(files.Select(kv => kv.Value));
        lock (Gate)
        {
            if (last is { } l && l.Game == game && l.Signature == signature) return l.Index.With(cached: true);
        }
        var index = Scan(files);
        lock (Gate) last = (game, signature, index);
        return index;
    }

    /// <summary>The stats, and the meshes whose path contains the search text (curated ones first, then the most placed).</summary>
    public static CatalogIndexResult Query(MEGame game, string? search, string? category, bool curatedOnly, int limit)
    {
        var index = Load(game);
        if (category is not null && !CatalogRules.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown category '{category}'. Use: {string.Join(", ", CatalogRules.Categories)}.");
        }
        IEnumerable<CatalogMesh> found = index.Meshes;
        var filtered = !string.IsNullOrWhiteSpace(search) || category is not null || curatedOnly;
        if (!string.IsNullOrWhiteSpace(search)) found = found.Where(m => m.Path.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        if (category is not null) found = found.Where(m => string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase));
        if (curatedOnly) found = found.Where(m => m.Curated);
        var matches = filtered ? found.OrderByDescending(m => m.Curated).ThenByDescending(m => m.Placed).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList() : [];

        var curated = index.Meshes.Where(m => m.Curated).ToList();
        return new CatalogIndexResult(index.FilesScanned, index.FilesWithMeshes, index.FilesSkipped, index.MeshExports, index.Meshes.Count, curated.Count,
            CatalogRules.Curations.ToDictionary(c => c, c => index.Meshes.Count(m => m.Curation == c)),
            CatalogRules.Categories.ToDictionary(c => c, c => curated.Count(m => m.Category == c)),
            index.ScanSeconds, index.Cached, index.Errors.Count, index.Errors.Take(10).ToList(),
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(), matches.Count, matches.Take(Math.Max(0, limit)).ToList());
    }

    private sealed record Row(string File, string FilePath, long FileBytes, string Path, bool Forced, float[] Dims, int Tris);

    private static CatalogIndex Scan(List<KeyValuePair<string, string>> loaded)
    {
        var sw = Stopwatch.StartNew();
        var skipped = loaded.Where(kv => IsOwn(kv.Value)).Select(kv => kv.Key).ToList();
        var files = loaded.Where(kv => !IsOwn(kv.Value)).ToList();
        var rows = new ConcurrentBag<Row>();
        var placed = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentBag<string>();
        var withMeshes = 0;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, kv =>
        {
            try
            {
                var bytes = new FileInfo(kv.Value).Length;
                var name = Path.GetFileNameWithoutExtension(kv.Key);
                using var pcc = MEPackageHandler.UnsafePartialLoad(kv.Value, e => e.ClassName is "StaticMesh" or "StaticMeshComponent");
                var meshes = 0;
                foreach (var export in pcc.Exports)
                {
                    if (export.ClassName == "StaticMeshComponent")
                    {
                        if (!export.InstancedFullPath.StartsWith("TheWorld.", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            if (export.GetProperty<ObjectProperty>("StaticMesh") is { Value: not 0 } mesh && pcc.GetEntry(mesh.Value) is { } target)
                            {
                                placed.AddOrUpdate(target.InstancedFullPath, 1, (_, n) => n + 1);
                            }
                        }
                        catch (Exception e)
                        {
                            errors.Add($"{kv.Key} #{export.UIndex} {export.ClassName}: {e.GetType().Name}: {e.Message}");
                        }
                        continue;
                    }
                    if (export.ClassName != "StaticMesh") continue;
                    meshes++;
                    try
                    {
                        rows.Add(new Row(name, kv.Value, bytes, export.InstancedFullPath, export.IsForcedExport, Dims(export), Tris(export)));
                    }
                    catch (Exception e)
                    {
                        errors.Add($"{kv.Key} #{export.UIndex} {export.InstancedFullPath}: {e.GetType().Name}: {e.Message}");
                    }
                }
                if (meshes > 0) Interlocked.Increment(ref withMeshes);
            }
            catch (Exception e)
            {
                errors.Add($"{kv.Key}: {e.GetType().Name}: {e.Message}");
            }
        });

        var meshes = new List<CatalogMesh>();
        foreach (var group in rows.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            // The smallest file opens fastest; copies of a mesh in the game's files are the same (checked on 1,579 meshes).
            var source = group.MinBy(r => (r.FileBytes, r.File.ToLowerInvariant()))!;
            var count = placed.GetValueOrDefault(source.Path);
            var (curation, category) = CatalogRules.Classify(source.Path, source.Dims, count);
            meshes.Add(new CatalogMesh(source.Path, source.Path.Split('.')[^1], source.File, source.FilePath, source.FileBytes,
                group.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).Count(), source.Dims.Select(d => (int)MathF.Round(d)).ToArray(),
                source.Tris, count, source.Forced, curation, category, CatalogRules.IsCurated(curation), source.Dims.Max() > CatalogRules.LargeSize));
        }
        meshes.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));

        return new CatalogIndex
        {
            Meshes = meshes,
            byPath = meshes.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase),
            FilesScanned = files.Count,
            FilesWithMeshes = withMeshes,
            FilesSkipped = skipped,
            MeshExports = rows.Count,
            ScanSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            Errors = errors.Order(StringComparer.Ordinal).ToList(),
        };
    }

    private CatalogIndex With(bool cached) => new()
    {
        Meshes = Meshes, byPath = byPath, FilesScanned = FilesScanned, FilesWithMeshes = FilesWithMeshes, FilesSkipped = FilesSkipped,
        MeshExports = MeshExports, ScanSeconds = ScanSeconds, Errors = Errors, Cached = cached,
    };

    private static bool IsOwn(string path) => OwnDlc.Any(d => path.Contains($"\\{d}\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The bounds box's full size from the 28 bytes after the properties (BoxSphereBounds: origin, extent, radius), the first
    /// field of the binary: the same values a full parse gives, without one.
    /// </summary>
    private static float[] Dims(ExportEntry mesh)
    {
        var bounds = MemoryMarshal.Cast<byte, float>(mesh.DataReadOnly.Slice(mesh.propsEnd(), 28));
        return [2 * bounds[3], 2 * bounds[4], 2 * bounds[5]];
    }

    private static int Tris(ExportEntry mesh) => (int)ObjectBinary.From<StaticMesh>(mesh).LODModels[0].Elements.Sum(e => e.NumTriangles);

    /// <summary>Path, size and write time of every file, hashed: a different value means an install or a build changed the game's files.</summary>
    private static long Signature(IEnumerable<string> paths)
    {
        var hash = new HashCode();
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            hash.Add(path, StringComparer.OrdinalIgnoreCase);
            hash.Add(info.Exists ? info.Length : -1);
            hash.Add(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        }
        return hash.ToHashCode();
    }
}
