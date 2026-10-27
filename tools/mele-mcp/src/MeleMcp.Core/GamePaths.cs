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

    [GeneratedRegex(@"^\\\\(?:wsl\.localhost|wsl\$)\\[^\\]+(\\.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WslSharePath();

    [GeneratedRegex(@"^\\\\(?:wsl\.localhost|wsl\$)\\[^\\]+\\mnt\\([a-zA-Z])(\\.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WslDriveSharePath();

    /// <summary>
    /// One spelling per file, for comparing paths: full path, no \\?\ or \\.\ prefix, a drive reached through the WSL share
    /// (\\wsl$\&lt;distro&gt;\mnt\d\x) written D:\x. Case is left as is; compare with OrdinalIgnoreCase.
    /// </summary>
    public static string Canonical(string path)
    {
        var p = ToWindowsPath(path.Trim());
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];
        p = Path.GetFullPath(p);
        var drive = WslDriveSharePath().Match(p);
        return drive.Success ? $"{char.ToUpperInvariant(drive.Groups[1].Value[0])}:{(drive.Groups[2].Success ? drive.Groups[2].Value : "\\")}" : p;
    }

    /// <summary>
    /// The WSL form of a Windows path, for Claude's own file tools: D:\x → /mnt/d/x, and \\wsl.localhost\&lt;distro&gt;\x → /x (where a
    /// WSL path such as /home/... lands, since Claude Code starts us in a \\wsl.localhost\ folder); other paths unchanged.
    /// </summary>
    public static string ToWslPath(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            return $"/mnt/{char.ToLowerInvariant(path[0])}/{path[3..].Replace('\\', '/')}";
        }
        var share = WslSharePath().Match(path);
        return share.Success ? (share.Groups[1].Success ? share.Groups[1].Value.Replace('\\', '/') : "/") : path;
    }

    /// <summary>Forgets the cached file lists, so files installed since the last lookup are found.</summary>
    public static void RefreshLoadedFiles() => MELoadedFiles.InvalidateCaches();

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
        // Cached lookup: TryGetHighestMountedFile forces a full rescan on every call. Tools that install or copy files
        // must call MELoadedFiles.InvalidateCaches() afterwards.
        if (MELoadedFiles.GetFilesLoadedInGame(game).TryGetValue(fileName, out var found)) return found;

        throw new FileNotFoundException($"'{fileOrPath}' is not an existing path and no file named '{fileName}' is loaded by {game}.");
    }
}
