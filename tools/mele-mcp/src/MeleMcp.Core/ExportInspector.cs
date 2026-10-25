using System.Globalization;
using System.Text;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

public sealed record ExportRow(int UIndex, string ClassName, string InstancedFullPath);

public sealed record ExportSearchResult(string Path, int TotalMatches, IReadOnlyList<ExportRow> Exports);

public sealed record ExportReference(ExportRow Referencer, IReadOnlyList<string> Via);

public sealed record ExportReferences(ExportRow Target, IReadOnlyList<ExportReference> ReferencedBy);

public static class ExportInspector
{
    public static ExportSearchResult Search(MEGame game, string fileOrPath, string? className, string? nameContains, int limit)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);

        var matches = pcc.Exports
            .Where(e => className is null || e.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
            .Where(e => nameContains is null || e.InstancedFullPath.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new ExportSearchResult(pcc.FilePath, matches.Count, matches.Take(limit).Select(Row).ToList());
    }

    /// <summary>Properties of one export as an indented text tree; object references are resolved to class and path.</summary>
    public static string GetProperties(MEGame game, string fileOrPath, string export)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        var exp = Resolve(pcc, export);

        var sb = new StringBuilder();
        sb.Append('#').Append(exp.UIndex).Append(' ').Append(exp.ClassName).Append(' ').AppendLine(exp.InstancedFullPath);
        foreach (var prop in exp.GetProperties())
        {
            Write(sb, pcc, Label(prop), prop, depth: 1);
        }
        return sb.ToString();
    }

    public static ExportReferences GetReferencers(MEGame game, string fileOrPath, string export)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        var exp = Resolve(pcc, export);

        var referencers = exp.GetEntriesThatReferenceThisOne()
            .Select(kv => new ExportReference(Row(kv.Key), kv.Value))
            .OrderBy(r => r.Referencer.UIndex)
            .ToList();
        return new ExportReferences(Row(exp), referencers);
    }

    /// <summary>Accepts a UIndex ("123"), an instanced full path, or an object name that is unique in the package.</summary>
    internal static ExportEntry Resolve(IMEPackage pcc, string export)
    {
        if (int.TryParse(export.TrimStart('#'), out var uIndex))
        {
            return pcc.GetUExport(uIndex) ?? throw new ArgumentException($"No export with UIndex {uIndex} (exports are 1..{pcc.ExportCount}).");
        }

        var byPath = pcc.Exports.Where(e => e.InstancedFullPath.Equals(export, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byPath.Count == 1) return byPath[0];

        var byName = pcc.Exports.Where(e => e.ObjectName.Instanced.Equals(export, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new ArgumentException($"No export named '{export}'. Use pkg_search to find it."),
            _ => throw new ArgumentException($"'{export}' is ambiguous: {string.Join(", ", byName.Select(e => $"#{e.UIndex} {e.InstancedFullPath}"))}"),
        };
    }

    private static ExportRow Row(IEntry e) => new(e.UIndex, e.ClassName, e.InstancedFullPath);

    private static string Label(Property p) =>
        p.StaticArrayIndex > 0 ? $"{p.Name.Instanced}[{p.StaticArrayIndex}]" : p.Name.Instanced;

    private static void Write(StringBuilder sb, IMEPackage pcc, string label, Property prop, int depth)
    {
        if (prop is NoneProperty) return;

        sb.Append(' ', depth * 2).Append(label);
        switch (prop)
        {
            case StructProperty s:
                sb.Append(" (").Append(s.StructType).AppendLine(")");
                foreach (var child in s.Properties)
                {
                    Write(sb, pcc, Label(child), child, depth + 1);
                }
                return;
            case ImmutableByteArrayProperty bytes:
                sb.Append(" = <").Append(bytes.Bytes.Length).AppendLine(" bytes>");
                return;
            case ArrayPropertyBase array:
                sb.Append(" (array, ").Append(array.Count).AppendLine(")");
                for (var i = 0; i < array.Count; i++)
                {
                    Write(sb, pcc, $"[{i}]", array[i], depth + 1);
                }
                return;
        }

        sb.Append(" = ").AppendLine(FormatScalar(pcc, prop));
    }

    /// <summary>One-line value text for a non-container property.</summary>
    internal static string FormatScalar(IMEPackage pcc, Property prop) =>
        prop switch
        {
            ObjectProperty o => DescribeEntry(pcc, o.Value),
            NameProperty n => n.Value.Instanced,
            EnumProperty e => $"{e.EnumType.Instanced}.{e.Value.Instanced}",
            ByteProperty b => b.Value.ToString(CultureInfo.InvariantCulture),
            BioMask4Property m => m.Value.ToString(CultureInfo.InvariantCulture),
            IntProperty i => i.Value.ToString(CultureInfo.InvariantCulture),
            FloatProperty f => f.Value.ToString("R", CultureInfo.InvariantCulture),
            BoolProperty b => b.Value ? "True" : "False",
            StrProperty s => $"\"{s.Value}\"",
            StringRefProperty r => $"${r.Value}",
            DelegateProperty d => $"{DescribeEntry(pcc, d.Value.ContainingObjectUIndex)}.{d.Value.FunctionName.Instanced}",
            _ => $"<{prop.PropType}>",
        };

    private static string DescribeEntry(IMEPackage pcc, int uIndex)
    {
        if (uIndex == 0) return "None";
        var entry = pcc.GetEntry(uIndex);
        return entry is null ? $"<invalid #{uIndex}>" : $"{entry.ClassName}'{entry.InstancedFullPath}' (#{uIndex})";
    }
}
