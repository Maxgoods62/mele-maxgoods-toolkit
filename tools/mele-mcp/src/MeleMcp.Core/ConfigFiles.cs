using System.IO.Enumeration;
using System.Text;
using LegendaryExplorerCore.Coalesced;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LecConfig = LegendaryExplorerCore.Coalesced.Config.ConfigTools;
using LecMerge = LegendaryExplorerCore.Coalesced.Config.ConfigMerge;

namespace MeleMcp.Core;

/// <summary>One value as a file holds it.</summary>
/// <param name="File">Path under the game's BioGame folder.</param>
/// <param name="Origin">"base", or the DLC folder.</param>
/// <param name="Mount">The DLC's mount priority; null for the base game.</param>
/// <param name="Operation">set, add, addunique, remove or clear.</param>
/// <param name="Native">The form the file holds: the key's prefix (LE1, LE2) or "type N" (LE3).</param>
public sealed record ConfigRow(string File, string Origin, int? Mount, string Operation, string Native, string? Value);

/// <param name="TotalRows">Rows of this key, before the limit and after the file filter.</param>
/// <param name="Rows">Load order: base game, then DLC by rising mount.</param>
/// <param name="Effective">The rows replayed in order (LE1: the base file's rows, all the game loads). Only when more than one file holds the key, all its rows are listed and no file filter is set.</param>
public sealed record ConfigKeyRows(string Asset, string Section, string Key, int TotalRows, IReadOnlyList<ConfigRow> Rows, IReadOnlyList<string>? Effective);

/// <param name="Root">The BioGame folder that the rows' File paths start from.</param>
public sealed record ConfigGetResult(string Game, string Root, int FilesRead, int TotalKeys, int TotalRows, int ReturnedRows,
    IReadOnlyList<ConfigKeyRows> Keys, IReadOnlyList<string> Notes);

/// <param name="Value">Not needed for clear.</param>
/// <param name="Operation">set, add, addunique, remove or clear; default set.</param>
public sealed record ConfigEntry(string Asset, string Section, string Key, string? Value = null, string? Operation = null);

/// <param name="Line">The entry in the file's native form.</param>
/// <param name="File">File name in the mod's CookedPCConsole folder.</param>
/// <param name="Outcome">added, replaced or unchanged.</param>
public sealed record ConfigEntryResult(string Asset, string Section, string Key, string Operation, string Line, string File, string Outcome);

/// <param name="Format">m3cd (LE1), ini (LE2) or bin (LE3).</param>
/// <param name="Written">Files written. Empty when every entry was already there.</param>
public sealed record ConfigSetResult(string Game, string Format, string Folder, IReadOnlyList<ConfigEntryResult> Entries, IReadOnlyList<string> Written,
    IReadOnlyList<string> BackupPaths, IReadOnlyList<string> Notes);

/// <summary>
/// Game config in each game's own layout. LE1: one Coalesced_INT.bin, deltas as ConfigDelta-*.m3cd that M3 merges into it.
/// LE2: Coalesced_&lt;language&gt;.bin, loose inis in each DLC. LE3: Coalesced.bin, a Default_&lt;DLC&gt;.bin in each DLC.
/// Text files are edited line by line, so comments, layout and every other byte stay as they are.
/// </summary>
public static class ConfigFiles
{
    private const string Set = "set", Add = "add", AddUnique = "addunique", Remove = "remove", Clear = "clear";

    private sealed record Row(string Asset, string Section, string Key, string Operation, string Native, string? Value);
    private sealed record Layer(string Path, string Relative, string Origin, int? Mount);

    private sealed class KeyGroup(string asset, string section, string key)
    {
        public string Asset { get; } = asset;
        public string Section { get; } = section;
        public string Key { get; } = key;
        public List<ConfigRow> Rows { get; } = [];
    }

    public static ConfigGetResult Get(MEGame game, string? asset, string? file, string? section, string? key, string? textContains, string? language, int limit)
    {
        LecHost.EnsureInitialized();
        if (limit <= 0) throw new ArgumentException("limit must be positive.");
        var root = MEDirectories.GetBioGamePath(game);
        var notes = new List<string>();
        if (game == MEGame.LE1)
        {
            notes.Add("LE1 loads only Coalesced_INT.bin: M3 merges ConfigDelta files into it at install, so on a modded install the base rows already hold them, and only the base rows count.");
        }
        var layers = Layers(game, root, language, notes);
        if (!string.IsNullOrWhiteSpace(file)) layers = layers.Where(l => l.Relative.Contains(file.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        var groups = new Dictionary<string, KeyGroup>(StringComparer.OrdinalIgnoreCase);
        var order = new List<KeyGroup>();
        foreach (var layer in layers)
        {
            foreach (var row in ReadLayer(game, layer.Path))
            {
                if (!string.IsNullOrWhiteSpace(asset) && !row.Asset.Equals(Stem(asset), StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(section) && !row.Section.Contains(section.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(key) && !FileSystemName.MatchesSimpleExpression(key.Trim(), row.Key, ignoreCase: true)) continue;
                var id = $"{row.Asset}\0{row.Section}\0{row.Key}";
                if (!groups.TryGetValue(id, out var group))
                {
                    groups[id] = group = new KeyGroup(row.Asset, row.Section, row.Key);
                    order.Add(group);
                }
                group.Rows.Add(new ConfigRow(layer.Relative, layer.Origin, layer.Mount, row.Operation, row.Native, row.Value));
            }
        }

        // A key is kept whole: the row with the text is rarely the one that wins.
        if (!string.IsNullOrEmpty(textContains))
        {
            order = order.Where(g => g.Rows.Any(r => r.Value?.Contains(textContains, StringComparison.OrdinalIgnoreCase) == true)).ToList();
        }

        var keys = new List<ConfigKeyRows>();
        var left = limit;
        foreach (var group in order)
        {
            if (left == 0) break;
            var rows = group.Rows.Take(left).ToList();
            left -= rows.Count;
            var replay = string.IsNullOrWhiteSpace(file) && rows.Count == group.Rows.Count && rows.Select(r => r.File).Distinct().Count() > 1;
            // LE1 reads no delta itself, and a merged delta is in the base file already: replaying both would count it twice.
            var effective = !replay ? null : Replay(game == MEGame.LE1 ? rows.Where(r => r.Mount is null) : rows);
            keys.Add(new ConfigKeyRows(group.Asset, group.Section, group.Key, group.Rows.Count, rows, effective));
        }

        return new ConfigGetResult(game.ToString(), root, layers.Count, order.Count, order.Sum(g => g.Rows.Count), limit - left, keys, notes);
    }

    public static ConfigSetResult SetEntries(MEGame game, string folder, IReadOnlyList<ConfigEntry> entries, string? deltaName)
    {
        LecHost.EnsureInitialized();
        if (entries.Count == 0) throw new ArgumentException("Give at least one entry.");
        var (cooked, dlc) = ModFolder(game, folder);
        var clean = entries.Select(e => Checked(game, e)).ToList();
        return game == MEGame.LE3 ? SetBin(game, cooked, dlc, clean) : SetText(game, cooked, dlc, clean, deltaName);
    }

    /// <summary>
    /// Base game, then DLC by rising mount. Unknown names in a DLC folder are left out, as LEC's bundles do: desktop.ini, for one.
    /// </summary>
    private static List<Layer> Layers(MEGame game, string root, string? language, List<string> notes)
    {
        var basePath = BasePath(game, language, notes);
        var layers = new List<Layer> { new(basePath, Path.GetRelativePath(root, basePath), "base", null) };
        var dlcRoot = MEDirectories.GetDLCPath(game);
        foreach (var (dlc, mount) in MELoadedDLC.GetDLCNamesWithMounts(game).OrderBy(kv => kv.Value))
        {
            var cooked = Path.Combine(dlcRoot, dlc, game.CookedDirName());
            if (!Directory.Exists(cooked)) continue;
            IEnumerable<string> files = game switch
            {
                MEGame.LE1 => Deltas(cooked),
                MEGame.LE2 => Directory.GetFiles(cooked, "*.ini", SearchOption.TopDirectoryOnly)
                    .Where(f => CoalescedConverter.ProperNames.Contains(Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
                _ => new[] { Path.Combine(cooked, $"Default_{dlc}.bin") }.Where(File.Exists),
            };
            layers.AddRange(files.Select(f => new Layer(f, Path.GetRelativePath(root, f), dlc, mount)));
        }
        return layers;
    }

    private static string BasePath(MEGame game, string? language, List<string> notes)
    {
        var path = LecConfig.GetCoalescedPath(game);
        var code = language?.Trim().ToUpperInvariant();
        if (game != MEGame.LE2)
        {
            if (code is not (null or "" or "INT")) notes.Add($"{game} has one base Coalesced file: language {language} ignored.");
            return path;
        }
        code = code switch
        {
            null or "" => "INT",
            "FR" => "FRA",
            "DE" => "DEU",
            "IT" => "ITA",
            "PL" or "PLPC" => "POL",
            _ => code,
        };
        var folder = Path.GetDirectoryName(path)!;
        var localized = Path.Combine(folder, $"Coalesced_{code}.bin");
        if (File.Exists(localized)) return localized;
        var known = Directory.GetFiles(folder, "Coalesced_*.bin").Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase);
        throw new FileNotFoundException($"LE2 has no Coalesced_{code}.bin. It has: {string.Join(", ", known)}.");
    }

    private static IEnumerable<string> Deltas(string cooked)
        => Directory.GetFiles(cooked, $"{LecMerge.CONFIG_MERGE_PREFIX}*{LecMerge.CONFIG_MERGE_EXTENSION}", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<Row> ReadLayer(MEGame game, string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(LecMerge.CONFIG_MERGE_EXTENSION, StringComparison.OrdinalIgnoreCase)) return ReadDelta(path);
        if (extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)) return ReadIni(path);
        return game == MEGame.LE3 ? ReadGame3Bin(path) : ReadBin(path);
    }

    private static IEnumerable<Row> ReadBin(string path)
    {
        using var stream = File.OpenRead(path);
        foreach (var file in CoalescedConverter.DecompileLE1LE2ToMemory(stream, Path.GetFileName(path)))
        {
            foreach (var section in file.Value.Sections)
            {
                foreach (var entry in section.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;
                    yield return Typed(Path.GetFileNameWithoutExtension(file.Key), section.Header, entry.Key, entry.Value, Add);
                }
            }
        }
    }

    private static IEnumerable<Row> ReadGame3Bin(string path)
    {
        foreach (var file in LoadGame3(path).Files)
        {
            var asset = Path.GetFileNameWithoutExtension(file.Name);
            foreach (var section in file.Sections)
            {
                foreach (var key in section.Value)
                {
                    foreach (var value in key.Value)
                    {
                        yield return new Row(asset, section.Key, key.Key, Game3Operation(value.Type), $"type {value.Type}", value.Value);
                    }
                }
            }
        }
    }

    /// <summary>A bare key in a DLC ini reads as set, not LEC's add: LE3's DLC files hold the same lines as type 0 (GesturesPackageName, for one).</summary>
    private static IEnumerable<Row> ReadIni(string path)
    {
        var asset = Path.GetFileNameWithoutExtension(path);
        foreach (var row in TextConfig.Load(path, delta: false).Rows()) yield return Typed(asset, row.Header, row.Key, row.Value, Set);
    }

    private static IEnumerable<Row> ReadDelta(string path)
    {
        foreach (var row in TextConfig.Load(path, delta: true).Rows())
        {
            var space = row.Header.IndexOf(' ');
            if (space <= 0) continue;
            yield return Typed(Path.GetFileNameWithoutExtension(row.Header[..space]), row.Header[(space + 1)..], row.Key, row.Value, Add);
        }
    }

    private static Row Typed(string asset, string section, string key, string? value, string bare)
    {
        var (operation, native) = key[0] switch
        {
            '+' => (AddUnique, "+"),
            '-' => (Remove, "-"),
            '!' => (Clear, "!"),
            '.' => (Add, "."),
            '>' => (Set, ">"),
            _ => (bare, ""),
        };
        return new Row(asset, section, native.Length > 0 ? key[1..] : key, operation, native, value);
    }

    private static string Game3Operation(int type) => type switch
    {
        0 => Set,
        1 => Clear,
        2 => Add,
        3 => AddUnique,
        4 => Remove,
        _ => $"type {type}",
    };

    private static List<string> Replay(IEnumerable<ConfigRow> rows)
    {
        var values = new List<string>();
        foreach (var row in rows)
        {
            var value = row.Value ?? "";
            switch (row.Operation)
            {
                case Clear:
                    values.Clear();
                    break;
                case Set:
                    values.Clear();
                    values.Add(value);
                    break;
                case Add:
                    values.Add(value);
                    break;
                case AddUnique:
                    if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
                    break;
                case Remove:
                    values.RemoveAll(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }
        return values;
    }

    /// <summary>The mod's CookedPCConsole folder and DLC name, from either the DLC root or CookedPCConsole.</summary>
    private static (string Cooked, string Dlc) ModFolder(MEGame game, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GamePaths.ToWindowsPath(folder.Trim())));
        var cookedName = game.CookedDirName();
        var cooked = Path.GetFileName(full).Equals(cookedName, StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, cookedName);
        EnsureWritable(cooked);
        var root = Path.GetDirectoryName(cooked)!;
        var dlc = Path.GetFileName(root);
        if (!dlc.StartsWith("DLC_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{folder} is not a DLC folder: give a DLC_<name> folder or its {cookedName}.");
        }
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"No folder {root}.");
        return (cooked, dlc);
    }

    internal static void EnsureWritable(string path)
    {
        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 }) PackageWriter.EnsureWritable(game, path);
    }

    private static ConfigEntry Checked(MEGame game, ConfigEntry entry)
    {
        var operation = string.IsNullOrWhiteSpace(entry.Operation) ? Set : entry.Operation.Trim().ToLowerInvariant();
        if (operation is not (Set or Add or AddUnique or Remove or Clear))
        {
            throw new ArgumentException($"Unknown operation '{entry.Operation}'. Use set, add, addunique, remove or clear.");
        }
        var asset = CoalescedConverter.ProperNames.FirstOrDefault(n => n.Equals(Stem(entry.Asset), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown config asset '{entry.Asset}'. Use one of: {string.Join(", ", CoalescedConverter.ProperNames)}.");
        var section = entry.Section?.Trim();
        if (string.IsNullOrEmpty(section) || section.Contains(']') || section.Contains('\n') || section.Contains('\r'))
        {
            throw new ArgumentException($"Bad section '{entry.Section}'.");
        }
        var key = entry.Key?.Trim();
        if (string.IsNullOrEmpty(key) || ConfigFileProxy.IsTyped(key) || key.Contains('=') || key.Contains('\n') || key.Contains('\r'))
        {
            throw new ArgumentException($"Bad key '{entry.Key}': no '=', and no + - ! . > in front (the operation sets that).");
        }
        var value = operation == Clear ? null : entry.Value ?? throw new ArgumentException($"{asset} [{section}] {key}: {operation} needs a value.");
        if (value?.Contains('\n') == true || value?.Contains('\r') == true) throw new ArgumentException($"{asset} [{section}] {key}: a value is one line.");
        // Ini readers trim values, so an untrimmed one would never read back the same.
        if (game != MEGame.LE3) value = value?.Trim();
        return new ConfigEntry(asset, section, key, value, operation);
    }

    private static ConfigSetResult SetText(MEGame game, string cooked, string dlc, List<ConfigEntry> entries, string? deltaName)
    {
        var delta = game == MEGame.LE1;
        var files = new Dictionary<string, TextConfig>(StringComparer.OrdinalIgnoreCase);
        TextConfig Open(string path)
        {
            if (!files.TryGetValue(path, out var file))
            {
                EnsureWritable(path);
                files[path] = file = File.Exists(path) ? TextConfig.Load(path, delta) : TextConfig.New(path, delta);
            }
            return file;
        }

        var results = new List<ConfigEntryResult>();
        foreach (var entry in entries)
        {
            var (path, header) = delta
                ? (DeltaPath(cooked, dlc, entry, deltaName, Open), $"{entry.Asset}.ini {entry.Section}")
                : (IniPath(cooked, entry.Asset), entry.Section);
            var file = Open(path);
            var (outcome, line) = file.Apply(header, entry.Key, entry.Operation!, Prefix(entry.Operation!, delta), entry.Value);
            results.Add(new ConfigEntryResult(entry.Asset, entry.Section, entry.Key, entry.Operation!, line, Path.GetFileName(path), outcome));
        }

        // Several files, saved one after the other with no rollback: a failure after the first names those saved and their backups.
        var writes = new BuildWrites();
        writes.Guard(() =>
        {
            foreach (var file in files.Values.Where(f => f.Changed))
            {
                Directory.CreateDirectory(cooked);
                var backup = File.Exists(file.Path) ? PackageWriter.Backup(file.Path) : null;
                file.Save();
                writes.Wrote(file.Path, backup);
            }
            return 0;
        });
        var written = writes.Written.Select(w => w.File).ToList();
        var backups = writes.Backups;

        var notes = new List<string>();
        if (delta)
        {
            if (written.Count > 0) notes.Add("Install the mod with M3: it merges ConfigDelta files into Coalesced at install.");
            if (results.Any(r => r.Operation == Clear)) notes.Add("M3 matches a clear's key with the case the Coalesced file spells it.");
        }
        return new ConfigSetResult(game.ToString(), delta ? "m3cd" : "ini", cooked, results, written, backups, notes);
    }

    private static string Prefix(string operation, bool delta) => operation switch
    {
        Set => delta ? ">" : "",
        Add => delta ? "" : ".",
        AddUnique => "+",
        Remove => "-",
        _ => "!",
    };

    private static string IniPath(string cooked, string asset)
    {
        var existing = Directory.Exists(cooked)
            ? Directory.GetFiles(cooked, "*.ini", SearchOption.TopDirectoryOnly).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(asset, StringComparison.OrdinalIgnoreCase))
            : null;
        return existing ?? Path.Combine(cooked, $"BIO{asset[3..]}.ini");
    }

    /// <summary>The ConfigDelta file that already holds the row, else the key, else the section, else the one named for the mod (or deltaName).</summary>
    private static string DeltaPath(string cooked, string dlc, ConfigEntry entry, string? deltaName, Func<string, TextConfig> open)
    {
        if (!string.IsNullOrWhiteSpace(deltaName)) return Path.Combine(cooked, DeltaFile(deltaName));
        var header = $"{entry.Asset}.ini {entry.Section}";
        var existing = Directory.Exists(cooked) ? Deltas(cooked).ToList() : [];
        var prefix = Prefix(entry.Operation!, delta: true);
        var found = existing.FirstOrDefault(f => open(f).HoldsRow(header, entry.Key, prefix, entry.Value))
                    ?? existing.FirstOrDefault(f => open(f).HoldsKey(header, entry.Key))
                    ?? existing.FirstOrDefault(f => open(f).HasSection(header));
        if (found is not null) return found;
        var name = dlc.StartsWith("DLC_MOD_", StringComparison.OrdinalIgnoreCase) ? dlc["DLC_MOD_".Length..] : dlc;
        return Path.Combine(cooked, DeltaFile(name));
    }

    private static string DeltaFile(string name)
    {
        var bare = name.Trim();
        if (bare.EndsWith(LecMerge.CONFIG_MERGE_EXTENSION, StringComparison.OrdinalIgnoreCase)) bare = bare[..^LecMerge.CONFIG_MERGE_EXTENSION.Length];
        if (bare.StartsWith(LecMerge.CONFIG_MERGE_PREFIX, StringComparison.OrdinalIgnoreCase)) bare = bare[LecMerge.CONFIG_MERGE_PREFIX.Length..];
        if (bare.Length == 0 || bare.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException($"Bad delta name '{name}'.");
        return $"{LecMerge.CONFIG_MERGE_PREFIX}{bare}{LecMerge.CONFIG_MERGE_EXTENSION}";
    }

    /// <summary>
    /// Works on LEC's raw file model. Its XML route (ConfigAssetBundle) trims values and is not needed: this keeps every other
    /// value as the file holds it. Sections, keys and file names are lower case as in BioWare's files; LEC's compiler does the same.
    /// </summary>
    private static ConfigSetResult SetBin(MEGame game, string cooked, string dlc, List<ConfigEntry> entries)
    {
        var path = Path.Combine(cooked, $"Default_{dlc}.bin");
        EnsureWritable(path);
        var existed = File.Exists(path);
        var coal = existed ? LoadGame3(path) : new CoalescedFileXml { Version = 1 };
        var results = new List<ConfigEntryResult>();
        foreach (var entry in entries)
        {
            var type = entry.Operation switch { Set => 0, Clear => 1, Add => 2, AddUnique => 3, _ => 4 };
            var outcome = ApplyBin(coal, entry, type);
            var line = entry.Operation == Clear ? $"type {type}" : $"type {type}: {entry.Value}";
            results.Add(new ConfigEntryResult(entry.Asset, entry.Section, entry.Key, entry.Operation!, line, Path.GetFileName(path), outcome));
        }

        var written = new List<string>();
        var backups = new List<string>();
        if (results.Any(r => r.Outcome != "unchanged"))
        {
            // LEC's Huffman encoder needs two symbols, and an empty value is only the terminator.
            var texts = coal.Files.SelectMany(f => f.Sections.Values).SelectMany(s => s.Values).SelectMany(v => v).Where(v => v.Value is not null).ToList();
            if (texts.Count > 0 && texts.All(v => v.Value.Length == 0))
            {
                throw new ArgumentException($"LEC cannot compile {Path.GetFileName(path)} when every value in it is empty: give one a text.");
            }
            using var output = new MemoryStream();
            coal.Serialize(output);
            Directory.CreateDirectory(cooked);
            if (existed) backups.Add(PackageWriter.Backup(path));
            AtomicFile.WriteAllBytes(path, output.ToArray());
            written.Add(path);
        }
        return new ConfigSetResult(game.ToString(), "bin", cooked, results, written, backups, []);
    }

    private static CoalescedFileXml LoadGame3(string path)
    {
        using var input = File.OpenRead(path);
        var coal = new CoalescedFileXml();
        coal.Deserialize(input);
        return coal;
    }

    private static string ApplyBin(CoalescedFileXml coal, ConfigEntry entry, int type)
    {
        var file = coal.Files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name).Equals(entry.Asset, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            file = new FileEntry($@"..\..\biogame\config\{entry.Asset.ToLowerInvariant()}.ini");
            coal.Files.Add(file);
        }
        var sectionName = entry.Section.ToLowerInvariant();
        if (!file.Sections.TryGetValue(sectionName, out var section)) file.Sections.Add(sectionName, section = new());
        var keyName = entry.Key.ToLowerInvariant();
        if (!section.TryGetValue(keyName, out var values)) section.Add(keyName, values = new());

        var value = entry.Operation == Clear ? null : entry.Value;
        var same = values.FindIndex(v => v.Type == type && v.Value == value) >= 0;
        var row = new PropertyValue(type, value);
        switch (entry.Operation)
        {
            case Set:
                if (same && values.Count == 1) return "unchanged";
                var replaced = values.Count > 0;
                values.Clear();
                values.Add(row);
                return replaced ? "replaced" : "added";
            case Clear:
                if (same) return "unchanged";
                values.Insert(0, row);
                return "added";
            case Remove:
                var dropped = values.RemoveAll(v => v.Type is 0 or 2 or 3 && v.Value == value) > 0;
                if (same) return dropped ? "replaced" : "unchanged";
                values.Add(row);
                return dropped ? "replaced" : "added";
            default:
                if (same) return "unchanged";
                values.Add(row);
                return "added";
        }
    }

    private static string Stem(string asset)
    {
        var name = asset.Trim();
        return name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>One text file as lines; a line keeps its own \r, so a file saved with no change is the same bytes.</summary>
    private sealed class TextConfig
    {
        public readonly record struct TextRow(int Line, string Header, string Key, string Value);

        private readonly List<string> lines;
        private readonly Encoding encoding;
        private readonly byte[] preamble;
        private readonly bool crlf;

        private TextConfig(string path, List<string> lines, Encoding encoding, byte[] preamble, bool crlf)
        {
            Path = path;
            this.lines = lines;
            this.encoding = encoding;
            this.preamble = preamble;
            this.crlf = crlf;
        }

        public string Path { get; }
        public bool Changed { get; private set; }

        /// <summary>A delta is read as LEC reads it (UTF-8 unless a BOM says otherwise), an ini byte for byte: DLC inis are ANSI.</summary>
        public static TextConfig Load(string path, bool delta)
        {
            var bytes = File.ReadAllBytes(path);
            (Encoding Encoding, int Skip) form = bytes switch
            {
                [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
                [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(false), 3),
                _ => (delta ? new UTF8Encoding(false) : Encoding.Latin1, 0),
            };
            var text = form.Encoding.GetString(bytes, form.Skip, bytes.Length - form.Skip);
            return new TextConfig(path, text.Split('\n').ToList(), form.Encoding, bytes[..form.Skip], text.Contains("\r\n"));
        }

        /// <summary>The form level_build's sideloader file has (LF), or the vanilla DLC inis' (CRLF).</summary>
        public static TextConfig New(string path, bool delta)
            => new(path, [""], delta ? new UTF8Encoding(false) : Encoding.Latin1, [], !delta);

        public void Save() => AtomicFile.WriteAllBytes(Path, [.. preamble, .. encoding.GetBytes(string.Join('\n', lines))]);

        public IEnumerable<TextRow> Rows()
        {
            string? header = null;
            for (var i = 0; i < lines.Count; i++)
            {
                var text = lines[i].Trim();
                if (text.Length == 0) continue;
                if (text[0] == '[' && text[^1] == ']')
                {
                    header = text.Trim('[', ']');
                    continue;
                }
                if (header is null || text[0] == ';' || text.StartsWith("//", StringComparison.Ordinal)) continue;
                var separator = text.IndexOf('=');
                if (separator <= 0) continue;
                var key = text[..separator].Trim();
                if (key.Length > 0) yield return new TextRow(i, header, key, text[(separator + 1)..].Trim());
            }
        }

        public bool HoldsKey(string header, string key) => KeyRows(header, key).Count > 0;

        public bool HoldsRow(string header, string key, string prefix, string? value)
            => KeyRows(header, key).Any(r => Own(r, prefix) && (value is null || r.Value == value));

        public bool HasSection(string header) => lines.Any(l => IsHeader(l, header));

        /// <returns>added, replaced or unchanged, and the line as the file has it: a key already there keeps its spelling.</returns>
        public (string Outcome, string Line) Apply(string header, string key, string operation, string prefix, string? value)
        {
            var rows = KeyRows(header, key);
            var line = $"{prefix}{(rows.Count > 0 ? Bare(rows[0].Key) : key)}={(operation == Clear ? "CLEAR" : value)}";
            if (Equals(encoding, Encoding.Latin1) && line.Any(c => c > 0xFF))
            {
                throw new ArgumentException($"{line}: {System.IO.Path.GetFileName(Path)} is an ANSI file, this text does not fit it.");
            }
            var same = rows.Any(r => Own(r, prefix) && (operation == Clear || r.Value == value));
            switch (operation)
            {
                case Set:
                    if (same && rows.Count == 1) return ("unchanged", line);
                    if (rows.Count == 0)
                    {
                        Insert(header, line, null);
                        return ("added", line);
                    }
                    var first = rows[0].Line;
                    lines[first] = lines[first].EndsWith('\r') ? line + '\r' : line;
                    foreach (var row in rows.Skip(1).Reverse()) lines.RemoveAt(row.Line);
                    Changed = true;
                    return ("replaced", line);
                case Clear:
                    if (same) return ("unchanged", line);
                    Insert(header, line, rows.Count > 0 ? rows[0].Line : null);
                    return ("added", line);
                case Remove:
                    var dropped = rows.Where(r => !Own(r, prefix) && r.Key[0] != '!' && r.Value == value).Reverse().ToList();
                    foreach (var row in dropped) lines.RemoveAt(row.Line);
                    Changed |= dropped.Count > 0;
                    if (same) return (dropped.Count > 0 ? "replaced" : "unchanged", line);
                    Insert(header, line, AfterLastRow(header, key));
                    return (dropped.Count > 0 ? "replaced" : "added", line);
                default:
                    if (same) return ("unchanged", line);
                    Insert(header, line, rows.Count > 0 ? rows[^1].Line + 1 : null);
                    return ("added", line);
            }
        }

        private int? AfterLastRow(string header, string key) => KeyRows(header, key) is { Count: > 0 } rows ? rows[^1].Line + 1 : null;

        private List<TextRow> KeyRows(string header, string key)
            => Rows().Where(r => r.Header.Equals(header, StringComparison.OrdinalIgnoreCase) && Bare(r.Key).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();

        private static string Bare(string key) => ConfigFileProxy.IsTyped(key) ? key[1..] : key;

        private static bool Own(TextRow row, string prefix) => prefix.Length == 0 ? !ConfigFileProxy.IsTyped(row.Key) : row.Key[0] == prefix[0];

        private static bool IsHeader(string line, string header)
        {
            var text = line.Trim();
            return text.Length > 1 && text[0] == '[' && text[^1] == ']' && text.Trim('[', ']').Equals(header, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Puts a line at index, or at the end of the section's last block, or in a new section at the file's end.</summary>
        private void Insert(string header, string line, int? index)
        {
            var eol = crlf ? "\r" : "";
            var position = index ?? SectionEnd(header);
            Changed = true;
            if (position < lines.Count)
            {
                lines.Insert(position.Value, line + eol);
                return;
            }
            // The list's last item is the text after the final newline: empty, unless the file ends without one.
            if (lines[^1].Length > 0)
            {
                lines[^1] += eol;
                lines.Add("");
            }
            var end = lines.Count - 1;
            if (position is null)
            {
                if (end > 0 && lines[end - 1].Trim().Length > 0) lines.Insert(end++, eol);
                lines.Insert(end++, $"[{header}]{eol}");
            }
            lines.Insert(end, line + eol);
        }

        /// <summary>After the last line of the section's last block that is not blank; null when there is no such section.</summary>
        private int? SectionEnd(string header)
        {
            int? end = null;
            var inside = false;
            for (var i = 0; i < lines.Count; i++)
            {
                if (IsAnyHeader(lines[i]))
                {
                    inside = IsHeader(lines[i], header);
                    if (inside) end = i + 1;
                }
                else if (inside && lines[i].Trim().Length > 0)
                {
                    end = i + 1;
                }
            }
            return end;
        }

        private static bool IsAnyHeader(string line)
        {
            var text = line.Trim();
            return text.Length > 1 && text[0] == '[' && text[^1] == ']';
        }
    }
}
