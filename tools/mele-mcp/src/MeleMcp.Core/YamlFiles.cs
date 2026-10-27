using System.Globalization;
using LegendaryExplorerCore.Packages;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MeleMcp.Core;

/// <summary>Shared pieces of the YAML builders (conv_build, plot_build): reading values with line numbers, finding files, text per language.</summary>
internal static class YamlFiles
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
