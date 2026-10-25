using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MeleMcp.Core;

/// <param name="Path">Sound cue path stored with the line.</param>
/// <param name="SoundCueIndex">Index into the set's ReferencedSoundCues (-1 = none).</param>
/// <param name="Length">Time of the last key, in seconds.</param>
public sealed record FaceFxLineInfo(int Index, string Name, string? Id, string? Path, int SoundCueIndex, int Curves, int Keys, float Length, float FadeIn, float FadeOut);

public sealed record FaceFxSetLines(ExportRow AnimSet, int Version, int NameCount, IReadOnlyList<FaceFxLineInfo> Lines);

public sealed record FaceFxLineCopy(string Source, string Name);

public sealed record FaceFxCopyResult(string Path, ExportRow AnimSet, IReadOnlyList<FaceFxLineInfo> Created, IReadOnlyList<string> Replaced, string BackupPath);

/// <summary>FaceFX lip-sync animations: the lines inside a FaceFXAnimSet's binary.</summary>
public static class FaceFxSets
{
    public static FaceFxSetLines Get(MEGame game, string fileOrPath, string export)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        var set = ExportInspector.Resolve(pcc, export);
        if (set.ClassName != "FaceFXAnimSet") throw new ArgumentException($"#{set.UIndex} is a {set.ClassName}, not a FaceFXAnimSet.");

        var bin = ObjectBinary.From<FaceFXAnimSet>(set);
        return new FaceFxSetLines(ExportInspector.Row(set), bin.Version, bin.Names.Count, bin.Lines.Select(Info).ToList());
    }

    /// <summary>
    /// Copies lines under new names, e.g. FXA_152036 → FXA_74210010 so a node whose VO track has strref 74210010 gets
    /// the template's lip movement. Without <paramref name="withSound"/> the copy drops its sound cue (index -1, empty
    /// path and id), so the template's voice does not play over the new text. A line that already has the new name is replaced.
    /// </summary>
    public static FaceFxCopyResult CopyLines(MEGame game, string fileOrPath, string export, IReadOnlyList<FaceFxLineCopy> copies, bool withSound)
    {
        if (copies.Count == 0) throw new ArgumentException("No lines given.");

        var ((row, created, replaced, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var set = ExportInspector.Resolve(pcc, export);
            if (set.ClassName != "FaceFXAnimSet") throw new ArgumentException($"#{set.UIndex} is a {set.ClassName}, not a FaceFXAnimSet.");
            var (created, replaced, _) = CopyIn(set, copies, withSound, skipMissing: false);
            return (ExportInspector.Row(set), created, replaced, pcc.FilePath);
        });

        return new FaceFxCopyResult(path, row, created, replaced, backup);
    }

    /// <summary>Copies lines inside an already open set. A missing source line throws, or is reported when <paramref name="skipMissing"/>.</summary>
    internal static (List<FaceFxLineInfo> Created, List<string> Replaced, List<string> Missing) CopyIn(
        ExportEntry set, IReadOnlyList<FaceFxLineCopy> copies, bool withSound, bool skipMissing)
    {
        var bin = ObjectBinary.From<FaceFXAnimSet>(set);
        var created = new List<FaceFXLine>();
        var replaced = new List<string>();
        var missing = new List<string>();
        foreach (var copy in copies)
        {
            var source = bin.Lines.FirstOrDefault(l => l.NameAsString == copy.Source);
            if (source is null)
            {
                if (skipMissing)
                {
                    missing.Add(copy.Source);
                    continue;
                }
                throw new ArgumentException($"#{set.UIndex} has no line '{copy.Source}'. It has: {string.Join(", ", bin.Lines.Select(l => l.NameAsString))}.");
            }
            if (bin.Lines.RemoveAll(l => l.NameAsString == copy.Name) > 0) replaced.Add(copy.Name);

            var line = source.Clone();
            var nameIndex = bin.Names.IndexOf(copy.Name);
            if (nameIndex < 0)
            {
                bin.Names.Add(copy.Name);
                nameIndex = bin.Names.Count - 1;
            }
            line.NameIndex = nameIndex;
            line.NameAsString = copy.Name;
            if (!withSound)
            {
                line.Index = -1;
                line.Path = "";
                line.ID = "";
            }
            bin.Lines.Add(line);
            created.Add(line);
        }

        if (created.Count > 0) set.WriteBinary(bin);
        return (created.Select(l => Info(l, bin.Lines.IndexOf(l))).ToList(), replaced, missing);
    }

    private static FaceFxLineInfo Info(FaceFXLine l, int i) => new(
        i, l.NameAsString, l.ID, l.Path, l.Index, l.AnimationNames.Count, l.Points.Count,
        l.Points.Count == 0 ? 0 : l.Points.Max(p => p.time), l.FadeInTime, l.FadeOutTime);
}
