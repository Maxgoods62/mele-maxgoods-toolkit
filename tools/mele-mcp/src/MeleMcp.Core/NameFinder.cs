using System.Collections.Concurrent;
using System.IO.Enumeration;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

public sealed record NameMatch(string File, string Path, IReadOnlyList<string> Names);

public sealed record NameSearchResult(int FilesScanned, IReadOnlyList<NameMatch> Matches);

public static class NameFinder
{
    /// <summary>
    /// Finds the loaded packages whose name table contains a name with <paramref name="nameContains"/> in it. Any tag,
    /// object name, property name or Kismet variable name used in a package is in its name table, so this answers
    /// "which files mention X" without loading export data.
    /// </summary>
    public static NameSearchResult Find(MEGame game, string nameContains, string fileFilter, int limit)
    {
        LecHost.EnsureInitialized();
        var files = GamePaths.LoadedFiles(game)
            .Where(kv => kv.Key.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase))
            .Where(kv => FileSystemName.MatchesSimpleExpression(fileFilter, kv.Key, ignoreCase: true))
            .ToList();

        var matches = new ConcurrentBag<NameMatch>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, kv =>
        {
            using var pcc = MEPackageHandler.UnsafePartialLoad(kv.Value, _ => false);
            var names = pcc.Names.Where(n => n.Contains(nameContains, StringComparison.OrdinalIgnoreCase)).ToList();
            if (names.Count > 0) matches.Add(new NameMatch(kv.Key, kv.Value, names));
        });

        return new NameSearchResult(files.Count, matches.OrderBy(m => m.File, StringComparer.OrdinalIgnoreCase).Take(limit).ToList());
    }
}
