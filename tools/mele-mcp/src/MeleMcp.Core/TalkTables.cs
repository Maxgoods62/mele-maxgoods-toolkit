using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME2ME3;

namespace MeleMcp.Core;

/// <param name="StringCount">Strings with text; entries the game cooked out (negative ids, no text) are not counted.</param>
/// <param name="FemaleCount">Entries of the female table: lines that differ for a female Shepard (a few in INT, thousands in JPN).</param>
/// <param name="Matches">Strings the filters kept, before limit.</param>
public sealed record TlkTableStrings(string Path, string Game, int StringCount, int FemaleCount, int Matches, IReadOnlyList<TlkString> Strings);

public sealed record TlkTableChange(int Id, string? OldText, string NewText, string? OldFemale, string? NewFemale);

/// <param name="BackupPath">Null when the file was created or nothing changed.</param>
public sealed record TlkTableEdit(string Path, bool Created, int StringCount, IReadOnlyList<TlkTableChange> Changes, IReadOnlyList<string> Notes, string? BackupPath);

/// <summary>
/// Game 2 and 3 strings: loose .tlk files, one per module and language. <c>BIOGame_&lt;LANG&gt;.tlk</c> is the base game; a DLC folder's module is
/// named in its config. LE2: <c>BIOEngine.ini</c> [Engine.DLCModules] &lt;folder&gt;=N gives <c>DLC_N_&lt;LANG&gt;.tlk</c> (all 29 vanilla folders; Mount.dlc's
/// TLKID is the strref of the DLC's name, not this number). LE3: the folder's own name, plus <c>DLC_Shared</c> where its Coalesced lists
/// [AlternateNonPackageResources] TalkTableModules (in all 23 installed folders exactly the ones holding a DLC_Shared .tlk). _Test_ and _NonShip_ files are no module.
/// </summary>
public static class TalkTables
{
    private const string EngineIni = "BIOEngine.ini";
    private const string SharedModule = "DLC_Shared";

    // Every vanilla .tlk, DLC ones included, holds these: 134217729 is the module, the other two come from the language. M3's Starter Kit
    // writes the same three strings at its own ids after the DLC's name, so the mods it made have none of these.
    private const int ModuleNameId = 134217729;
    private const int LocaleId = 134217730;
    private const int GenderId = 134217731;

    private static readonly string[] BaseModules = ["BIOGame", "BioGame_Update"];

    private static readonly string[] Languages = ["INT", "DEU", "ESN", "FRA", "ITA", "JPN", "POL", "RUS"];

    internal static IReadOnlyList<string> AllLanguages => Languages;

    /// <summary>The string id holding a file's module name, the one header string that differs between modules.</summary>
    internal static int ModuleStringId => ModuleNameId;

    /// <summary>The file suffix of a language: LE1's short names (the tools' default FR among them) work too.</summary>
    internal static string Language(string language)
    {
        var lang = language.Trim().ToUpperInvariant() switch
        {
            "FR" => "FRA",
            "DE" => "DEU",
            "ES" => "ESN",
            "IT" => "ITA",
            "JA" => "JPN",
            "PL" or "PLPC" => "POL",
            "RU" or "RA" => "RUS",
            var other => other,
        };
        return Languages.Contains(lang) ? lang : throw new ArgumentException($"Unknown language '{language}'. Use {string.Join(", ", Languages)}.");
    }

    /// <summary>
    /// Text of every string id for a language: base game, then each enabled DLC by ascending mount, so the highest mount wins an id
    /// they share. Male is the main table, as in LEC; Female holds only the ids whose female text differs from it. A negative id with
    /// no text marks a line cooked out of the base game (its positive id with the top bit set): it reads as an empty line.
    /// </summary>
    internal static (Dictionary<int, string> Male, Dictionary<int, string> Female) Load(MEGame game, string language)
    {
        LecHost.EnsureInitialized();
        var lang = Language(language);
        var cooked = MEDirectories.GetCookedPath(game);
        var first = Path.Combine(cooked, $"{BaseModules[0]}_{lang}.tlk");
        if (!File.Exists(first)) throw new FileNotFoundException($"{game} has no {first}.");

        var male = new Dictionary<int, string>();
        var female = new Dictionary<int, string>();
        foreach (var module in BaseModules) Read(Path.Combine(cooked, $"{module}_{lang}.tlk"), male, female);
        foreach (var dlc in EnabledDlc(game))
        {
            foreach (var module in Modules(game, dlc)) Read(Path.Combine(dlc, game.CookedDirName(), $"{module}_{lang}.tlk"), male, female);
        }
        return (male, female);
    }

    private static void Read(string path, Dictionary<int, string> male, Dictionary<int, string> female)
    {
        if (!File.Exists(path)) return;
        var tlk = new ME2ME3TalkFile(path);
        foreach (var s in tlk.StringRefs.Take(tlk.MaleEntryCount))
        {
            if (s.Data is null)
            {
                male.TryAdd(s.StringID & int.MaxValue, "");
                continue;
            }
            male[s.StringID] = s.Data;
            female.Remove(s.StringID);
        }
        foreach (var s in tlk.StringRefs.Skip(tlk.MaleEntryCount))
        {
            if (s.Data is not null && male.GetValueOrDefault(s.StringID) != s.Data) female[s.StringID] = s.Data;
        }
    }

    private static IEnumerable<string> EnabledDlc(MEGame game) => MELoadedDLC.GetEnabledDLCFolders(game).OrderBy(d => MELoadedDLC.GetMountPriority(d, game));

    /// <summary>The talk table modules of one DLC folder, in the order they are read.</summary>
    private static List<string> Modules(MEGame game, string dlc)
    {
        var folder = Path.GetFileName(dlc);
        var cooked = Path.Combine(dlc, game.CookedDirName());
        if (game.IsGame3())
        {
            return File.Exists(Path.Combine(cooked, $"{SharedModule}_{Languages[0]}.tlk")) ? [SharedModule, folder] : [folder];
        }

        var ini = Path.Combine(cooked, EngineIni);
        var number = File.Exists(ini) ? DuplicatingIni.LoadIni(ini).GetValue("Engine.DLCModules", folder)?.Value : null;
        return string.IsNullOrWhiteSpace(number) ? [] : [$"DLC_{number.Trim()}"];
    }

    public static TlkTableStrings Get(MEGame game, string fileOrPath, int? id, string? textContains, int limit)
    {
        LecHost.EnsureInitialized();
        var path = Resolve(game, fileOrPath);
        var tlk = new ME2ME3TalkFile(path);
        var female = new Dictionary<int, string>();
        foreach (var s in tlk.StringRefs.Skip(tlk.MaleEntryCount))
        {
            if (s.Data is not null) female[s.StringID] = s.Data;
        }

        var strings = tlk.StringRefs.Take(tlk.MaleEntryCount)
            .Where(s => s.Data is not null)
            .Select(s => new TlkString(s.StringID, s.Data, female.GetValueOrDefault(s.StringID)))
            .ToList();
        var matches = strings
            .Where(s => (id is null || s.Id == id)
                        && (textContains is null || s.Text.Contains(textContains, StringComparison.OrdinalIgnoreCase)
                                                 || (s.Female?.Contains(textContains, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
        return new TlkTableStrings(path, game.ToString(), strings.Count, tlk.FemaleEntryCount, matches.Count, matches.Take(limit).ToList());
    }

    /// <summary>
    /// Adds or replaces strings in one .tlk file, creating it if missing. Without Female, an id in both tables gets the new text in
    /// both, as LEC's ReplaceString does; with it, the second entry of an id is the female one, as in LEC's AddString. Leaves the file
    /// as it is when every string already has its text (re-encoding builds a new Huffman tree).
    /// </summary>
    public static TlkTableEdit Set(MEGame game, string path, IReadOnlyList<TlkString> strings)
    {
        LecHost.EnsureInitialized();
        if (strings.Count == 0) throw new ArgumentException("No strings given.");
        if (strings.FirstOrDefault(s => s.Id <= 0) is { } bad) throw new ArgumentException($"String id {bad.Id}: ids are positive.");

        var windows = GamePaths.ToWindowsPath(path);
        if (!Path.IsPathFullyQualified(windows)) throw new ArgumentException($"'{path}': tlk_set takes the full path of a mod's .tlk file.");
        if (!windows.EndsWith(".tlk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"'{path}' is not a .tlk file.");
        var full = Path.GetFullPath(windows);
        PackageWriter.EnsureWritable(game, full);

        var created = !File.Exists(full);
        var refs = created ? NewFile(game, full) : new ME2ME3TalkFile(full).StringRefs;
        var changed = false;
        var changes = new List<TlkTableChange>();
        foreach (var s in strings)
        {
            var same = refs.Where(r => r.StringID == s.Id).ToList();
            var old = same.FirstOrDefault()?.Data;
            var oldFemale = same.Count > 1 ? same[^1].Data : null;
            var female = s.Female ?? (same.Count > 1 ? s.Text : null);
            changed |= same.Count == 0 || old != s.Text || oldFemale != female;

            if (same.Count == 0) refs.Add(new TLKStringRef(s.Id, s.Text, 0));
            else same[0].Data = s.Text;
            if (same.Count > 1) same[^1].Data = female;
            else if (female is not null) refs.Add(new TLKStringRef(s.Id, female, 0));
            changes.Add(new TlkTableChange(s.Id, old, s.Text, oldFemale, female));
        }

        var count = refs.Where(r => r.Data is not null).Select(r => r.StringID).Distinct().Count();
        if (!created && !changed) return new TlkTableEdit(full, false, count, changes, NameNotes(game, full), null);

        var backup = created ? null : PackageWriter.Backup(full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        AtomicFile.Write(full, temporary => HuffmanCompression.SaveToTlkFile(temporary, refs));
        return new TlkTableEdit(full, created, count, changes, NameNotes(game, full), backup);
    }

    /// <summary>The header strings of a new file, in the file's language; the name must end in one, as the game looks it up by that suffix.</summary>
    private static List<TLKStringRef> NewFile(MEGame game, string full)
    {
        var (module, lang) = SplitName(full);
        if (lang is null) throw new ArgumentException($"A new .tlk is named <module>_<LANG>.tlk, with LANG one of {string.Join(", ", Languages)}: {Path.GetFileName(full)}.");

        var header = new ME2ME3TalkFile(Path.Combine(MEDirectories.GetCookedPath(game), $"{BaseModules[0]}_{lang}.tlk")).StringRefs;
        var refs = new List<TLKStringRef> { new(ModuleNameId, module, 0) };
        refs.AddRange(header.Where(r => r.StringID is LocaleId or GenderId).Select(r => new TLKStringRef(r.StringID, r.Data, 0)));
        return refs;
    }

    private static (string Module, string? Language) SplitName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var cut = stem.LastIndexOf('_');
        return cut > 0 && Languages.Contains(stem[(cut + 1)..].ToUpperInvariant()) ? (stem[..cut], stem[(cut + 1)..].ToUpperInvariant()) : (stem, null);
    }

    /// <summary>A file in a DLC folder that is not one of the folder's modules is never loaded: say so instead of leaving it silent.</summary>
    private static IReadOnlyList<string> NameNotes(MEGame game, string full)
    {
        var cooked = Path.GetDirectoryName(full)!;
        var dlc = Path.GetDirectoryName(cooked);
        if (dlc is null || !Path.GetFileName(dlc).StartsWith("DLC_", StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(cooked).Equals(game.CookedDirName(), StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var modules = Modules(game, dlc);
        if (modules.Count == 0) return [$"{Path.Combine(game.CookedDirName(), EngineIni)} of {Path.GetFileName(dlc)} has no [Engine.DLCModules] {Path.GetFileName(dlc)}=<n> line: the game finds no talk table for this folder."];
        var (module, _) = SplitName(full);
        return modules.Contains(module, StringComparer.OrdinalIgnoreCase)
            ? []
            : [$"{Path.GetFileName(dlc)} loads {string.Join(" and ", modules.Select(m => $"{m}_<LANG>.tlk"))}, so the game does not load {Path.GetFileName(full)}."];
    }

    /// <summary>An existing path as it is; a file name in the highest mounted folder holding it, enabled DLC only (.tlk is not in LEC's file list).</summary>
    private static string Resolve(MEGame game, string fileOrPath)
    {
        var candidate = GamePaths.ToWindowsPath(fileOrPath);
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        var name = Path.GetFileName(candidate);
        if (!Path.HasExtension(name)) name += ".tlk";
        if (!name.EndsWith(".tlk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{game} strings live in .tlk files, not in {name}.");

        var folders = EnabledDlc(game).Reverse().Select(d => Path.Combine(d, game.CookedDirName())).Append(MEDirectories.GetCookedPath(game));
        return folders.Select(f => Path.Combine(f, name)).FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException($"'{fileOrPath}' is not an existing path and no file named '{name}' is in {game}'s base game or enabled DLC.");
    }
}
