using System.Diagnostics.CodeAnalysis;
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

    private static readonly object LoadedGate = new();

    /// <summary>Per game, the <see cref="FolderSignature"/> LEC's cached file list was last checked against (<see cref="LoadedFiles(MEGame, bool)"/>).</summary>
    private static readonly Dictionary<MEGame, long> loadedAt = [];

    /// <summary>
    /// Forgets the cached file lists, so files installed since the last lookup are found, a change <see cref="FolderSignature"/>
    /// cannot see included (a file added in a subfolder of a cooked folder).
    /// </summary>
    [SuppressMessage("ApiDesign", "RS0030", Justification = "The one place LEC's file list is invalidated.")]
    public static void RefreshLoadedFiles()
    {
        lock (LoadedGate)
        {
            MELoadedFiles.InvalidateCaches();
            loadedAt.Clear();
        }
    }

    /// <summary>
    /// The files the game loads, name → path (highest mounted copy, DLC mods included): LEC's cached list. Every lookup by file name
    /// goes through here, never through MELoadedFiles itself. LEC keeps one list per game for the whole process, so an install
    /// or uninstall that never called <see cref="RefreshLoadedFiles"/> (M3's own window, a copy by hand) would leave every tool
    /// opening the copy the old list names, or a file no longer there. So each call compares <see cref="FolderSignature"/> with the
    /// one the list was checked against, and rescans when it differs. <paramref name="rescan"/> forces one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadedFiles(MEGame game, bool rescan = false) =>
        // Taken before the scan: a change during the scan gives the next call another signature, so that call scans again.
        LoadedFiles(game, FolderSignature(MEDirectories.GetBioGamePath(game), MEDirectories.GetDLCPath(game), game.CookedDirName()), rescan);

    /// <summary><see cref="LoadedFiles(MEGame, bool)"/> with the folders' signature given, so a test can change it without writing in the install.</summary>
    [SuppressMessage("ApiDesign", "RS0030", Justification = "The one place LEC's cached file list is read.")]
    internal static IReadOnlyDictionary<string, string> LoadedFiles(MEGame game, long signature, bool rescan)
    {
        lock (LoadedGate)
        {
            var stale = rescan || !loadedAt.TryGetValue(game, out var had) || had != signature;
            // A scan that throws (a mounted DLC folder with no cooked folder) records nothing, so the next call scans again.
            var files = MELoadedFiles.GetFilesLoadedInGame(game, forceReload: stale);
            loadedAt[game] = signature;
            return files;
        }
    }

    /// <summary>
    /// The files the game loads with more extensions than LEC's list has (.cnd), from a scan of their own: the root override keeps
    /// them out of LEC's cached list, which other tools read as packages. Nothing is cached, so nothing can go stale.
    /// </summary>
    [SuppressMessage("ApiDesign", "RS0030", Justification = "A scan that bypasses LEC's cache, kept beside the cached one.")]
    internal static IReadOnlyDictionary<string, string> LoadedFilesWith(MEGame game, params string[] extensions) =>
        MELoadedFiles.GetFilesLoadedInGame(game, gameRootOverride: MEDirectories.GetDefaultGamePath(game), additionalExtensions: extensions);

    /// <summary>
    /// What LEC's file list hangs on, hashed, read without listing a single file, so <see cref="LoadedFiles(MEGame, bool)"/> can check it on every
    /// call: the write times of the base game's cooked folder and of BioGame\DLC, and per DLC folder its name, the write time of
    /// its cooked folder, and the size and write time of its AutoLoad.ini (LE1 counts a DLC_MOD folder that has one, and reads its
    /// mount there) and of its Mount.dlc (the same for LE2 and LE3). A folder's write time moves when a file in it is added, removed
    /// or renamed, so an install, an uninstall or a copy into a cooked folder shows; a file rewritten in place does not, and leaves
    /// the list as it was. A file added in a subfolder of a cooked folder does not show either: <see cref="RefreshLoadedFiles"/>.
    /// </summary>
    internal static long FolderSignature(string? bioGame, string? dlcDir, string cookedDirName)
    {
        var hash = new HashCode();
        void Add(FileSystemInfo info)
        {
            hash.Add(info.Exists);
            if (!info.Exists) return;
            hash.Add(info.LastWriteTimeUtc.Ticks);
            if (info is FileInfo file) hash.Add(file.Length);
        }

        if (bioGame is not null)
        {
            hash.Add(bioGame, StringComparer.OrdinalIgnoreCase);
            Add(new DirectoryInfo(Path.Combine(bioGame, cookedDirName)));
        }
        if (dlcDir is not null)
        {
            hash.Add(dlcDir, StringComparer.OrdinalIgnoreCase);
            var dlcFolder = new DirectoryInfo(dlcDir);
            Add(dlcFolder);
            if (dlcFolder.Exists)
            {
                foreach (var dlc in dlcFolder.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    hash.Add(dlc.Name, StringComparer.OrdinalIgnoreCase);
                    Add(new FileInfo(Path.Combine(dlc.FullName, "AutoLoad.ini")));
                    var cooked = Path.Combine(dlc.FullName, cookedDirName);
                    Add(new DirectoryInfo(cooked));
                    Add(new FileInfo(Path.Combine(cooked, "Mount.dlc")));
                }
            }
        }
        return hash.ToHashCode();
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
        // Cached lookup, checked for freshness (LoadedFiles): TryGetHighestMountedFile forces a full rescan on every call.
        if (LoadedFiles(game).TryGetValue(fileName, out var found)) return found;

        throw new FileNotFoundException($"'{fileOrPath}' is not an existing path and no file named '{fileName}' is loaded by {game}.");
    }
}
