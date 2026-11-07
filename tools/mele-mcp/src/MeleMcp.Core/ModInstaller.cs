using System.Diagnostics;
using System.Text;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;

namespace MeleMcp.Core;

/// <param name="Completed">M3 logged the end of the install (for LE1, its AutoTOC after the Coalesced merge) before the timeout.</param>
/// <param name="Differences">Mod files whose installed copy is missing or differs; empty when the install matches the library.</param>
/// <param name="Log">M3 log lines written during the install: warnings, errors and the install steps.</param>
public sealed record ModInstallResult(string ModName, string ModDesc, string ModManager, bool Completed, double Seconds, IReadOnlyList<string> DlcFolders,
    int FilesChecked, IReadOnlyList<string> Differences, bool BootRequested, IReadOnlyList<string> Log);

/// <summary>
/// Installs a mod with ME3Tweaks Mod Manager (M3): `--installmod`, the way LEX drives it. M3 is a GUI app that acts on its
/// command line once its window is up (a second launch hands the arguments to the running one), so this waits on M3's log.
/// </summary>
public static class ModInstaller
{
    private const string LogDir = @"C:\ProgramData\ME3TweaksModManager\logs";
    private const string SettingsFile = @"C:\ProgramData\ME3TweaksModManager\settings.ini";

    public static ModInstallResult Install(MEGame game, string mod, bool bootGame, int timeoutSeconds)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("M3 runs on Windows.");
        if (!game.IsLEGame()) throw new ArgumentException("mod_install handles LE1, LE2 and LE3.");
        LecHost.EnsureInitialized();

        var m3 = M3Path();
        var moddesc = FindModDesc(game, mod, m3);
        var ini = ReadIni(moddesc);
        var name = ini.GetValueOrDefault("ModInfo")?.GetValueOrDefault("modname") ?? Path.GetFileName(Path.GetDirectoryName(moddesc)!);
        var modGame = ini.GetValueOrDefault("ModInfo")?.GetValueOrDefault("game");
        if (!string.Equals(modGame, game.ToString(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{moddesc} is a {modGame ?? "?"} mod, not {game}.");
        var customDlc = ini.GetValueOrDefault("CUSTOMDLC");
        var sources = Split(customDlc?.GetValueOrDefault("sourcedirs"));
        var targets = Split(customDlc?.GetValueOrDefault("destdirs"));
        if (sources.Count != targets.Count) throw new ArgumentException($"{moddesc}: [CUSTOMDLC] sourcedirs and destdirs differ in length.");

        // Held from the check below to the compare: no other mele-mcp call (game_cycle's boot, another session's install) can start
        // or touch this game between the check and M3's copy. game_cycle already holds it, and takes it again here.
        using var held = GameLock.ForGame(game);
        // M3 overwrites the installed DLC folders; the game holds them open while it runs.
        var process = game switch { MEGame.LE1 => "MassEffect1", MEGame.LE2 => "MassEffect2", _ => "MassEffect3" };
        if (Process.GetProcessesByName(process).Length > 0) throw new InvalidOperationException($"{process}.exe is running. Close the game first (the launcher can stay open).");
        var dlcRoot = Path.Combine(MEDirectories.GetDefaultGamePath(game) ?? throw new InvalidOperationException($"No {game} install found."), "BioGame", "DLC");

        // "Initializing mod installer panel for mod: <name> ..." starts it. After the main stage, which holds the merge mods, M3 still
        // merges Coalesced on LE1, syncs PlotManager and shaders on LE2 and LE3, and on every game ends with its AutoTOC panel. A failed
        // install logs "Exiting modinstaller", opens a dialog that waits for a click and never reaches that panel.
        var begin = $"Initializing mod installer panel for mod: {name}";
        const string end = "Panel closing: AutoTOC";
        const string failed = "<<<<<<< Exiting modinstaller";
        var log = new StringBuilder();
        var completed = false;
        DateTime started;
        // M3 is one app for all three games: a second install's lines would land in the log this one reads.
        using (GameLock.ForM3())
        {
            var offsets = LogOffsets();
            started = DateTime.UtcNow;
            var arguments = new List<string> { "--installmod", moddesc, "--game", game.ToString() };
            if (bootGame) arguments.Add("--bootgame");
            StartM3(m3, arguments);

            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(timeoutSeconds))
            {
                Thread.Sleep(1000);
                log.Append(ReadNew(offsets));
                var text = log.ToString();
                var at = text.IndexOf(begin, StringComparison.Ordinal);
                if (at >= 0 && text.IndexOf(end, at, StringComparison.Ordinal) >= 0)
                {
                    completed = true;
                    break;
                }
                if (at >= 0 && text.IndexOf(failed, at, StringComparison.Ordinal) >= 0) break;
            }
        }

        var differences = new List<string>();
        var checkedCount = 0;
        var modDir = Path.GetDirectoryName(moddesc)!;
        for (var i = 0; i < sources.Count; i++)
        {
            var source = Path.Combine(modDir, sources[i]);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var installed = Path.Combine(dlcRoot, targets[i], relative);
                checkedCount++;
                if (!File.Exists(installed)) differences.Add($"{targets[i]}\\{relative}: missing");
                else if (!SameBytes(file, installed)) differences.Add($"{targets[i]}\\{relative}: differs");
            }
        }
        GamePaths.RefreshLoadedFiles();

        // The compare covers [CUSTOMDLC] files. A merge mod patches a core file in place, so M3's log is what shows that it did.
        if (completed && Split(ini.GetValueOrDefault("BASEGAME")?.GetValueOrDefault("mergemods")).Count > 0 && !log.ToString().Contains("Saving package", StringComparison.Ordinal))
            differences.Add("mergemods: M3 logged no 'Saving package', so no file was patched");

        var lines = log.ToString().Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Contains("[WRN]") || l.Contains("[ERR]") || l.Contains("[FTL]") || l.Contains("mod install", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("Merging M3 Config Delta") || l.Contains("Autotocing") || l.Contains("Panel closing")
                        || l.Contains("Applying ") || l.Contains("Merging changes into") || l.Contains("Saving package") || l.Contains("was not modified")
                        || l.Contains("File not found in game") || l.Contains("An error occurred installing mergemod") || l.Contains(failed))
            .ToList();
        return new ModInstallResult(name, moddesc, m3, completed, Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
            targets, checkedCount, differences, bootGame, lines);
    }

    /// <summary>
    /// Starts the game the way M3's Start game button does (M3 --bootgame), without installing anything. Refused while another
    /// mele-mcp call installs through M3. game_cycle, its one caller, holds the game's lock.
    /// </summary>
    public static void Boot(MEGame game)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("M3 runs on Windows.");
        using (GameLock.ForM3()) StartM3(M3Path(), ["--game", game.ToString(), "--bootgame"]);
    }

    internal static string M3Path()
    {
        var m3 = Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\ME3Tweaks", "ExecutableLocation", null) as string;
        if (m3 is null || !File.Exists(m3)) throw new FileNotFoundException("ME3TweaksModManager.exe not found (HKCU\\SOFTWARE\\ME3Tweaks ExecutableLocation). Start M3 once.");
        return m3;
    }

    /// <summary>
    /// Shell execute, so M3 (and the game it boots) do not inherit this server's stdio pipes: an MCP client waits for those to
    /// close, which made calls hang until M3 exited.
    /// </summary>
    internal static void StartM3(string m3, IEnumerable<string> arguments) =>
        Process.Start(new ProcessStartInfo(m3, string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))) { UseShellExecute = true })?.Dispose();

    /// <summary>The mod's moddesc.ini and its custom DLC folders (sourcedirs) in M3's library.</summary>
    internal static (string ModDesc, IReadOnlyList<string> DlcFolders) Locate(MEGame game, string mod)
    {
        var moddesc = FindModDesc(game, mod, M3Path());
        var sources = Split(ReadIni(moddesc).GetValueOrDefault("CUSTOMDLC")?.GetValueOrDefault("sourcedirs"));
        if (sources.Count == 0) throw new ArgumentException($"{moddesc} has no [CUSTOMDLC] sourcedirs.");
        return (moddesc, sources.Select(d => Path.Combine(Path.GetDirectoryName(moddesc)!, d)).ToList());
    }

    /// <summary>A moddesc.ini, a mod folder, or a mod's folder name in M3's library (mods\&lt;game&gt;\&lt;name&gt;).</summary>
    private static string FindModDesc(MEGame game, string mod, string m3)
    {
        var path = GamePaths.ToWindowsPath(mod);
        if (File.Exists(path) && path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(path);
        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "moddesc.ini"))) return Path.GetFullPath(Path.Combine(path, "moddesc.ini"));

        var library = ReadIni(SettingsFile).GetValueOrDefault("ModLibrary")?.GetValueOrDefault("LibraryPath") is { Length: > 0 } set
            ? set : Path.Combine(Path.GetDirectoryName(m3)!, "mods");
        var named = Path.Combine(library, game.ToString(), mod, "moddesc.ini");
        return File.Exists(named) ? named : throw new FileNotFoundException($"No moddesc.ini at {path}, nor a mod '{mod}' in M3's library ({Path.Combine(library, game.ToString())}).");
    }

    internal static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return sections;
        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = sections[line[1..^1]] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (current is not null && eq > 0) current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    private static List<string> Split(string? value) => value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];

    /// <summary>Where M3's logs end now: <see cref="ReadNew"/> from here gives only what it writes next.</summary>
    internal static Dictionary<string, long> LogOffsets() =>
        Directory.Exists(LogDir) ? Directory.EnumerateFiles(LogDir, "*.txt").ToDictionary(f => f, f => new FileInfo(f).Length) : [];

    /// <summary>Text M3 appended to its logs since the last read (M3 keeps them open; a new day starts a new file).</summary>
    internal static string ReadNew(Dictionary<string, long> offsets)
    {
        if (!Directory.Exists(LogDir)) return "";
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(LogDir, "*.txt"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var from = offsets.GetValueOrDefault(file);
            if (stream.Length <= from) continue;
            stream.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text.Append(reader.ReadToEnd());
            offsets[file] = stream.Length;
        }
        return text.ToString();
    }

    private static bool SameBytes(string a, string b)
    {
        var (fa, fb) = (new FileInfo(a), new FileInfo(b));
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        var (ba, bb) = (new byte[81920], new byte[81920]);
        int read;
        while ((read = sa.Read(ba, 0, ba.Length)) > 0)
        {
            sb.ReadExactly(bb, 0, read);
            if (!ba.AsSpan(0, read).SequenceEqual(bb.AsSpan(0, read))) return false;
        }
        return true;
    }
}
