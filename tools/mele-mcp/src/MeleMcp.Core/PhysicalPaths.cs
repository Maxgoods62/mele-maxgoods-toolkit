using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MeleMcp.Core;

/// <summary>
/// The write guard's second look: what a path is on disk, not how it is spelled. A spelling compare misses
/// \\localhost\D$\..., a junction and \\?\Volume{guid}\... into the same folder. A path that can't be opened gives no answer
/// here, and the spelling compare still stands.
/// </summary>
internal static class PhysicalPaths
{
    private const uint ReadAttributes = 0x80;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // CreateFile opens a folder only with this flag

    private readonly record struct Identity(uint Volume, ulong File);

    /// <summary>True when path, or the deepest part of it that exists, is root or lies under it.</summary>
    public static bool IsInside(string path, string root)
    {
        var rootId = Identify(root, out var rootFinal);
        var start = DeepestExisting(path);
        if (rootId is null || start is null) return false;

        var startId = Identify(start, out var final);
        if (startId == rootId) return true;
        // Links are resolved here: a junction anywhere on the way leaves its target's real folders.
        if (final is not null && rootFinal is not null && final.StartsWith(rootFinal.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase)) return true;
        // A share of this machine keeps its own spelling in the final path, so its folders are compared one by one.
        foreach (var from in new[] { start, final })
        {
            for (var dir = from; dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if (Identify(dir, out _) == rootId) return true;
            }
        }
        return false;
    }

    private static string? DeepestExisting(string path)
    {
        // Not GamePaths.Canonical: it drops \\?\, which leaves a \\?\Volume{guid}\ path relative.
        for (var p = Path.GetFullPath(GamePaths.ToWindowsPath(path.Trim())); p is not null; p = Path.GetDirectoryName(p))
        {
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        return null;
    }

    private static Identity? Identify(string path, out string? final)
    {
        final = null;
        using var handle = CreateFileW(path, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length > 0 && length < buffer.Capacity) final = GamePaths.Canonical(buffer.ToString());

        if (!GetFileInformationByHandle(handle, out var info)) return null;
        var file = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return file == 0 ? null : new Identity(info.VolumeSerialNumber, file); // a file system without ids reports 0 for all
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
