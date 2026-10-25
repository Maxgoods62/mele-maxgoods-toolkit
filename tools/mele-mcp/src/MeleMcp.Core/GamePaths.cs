using System.Text.RegularExpressions;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

public static partial class GamePaths
{
    [GeneratedRegex(@"^/mnt/([a-zA-Z])(/.*)?$")]
    private static partial Regex WslDrivePath();

    /// <summary>
    /// Converts a WSL drive path (/mnt/d/...) to a Windows path (D:\...). Other inputs are returned unchanged,
    /// because Claude runs in WSL and will often pass paths in that form.
    /// </summary>
    public static string ToWindowsPath(string path)
    {
        var m = WslDrivePath().Match(path.Trim());
        if (!m.Success) return path;
        var rest = m.Groups[2].Success ? m.Groups[2].Value.Replace('/', '\\') : "\\";
        return $"{char.ToUpperInvariant(m.Groups[1].Value[0])}:{rest}";
    }

    /// <summary>
    /// Resolves a package reference to a full path: an existing path is used as-is, otherwise it is treated as a file name
    /// and looked up in the game's loaded files (highest mounted copy, DLC mods included).
    /// </summary>
    public static string ResolvePackage(MEGame game, string fileOrPath)
    {
        var candidate = ToWindowsPath(fileOrPath);
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        var fileName = Path.GetFileName(candidate);
        if (!Path.HasExtension(fileName)) fileName += ".pcc";
        if (MELoadedFiles.TryGetHighestMountedFile(game, fileName, out var found)) return found;

        throw new FileNotFoundException($"'{fileOrPath}' is not an existing path and no file named '{fileName}' is loaded by {game}.");
    }
}
