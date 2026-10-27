using System.Globalization;
using YamlDotNet.RepresentationModel;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <summary>Edits a level YAML file in its text (npc_place, live_save), so its comments and layout stay as written.</summary>
internal static class YamlEdits
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static string Location(float x, float y, float z) => $"[{string.Join(", ", new[] { x, y, z }.Select(f => f.ToString("R", Inv)))}]";

    internal static string Degrees(float yaw) => yaw.ToString("0.###", Inv);

    /// <summary>An entry of a top-level mapping such as npcs; with no name, its only entry.</summary>
    internal static KeyValuePair<YamlNode, YamlNode> Entry(YamlMappingNode root, string section, string? name)
    {
        var entries = root.Children.FirstOrDefault(c => Scalar(c.Key) == section).Value as YamlMappingNode
                      ?? throw new ArgumentException($"The file has no '{section}' mapping; is it a level_build file?");
        var entry = name is null
            ? entries.Children.Count == 1 ? entries.Children.First() : throw new ArgumentException($"The file has {entries.Children.Count} {section}; name one: {string.Join(", ", entries.Children.Keys.Select(Scalar))}.")
            : entries.Children.FirstOrDefault(c => Scalar(c.Key).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry.Key is null) throw new ArgumentException($"No '{name}' in {section}. The file has: {string.Join(", ", entries.Children.Keys.Select(Scalar))}.");
        return entry;
    }

    /// <summary>Replaces these keys' values in an entry (or adds them above its first key), leaving everything else as written.</summary>
    internal static (string Text, List<string> Changes) SetKeys(string text, YamlNode entry, IReadOnlyList<(string Key, string Value)> wanted)
    {
        var body = entry as YamlMappingNode ?? throw Fail(entry, "the entry must be a mapping.");
        // Splice from the end of the text so earlier offsets stay valid.
        var edits = new List<(long Start, long End, string Text)>();
        var changes = new List<string>();
        foreach (var (key, value) in wanted)
        {
            var existing = body.Children.FirstOrDefault(c => Scalar(c.Key) == key);
            if (existing.Value is { } node)
            {
                // A flow list's End mark is the end of its '[' token; the value ends at the ']' after its last item.
                var end = node is YamlSequenceNode { Children.Count: > 0 } list ? text.IndexOf(']', (int)list.Children[^1].End.Index) + 1 : node.End.Index;
                var old = text[(int)node.Start.Index..(int)end];
                if (old == value) continue;
                edits.Add((node.Start.Index, end, value));
                changes.Add($"{key}: {old} → {value}");
            }
            else
            {
                // A new line above the entry's first key, at its indentation.
                var first = body.Children.First().Key;
                var at = text.LastIndexOf('\n', (int)first.Start.Index) + 1;
                edits.Add((at, at, $"{new string(' ', (int)first.Start.Column - 1)}{key}: {value}{NewLine(text)}"));
                changes.Add($"{key}: {value} (added)");
            }
        }
        foreach (var (start, end, value) in edits.OrderByDescending(e => e.Start))
        {
            text = text[..(int)start] + value + text[(int)end..];
        }
        return (text, changes);
    }

    /// <summary>
    /// Adds an entry at the end of a top-level block mapping, after its last value's line (comments before the next section
    /// stay with that section). A missing section is added at the end of the file, under its comment.
    /// </summary>
    internal static string AddEntry(string text, YamlMappingNode root, string section, string name, IReadOnlyList<(string Key, string Value)> keys, string sectionComment)
    {
        var newline = NewLine(text);
        var existing = root.Children.FirstOrDefault(c => Scalar(c.Key) == section);
        if (existing.Key is null)
        {
            var lines = string.Concat(keys.Select(k => $"    {k.Key}: {k.Value}{newline}"));
            var separator = text.EndsWith('\n') ? newline : newline + newline;
            return $"{text}{separator}# {sectionComment}{newline}{section}:{newline}  {name}:{newline}{lines}";
        }
        if (existing.Value is not YamlMappingNode { Style: not YamlDotNet.Core.Events.MappingStyle.Flow, Children.Count: > 0 } entries)
        {
            throw Fail(existing.Value, $"'{section}' must be a block mapping with at least one entry to add to it.");
        }
        if (entries.Children.Keys.Any(k => Scalar(k).Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"{section} already has '{name}'.");

        var indent = (int)entries.Children.First().Key.Start.Column - 1;
        var lineStart = text.IndexOf('\n', (int)LastLeaf(entries).Start.Index);
        var at = lineStart < 0 ? text.Length : lineStart + 1;
        var entry = $"{new string(' ', indent)}{name}:{newline}" + string.Concat(keys.Select(k => $"{new string(' ', indent + 2)}{k.Key}: {k.Value}{newline}"));
        return at == text.Length && !text.EndsWith('\n') ? text + newline + entry : text[..at] + entry + text[at..];
    }

    /// <summary>A top-level key on its own line, above another top-level key (or at the end of the file without it).</summary>
    internal static string AddTopLevel(string text, YamlMappingNode root, string key, string value, string before)
    {
        var line = $"{key}: {value}{NewLine(text)}";
        var next = root.Children.Keys.FirstOrDefault(k => Scalar(k) == before);
        if (next is null) return text + (text.EndsWith('\n') ? "" : NewLine(text)) + line;
        var at = text.LastIndexOf('\n', (int)next.Start.Index) + 1;
        return text[..at] + line + text[at..];
    }

    /// <summary>The last value written inside a node: a mapping or list ends where its last child does.</summary>
    private static YamlNode LastLeaf(YamlNode node) => node switch
    {
        YamlMappingNode { Style: not YamlDotNet.Core.Events.MappingStyle.Flow, Children.Count: > 0 } m => LastLeaf(m.Children.Last().Value),
        YamlSequenceNode { Style: not YamlDotNet.Core.Events.SequenceStyle.Flow, Children.Count: > 0 } s => LastLeaf(s.Children[^1]),
        _ => node,
    };

    private static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
}
