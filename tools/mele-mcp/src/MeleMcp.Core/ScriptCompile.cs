using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.UnrealScript;
using LegendaryExplorerCore.UnrealScript.Compiling.Errors;

namespace MeleMcp.Core;

/// <param name="Line">1-based line of the source given, when the compiler knows it.</param>
/// <param name="Column">1-based column.</param>
public sealed record ScriptMessage(string Text, int? Line, int? Column);

/// <param name="How">function, state, class (whole class replaced) or add-to-class (one member added or replaced).</param>
/// <param name="DataChanged">The target export's bytes differ from before. False on a compile of unchanged text means the compiler reproduced the original exactly.</param>
/// <param name="Warnings">What a successful compile cannot tell: shipping rules, a changed virtual function table.</param>
public sealed record ScriptCompileResult(
    string Path,
    ExportRow Target,
    string How,
    bool Compiled,
    bool Saved,
    bool DryRun,
    bool DataChanged,
    int DataSizeBefore,
    int DataSizeAfter,
    string? BackupPath,
    IReadOnlyList<ScriptMessage> Errors,
    IReadOnlyList<ScriptMessage> CompilerWarnings,
    IReadOnlyList<string> Warnings);

internal sealed record ScriptRun(string How, bool Compiled, IReadOnlyList<ScriptMessage> Errors, IReadOnlyList<ScriptMessage> CompilerWarnings, IReadOnlyList<string> Warnings);

/// <summary>Compiles UnrealScript text into a function, state or class export of a package copy, in all three games.</summary>
public static partial class ScriptCompile
{
    private const string AddToClass = "add-to-class";

    // Merge-mod targets, from M3's merge_mods.md. A mod's own Startup_DLC_MOD_* or PlotManagerDLC_MOD_* is its own file, shipped whole like any other.
    [GeneratedRegex(@"^(?!.*DLC_MOD_)(Core|Engine|GameFramework|GFxUI|IpDrv|WwiseAudio|SFXOnlineFoundation|SFXGame|BIOGame|EntryMenu|PlotManager.*|Startup.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CoreFile();

    // The first line export_decompile prints, so its output can be passed back unedited.
    [GeneratedRegex(@"\A#\d+ \S+ [^\r\n]*")]
    private static partial Regex DecompileHeader();

    [GeneratedRegex(@"\A(\s|//[^\n]*\n|/\*.*?\*/)*class\s+\w", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ClassDeclaration();

    public static ScriptCompileResult Compile(MEGame game, string fileOrPath, string export, string? source, string? sourceFile, bool dryRun)
    {
        var text = ReadSource(source, sourceFile);

        using var pcc = PackageInspector.Open(game, fileOrPath);
        // A dry run writes nothing, but it would prove little about the file a later save refuses, so it is refused the same way.
        PackageWriter.EnsureWritable(pcc.Game, pcc.FilePath);
        var target = ExportInspector.Resolve(pcc, export);
        var before = target.Data;
        var run = CompileInto(target, text);

        string? backup = null;
        var saved = run.Compiled && !dryRun;
        if (saved)
        {
            backup = PackageWriter.Save(pcc);
        }

        return new ScriptCompileResult(pcc.FilePath, ExportInspector.Row(target), run.How, run.Compiled, saved, dryRun,
            run.Compiled && !before.AsSpan().SequenceEqual(target.Data), before.Length, run.Compiled ? target.DataSize : before.Length, backup,
            run.Errors, run.CompilerWarnings, run.Warnings);
    }

    /// <summary>
    /// Compiles into an open package and leaves it changed in memory, so that a later compile sees this one, as M3 applies a merge mod's
    /// changes in order. Saving is the caller's.
    /// </summary>
    internal static ScriptRun CompileInto(ExportEntry target, string text)
    {
        var pcc = target.FileRef;
        var name = target.ObjectName.Instanced;
        var how = HowToCompile(target, text);
        var vTableBefore = VTable(target);

        using var cache = new PackageCache();
        var usop = new UnrealScriptOptionsPackage { Cache = cache };
        using var lib = new FileLib(pcc);
        if (!lib.Initialize(usop))
        {
            throw new InvalidOperationException("Script symbols failed to load: " + string.Join("; ", lib.InitializationLog.Content.Take(5)));
        }

        var log = how switch
        {
            "function" => UnrealScriptCompiler.CompileFunction(target, text, lib, usop).log,
            "state" => UnrealScriptCompiler.CompileState(target, text, lib, usop).log,
            "class" => UnrealScriptCompiler.CompileClass(pcc, text, lib, usop, target, target.Parent, intendedClassName: name).log,
            _ => UnrealScriptCompiler.AddOrReplaceInClass(target, text, lib, usop),
        };

        var inSource = how == AddToClass ? text.Length : int.MaxValue;
        var errors = Messages(log, log.AllErrors, inSource);
        // LEC gives some rejections no message at all (function text for a state export), and renames the export to the source's name.
        if (errors.Count == 0 && !log.Content.Any(m => m.Message == "Compiled!")) errors.Add(new ScriptMessage($"The source is not a {how} declaration.", null, null));
        if (!target.ObjectName.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new ScriptMessage($"The source declares '{target.ObjectName.Instanced}', the target export is '{name}'.", null, null));
        }
        var compiled = errors.Count == 0 && !log.HasErrors && !log.HasLexErrors;

        var warnings = new List<string>();
        if (CoreFile().IsMatch(Path.GetFileNameWithoutExtension(pcc.FilePath)))
        {
            warnings.Add($"{Path.GetFileName(pcc.FilePath)} is a core file, never shipped whole: this copy is fine to work on, shipping the change needs an M3 merge mod (.m3m).");
        }
        if (compiled && vTableBefore is not null && !vTableBefore.SequenceEqual(VTable(target)!))
        {
            warnings.Add($"The virtual function table of {name} changed: subclasses keep their own copy of it, and LEC does not update them.");
        }
        return new ScriptRun(how, compiled, errors, Messages(log, log.AllWarnings, inSource), warnings);
    }

    internal static string ReadSource(string? source, string? sourceFile)
    {
        if ((source is null) == (sourceFile is null)) throw new ArgumentException("Give exactly one of source and sourceFile.");
        var text = source ?? File.ReadAllText(GamePaths.ToWindowsPath(sourceFile!));
        // Blanked, not cut, so error lines still count from the text the caller has.
        return DecompileHeader().Replace(text, "");
    }

    private static string HowToCompile(ExportEntry target, string text) => target.ClassName switch
    {
        "Function" => "function",
        "State" => "state",
        "Class" => ClassDeclaration().IsMatch(text) ? "class" : AddToClass,
        _ => throw new ArgumentException($"#{target.UIndex} is a {target.ClassName}. script_compile takes a Function, State or Class export; a struct or enum goes through its Class."),
    };

    private static int[]? VTable(ExportEntry target) =>
        target.IsClass && target.Game.IsGame3() ? ObjectBinary.From<UClass>(target).VirtualFunctionTable.Select(i => (int)i).ToArray() : null;

    private static List<ScriptMessage> Messages(MessageLog log, IEnumerable<LogMessage> messages, int inSource) => messages.Select(m =>
    {
        if (m is not PositionedMessage { Line: > 0 } at || log.LineLookup is null) return new ScriptMessage(m.Message, null, null);
        // AddOrReplaceInClass compiles the rest of the class from decompiled text the caller never saw, with the source's line table.
        if (at.Start >= inSource) return new ScriptMessage("In the class's decompiled text: " + m.Message, null, null);
        return new ScriptMessage(m.Message, at.Line, log.LineLookup.GetColumnFromCharIndex(at.Start) + 1);
    }).ToList();
}
