using System.Globalization;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core;

public sealed record PropertyChange(string Export, string PropertyPath, string Value);

/// <param name="Note">What the tool did to the value given, or could not check; null when it stored it as given.</param>
public sealed record AppliedChange(int UIndex, string Export, string PropertyPath, string OldValue, string NewValue, string? Note = null);

public sealed record PropertyEdits(string Path, IReadOnlyList<AppliedChange> Changes, string BackupPath);

public static partial class PropertyEditor
{
    [GeneratedRegex(@"^(?<name>[^.\[\]]+)(\[(?<index>\d+)\])?$")]
    private static partial Regex Segment();

    /// <summary>
    /// Sets leaf properties addressed by paths such as <c>VectorParameterValues[11].ParameterValue.G</c>, then saves once.
    /// All changes are validated before anything is written: one bad path leaves the file untouched.
    /// </summary>
    public static PropertyEdits SetProperties(MEGame game, string fileOrPath, IReadOnlyList<PropertyChange> changes)
    {
        if (changes.Count == 0) throw new ArgumentException("No changes given.");

        var (applied, backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var touched = new Dictionary<ExportEntry, PropertyCollection>();
            var results = new List<AppliedChange>();
            foreach (var change in changes)
            {
                var exp = ExportInspector.Resolve(pcc, change.Export);
                if (!touched.TryGetValue(exp, out var props)) touched[exp] = props = exp.GetProperties();

                var leaf = Navigate(pcc, exp, props, change.PropertyPath);
                var oldValue = ExportInspector.FormatScalar(pcc, leaf);
                var note = Assign(pcc, leaf, change.Value);
                results.Add(new AppliedChange(exp.UIndex, exp.InstancedFullPath, change.PropertyPath, oldValue, ExportInspector.FormatScalar(pcc, leaf), note));
            }
            foreach (var (exp, props) in touched) exp.WriteProperties(props);
            return (results, pcc.FilePath);
        });

        return new PropertyEdits(applied.FilePath, applied.results, backup);
    }

    private static Property Navigate(IMEPackage pcc, ExportEntry exp, PropertyCollection props, string propertyPath)
    {
        var scope = props;
        var container = exp.ClassName;
        Property? current = null;
        var segments = propertyPath.Split('.');
        for (var s = 0; s < segments.Length; s++)
        {
            var m = Segment().Match(segments[s]);
            if (!m.Success) throw new ArgumentException($"Bad path segment '{segments[s]}'. Use Name, Name[index], separated by dots.");

            var name = m.Groups["name"].Value;
            current = scope.FirstOrDefault(p => p.Name.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? AddFromClassInfo(pcc, scope, name, container, s == 0 ? exp : null)
                ?? throw new ArgumentException($"No property '{name}' here. Available: {string.Join(", ", scope.Where(p => p is not NoneProperty).Select(p => p.Name.Instanced))}");

            if (m.Groups["index"].Success)
            {
                if (current is not ArrayPropertyBase array) throw new ArgumentException($"'{name}' is not an array.");
                var index = int.Parse(m.Groups["index"].Value, CultureInfo.InvariantCulture);
                if (index >= array.Count) throw new ArgumentException($"'{name}' has {array.Count} elements; index {index} is out of range.");
                current = array[index];
            }

            if (s < segments.Length - 1)
            {
                if (current is not StructProperty st) throw new ArgumentException($"'{segments[s]}' is a {current.PropType}, not a struct, so the path cannot continue.");
                scope = st.Properties;
                container = st.StructType;
            }
        }
        return current!;
    }

    /// <summary>
    /// A property still at its class default is not serialized, so a fresh object (kismet_add) lacks e.g. Originator or ObjValue.
    /// Creates it with its default value when the class or struct declares it.
    /// </summary>
    private static Property? AddFromClassInfo(IMEPackage pcc, PropertyCollection scope, string name, string container, ExportEntry? exp)
    {
        var info = GlobalUnrealObjectInfo.GetPropertyInfo(pcc.Game, name, container, containingExport: exp);
        if (info is null) return null;
        var prop = GlobalUnrealObjectInfo.GetDefaultProperty(pcc.Game, name, info, null, pcc);
        if (prop is null) return null;
        scope.AddOrReplaceProp(prop);
        return prop;
    }

    internal static string? Assign(IMEPackage pcc, Property leaf, string value)
    {
        var inv = CultureInfo.InvariantCulture;
        string? note = null;
        switch (leaf)
        {
            case FloatProperty f: f.Value = NumberText.Float(value); break;
            case IntProperty i: i.Value = int.Parse(value, inv); break;
            case BoolProperty b: b.Value = bool.Parse(value); break;
            case ByteProperty b: b.Value = byte.Parse(value, inv); break;
            case StrProperty s: (s.Value, note) = Unquoted(value); break;
            case NameProperty n: n.Value = NameReference.FromInstancedString(value); break;
            case EnumProperty e: (e.Value, note) = EnumValue(pcc.Game, e.EnumType.Instanced, value); break;
            case StringRefProperty r: r.Value = int.Parse(value.TrimStart('$'), inv); break;
            case ObjectProperty o:
                var uIndex = int.Parse(value.TrimStart('#'), inv);
                if (uIndex != 0 && pcc.GetEntry(uIndex) is null) throw new ArgumentException($"No entry #{uIndex} in this package.");
                o.Value = uIndex;
                break;
            default:
                throw new ArgumentException($"Cannot set a {leaf.PropType} directly. Address one of its fields, e.g. ParameterValue.G.");
        }
        return note;
    }

    /// <summary>
    /// An enum value given by its name, or as export_get prints it (EnumType.VALUE), as the enum's own name for it: the letter case
    /// given does not matter. A name the enum lacks is refused with the names it has, before anything is written. An enum that
    /// LEC's table lacks (a mod's own) cannot be checked: the name is stored as given, and the note says so.
    /// </summary>
    private static (NameReference Value, string? Note) EnumValue(MEGame game, string enumType, string text)
    {
        var name = text.Trim();
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            var given = name[..dot];
            if (!given.Equals(enumType, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"'{text}': this property is a {enumType}, not a {given}.");
            name = name[(dot + 1)..];
        }
        if (name.Length == 0) throw new ArgumentException($"'{text}' names no value of {enumType}.");

        var values = GlobalUnrealObjectInfo.GetEnumValues(game, enumType, includeNone: true);
        if (values is null)
        {
            return (NameReference.FromInstancedString(name), $"{game} has no enum {enumType} in LEC's table (a mod's own enum?): '{name}' was stored without a check.");
        }
        // FindIndex, not FirstOrDefault: NameReference is a struct, and its default has a null name.
        var index = values.FindIndex(v => v.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new ArgumentException($"{enumType} has no value '{name}'. Use one of: {string.Join(", ", values.Select(v => v.Instanced))}.");
        return (values[index], null);
    }

    /// <summary>
    /// A string as given, or without the one pair of double quotes export_get prints it in ("text"), so that its output can be
    /// given back. Text that really starts and ends with a quote is given with the pair doubled: ""text"".
    /// </summary>
    private static (string Value, string? Note) Unquoted(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? (text[1..^1], "Stored without the double quotes around it: export_get prints them, and they are not part of the string. Double the pair to keep one.")
            : (text, null);
}
