using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Writes UnrealScript into packages; export_decompile reads it.</summary>
[McpServerToolType]
public static class ScriptTools
{
    [McpServerTool(Name = "script_compile", Destructive = true),
     Description("Compiles UnrealScript into a mod's package copy with LEC's compiler, in LE1, LE2 or LE3. Target a Function or State export with its full text "
                 + "(export_decompile's output works as is), or a Class export with the whole class, or with one var, struct, enum, function or state: that one is "
                 + "added or replaced (LEC's AddOrReplaceInClass) and the rest of the class is compiled again from its decompiled text, so a class whose text does not compile refuses it. LE1 and LE2 plot conditionals "
                 + "are F<id> functions of BioAutoConditionals, so this also edits those; LE3's are in .cnd files (cnd_set). "
                 + "Errors have 1-based Line and Column in the source given; the file is saved only when the compile succeeds, dryRun never saves. Native functions cannot be compiled. "
                 + "A core file (SFXGame, Engine, Startup*...) is compiled on the copy with a warning: shipping it needs a merge mod. "
                 + "Tested on scratch copies by decompile, compile, decompile. Not tested in game: a successful compile does not show that the game accepts the result. "
                 + "Backs the package up first. Refuses files inside the game install, for a dry run too.")]
    public static ScriptCompileResult Compile(
        [Description("Package: the mod's copy. Full Windows or /mnt/<drive> path, or a file name that does not resolve to the game install.")] string package,
        [Description("Function, State or Class export: UIndex, instanced full path, or a name unique in the package.")] string export,
        [Description("UnrealScript text.")] string? source = null,
        [Description("Text file holding the source, instead of source (Windows or /mnt/<drive> path).")] string? sourceFile = null,
        [Description("Compile and report, save nothing.")] bool dryRun = false,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => ScriptCompile.Compile(PackageTools.ParseGame(game), package, export, source, sourceFile, dryRun));
}
