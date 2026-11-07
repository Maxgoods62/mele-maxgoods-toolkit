using System.Globalization;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MeleMcp.Core;

/// <summary>Shared pieces of the YAML builders (conv_build, plot_build): reading values with line numbers, finding files, text per language.</summary>
internal static partial class YamlFiles
{
    /// <summary>Text-only languages fall back to the language whose text they show (FE = French text, English voice).</summary>
    internal static readonly Dictionary<string, string> TextLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FE"] = "FR", ["GE"] = "DE", ["IE"] = "IT", ["RA"] = "RU", ["PLPC"] = "PL",
    };

    /// <summary>A path as given, then relative to each folder in turn, then a file name the game loads.</summary>
    internal static string Locate(MEGame game, string package, params string[] dirs)
    {
        var candidate = GamePaths.ToWindowsPath(package);
        foreach (var dir in dirs)
        {
            var relative = Path.Combine(dir, candidate);
            if (File.Exists(relative)) return Path.GetFullPath(relative);
        }
        return GamePaths.ResolvePackage(game, candidate);
    }

    /// <summary>
    /// The package a builder writes, and whether it is missing: the file's 'package' (or the tool's), a full path or a path under
    /// the mod root that starts with the DLC folder (DLC_MOD_MyMod\CookedPCConsole\BIOA_MyMap.pcc). The mod root is the YAML's
    /// folder when it has that DLC folder, else the mod of M3's library that has it. A bare file name is looked up as before (the
    /// YAML's folder, then a file the game loads) and never missing: a builder must not create it, it could resolve into the game
    /// install. With <paramref name="outRoot"/> (mod_build out=) that folder is the mod root, a full path goes under it by its DLC
    /// folder, and a DLC folder it does not have yet is first copied from where the build would write without out=. That copy is
    /// a snapshot: later builds into out= build on it as it is, and a note names the source files newer than their copy or missing from it.
    /// </summary>
    internal static (string Path, bool Missing) Target(MEGame game, string package, string yamlDir, string? outRoot, List<string> notes)
    {
        var given = GamePaths.ToWindowsPath(package);
        if (outRoot is null)
        {
            if (Path.IsPathRooted(given)) return Found(Path.GetFullPath(given));
            if (DlcPart(given) is null) return (Locate(game, given, yamlDir), false);
            var root = ModRoot(game, given, yamlDir)
                       ?? throw new FileNotFoundException($"No package '{package}': neither the YAML's folder ({yamlDir}) nor a mod of M3's library has its DLC "
                                                          + "folder. Make the DLC folder beside the YAML, give a full path, or build with mod_build out=.");
            return Found(Path.GetFullPath(Path.Combine(root, given)));
        }

        var into = Path.GetFullPath(GamePaths.ToWindowsPath(outRoot));
        var part = DlcPart(Path.IsPathRooted(given) ? Path.GetFullPath(given) : given)
                   ?? throw new ArgumentException($"package '{package}': with out=, give it from its DLC folder on (DLC_MOD_MyMod\\CookedPCConsole\\{Path.GetFileName(given)}), or as a full path in a DLC folder.");
        var source = Path.IsPathRooted(given) ? Path.GetFullPath(given)
            : ModRoot(game, given, yamlDir) is { } from ? Path.GetFullPath(Path.Combine(from, given)) : null;
        var target = Path.GetFullPath(Path.Combine(into, part));
        var to = DlcFolder(target)!;
        if (source is not null && DlcFolder(source) is { } copied && Directory.Exists(copied))
        {
            if (!Directory.Exists(to))
            {
                PackageWriter.EnsureWritable(game, target);
                CopyFolder(copied, to);
                notes.Add($"out=: copied {copied} to {to}; the build changes the copy. Later builds into out= keep this copy: delete {to} to copy the source again.");
            }
            else if (Stale(copied, to) is { Count: > 0 } stale)
            {
                notes.Add($"out=: {to} is the copy of {copied} an earlier build made, and this build changes it as it is: {stale.Count} source file(s) "
                          + $"are newer than their copy, or were added to the source since and are missing from it ({string.Join(", ", stale.Take(3))}{(stale.Count > 3 ? ", ..." : "")}). Delete {to} to copy the source again.");
            }
        }
        return Found(target);
    }

    /// <summary>The file a YAML's 'package' names when it exists: beside the YAML (or a full path), or under its mod root; else null.</summary>
    internal static string? Existing(MEGame game, string package, string yamlDir)
    {
        var given = GamePaths.ToWindowsPath(package);
        var beside = Path.GetFullPath(Path.Combine(yamlDir, given));
        if (File.Exists(beside)) return beside;
        if (Path.IsPathRooted(given) || DlcPart(given) is null) return null;
        return ModRoot(game, given, yamlDir) is { } root && Path.GetFullPath(Path.Combine(root, given)) is var path && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// A package a builder only reads (a mesh's, the prop template's) as 'package' is found: a relative path beside the YAML or
    /// under its mod root (<see cref="Existing"/>), first under <paramref name="outRoot"/> by its DLC folder with mod_build out=.
    /// A full path, a file name the YAML's folder lacks, or a path found nowhere stays as given (a file the game loads).
    /// </summary>
    internal static string Source(MEGame game, string package, string yamlDir, string? outRoot)
    {
        var given = GamePaths.ToWindowsPath(package);
        if (Path.IsPathRooted(given)) return package;
        if (outRoot is not null && DlcPart(given) is { } part && Path.GetFullPath(Path.Combine(GamePaths.ToWindowsPath(outRoot), part)) is var copy && File.Exists(copy)) return copy;
        return Existing(game, package, yamlDir) ?? package;
    }

    /// <summary>
    /// A file other than the package that a build changes (a GlobalTlk, the AutoLoad.ini), found at <paramref name="path"/>. With
    /// <paramref name="outRoot"/> (mod_build out=) its copy there instead, by its DLC folder as <see cref="Target"/> maps the
    /// package; refused when out= has no such copy (a file outside a DLC folder, of a DLC folder the build did not copy, or added
    /// to the source after the copy): an out= build never writes the source.
    /// </summary>
    internal static string InOut(string path, string? outRoot, string what)
    {
        if (outRoot is null) return path;
        var into = Path.GetFullPath(GamePaths.ToWindowsPath(outRoot));
        var full = Path.GetFullPath(path);
        if (full.StartsWith(into.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return full;
        var copy = DlcPart(full) is { } part ? Path.GetFullPath(Path.Combine(into, part)) : null;
        if (copy is not null && File.Exists(copy)) return copy;
        // The DLC folder was copied before this file was in it: the copy is a snapshot (Target).
        var snapshot = copy is not null && File.Exists(full) && DlcFolder(copy) is { } folder && Directory.Exists(folder)
            ? $" {folder} is the copy an earlier build made, and it lacks any file added to the source since: delete it to copy the source again."
            : "";
        throw new ArgumentException($"{what} {full} has no copy under out= ({into}){(copy is null ? ": it is in no DLC folder" : $": no {copy}")}.{snapshot} An out= build changes "
                                    + "nothing outside out=, so the file was not built. Keep it in the package's DLC folder (a file name finds it beside the package), or build without out=.");
    }

    private static (string Path, bool Missing) Found(string path) => (path, !File.Exists(path));

    /// <summary>
    /// The folder a relative path with a DLC folder lies under: the YAML's folder when it has the path up to that DLC folder,
    /// else the one mod of M3's library (mods\&lt;game&gt;\&lt;mod&gt;) that has it; null when none does.
    /// </summary>
    private static string? ModRoot(MEGame game, string relative, string yamlDir)
    {
        var part = DlcPart(relative)!;
        var prefix = relative[..(relative.Length - part.Length)] + part.Split('\\')[0];
        if (Directory.Exists(Path.Combine(yamlDir, prefix))) return yamlDir;
        string library;
        try
        {
            library = Path.Combine(CatalogBuilder.Library(), game.ToString());
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        var mods = Directory.Exists(library) ? Directory.EnumerateDirectories(library).Where(m => Directory.Exists(Path.Combine(m, prefix))).ToList() : [];
        return mods.Count switch
        {
            0 => null,
            1 => mods[0],
            _ => throw new ArgumentException($"{mods.Count} mods of M3's library have {prefix} ({string.Join(", ", mods.Select(Path.GetFileName))}): give a full path, or mod_build out=."),
        };
    }

    /// <summary>The path from its last DLC folder on (D:\x\DLC_MOD_MyMod\CookedPCConsole\A.pcc → DLC_MOD_MyMod\CookedPCConsole\A.pcc); null without one.</summary>
    private static string? DlcPart(string path)
    {
        var parts = path.Split('\\', '/');
        for (var at = parts.Length - 2; at >= 0; at--)
        {
            if (parts[at].StartsWith("DLC_", StringComparison.OrdinalIgnoreCase)) return string.Join('\\', parts[at..]);
        }
        return null;
    }

    /// <summary>The full path of the DLC folder a file lies in (the last one of its path); null outside one.</summary>
    private static string? DlcFolder(string path)
    {
        var full = Path.GetFullPath(path);
        return DlcPart(full) is { } part ? full[..^(part.Length - part.Split('\\')[0].Length)] : null;
    }

    /// <summary>
    /// Copies a DLC folder whole. <see cref="Target"/> reads "the folder exists" as "the copy is done", so the files go to a
    /// sibling folder first, renamed to <paramref name="to"/> once every one is in: a copy that stops half way (a full disk, a
    /// file LEX holds) leaves no partial DLC folder for the next build to take as done and create missing packages in. A leftover
    /// of such a copy is removed and the copy redone.
    /// </summary>
    internal static void CopyFolder(string from, string to)
    {
        var partial = PartialCopy(to);
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        try
        {
            Directory.CreateDirectory(partial);
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(partial, Path.GetRelativePath(from, dir)));
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(partial, Path.GetRelativePath(from, file)));
            Directory.Move(partial, to);
        }
        catch (Exception)
        {
            try
            {
                if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left for the next copy, which removes it first.
            }
            throw;
        }
    }

    /// <summary>Where <see cref="CopyFolder"/> copies before the rename: beside the DLC folder, a name that is no DLC folder.</summary>
    internal static string PartialCopy(string to) => Path.Combine(Path.GetDirectoryName(to)!, $".{Path.GetFileName(to)}.copying");

    /// <summary>
    /// The source files (relative paths) changed or added since an out= copy was made. File.Copy keeps a file's write time, and a
    /// build only makes the copy's files newer, so a source file newer than its copy changed after the copy was made. A source file
    /// missing from the copy counts only when it was created after the copy (the copy folder's creation time, which the rename at
    /// the end of <see cref="CopyFolder"/> keeps): a build into out= deletes files from its own copy too (a fork with no entries
    /// left, a stale localized copy), and those are no change of the source.
    /// </summary>
    internal static List<string> Stale(string from, string to)
    {
        var copied = Directory.GetCreationTimeUtc(to);
        return Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
            .Select(file => (Relative: Path.GetRelativePath(from, file), Source: file))
            .Where(f => Path.Combine(to, f.Relative) is var copy && (File.Exists(copy)
                ? File.GetLastWriteTimeUtc(f.Source) > File.GetLastWriteTimeUtc(copy)
                : File.GetCreationTimeUtc(f.Source) > copied))
            .Select(f => f.Relative)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>DLC_MOD_X_GlobalTlk_FR → FR; DLC_MOD_X_GlobalTlk → INT.</summary>
    internal static string LanguageOfFile(string name)
    {
        var tail = name[(name.LastIndexOf('_') + 1)..];
        return name.Contains('_') && tail.Length is >= 2 and <= 4 && tail.All(char.IsUpper) && !tail.Equals("TLK", StringComparison.Ordinal) ? tail : "INT";
    }

    internal static List<TlkString> StringsFor(Dictionary<int, Dictionary<string, string>> strings, string language, string defaultLanguage) =>
        strings.Select(s => new TlkString(s.Key,
                s.Value.GetValueOrDefault(language)
                ?? (TextLanguage.TryGetValue(language, out var shown) ? s.Value.GetValueOrDefault(shown) : null)
                ?? s.Value.GetValueOrDefault(defaultLanguage)
                ?? s.Value.Values.First()))
            .ToList();

    internal static void Collect(Dictionary<int, Dictionary<string, string>> strings, int strRef, Dictionary<string, string> text, int line)
    {
        if (strings.TryGetValue(strRef, out var existing) && !existing.OrderBy(p => p.Key).SequenceEqual(text.OrderBy(p => p.Key)))
        {
            throw new ArgumentException($"line {line}: strref {strRef} is given two different texts.");
        }
        strings[strRef] = text;
    }

    internal static Dictionary<string, string> Text(YamlNode value)
    {
        if (value is YamlScalarNode) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["*"] = Scalar(value) };
        if (value is not YamlMappingNode m) throw Fail(value, "text is a string or a mapping of languages (fr, en...).");
        return m.Children.ToDictionary(c => Language(Scalar(c.Key)), c => Scalar(c.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Language codes as the game names them; en means INT.</summary>
    internal static string Language(string code) => code.Trim().ToUpperInvariant() switch
    {
        "EN" or "INT" => "INT",
        var other => other,
    };

    internal static IEnumerable<(string Key, YamlNode Value)> Entries(YamlMappingNode m, params string[] allowed)
    {
        foreach (var (k, v) in m.Children)
        {
            var key = Scalar(k);
            if (!allowed.Contains(key)) throw Fail(k, $"unknown key '{key}'. Allowed here: {string.Join(", ", allowed)}.");
            yield return (key, v);
        }
    }

    internal const string RequiresKey = "requires";

    /// <summary>
    /// A builder file's top-level keys: its own (<paramref name="allowed"/>), and 'requires: mele-mcp &gt;= X.Y.Z', optional in
    /// any mod file: the oldest mele-mcp that builds it. An older server refuses the file before it reads any other key.
    /// </summary>
    internal static IEnumerable<(string Key, YamlNode Value)> TopLevel(YamlMappingNode root, params string[] allowed)
    {
        CheckRequires(root);
        return Entries(root, [.. allowed, RequiresKey]).Where(e => e.Key != RequiresKey);
    }

    /// <summary>Refuses a file whose 'requires' names a newer mele-mcp than this server (<see cref="Versions.Server"/>).</summary>
    internal static void CheckRequires(YamlMappingNode root)
    {
        var node = root.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: RequiresKey }).Value;
        if (node is null) return;
        var match = Requires().Match(Scalar(node));
        if (!match.Success) throw Fail(node, "'requires' is 'mele-mcp >= X.Y.Z', the oldest mele-mcp that builds the file.");
        var wanted = Versions.ThreeParts(System.Version.Parse(match.Groups[1].Value));
        if (Versions.Server < wanted)
        {
            throw Fail(node, $"the file requires mele-mcp >= {wanted}, and this server is {Versions.Server}. Update mele-mcp to {wanted} or later, then reconnect it.");
        }
    }

    [GeneratedRegex(@"^\s*mele-mcp\s*>=\s*(\d+\.\d+(?:\.\d+)?)\s*$")]
    private static partial Regex Requires();

    /// <summary>The file's top-level mapping; YAML syntax errors keep their line.</summary>
    internal static YamlMappingNode Root(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException e)
        {
            throw new ArgumentException($"line {e.Start.Line}: {e.Message}");
        }
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root ? root : throw new ArgumentException("The file is not a YAML mapping.");
    }

    internal static string Scalar(YamlNode value) => value is YamlScalarNode { Value: { } s } ? s : throw Fail(value, "expected a single value.");

    /// <summary>A key given no value: 'vanilla:' alone (its last entry deleted by hand), '~' or 'null'.</summary>
    internal static bool Null(YamlNode value) =>
        value is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any, Value: null or "" or "~" or "null" or "Null" or "NULL" };

    internal static IEnumerable<YamlNode> Sequence(YamlNode value) => value is YamlSequenceNode s ? s.Children : throw Fail(value, "expected a list.");

    internal static int Int(YamlNode value) =>
        int.TryParse(Scalar(value).TrimStart('$'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw Fail(value, "expected a whole number.");

    internal static bool Bool(YamlNode value) =>
        bool.TryParse(Scalar(value), out var b) ? b : throw Fail(value, "expected true or false.");

    internal static float Float(YamlNode value) =>
        float.TryParse(Scalar(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : throw Fail(value, "expected a positive number of seconds.");

    internal static float Number(YamlNode value) =>
        float.TryParse(Scalar(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : throw Fail(value, "expected a number.");

    /// <summary>A list of <paramref name="min"/> to <paramref name="max"/> numbers: [x, y, z], [r, g, b]...</summary>
    internal static float[] Numbers(YamlNode value, int min, int max)
    {
        var numbers = Sequence(value).Select(Number).ToArray();
        return numbers.Length >= min && numbers.Length <= max ? numbers
            : throw Fail(value, min == max ? $"expected {min} numbers." : $"expected {min} to {max} numbers.");
    }

    internal static ArgumentException Fail(YamlNode node, string message) => new($"line {node.Start.Line}: {message}");
}
