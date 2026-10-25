using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME1;

namespace MeleMcp.Core;

public sealed record TlkString(int Id, string Text);

public sealed record TlkFileStrings(ExportRow TalkFile, int StringCount, IReadOnlyList<TlkString> Strings);

public sealed record TlkStringChange(int Id, string? OldText, string NewText);

public sealed record TlkFileEdit(ExportRow TalkFile, IReadOnlyList<TlkStringChange> Changes);

public sealed record TlkEdits(string Path, IReadOnlyList<TlkFileEdit> Files, string BackupPath);

/// <summary>Game 1 strings: <c>BioTlkFile</c> exports inside packages (mod GlobalTlk files, conversation TLK sets).</summary>
public static class TalkFiles
{
    public static IReadOnlyList<TlkFileStrings> Get(MEGame game, string fileOrPath, string? export, int? id, string? textContains, int limit)
    {
        RequireGame1(game);
        using var pcc = PackageInspector.Open(game, fileOrPath);
        return Targets(pcc, export).Select(e =>
        {
            var tlk = new ME1TalkFile(e);
            var matches = tlk.StringRefs
                .Where(s => (id is null || s.StringID == id)
                            && (textContains is null || (s.Data?.Contains(textContains, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Take(limit)
                .Select(s => new TlkString(s.StringID, s.Data ?? ""))
                .ToList();
            return new TlkFileStrings(ExportInspector.Row(e), tlk.StringRefs.Count, matches);
        }).ToList();
    }

    /// <summary>Adds or replaces strings in every BioTlkFile of the package (male and female copies), or in one export.</summary>
    public static TlkEdits Set(MEGame game, string fileOrPath, IReadOnlyList<TlkString> strings, string? export)
    {
        RequireGame1(game);
        if (strings.Count == 0) throw new ArgumentException("No strings given.");

        var ((files, path), backup) = PackageWriter.Edit(game, fileOrPath, pcc =>
        {
            var edits = new List<TlkFileEdit>();
            foreach (var target in Targets(pcc, export))
            {
                var tlk = new ME1TalkFile(target);
                var changes = strings.Select(s =>
                {
                    var old = tlk.StringRefs.Find(r => r.StringID == s.Id)?.Data;
                    tlk.ReplaceString(s.Id, s.Text, addIfNotFound: true);
                    return new TlkStringChange(s.Id, old, s.Text);
                }).ToList();

                var huffman = new HuffmanCompression();
                huffman.LoadInputData(tlk.StringRefs);
                huffman.SerializeTalkfileToExport(target);
                edits.Add(new TlkFileEdit(ExportInspector.Row(target), changes));
            }
            return (edits, pcc.FilePath);
        });

        return new TlkEdits(path, files, backup);
    }

    private static List<ExportEntry> Targets(IMEPackage pcc, string? export)
    {
        var targets = export is null
            ? pcc.Exports.Where(e => e.ClassName == "BioTlkFile").ToList()
            : [ExportInspector.Resolve(pcc, export)];
        if (targets.Count == 0) throw new ArgumentException($"{pcc.FilePath} has no BioTlkFile export.");
        if (targets.FirstOrDefault(e => e.ClassName != "BioTlkFile") is { } wrong)
        {
            throw new ArgumentException($"#{wrong.UIndex} is a {wrong.ClassName}, not a BioTlkFile.");
        }
        return targets;
    }

    private static void RequireGame1(MEGame game)
    {
        if (!game.IsGame1()) throw new ArgumentException("Package TLKs are Game 1 only; LE2/LE3 strings live in .tlk files.");
    }
}
