using System.Globalization;
using System.Text;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.UnrealScript;
using LegendaryExplorerCore.UnrealScript.Analysis.Visitors;
using LegendaryExplorerCore.UnrealScript.Language.Tree;

namespace MeleMcp.Core;

/// <summary>An export left out of the text, and why (it failed to decompile, or another export has its path and class).</summary>
public sealed record SkippedExport(int UIndex, string ClassName, string Path, string Why);

/// <summary>
/// The text compiled back into an unsaved copy of the package: Same when every export decompiles to the same text again,
/// BytesSame when its serialized properties are byte for byte the original ones.
/// </summary>
public sealed record TextRoundTrip(
    bool Same,
    IReadOnlyList<string> Errors,
    int ChangedCount,
    IReadOnlyList<string> Changed,
    bool BytesSame,
    int BytesDifferCount,
    IReadOnlyList<string> BytesDiffer);

public sealed record PackageTextResult(
    string Package,
    string Game,
    int Exports,
    int ScriptExports,
    int ClassDefaults,
    IReadOnlyList<SkippedExport> Skipped,
    IReadOnlyList<ClassCount> BinaryNotInText,
    int Chars,
    string? OutPath,
    string? WslPath,
    TextRoundTrip? RoundTrip,
    string? Text);

public static class PackageText
{
    /// <summary>First line of every file pkg_to_text writes: it only overwrites files that start with it.</summary>
    public const string Header = "// pkg_to_text";

    private static readonly MEGame[] Games = [MEGame.LE1, MEGame.LE2, MEGame.LE3];

    /// <summary>
    /// The properties of a package's exports as LEC bulk-properties text (Begin Object Class=… Name='path' … End Object, the
    /// format LEC's CompileBulkPropertiesFile reads back). Like LEC's DecompileBulkProps, it leaves out script exports
    /// (export_decompile shows them), class defaults and trash; unlike it, one export that fails is skipped, not the whole file.
    /// Binary data after the properties (meshes, textures, sounds...) is not in the text: BinaryNotInText counts it by class.
    /// </summary>
    public static PackageTextResult Dump(MEGame game, string fileOrPath, string? className, string? pathContains, string? outPath,
        bool uIndexComments, bool roundTrip, int maxChars)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        using var cache = new PackageCache();
        var usop = new UnrealScriptOptionsPackage { Cache = cache };
        using var lib = InitializedLib(pcc, usop);

        int scriptExports = 0, classDefaults = 0;
        var skipped = new List<SkippedExport>();
        var binary = new Dictionary<string, int>(StringComparer.Ordinal);
        var written = new List<(ExportEntry Export, string Text)>();

        foreach (var export in pcc.Exports)
        {
            if (className is not null && !export.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase)) continue;
            if (pathContains is not null && !export.InstancedFullPath.Contains(pathContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (export.IsTrash()) continue;
            if (export.IsScriptExport()) { scriptExports++; continue; }
            if (export.IsInDefaultsTree()) { classDefaults++; continue; }

            // CompileBulkPropertiesFile finds each block's object with FindEntry(path, class). When another entry has the same path
            // and class, that may be the other one (the lookup table keeps the entry its tree walk visits last), so only the entry
            // FindEntry returns gets a block: the text never lands in a different export, and an import twin would fail the compile.
            if (pcc.FindEntry(export.InstancedFullPath, export.ClassName) is var twin && twin != export)
            {
                skipped.Add(new(export.UIndex, export.ClassName, export.InstancedFullPath, twin switch
                {
                    ExportEntry other => $"export #{other.UIndex} has the same path and class, and LEC's compiler finds that one",
                    ImportEntry => "an import has the same path and class, and LEC's compiler finds that one",
                    _ => "LEC's compiler does not find it by path and class",
                }));
                continue;
            }

            var (text, error) = Decompile(export, lib, usop);
            if (text is null)
            {
                skipped.Add(new(export.UIndex, export.ClassName, export.InstancedFullPath, error!));
                continue;
            }
            written.Add((export, text));
            if (export.DataSize > export.propsEnd()) binary[export.ClassName] = binary.GetValueOrDefault(export.ClassName) + 1;
        }

        var fileName = Path.GetFileName(pcc.FilePath);
        var sb = new StringBuilder();
        sb.Append(Header).Append(' ').Append(fileName).Append(" (").Append(pcc.Game).Append("): ").Append(written.Count)
          .Append(written.Count == 1 ? " export" : " exports").Append(", properties only. Compile back with LEC's CompileBulkPropertiesFile.\n");
        foreach (var (export, text) in written)
        {
            sb.Append('\n');
            if (uIndexComments) sb.Append("//#").Append(export.UIndex).Append('\n');
            sb.Append(text).Append('\n');
        }
        var all = sb.ToString();
        if (outPath is null && all.Length > maxChars)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                "The text is {0:N0} characters ({1} exports), over maxChars {2:N0}. "
                + "Pass outPath to write it to a file, narrow it with className or pathContains, or raise maxChars.", all.Length, written.Count, maxChars));
        }

        var check = roundTrip ? RoundTrip(pcc, written, all, usop) : null;
        var fullOut = outPath is null ? null : Write(outPath, fileName, all);

        return new PackageTextResult(
            Package: pcc.FilePath,
            Game: pcc.Game.ToString(),
            Exports: written.Count,
            ScriptExports: scriptExports,
            ClassDefaults: classDefaults,
            Skipped: skipped,
            BinaryNotInText: binary.Select(kv => new ClassCount(kv.Key, kv.Value)).OrderByDescending(c => c.Count).ThenBy(c => c.ClassName).ToList(),
            Chars: all.Length,
            OutPath: fullOut,
            WslPath: fullOut is null ? null : GamePaths.ToWslPath(fullOut),
            RoundTrip: check,
            Text: fullOut is null ? all : null);
    }

    private static FileLib InitializedLib(IMEPackage pcc, UnrealScriptOptionsPackage usop)
    {
        var lib = new FileLib(pcc);
        if (lib.Initialize(usop)) return lib;
        lib.Dispose();
        throw new InvalidOperationException("Script symbols failed to load: " + string.Join("; ", lib.InitializationLog.Content.Take(5)));
    }

    /// <summary>
    /// One export as a bulk-properties block, built as LEC's DecompileBulkProps builds it. The formatter's \r\n become \n; nothing
    /// else changes, as string literals may hold other line separators (U+2028...) that LEC does not escape.
    /// </summary>
    private static (string? Text, string? Error) Decompile(ExportEntry export, FileLib lib, UnrealScriptOptionsPackage usop)
    {
        try
        {
            if (UnrealScriptCompiler.ExportToAstNode(export, lib, usop) is not DefaultPropertiesBlock props)
            {
                return (null, "LEC returned no properties block");
            }
            var visitor = new CodeBuilderVisitor<PlainTextStringBuilderCodeFormatter>();
            new Subobject(export.InstancedFullPath, new Class(export.ClassName, null, null, default), props.Statements).AcceptVisitor(visitor);
            return (visitor.GetOutput().Replace("\r\n", "\n").Trim('\n'), null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Compiles the text into the open package (never saved: it was loaded from disk for this call only), then decompiles each
    /// written export again. Proves the text alone rebuilds those exports' properties, which a future text_to_pkg relies on.
    /// </summary>
    private static TextRoundTrip RoundTrip(IMEPackage pcc, List<(ExportEntry Export, string Text)> written, string all, UnrealScriptOptionsPackage usop)
    {
        var before = written.Select(w => PropertyBytes(w.Export)).ToList();
        // Empty first, so an export the compile skips reads as changed instead of keeping its old properties.
        foreach (var (export, _) in written) export.WriteProperties(new PropertyCollection());
        var log = UnrealScriptCompiler.CompileBulkPropertiesFile(all, pcc, usop);
        if (log.HasErrors || log.HasLexErrors)
        {
            // LEC appends the exception's stack trace to some messages: keep the first line.
            var errors = log.AllErrors.Select(m => (m.ToString() ?? "").Split('\n')[0].TrimEnd('\r')).Take(20).ToList();
            return new TextRoundTrip(false, errors.Count > 0 ? errors : ["compile failed without an error message"], 0, [], false, 0, []);
        }

        using var lib = InitializedLib(pcc, usop);
        var changed = new List<string>();
        var bytesDiffer = new List<string>();
        for (var i = 0; i < written.Count; i++)
        {
            var (export, text) = written[i];
            var label = $"#{export.UIndex} {export.ClassName} {export.InstancedFullPath}";
            var (again, error) = Decompile(export, lib, usop);
            if (again != text) changed.Add(error is null ? label : $"{label}: {error}");
            if (!before[i].AsSpan().SequenceEqual(PropertyBytes(export))) bytesDiffer.Add(label);
        }
        return new TextRoundTrip(changed.Count == 0, [], changed.Count, changed.Take(20).ToList(),
            bytesDiffer.Count == 0, bytesDiffer.Count, bytesDiffer.Take(20).ToList());
    }

    /// <summary>The serialized properties: from the start of the export's data to the end of its property list.</summary>
    private static byte[] PropertyBytes(ExportEntry export) => export.DataReadOnly[..export.propsEnd()].ToArray();

    /// <summary>
    /// Writes UTF-8 without BOM. Refuses the game install, and any existing file other than this package's own earlier text.
    /// </summary>
    private static string Write(string outPath, string packageFile, string text)
    {
        var full = Path.GetFullPath(GamePaths.ToWindowsPath(outPath));
        foreach (var game in Games) PackageWriter.EnsureWritable(game, full);
        var ours = $"{Header} {packageFile} (";
        if (File.Exists(full) && File.ReadLines(full).FirstOrDefault() is var first && first?.StartsWith(ours, StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException(first?.StartsWith(Header, StringComparison.Ordinal) == true
                ? $"Refusing to overwrite {full}: it holds another package's text ({first}). Pick another outPath."
                : $"Refusing to overwrite {full}: it is not a pkg_to_text file (its first line is not '{ours}...').");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }
}
