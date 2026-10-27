using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

/// <summary>One copy of a file: the folder it is in (BioGame, Engine or a DLC folder), its mount, and why the game skips it.</summary>
public sealed record FileCopy(string Source, int? Mount, string Path, bool Loaded, string? Why);

public sealed record FoundFile(
    string File,
    string? Path,
    string? WslPath,
    string? Source,
    int? Mount,
    IReadOnlyList<FileCopy> Copies,
    string? TocPath,
    string? LecPath,
    IReadOnlyList<string> Notes);

public sealed record FindFileResult(string Game, string Rules, int TotalMatches, IReadOnlyList<FoundFile> Files, IReadOnlyList<string> FolderWarnings);

/// <summary>
/// Which copy of a file the game loads. LE1 resolves package names through BioGame\PCConsoleTOC.bin, one path per file name,
/// which the AutoTOC ASI rewrites at every boot; this emulates the installed AutoTOC (v2 from AutoTOC-asi, or v3 from LExASIs)
/// and reads the TOC on disk. LE2 and LE3 follow LEC's MELoadedFiles. Sources: AutoTOC-asi dllmain.cpp at bc8cb1d, LExASIs
/// AutoTOC/AEntry.cpp, Common/Mount.cpp and Autoload/DLCPackage.cpp at aaad03a.
/// </summary>
public static partial class FileFinder
{
    private const string BaseGame = "BioGame";
    private const string Engine = "Engine";

    /// <summary>AutoTOC skips directories with these names (case-sensitive) at any depth.</summary>
    private static readonly HashSet<string> SkippedDirs = new(["DLC", "Patches", "Splash", "Config"], StringComparer.Ordinal);

    /// <summary>The extensions AutoTOC puts in the TOC, compared case-sensitively.</summary>
    private static readonly HashSet<string> TocExtensionsV2 =
        new([".pcc", ".afc", ".bik", ".bin", ".tlk", ".txt", ".cnd", ".upk", ".tfc", ".isb", ".usf", ".ini", ".dlc"], StringComparer.Ordinal);
    private static readonly HashSet<string> TocExtensionsV3 =
        new([".pcc", ".bik", ".bin", ".txt", ".upk", ".tfc", ".usf", ".ini", ".isb"], StringComparer.Ordinal);

    /// <summary>A folder with files: BioGame, Engine or a DLC folder, and whether its files go in the TOC (and if not, why).</summary>
    private sealed record Folder(string Name, string Dir, int Rank, int? Mount, bool Mounted, string? Why);

    private sealed record Copy(Folder Folder, string Path, int Order);

    /// <summary>The installed AutoTOC: 2 (AutoTOC-asi, no version resource), 3 (LExASIs) or 0 (none found).</summary>
    private sealed record AutoToc(int Version, string? File)
    {
        public HashSet<string> Extensions => Version == 2 ? TocExtensionsV2 : TocExtensionsV3;
    }

    /// <summary>
    /// LExASIs' mount line (Autoload v13, AutoTOC v3): sscanf(line, " ModMount = %d") on every line, section ignored. Whitespace
    /// is C's isspace, digits ASCII.
    /// </summary>
    [GeneratedRegex(@"^[ \t\n\v\f\r]*ModMount[ \t\n\v\f\r]*=[ \t\n\v\f\r]*([+-]?[0-9]+)")]
    private static partial Regex AsiModMount();

    /// <summary>std::stoi: optional whitespace and sign, then digits; the rest is ignored.</summary>
    [GeneratedRegex(@"^[ \t\n\v\f\r]*[+-]?[0-9]+")]
    private static partial Regex LeadingInt();

    public static FindFileResult Find(MEGame game, string name, int limit)
    {
        LecHost.EnsureInitialized();
        var pattern = Pattern(name);
        var warnings = new List<string>();
        var autoToc = game == MEGame.LE1 ? DetectAutoToc(game, warnings) : null;
        // With no AutoTOC ASI nothing applies its folder rules at boot (the TOC on disk is the answer), so their warnings would mislead.
        var folders = Folders(game, autoToc, autoToc is { File: null } ? [] : warnings);

        var copies = new Dictionary<string, List<Copy>>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var folder in folders)
        {
            foreach (var path in autoToc is null ? CookedFiles(game, folder.Dir) : TocTree(folder.Dir))
            {
                var file = Path.GetFileName(path);
                if (!FileSystemName.MatchesSimpleExpression(pattern, file, ignoreCase: true)) continue;
                if (!copies.TryGetValue(file, out var list)) copies[file] = list = [];
                list.Add(new Copy(folder, path, order++));
            }
        }

        var gameRoot = MEDirectories.GetDefaultGamePath(game);
        var toc = autoToc is null ? null : ReadToc(game);
        var lec = LecFiles(game, warnings);
        var files = copies
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(kv => Describe(kv.Key, kv.Value, game, autoToc, gameRoot, toc, lec))
            .ToList();
        var rules = autoToc switch
        {
            null => "LEC: BioGame and DLC_ folders with a CookedPCConsole\\Mount.dlc, highest mount wins",
            { File: null } => $"No AutoTOC ASI in {MEDirectories.GetASIPath(game)}: the game loads the copy BioGame\\PCConsoleTOC.bin lists, "
                              + "which changes only when M3 runs AutoTOC",
            { Version: 2 } => $"{autoToc.File} (AutoTOC v2): DLC_ folders with an AutoLoad.ini [ME1DLCMOUNT] ModMount above 0, highest mount wins",
            _ => $"{autoToc.File} (AutoTOC v3): DLC_MOD_ folders with an AutoLoad.ini ModMount above 0, highest mount wins",
        };
        return new FindFileResult(game.ToString(), rules, copies.Count, files, warnings);
    }

    /// <summary>A bare name without extension means a package (BIOA_NOR10 → BIOA_NOR10.pcc); a path keeps only its file name.</summary>
    private static string Pattern(string name)
    {
        var file = Path.GetFileName(GamePaths.ToWindowsPath(name.Trim()).Replace('/', '\\'));
        if (file.Length == 0) throw new ArgumentException("Give a file name, e.g. BIOA_NOR10.pcc, or a wildcard such as BIOA_NOR10*.");
        return file.IndexOfAny(['*', '?']) < 0 && !Path.HasExtension(file) ? file + ".pcc" : file;
    }

    private static AutoToc DetectAutoToc(MEGame game, List<string> warnings)
    {
        var asiDir = MEDirectories.GetASIPath(game);
        var found = Directory.Exists(asiDir)
            ? Directory.EnumerateFiles(asiDir, "*.asi").Where(f => Path.GetFileName(f).Contains("AutoTOC", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        if (found.Count == 0) return new AutoToc(3, null);
        if (found.Count > 1) warnings.Add($"Several AutoTOC ASIs ({string.Join(", ", found.Select(Path.GetFileName))}): each rewrites the TOC at boot.");

        // LExASIs builds carry a version resource (3.0.4.x); the 2021 AutoTOC-asi build has none.
        return new AutoToc(FileVersionInfo.GetVersionInfo(found[0]).FileMajorPart >= 3 ? 3 : 2, Path.GetFileName(found[0]));
    }

    private static List<Folder> Folders(MEGame game, AutoToc? autoToc, List<string> warnings)
    {
        var bioGame = MEDirectories.GetBioGamePath(game);
        if (bioGame is null || !Directory.Exists(bioGame))
        {
            throw new DirectoryNotFoundException($"{game} is not installed where LEC looks ({bioGame ?? "no path"}). Check le_status.");
        }

        var folders = new List<Folder> { new(BaseGame, bioGame, 0, null, true, null) };
        var engine = Path.Combine(MEDirectories.GetDefaultGamePath(game), Engine);
        if (autoToc is not null && Directory.Exists(engine)) folders.Add(new(Engine, engine, 1, null, true, null));

        var dlcRoot = MEDirectories.GetDLCPath(game);
        if (!Directory.Exists(dlcRoot)) return folders;

        // Not sorted: on a tie AutoTOC keeps the folder its directory listing returns last, and this is the same listing.
        var dlcs = Directory.EnumerateDirectories(dlcRoot)
            .Select(dir => autoToc is null ? LecFolder(dir, game) : Le1Folder(dir, autoToc, warnings))
            .ToList();
        if (autoToc is not null)
        {
            foreach (var same in dlcs.Where(f => f.Mounted).GroupBy(f => f.Mount).Where(g => g.Count() > 1))
            {
                var kept = same.Last();
                warnings.Add($"{string.Join(", ", same.SkipLast(1).Select(f => f.Name))} and {kept.Name} share mount {same.Key}: AutoTOC keeps "
                             + $"only {kept.Name}, the folder listed last, and none of the others' files. Give each mod its own mount.");
                foreach (var dropped in same.SkipLast(1))
                {
                    dlcs[dlcs.IndexOf(dropped)] = dropped with
                    {
                        Mounted = false,
                        Why = $"folder left out of the TOC: {kept.Name} has the same mount ({same.Key}) and AutoTOC keeps the folder listed last",
                    };
                }
            }
        }
        else
        {
            foreach (var same in dlcs.Where(f => f.Mounted).GroupBy(f => f.Mount).Where(g => g.Count() > 1))
            {
                warnings.Add($"{string.Join(" and ", same.Select(f => f.Name))} share mount {same.Key}: which one wins a file they both have "
                             + "is not defined. Give each mod its own mount.");
            }
        }
        folders.AddRange(dlcs);
        return folders;
    }

    private static Folder Le1Folder(string dir, AutoToc autoToc, List<string> warnings)
    {
        var name = Path.GetFileName(dir);
        var prefix = autoToc.Version == 2 ? "DLC_" : "DLC_MOD_";
        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return new(name, dir, 2, null, false, $"folder not in the TOC: AutoTOC v{autoToc.Version} only reads folders named {prefix}... (case-sensitive)");
        }
        var ini = Path.Combine(dir, "AutoLoad.ini");
        if (!File.Exists(ini)) return new(name, dir, 2, null, false, "folder not in the TOC: it has no AutoLoad.ini");

        var (mount, problem) = autoToc.Version == 2 ? SectionMount(ini) : LineMount(ini);
        if (mount is not > 0)
        {
            warnings.Add($"{name}: AutoLoad.ini {problem}. AutoTOC needs a mount above 0 and closes the game at boot (\"Broken Mod Warning\").");
            return new(name, dir, 2, mount, false, $"the game does not start: AutoLoad.ini {problem}");
        }
        if (!name.StartsWith("DLC_MOD_", StringComparison.Ordinal))
        {
            warnings.Add($"{name}: AutoTOC v2 puts its files in the TOC, but the Autoload ASI only reads DLC_MOD_ folders: its AutoLoad.ini "
                         + "(global packages, TLKs, plot maps, TFCs) is ignored.");
        }
        return new(name, dir, 2, mount, true, null);
    }

    /// <summary>
    /// AutoTOC v2: GetPrivateProfileStringA("ME1DLCMOUNT", "ModMount") then std::stoi, which gives 0 when it throws. Windows reads
    /// only the first [ME1DLCMOUNT] section, decodes UTF-16 only with its FF FE mark, and sees a UTF-8 BOM as part of line 1 (so a
    /// section header there is not one).
    /// </summary>
    private static (int? Mount, string Problem) SectionMount(string ini)
    {
        var bytes = File.ReadAllBytes(ini);
        var text = bytes is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2) : Encoding.Latin1.GetString(bytes);
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (inSection) break;
                var end = line.IndexOf(']');
                inSection = end > 0 && line[1..end].Trim().Equals("ME1DLCMOUNT", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (!inSection || eq < 0 || !line[..eq].Trim().Equals("ModMount", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
            if (value.Length == 0) return (null, "has an empty ModMount");
            var m = LeadingInt().Match(value);
            var mount = m.Success && int.TryParse(m.Value, out var v) ? v : 0;
            return (mount, $"has ModMount = {value}");
        }
        return (null, "has no ModMount in its first [ME1DLCMOUNT] section"
                      + (bytes is [0xEF, 0xBB, 0xBF, (byte)'[', ..] ? " (Windows does not see the header on line 1: the file starts with a UTF-8 BOM)" : ""));
    }

    /// <summary>
    /// AutoTOC v3 (Common::TryReadMountPriority): the raw bytes split on \n, the first line matching " ModMount = %d" with a value of
    /// 0 or more. A UTF-16 file, or a BOM on that line, never matches.
    /// </summary>
    private static (int? Mount, string Problem) LineMount(string ini)
    {
        foreach (var line in Encoding.Latin1.GetString(File.ReadAllBytes(ini)).Split('\n'))
        {
            var m = AsiModMount().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var mount) && mount >= 0) return (mount, $"has ModMount = {mount}");
        }
        return (null, "has no ModMount line with a value of 0 or more (as plain ANSI text)");
    }

    private static Folder LecFolder(string dir, MEGame game)
    {
        var name = Path.GetFileName(dir);
        return MELoadedDLC.IsEnabledDLC(dir, game)
            ? new(name, dir, 2, MELoadedDLC.GetMountPriority(dir, game), true, null)
            : new(name, dir, 2, null, false, "folder not mounted: no CookedPCConsole\\Mount.dlc, or not named DLC_");
    }

    /// <summary>Every file AutoTOC walks under a folder, in its order: the directory listing, recursing into each subfolder in place.</summary>
    private static IEnumerable<string> TocTree(string dir)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry is not DirectoryInfo sub)
            {
                yield return entry.FullName;
            }
            else if (!SkippedDirs.Contains(sub.Name))
            {
                foreach (var file in TocTree(sub.FullName)) yield return file;
            }
        }
    }

    /// <summary>LEC's view (LE2, LE3): every file under the folder's CookedPCConsole.</summary>
    private static IEnumerable<string> CookedFiles(MEGame game, string dir)
    {
        var cooked = Path.Combine(dir, game.CookedDirName());
        return Directory.Exists(cooked) ? Directory.EnumerateFiles(cooked, "*", SearchOption.AllDirectories) : [];
    }

    /// <summary>
    /// LEC's cached name → path list, which the other tools resolve file names with. It throws when a DLC folder it counts as
    /// mounted has no CookedPCConsole (e.g. an AutoLoad.ini-only mod), and so does every tool that takes a file name.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? LecFiles(MEGame game, List<string> warnings)
    {
        try
        {
            return MELoadedFiles.GetFilesLoadedInGame(game);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"LEC's file list fails ({ex.GetType().Name}: {ex.Message}): the other mele tools fail on every file name until this is fixed.");
            return null;
        }
    }

    private static FoundFile Describe(string file, List<Copy> found, MEGame game, AutoToc? autoToc, string? gameRoot,
        ILookup<string, string>? toc, IReadOnlyDictionary<string, string>? lec)
    {
        var notes = new List<string>();

        // Walk order: BioGame, Engine, then DLC folders by ascending mount; each later copy replaces the earlier one.
        var walk = found
            .OrderBy(c => c.Folder.Rank)
            .ThenBy(c => c.Folder.Mount ?? 0)
            .ThenBy(c => c.Order)
            .ToList();
        var listed = toc is not null && gameRoot is not null
            ? toc[file].Select(rel => Path.GetFullPath(Path.Combine(gameRoot, rel))).ToList()
            : [];
        Copy? winner;
        if (autoToc is { File: null })
        {
            winner = walk.LastOrDefault(c => listed.Contains(c.Path, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            var candidates = walk.Where(c => c.Folder.Mounted && Listable(game, autoToc, c.Path)).ToList();
            winner = candidates.Count > 0 ? candidates[^1] : null;
            if (winner is not null && candidates.Count(c => c.Folder == winner.Folder) > 1)
            {
                notes.Add($"{winner.Folder.Name} has this file more than once: the copy found last wins.");
            }
        }

        var copies = walk
            .AsEnumerable()
            .Reverse()
            .Select(c => new FileCopy(c.Folder.Name, c.Folder.Mount, c.Path, c == winner, Why(game, c, winner, autoToc)))
            .OrderByDescending(c => c.Loaded)
            .ThenBy(c => c.Why?.StartsWith("overridden", StringComparison.Ordinal) == true ? 0 : 1)
            .ToList();

        string? tocPath = null;
        if (autoToc is { File: null })
        {
            if (listed.Count == 0)
            {
                notes.Add("Not in PCConsoleTOC.bin: with no AutoTOC ASI, the game cannot load it until M3 runs AutoTOC.");
            }
            else if (winner is null)
            {
                tocPath = listed[0];
                notes.Add($"PCConsoleTOC.bin lists {tocPath}, which is not on disk.");
            }
        }
        else if (toc is not null)
        {
            if (winner is not null && listed.Count == 0)
            {
                notes.Add("Not in PCConsoleTOC.bin yet: AutoTOC adds it at the next boot.");
            }
            else if (listed.Count > 0 && (winner is null || !listed.Contains(winner.Path, StringComparer.OrdinalIgnoreCase)))
            {
                tocPath = listed[0];
                notes.Add($"PCConsoleTOC.bin, written at the last boot, lists {tocPath}: AutoTOC rewrites it at the next boot.");
            }
        }

        string? lecPath = null;
        if (file.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase) && lec is not null
            && lec.TryGetValue(file, out var other) && !string.Equals(other, winner?.Path, StringComparison.OrdinalIgnoreCase))
        {
            lecPath = other;
            notes.Add($"The other mele tools open {other} for this name (LEC's cached file list; le_refresh_files rescans it).");
        }
        if (winner is null && autoToc is not { File: null }) notes.Add("No copy loads: none is in a folder or with an extension the game reads.");

        return new FoundFile(file, winner?.Path, winner is null ? null : GamePaths.ToWslPath(winner.Path), winner?.Folder.Name,
            winner?.Folder.Mount, copies, tocPath, lecPath, notes);
    }

    /// <summary>Whether the game can load this copy at all: in the TOC's extension list (LE1), not LE3's unused Startup copy.</summary>
    private static bool Listable(MEGame game, AutoToc? autoToc, string path) =>
        autoToc is null ? !IsLe3FauxStartup(game, path) : autoToc.Extensions.Contains(Path.GetExtension(path));

    private static string? Why(MEGame game, Copy copy, Copy? winner, AutoToc? autoToc)
    {
        if (copy == winner) return null;
        if (autoToc is { File: null }) return "not the copy PCConsoleTOC.bin lists (with no AutoTOC ASI it changes only when M3 runs AutoTOC)";
        if (!copy.Folder.Mounted) return copy.Folder.Why;
        if (autoToc is not null && !autoToc.Extensions.Contains(Path.GetExtension(copy.Path)))
        {
            return $"not in the TOC: AutoTOC v{autoToc.Version} lists only {string.Join(" ", autoToc.Extensions)} (case-sensitive)";
        }
        if (IsLe3FauxStartup(game, copy.Path)) return "never loaded: a leftover of LE3's cooking (LEC skips it too)";
        if (winner is null) return null;
        if (winner.Folder == copy.Folder) return "found earlier in the same folder: the copy found last wins";
        return $"overridden by {winner.Folder.Name}{(winner.Folder.Mount is { } m ? $" (mount {m})" : "")}";
    }

    private static bool IsLe3FauxStartup(MEGame game, string path) =>
        game == MEGame.LE3 && path.EndsWith(Path.Combine("DLC_METR_Patch01", "CookedPCConsole", "Startup.pcc"), StringComparison.OrdinalIgnoreCase);

    /// <summary>LE1's only TOC, BioGame\PCConsoleTOC.bin: file name → path relative to the game folder, as AutoTOC last wrote it.</summary>
    private static ILookup<string, string>? ReadToc(MEGame game)
    {
        var path = Path.Combine(MEDirectories.GetBioGamePath(game), "PCConsoleTOC.bin");
        if (!File.Exists(path)) return null;
        return new TOCBinFile(path).GetAllEntries().ToLookup(e => Path.GetFileName(e.name), e => e.name, StringComparer.OrdinalIgnoreCase);
    }
}
