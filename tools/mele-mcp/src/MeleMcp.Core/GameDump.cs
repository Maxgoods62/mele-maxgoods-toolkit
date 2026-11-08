using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <summary>What a dump is, next to it as image.json.</summary>
/// <param name="ExeSha256">SHA-256 of the exe file on disk, which says which build the dump is.</param>
/// <param name="BaseAddress">Where the image was mapped: pointers inside the dump are this plus an RVA.</param>
/// <param name="UnreadablePages">4 KB pages that could not be read; they are zero in the dump.</param>
public sealed record DumpSidecar(string Game, string ExeFile, string ExeSha256, long ExeSize, string BaseAddress, long ImageSize, int UnreadablePages, DateTime DumpedUtc);

/// <param name="Skipped">True when the cached dump already matches the exe, so nothing was read.</param>
/// <param name="TextMatchesFile">The code section in memory equals the exe file's, which for a game means it is still encrypted.</param>
public sealed record ExeDumpResult(string Image, string SidecarFile, bool Skipped, bool TextMatchesFile, DumpSidecar Info);

/// <param name="Path">The PE file, for REA's open_binary.</param>
/// <param name="Base">REA's addresses are this plus an RVA.</param>
public sealed record FileLayoutResult(string Path, string Source, long Size, string Base);

/// <summary>An image read from a process, not stored yet.</summary>
public sealed record ProcessImage(byte[] Bytes, string ExePath, ulong Base, int UnreadablePages);

/// <summary>
/// The game's exe holds its code encrypted on disk, so native research reads the mapped image of the running game. The image is only
/// read (PROCESS_VM_READ) and lands in a per-game cache outside the game install: it is game code and never belongs in a repo.
/// </summary>
public static class GameDump
{
    /// <summary>Folder that replaces %LOCALAPPDATA%\mele-mcp\dumps.</summary>
    public const string DirEnvVar = "MELE_DUMP_DIR";

    private const string ImageFile = "image.bin";
    private const string SidecarName = "image.json";
    private const string FileLayoutName = "image-pe.exe";
    private const int Page = 4096;
    private const int TextSample = 64 * 1024;

    private static readonly MEGame[] Games = [MEGame.LE1, MEGame.LE2, MEGame.LE3];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Dumps the running game, unless the cache already holds this build and <paramref name="force"/> is off.</summary>
    public static ExeDumpResult Dump(MEGame game, bool force)
    {
        LecHost.EnsureInitialized();
        var dir = CacheDir(game);
        foreach (var g in Games) PackageWriter.EnsureWritable(g, dir);

        var exe = MEDirectories.GetExecutablePath(game);
        if (!force && File.Exists(exe) && ReadSidecar(dir) is { } cached && cached.ExeSha256 == Sha256(exe) && File.Exists(Path.Combine(dir, ImageFile)))
        {
            return new ExeDumpResult(Path.Combine(dir, ImageFile), Path.Combine(dir, SidecarName), true, false, cached);
        }

        var processes = Process.GetProcessesByName(GameBridge.ProcessName(game));
        try
        {
            if (processes.Length == 0)
            {
                var cache = force ? "" : $", and the cache in {dir} holds no dump of the exe on disk";
                throw new InvalidOperationException($"{game} is not running ({GameBridge.ProcessName(game)}.exe){cache}. "
                                                    + "Start the game, wait for the main menu, then call exe_dump again. native_query's dump parameter reads an older image.bin.");
            }
            var image = ReadImage(processes[0]);
            if (TextMatchesFile(image.Bytes, image.ExePath))
            {
                throw new InvalidOperationException($"{game}'s code is still encrypted in memory (its code section equals the exe file's). Wait for the main menu, then call exe_dump again. Nothing was written.");
            }
            return Store(image, game.ToString(), dir);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>The cached dump of a game, for native_query.</summary>
    public static string CachedImage(MEGame game)
    {
        var path = Path.Combine(CacheDir(game), ImageFile);
        return File.Exists(path) ? path : throw new FileNotFoundException($"No cached dump of {game} at {path}. Call exe_dump with the game running, or pass dump with an image.bin's path.");
    }

    /// <summary>Reads a process's main module page by page, read-only. A page that cannot be read stays zero and is counted.</summary>
    public static ProcessImage ReadImage(Process process)
    {
        var module = process.MainModule ?? throw new InvalidOperationException($"Process {process.Id} has no main module.");
        var size = module.ModuleMemorySize;
        var start = module.BaseAddress;
        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, process.Id);
        if (handle == IntPtr.Zero) throw new InvalidOperationException($"OpenProcess failed on process {process.Id}: Windows error {Marshal.GetLastWin32Error()}.");
        try
        {
            var bytes = new byte[size];
            var page = new byte[Page];
            var unreadable = 0;
            for (var offset = 0; offset < size; offset += Page)
            {
                if (Native.ReadProcessMemory(handle, start + offset, page, Math.Min(Page, size - offset), out var read) && read != 0)
                {
                    Buffer.BlockCopy(page, 0, bytes, offset, (int)read);
                    if ((int)read < Math.Min(Page, size - offset)) unreadable++;
                }
                else
                {
                    unreadable++;
                }
            }
            return new ProcessImage(bytes, module.FileName, (ulong)start, unreadable);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Whether a sample of the code section reads the same in memory and in the exe file.</summary>
    public static bool TextMatchesFile(byte[] image, string exePath)
    {
        var text = PeImage.Sections(image).FirstOrDefault(s => s.Name == ".text");
        if (text == null || text.RawOffset == 0) return false;
        var length = Math.Min(TextSample, Math.Min(text.RawSize, text.VirtualSize));
        var fromFile = new byte[length];
        using var file = File.OpenRead(exePath);
        file.Seek(text.RawOffset, SeekOrigin.Begin);
        file.ReadExactly(fromFile);
        var same = 0;
        for (var i = 0; i < length; i++)
        {
            if (fromFile[i] == image[text.Rva + i]) same++;
        }
        // Relocations can change a few bytes of code that really is the file's.
        return same >= length * 9L / 10;
    }

    /// <summary>Writes image.bin and its sidecar to <paramref name="dir"/>. The sidecar goes last, so a broken write never looks like a dump.</summary>
    public static ExeDumpResult Store(ProcessImage image, string game, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ImageFile);
        var sidecar = Path.Combine(dir, SidecarName);
        File.Delete(sidecar);
        AtomicFile.WriteAllBytes(path, image.Bytes);
        var info = new DumpSidecar(game, Path.GetFileName(image.ExePath), Sha256(image.ExePath), new FileInfo(image.ExePath).Length, $"0x{image.Base:x}",
                                   image.Bytes.Length, image.UnreadablePages, DateTime.UtcNow);
        AtomicFile.WriteAllText(sidecar, JsonSerializer.Serialize(info, Json));
        return new ExeDumpResult(path, sidecar, false, TextMatchesFile(image.Bytes, image.ExePath), info);
    }

    /// <summary>
    /// Writes a dump as a PE a static analyser can load: a mapped image under its original headers makes Ghidra's loader read every section
    /// from the wrong offset and fail. By default next to the dump; an existing file is replaced only if it already has this layout.
    /// </summary>
    public static FileLayoutResult WriteFileLayout(MEGame game, string? dump, string? outPath)
    {
        LecHost.EnsureInitialized();
        var source = Path.GetFullPath(GamePaths.ToWindowsPath(dump ?? CachedImage(game)));
        if (!File.Exists(source)) throw new FileNotFoundException($"No dump at {source}.");
        var target = Path.GetFullPath(GamePaths.ToWindowsPath(outPath ?? Path.Combine(Path.GetDirectoryName(source) ?? ".", FileLayoutName)));
        foreach (var g in Games) PackageWriter.EnsureWritable(g, target);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("outPath is the dump itself: pick another file.");
        if (File.Exists(target) && !IsFileLayout(target)) throw new InvalidOperationException($"Refusing to overwrite {target}: it is not a file this tool wrote. Pick another outPath.");

        var mapped = File.ReadAllBytes(source);
        PeImage.RequireMapped(mapped, source);
        var image = PeImage.ToFileLayout(mapped);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.WriteAllBytes(target, image);
        var imageBase = BitConverter.ToUInt64(image, PeImage.OptionalHeader(image) + PeImage.ImageBaseField);
        return new FileLayoutResult(target, source, image.Length, $"0x{imageBase:x}");
    }

    private static bool IsFileLayout(string path)
    {
        var head = new byte[Page];
        using (var file = File.OpenRead(path)) file.ReadAtLeast(head, Page, throwOnEndOfStream: false);
        try
        {
            return PeImage.Sections(head) is { Count: > 0 } sections && sections.All(s => s.RawOffset == s.Rva);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>The sidecar next to an image.bin, or null.</summary>
    internal static DumpSidecar? SidecarOf(string imagePath) => ReadSidecar(Path.GetDirectoryName(imagePath) ?? ".");

    private static DumpSidecar? ReadSidecar(string dir)
    {
        var path = Path.Combine(dir, SidecarName);
        return File.Exists(path) ? JsonSerializer.Deserialize<DumpSidecar>(File.ReadAllText(path)) : null;
    }

    private static string CacheDir(MEGame game)
    {
        var root = Environment.GetEnvironmentVariable(DirEnvVar);
        root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mele-mcp", "dumps")
            : GamePaths.ToWindowsPath(root);
        return Path.GetFullPath(Path.Combine(root, game.ToString()));
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static class Native
    {
        public const int ProcessQueryInformation = 0x0400;
        public const int ProcessVmRead = 0x0010;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nint size, out nint read);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
