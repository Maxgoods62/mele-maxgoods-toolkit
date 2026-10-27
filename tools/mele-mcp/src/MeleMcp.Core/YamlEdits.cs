using System.Globalization;
using LegendaryExplorerCore.Packages;
using YamlDotNet.RepresentationModel;
using static MeleMcp.Core.YamlFiles;

namespace MeleMcp.Core;

/// <summary>Edits a level YAML file in its text (npc_place, live_save, editor_import), so its comments and layout stay as written.</summary>
internal static class YamlEdits
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The sections whose entries are actors the build finds again by tag (lights: docs/07 §11).</summary>
    private static readonly string[] ActorSections = ["npcs", "usables", "props", "lights"];

    internal static string Location(float x, float y, float z) => $"[{string.Join(", ", new[] { x, y, z }.Select(f => f.ToString("R", Inv)))}]";

    internal static string Degrees(float yaw) => yaw.ToString("0.###", Inv);

    /// <summary>A DrawScale or a DrawScale3D component: 4 decimals, as the editor rounds them.</summary>
    internal static string Scale(double scale) => scale.ToString("0.####", Inv);

    internal static string Scale3D(double[] scale) => $"[{string.Join(", ", scale.Select(Scale))}]";

    /// <summary>A light's radius: 2 decimals (editor_import takes radii within 0.01 as one).</summary>
    internal static string Radius(double radius) => radius.ToString("0.##", Inv);

    /// <summary>A light's brightness or falloff: 4 decimals (taken as one within 0.001).</summary>
    internal static string Fraction(double value) => value.ToString("0.####", Inv);

    /// <summary>A light's colour, [r, g, b] in whole numbers 0-255.</summary>
    internal static string Color(double[] rgb) => $"[{string.Join(", ", rgb.Select(c => ((int)Math.Round(c)).ToString(Inv)))}]";

    /// <summary>[pitch, yaw, roll] in degrees in (-180, 180], from Unreal units.</summary>
    internal static string Rotation(double[] units) => $"[{string.Join(", ", units.Select(u => Degrees((float)Rotations.Angle(Rotations.Degrees(u)))))}]";

    /// <summary>The key a new entry gets for a rotation (Unreal units): 'yaw' while the actor is upright, else 'rotation'.</summary>
    internal static (string Key, string Value) RotationKey(double[] units) =>
        Rotations.YawWith(units, 0, 0) is { } yaw ? ("yaw", Degrees((float)yaw)) : ("rotation", Rotation(units));

    /// <summary>A value as a YAML scalar: plain when it can be, else single-quoted (a Windows path, a name with spaces).</summary>
    internal static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') ? value : $"'{value.Replace("'", "''")}'";

    /// <summary>The npcs, usables, props or lights entry with this tag, as the build finds its actor; null when none has it.</summary>
    internal static (string Section, KeyValuePair<YamlNode, YamlNode> Entry)? Tagged(YamlMappingNode root, string tag)
    {
        var found = ActorSections.SelectMany(s => root.Children.FirstOrDefault(c => Scalar(c.Key) == s).Value is YamlMappingNode m
                ? m.Children.Select(c => (Section: s, Entry: c))
                : [])
            .Where(e => e.Entry.Value is YamlMappingNode body
                        && body.Children.Any(c => Scalar(c.Key) == "tag" && Scalar(c.Value).Equals(tag, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (found.Count > 1) throw new ArgumentException($"Several entries have tag '{tag}': {string.Join(", ", found.Select(f => $"{f.Section}.{Scalar(f.Entry.Key)}"))}.");
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>The tags of the actor entries (npcs, usables, props, lights).</summary>
    internal static HashSet<string> Tags(YamlMappingNode root) =>
        ActorSections.SelectMany(s => root.Children.FirstOrDefault(c => Scalar(c.Key) == s).Value is YamlMappingNode m ? m.Children.Values : [])
            .OfType<YamlMappingNode>()
            .SelectMany(body => body.Children.Where(c => Scalar(c.Key) == "tag").Select(c => Scalar(c.Value)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tags of the NPCs' lights (npcs.&lt;name&gt;.lights[].tag), each with where it is: npcs.reyes.lights.</summary>
    internal static Dictionary<string, string> NpcLightTags(YamlMappingNode root)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.Children.FirstOrDefault(c => Scalar(c.Key) == "npcs").Value is not YamlMappingNode npcs) return tags;
        foreach (var (name, body) in npcs.Children)
        {
            if (body is not YamlMappingNode npc || npc.Children.FirstOrDefault(c => Scalar(c.Key) == "lights").Value is not YamlSequenceNode lights) continue;
            foreach (var light in lights.Children.OfType<YamlMappingNode>())
            {
                if (light.Children.FirstOrDefault(c => Scalar(c.Key) == "tag").Value is YamlScalarNode { Value: { } tag }) tags.TryAdd(tag, $"npcs.{Scalar(name)}.lights");
            }
        }
        return tags;
    }

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

    /// <summary>
    /// Replaces these keys' values in an entry, leaving everything else as written. A missing key gets a line of its own after the
    /// last of <paramref name="after"/> the entry has (its value's last line), else above the entry's first key; in a flow entry
    /// ({ ... }), ', key: value' after that value, else after its last one.
    /// </summary>
    internal static (string Text, List<string> Changes) SetKeys(string text, YamlNode entry, IReadOnlyList<(string Key, string Value)> wanted, IReadOnlyList<string>? after = null)
    {
        var body = entry as YamlMappingNode ?? throw Fail(entry, "the entry must be a mapping.");
        var edits = new List<(long Start, long End, string Text)>();
        var changes = new List<string>();
        foreach (var (key, value) in wanted)
        {
            if (Pair(body, key) is { } existing)
            {
                var end = ValueEnd(text, existing.Value);
                var old = text[(int)existing.Value.Start.Index..end];
                if (old == value) continue;
                edits.Add((existing.Value.Start.Index, end, value));
                changes.Add($"{key}: {old} → {value}");
            }
            else
            {
                edits.Add(NewKeyLine(text, body, after, $"{key}: {value}"));
                changes.Add($"{key}: {value} (added)");
            }
        }
        return (Apply(text, edits), changes);
    }

    /// <summary>Removes these keys from an entry, each with its line (and a comment on it).</summary>
    internal static (string Text, List<string> Changes) RemoveKeys(string text, YamlNode entry, IReadOnlyList<string> keys)
    {
        var body = entry as YamlMappingNode ?? throw Fail(entry, "the entry must be a mapping.");
        var edits = new List<(long Start, long End, string Text)>();
        var changes = new List<string>();
        foreach (var key in keys)
        {
            if (Pair(body, key) is not { } existing) continue;
            edits.Add(Removal(text, body, existing));
            changes.Add($"{key}: {text[(int)existing.Value.Start.Index..ValueEnd(text, existing.Value)]} (removed)");
        }
        return (Apply(text, edits), changes);
    }

    /// <summary>
    /// Writes a rotation (Unreal units) into an actor entry so the build gives it back: 'yaw' while the entry has no 'rotation' and the
    /// rotation is a yaw over the pitch and roll the build keeps under one (the actor's own: <paramref name="keptPitch"/>,
    /// <paramref name="keptRoll"/>, 0 for a new or upright actor); else 'rotation: [pitch, yaw, roll]' in degrees, where 'yaw' was.
    /// Never leaves both keys. A value that already turns the actor the same way stays as written (200 and -160 are one yaw).
    /// </summary>
    internal static (string Text, List<string> Changes) SetRotation(string text, YamlNode entry, double[] units, double keptPitch = 0, double keptRoll = 0)
    {
        var body = entry as YamlMappingNode ?? throw Fail(entry, "the entry must be a mapping.");
        var yaw = Pair(body, "yaw");
        var rotation = Pair(body, "rotation");
        string Old(YamlNode node) => text[(int)node.Start.Index..ValueEnd(text, node)];
        if (rotation is null && Rotations.YawWith(units, keptPitch, keptRoll) is { } degrees)
        {
            if (yaw is { } y && Rotations.SameYaw(Number(y.Value), degrees)) return (text, []);
            return SetKeys(text, entry, [("yaw", Degrees((float)degrees))], ["location"]);
        }

        var value = Rotation(units);
        var edits = new List<(long Start, long End, string Text)>();
        var changes = new List<string>();
        if (rotation is { } r)
        {
            var old = Numbers(r.Value, 3, 3).Select(d => Rotations.Units(d)).ToArray();
            if (!Rotations.Same(old, units))
            {
                edits.Add((r.Value.Start.Index, ValueEnd(text, r.Value), value));
                changes.Add($"rotation: {Old(r.Value)} → {value}");
            }
            if (yaw is { } y)
            {
                edits.Add(Removal(text, body, y));
                changes.Add($"yaw: {Old(y.Value)} (removed)");
            }
        }
        else if (yaw is { } y)
        {
            // In the place of 'yaw': a comment after it stays on the line.
            edits.Add((y.Key.Start.Index, ValueEnd(text, y.Value), $"rotation: {value}"));
            changes.Add($"yaw: {Old(y.Value)} → rotation: {value}");
        }
        else
        {
            return SetKeys(text, entry, [("rotation", value)], ["location"]);
        }
        return (Apply(text, edits), changes);
    }

    /// <summary>
    /// Adds an entry at the end of a top-level block mapping, after its last value's line (comments before the next section
    /// stay with that section), with <paramref name="keyComment"/> after its name when given. A missing section is added at the end of
    /// the file, under its comment (one comment line per line of it); one with no entry left ('vanilla:' alone, '~', '{}') gets it
    /// under its header.
    /// </summary>
    internal static string AddEntry(string text, YamlMappingNode root, string section, string name, IReadOnlyList<(string Key, string Value)> keys, string sectionComment,
        string? keyComment = null)
    {
        var newline = NewLine(text);
        var nameLine = keyComment is null ? $"{name}:" : $"{name}:   # {keyComment}";
        var lines = string.Concat(keys.Select(k => $"    {k.Key}: {k.Value}{newline}"));
        var existing = root.Children.FirstOrDefault(c => Scalar(c.Key) == section);
        if (existing.Key is null)
        {
            var separator = text.EndsWith(newline + newline) ? "" : text.EndsWith('\n') ? newline : newline + newline;
            var comment = string.Concat(sectionComment.Split('\n').Select(l => $"# {l.TrimEnd('\r')}{newline}"));
            return $"{text}{separator}{comment}{section}:{newline}  {nameLine}{newline}{lines}";
        }
        if (Null(existing.Value) || existing.Value is YamlMappingNode { Style: YamlDotNet.Core.Events.MappingStyle.Flow, Children.Count: 0 })
        {
            // Its value ('~', '{}') goes with the blanks before it, a comment after it stays; the entry goes on the line after the header.
            var edits = new List<(long Start, long End, string Text)>();
            if (existing.Value is not YamlScalarNode { Value: null or "" })
            {
                var start = (int)existing.Value.Start.Index;
                while (start > existing.Key.End.Index && text[start - 1] is ' ' or '\t') start--;
                edits.Add((start, ValueEnd(text, existing.Value), ""));
            }
            var header = text.IndexOf('\n', (int)existing.Key.End.Index);
            var first = $"  {nameLine}{newline}{lines}";
            edits.Add(header < 0 ? (text.Length, text.Length, newline + first) : (header + 1, header + 1, first));
            return Apply(text, edits);
        }
        if (existing.Value is not YamlMappingNode { Style: not YamlDotNet.Core.Events.MappingStyle.Flow, Children.Count: > 0 } entries)
        {
            throw Fail(existing.Value, $"'{section}' must be a block mapping with at least one entry to add to it.");
        }
        if (entries.Children.Keys.Any(k => Scalar(k).Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"{section} already has '{name}'.");

        var indent = (int)entries.Children.First().Key.Start.Column - 1;
        var lineStart = text.IndexOf('\n', (int)LastLeaf(entries).Start.Index);
        var at = lineStart < 0 ? text.Length : lineStart + 1;
        var entry = $"{new string(' ', indent)}{nameLine}{newline}" + string.Concat(keys.Select(k => $"{new string(' ', indent + 2)}{k.Key}: {k.Value}{newline}"));
        return at == text.Length && !text.EndsWith('\n') ? text + newline + entry : text[..at] + entry + text[at..];
    }

    /// <summary>Removes the entry of a top-level mapping whose value is <paramref name="value"/>, with its lines (and a comment on them).</summary>
    internal static string RemoveEntry(string text, YamlMappingNode root, string section, YamlNode value)
    {
        var entries = root.Children.FirstOrDefault(c => Scalar(c.Key) == section).Value as YamlMappingNode ?? throw new ArgumentException($"The file has no '{section}' mapping.");
        var pair = entries.Children.FirstOrDefault(c => ReferenceEquals(c.Value, value));
        if (pair.Key is null) throw new ArgumentException($"'{section}' has no such entry.");
        return Apply(text, [Removal(text, entries, pair)]);
    }

    /// <summary>
    /// Adds 'propTemplate' above props when the file has none: a BioInert placed in that file (not one spawned since), whose
    /// copies show static meshes, lit by their light environment, or by prop lights where a light of the lights section covers them.
    /// Returns the change, or null when the file had one.
    /// </summary>
    internal static (string Text, string? Added) AddPropTemplate(MEGame game, string text, string package)
    {
        var root = Root(text);
        if (root.Children.Keys.Any(k => Scalar(k) == "propTemplate")) return (text, null);
        string inert;
        using (var pcc = PackageInspector.Open(game, package))
        {
            inert = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioInert" && e.Parent?.InstancedFullPath == "TheWorld.PersistentLevel")?.ObjectName.Instanced
                    ?? throw new ArgumentException($"{Path.GetFileName(GamePaths.ToWindowsPath(package))} has no BioInert to show static meshes on: add 'propTemplate: {{ package: <file>, actor: <a BioInert> }}' to the YAML.");
        }
        var standIn = $"{{ package: {Quote(package)}, actor: {inert} }}";
        return (AddTopLevel(text, root, "propTemplate", $"{standIn}   # a BioInert: copies of it show the static meshes, lit by their light environment (by prop lights where a light covers them)", "props"),
            $"propTemplate: {standIn}");
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

    private static KeyValuePair<YamlNode, YamlNode>? Pair(YamlMappingNode body, string key) =>
        body.Children.Where(c => Scalar(c.Key) == key).Select(c => (KeyValuePair<YamlNode, YamlNode>?)c).FirstOrDefault();

    /// <summary>
    /// A new key's line in an entry: after the last line of the last of <paramref name="after"/> it has, else above its first key, at
    /// its indentation. In a flow entry, ', key: value' after that value, else after its last one (in an empty one, the only key).
    /// </summary>
    private static (long Start, long End, string Text) NewKeyLine(string text, YamlMappingNode body, IReadOnlyList<string>? after, string line)
    {
        var anchor = body.Children.Where(c => after?.Contains(Scalar(c.Key)) == true).Select(c => c.Value).LastOrDefault();
        if (body.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
        {
            if (body.Children.Count == 0) return (body.Start.Index + 1, body.Start.Index + 1, $" {line} ");
            var at = ValueEnd(text, anchor ?? body.Children.Last().Value);
            return (at, at, $", {line}");
        }
        var first = body.Children.First().Key;
        var indented = $"{new string(' ', (int)first.Start.Column - 1)}{line}";
        if (anchor is null)
        {
            var above = text.LastIndexOf('\n', (int)first.Start.Index) + 1;
            return (above, above, indented + NewLine(text));
        }
        var end = text.IndexOf('\n', ValueEnd(text, anchor));
        return end < 0 ? (text.Length, text.Length, NewLine(text) + indented) : (end + 1, end + 1, indented + NewLine(text));
    }

    /// <summary>What removes a key: its lines in a block mapping; in a flow one, 'key: value' and the comma that separates it.</summary>
    private static (long Start, long End, string Text) Removal(string text, YamlMappingNode body, KeyValuePair<YamlNode, YamlNode> pair)
    {
        var end = ValueEnd(text, pair.Value);
        if (body.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
        {
            var next = body.Children.Keys.SkipWhile(k => k != pair.Key).Skip(1).FirstOrDefault();
            if (next is not null) return (pair.Key.Start.Index, next.Start.Index, "");
            var comma = text.LastIndexOf(',', (int)pair.Key.Start.Index);
            return (comma > body.Start.Index ? comma : pair.Key.Start.Index, end, "");
        }
        var lineEnd = text.IndexOf('\n', end);
        return (text.LastIndexOf('\n', (int)pair.Key.Start.Index) + 1, lineEnd < 0 ? text.Length : lineEnd + 1, "");
    }

    /// <summary>Splices edits into the text from its end, so earlier offsets stay valid; edits at one offset keep their order.</summary>
    private static string Apply(string text, List<(long Start, long End, string Text)> edits)
    {
        foreach (var (start, end, value) in edits.Select((e, i) => (Edit: e, Order: i)).OrderByDescending(e => e.Edit.Start).ThenByDescending(e => e.Order).Select(e => e.Edit))
        {
            text = text[..(int)start] + value + text[(int)end..];
        }
        return text;
    }

    /// <summary>
    /// Where a value ends in the text. A flow list's or mapping's End mark is the end of its opening token, so it ends at the closing
    /// bracket after its last item; a block one where its last value does.
    /// </summary>
    private static int ValueEnd(string text, YamlNode node) => node switch
    {
        YamlSequenceNode { Style: YamlDotNet.Core.Events.SequenceStyle.Flow } s => text.IndexOf(']', s.Children.Count > 0 ? ValueEnd(text, s.Children[^1]) : (int)s.Start.Index) + 1,
        YamlMappingNode { Style: YamlDotNet.Core.Events.MappingStyle.Flow } m => text.IndexOf('}', m.Children.Count > 0 ? ValueEnd(text, m.Children.Last().Value) : (int)m.Start.Index) + 1,
        YamlSequenceNode { Children.Count: > 0 } s => ValueEnd(text, s.Children[^1]),
        YamlMappingNode { Children.Count: > 0 } m => ValueEnd(text, m.Children.Last().Value),
        _ => (int)node.End.Index,
    };

    /// <summary>The last value written inside a node: a mapping or list ends where its last child does.</summary>
    private static YamlNode LastLeaf(YamlNode node) => node switch
    {
        YamlMappingNode { Style: not YamlDotNet.Core.Events.MappingStyle.Flow, Children.Count: > 0 } m => LastLeaf(m.Children.Last().Value),
        YamlSequenceNode { Style: not YamlDotNet.Core.Events.SequenceStyle.Flow, Children.Count: > 0 } s => LastLeaf(s.Children[^1]),
        _ => node,
    };

    private static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
}

/// <summary>
/// Rotations as the in-game editor saves them (Unreal units, 65536 = 360°) and the YAML writes them (degrees), compared as
/// orientations: (180, 0, 0) and (0, 180, 180) turn an actor the same way, so do yaw 200 and -160.
/// </summary>
internal static class Rotations
{
    /// <summary>Degrees within which each axis of two rotations must agree for them to be one (an Unreal unit is 0.0055°).</summary>
    internal const double Tolerance = 0.02;

    internal static double Degrees(double units) => units * 360 / 65536;

    internal static double Units(double degrees) => degrees * 65536 / 360;

    /// <summary>Degrees in (-180, 180], to 2 decimals.</summary>
    internal static double Angle(double degrees)
    {
        var d = Wrap(Math.Round(Wrap(degrees), 2));
        return d == 0 ? 0 : d;
    }

    internal static bool SameYaw(double a, double b) => Math.Abs(Wrap(a - b)) <= Tolerance;

    /// <summary>Whether two rotations (Unreal units) turn an actor the same way: the axes of their rotation matrices within <see cref="Tolerance"/>.</summary>
    internal static bool Same(double[] a, double[] b)
    {
        var (ax, ay, az) = Axes(a);
        var (bx, by, bz) = Axes(b);
        return Between(ax, bx) <= Tolerance && Between(ay, by) <= Tolerance && Between(az, bz) <= Tolerance;
    }

    /// <summary>The yaw (degrees, (-180, 180]) that turns an actor the same way with this pitch and roll (Unreal units); null when none does.</summary>
    internal static double? YawWith(double[] units, double pitch, double roll)
    {
        var (x, _, _) = Axes(units);
        foreach (var yaw in new[] { Degrees(units[1]), Math.Atan2(x.Y, x.X) * 180 / Math.PI })
        {
            if (Same(units, [pitch, Units(yaw), roll])) return Angle(yaw);
        }
        return null;
    }

    private static double Wrap(double degrees)
    {
        var d = degrees % 360;
        return d > 180 ? d - 360 : d <= -180 ? d + 360 : d;
    }

    /// <summary>The X, Y and Z axes of UE3's FRotationMatrix for pitch, yaw, roll in Unreal units.</summary>
    internal static ((double X, double Y, double Z) X, (double X, double Y, double Z) Y, (double X, double Y, double Z) Z) Axes(double[] units)
    {
        static double Radians(double u) => u * Math.PI * 2 / 65536;
        var (sp, cp) = Math.SinCos(Radians(units[0]));
        var (sy, cy) = Math.SinCos(Radians(units[1]));
        var (sr, cr) = Math.SinCos(Radians(units[2]));
        return ((cp * cy, cp * sy, sp),
            (sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp),
            (-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp));
    }

    /// <summary>The angle between two unit vectors, in degrees (atan2 of the cross and dot products: exact for small angles).</summary>
    private static double Between((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        var (cx, cy, cz) = (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), a.X * b.X + a.Y * b.Y + a.Z * b.Z) * 180 / Math.PI;
    }
}
