using System.Security.Cryptography;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

/// <param name="Property">Flattened path, e.g. Location.X, StreamingStates[0].InChunkName, or a subobject's: StaticMeshComponent[0].Materials[0].</param>
/// <param name="Ours">The value in the first export, or "(not set)": not serialized, so the class default.</param>
public sealed record PropertyDifference(string Property, string Ours, string Theirs);

/// <param name="Same">Flattened values equal on both sides.</param>
public sealed record ExportDiff(string Ours, string Theirs, int Same, int Different, IReadOnlyList<PropertyDifference> Differences);

/// <summary>
/// Compares two exports (e.g. our actor and the vanilla one it was cloned from) property by property, header and subobjects
/// included. References compare by class and path, not UIndex, and a reference into the export itself reads "&lt;self&gt;", so
/// clones in different files with different names compare equal where they match.
/// </summary>
public static class ExportDiffer
{
    private const string Unset = "(not set)";

    /// <param name="vsExport">The export in <paramref name="vsPackage"/>; omitted, the one with our export's instanced full path.</param>
    /// <param name="children">Also compares subobjects (exports inside them, e.g. components), paired by class and order.</param>
    /// <param name="ignore">Property names left out wherever they occur, e.g. Location, Rotation.</param>
    public static ExportDiff Diff(MEGame game, string package, string export, string vsPackage, string? vsExport,
                                  bool children, IReadOnlyCollection<string> ignore, int limit)
    {
        using var ours = PackageInspector.Open(game, package);
        using var theirs = PackageInspector.Open(game, vsPackage);
        var a = ExportInspector.Resolve(ours, export);
        var b = ExportInspector.Resolve(theirs, string.IsNullOrWhiteSpace(vsExport) ? a.InstancedFullPath : vsExport);

        var left = Flatten(a, children, ignore);
        var right = Flatten(b, children, ignore);
        var rightValues = right.ToDictionary(kv => kv.Key, kv => kv.Value);
        var leftKeys = left.Select(kv => kv.Key).ToHashSet();

        var differences = new List<PropertyDifference>();
        var same = 0;
        foreach (var (key, value) in left)
        {
            var other = rightValues.GetValueOrDefault(key, Unset);
            if (value == other) same++;
            else differences.Add(new PropertyDifference(key, value, other));
        }
        differences.AddRange(right.Where(kv => !leftKeys.Contains(kv.Key)).Select(kv => new PropertyDifference(kv.Key, Unset, kv.Value)));

        return new ExportDiff($"{ours.FilePath} #{a.UIndex} {a.ClassName} {a.InstancedFullPath}",
                              $"{theirs.FilePath} #{b.UIndex} {b.ClassName} {b.InstancedFullPath}",
                              same, differences.Count, differences.Take(Math.Max(1, limit)).ToList());
    }

    /// <summary>The export's header, properties and (optionally) subobjects as path → value, in file order.</summary>
    private static List<KeyValuePair<string, string>> Flatten(ExportEntry root, bool children, IReadOnlyCollection<string> ignore)
    {
        var values = new List<KeyValuePair<string, string>>();
        AddExport(values, "", root, root, ignore);
        if (!children) return values;

        foreach (var group in root.FileRef.Exports.Where(e => e.idxLink == root.UIndex).GroupBy(e => e.ClassName))
        {
            var index = 0;
            foreach (var child in group) AddExport(values, $"{group.Key}[{index++}].", child, root, ignore);
        }
        return values;
    }

    private static void AddExport(List<KeyValuePair<string, string>> values, string prefix, ExportEntry exp, ExportEntry root, IReadOnlyCollection<string> ignore)
    {
        values.Add(new($"{prefix}(class)", exp.ClassName));
        values.Add(new($"{prefix}(archetype)", Reference(exp.FileRef, exp.idxArchetype, root)));
        values.Add(new($"{prefix}(flags)", exp.ObjectFlags.ToString()));
        var binary = exp.DataSize - exp.propsEnd();
        if (binary > 0) values.Add(new($"{prefix}(binary)", $"{binary} bytes, {Hash(exp.DataReadOnly[exp.propsEnd()..])}"));

        foreach (var prop in exp.GetProperties())
        {
            if (!ignore.Contains(prop.Name.Instanced, StringComparer.OrdinalIgnoreCase))
                AddProperty(values, prefix + ExportInspector.Label(prop), prop, exp.FileRef, root);
        }
    }

    private static void AddProperty(List<KeyValuePair<string, string>> values, string path, Property prop, IMEPackage pcc, ExportEntry root)
    {
        switch (prop)
        {
            case NoneProperty:
                return;
            case StructProperty s:
                foreach (var child in s.Properties) AddProperty(values, $"{path}.{ExportInspector.Label(child)}", child, pcc, root);
                return;
            case ImmutableByteArrayProperty bytes:
                values.Add(new(path, $"{bytes.Bytes.Length} bytes, {Hash(bytes.Bytes)}"));
                return;
            case ArrayPropertyBase array:
                if (array.Count == 0) values.Add(new(path, "(empty)"));
                for (var i = 0; i < array.Count; i++) AddProperty(values, $"{path}[{i}]", array[i], pcc, root);
                return;
            case ObjectProperty o:
                values.Add(new(path, Reference(pcc, o.Value, root)));
                return;
            default:
                values.Add(new(path, ExportInspector.FormatScalar(pcc, prop)));
                return;
        }
    }

    /// <summary>Class'Path' without the UIndex, the root's own path replaced by &lt;self&gt;.</summary>
    private static string Reference(IMEPackage pcc, int uIndex, ExportEntry root)
    {
        if (uIndex == 0) return "None";
        if (pcc.GetEntry(uIndex) is not { } entry) return $"<invalid #{uIndex}>";
        var path = entry.InstancedFullPath;
        if (path == root.InstancedFullPath) path = "<self>";
        else if (path.StartsWith(root.InstancedFullPath + ".", StringComparison.Ordinal)) path = "<self>" + path[root.InstancedFullPath.Length..];
        return $"{entry.ClassName}'{path}'";
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..8];
}
