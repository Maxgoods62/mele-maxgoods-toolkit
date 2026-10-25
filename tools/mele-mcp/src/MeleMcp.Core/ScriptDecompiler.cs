using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.UnrealScript;

namespace MeleMcp.Core;

public static class ScriptDecompiler
{
    /// <summary>UnrealScript source of a class, state, function or struct export, as LEX's script editor shows it.</summary>
    public static string Decompile(MEGame game, string fileOrPath, string export)
    {
        using var pcc = PackageInspector.Open(game, fileOrPath);
        var target = ExportInspector.Resolve(pcc, export);

        using var cache = new PackageCache();
        var usop = new UnrealScriptOptionsPackage { Cache = cache };
        var lib = new FileLib(pcc);
        if (!lib.Initialize(usop))
        {
            throw new InvalidOperationException("Script symbols failed to load: " + string.Join("; ", lib.InitializationLog.Content.Take(5)));
        }

        var (_, text) = UnrealScriptCompiler.DecompileExport(target, lib, usop);
        return $"#{target.UIndex} {target.ClassName} {target.InstancedFullPath}\n{text}";
    }
}
