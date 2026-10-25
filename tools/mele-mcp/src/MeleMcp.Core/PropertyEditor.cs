using System.Globalization;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

public sealed record PropertyEdit(string Path, int UIndex, string Export, string PropertyPath, string OldValue, string NewValue, string BackupPath);

public static partial class PropertyEditor
{
    /// <summary>Backups go outside the mod folder, otherwise M3 would install them into the game.</summary>
    public const string BackupDirEnvVar = "MELE_BACKUP_DIR";
    private const string DefaultBackupDir = @"D:\MELE\backups\mele-mcp";

    [GeneratedRegex(@"^(?<name>[^.\[\]]+)(\[(?<index>\d+)\])?$")]
    private static partial Regex Segment();

    /// <summary>
    /// Sets one leaf property addressed by a path such as <c>VectorParameterValues[11].ParameterValue.G</c>,
    /// after copying the package to the backup folder.
    /// </summary>
    public static PropertyEdit SetProperty(MEGame game, string fileOrPath, string export, string propertyPath, string value)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        EnsureWritable(pcc);
        var exp = ExportInspector.Resolve(pcc, export);

        var props = exp.GetProperties();
        var leaf = Navigate(props, propertyPath);
        var oldValue = ExportInspector.FormatScalar(pcc, leaf);
        Assign(pcc, leaf, value);

        var backup = Backup(pcc.FilePath);
        exp.WriteProperties(props);
        pcc.Save();

        return new PropertyEdit(pcc.FilePath, exp.UIndex, exp.InstancedFullPath, propertyPath, oldValue,
            ExportInspector.FormatScalar(pcc, leaf), backup);
    }

    /// <summary>
    /// The game install is a deploy target, never an edit target: a file name can resolve to the base game or to another
    /// mod's installed copy (e.g. LE1CP overrides many level files). Edits go to the mod's source folder in the M3 library.
    /// </summary>
    private static void EnsureWritable(IMEPackage pcc)
    {
        var gameRoot = MEDirectories.GetDefaultGamePath(pcc.Game);
        if (gameRoot != null && pcc.FilePath.StartsWith(Path.GetFullPath(gameRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to modify a file inside the game install ({pcc.FilePath}). Edit the mod's copy in the M3 library, then install or copy it.");
        }
    }

    private static Property Navigate(PropertyCollection props, string propertyPath)
    {
        var scope = props;
        Property? current = null;
        var segments = propertyPath.Split('.');
        for (var s = 0; s < segments.Length; s++)
        {
            var m = Segment().Match(segments[s]);
            if (!m.Success) throw new ArgumentException($"Bad path segment '{segments[s]}'. Use Name, Name[index], separated by dots.");

            var name = m.Groups["name"].Value;
            current = scope.FirstOrDefault(p => p.Name.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase))
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
                scope = current is StructProperty st ? st.Properties
                    : throw new ArgumentException($"'{segments[s]}' is a {current.PropType}, not a struct, so the path cannot continue.");
            }
        }
        return current!;
    }

    private static void Assign(IMEPackage pcc, Property leaf, string value)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (leaf)
        {
            case FloatProperty f: f.Value = float.Parse(value, inv); break;
            case IntProperty i: i.Value = int.Parse(value, inv); break;
            case BoolProperty b: b.Value = bool.Parse(value); break;
            case ByteProperty b: b.Value = byte.Parse(value, inv); break;
            case StrProperty s: s.Value = value; break;
            case NameProperty n: n.Value = NameReference.FromInstancedString(value); break;
            case EnumProperty e: e.Value = NameReference.FromInstancedString(value); break;
            case StringRefProperty r: r.Value = int.Parse(value.TrimStart('$'), inv); break;
            case ObjectProperty o:
                var uIndex = int.Parse(value.TrimStart('#'), inv);
                if (uIndex != 0 && pcc.GetEntry(uIndex) is null) throw new ArgumentException($"No entry #{uIndex} in this package.");
                o.Value = uIndex;
                break;
            default:
                throw new ArgumentException($"Cannot set a {leaf.PropType} directly. Address one of its fields, e.g. ParameterValue.G.");
        }
    }

    private static string Backup(string path)
    {
        var dir = Environment.GetEnvironmentVariable(BackupDirEnvVar);
        dir = string.IsNullOrWhiteSpace(dir) ? DefaultBackupDir : GamePaths.ToWindowsPath(dir);
        Directory.CreateDirectory(dir);
        var target = System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{System.IO.Path.GetFileName(path)}");
        File.Copy(path, target);
        return target;
    }
}
