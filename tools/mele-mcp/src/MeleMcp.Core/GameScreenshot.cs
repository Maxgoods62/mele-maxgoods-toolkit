using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <param name="WslPath">The same file as seen from WSL, for Claude's Read tool.</param>
/// <param name="Method">PrintWindow (the window's own content, even when covered) or screen copy (what is on screen there).</param>
public sealed record Screenshot(string Path, string WslPath, int Width, int Height, string Method, string? Note);

/// <summary>
/// Captures the game window's client area to a PNG, so Claude can look at the result of a change itself. PrintWindow with
/// PW_RENDERFULLCONTENT asks DWM for the window's composed content, which works for a windowed DirectX game even behind other
/// windows; when that comes back black (exclusive fullscreen, some drivers), the screen area is copied instead.
/// </summary>
public static class GameScreenshot
{
    private const uint PwClientOnly = 0x1, PwRenderFullContent = 0x2;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <param name="maxWidth">Scales the image down to this width (0: full size); smaller images are cheaper to read.</param>
    public static Screenshot Take(MEGame game, string? outputPath, int maxWidth)
    {
        var path = OutputPath(game, outputPath);
        var processes = Process.GetProcessesByName(GameBridge.ProcessName(game));
        try
        {
            var window = processes.Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
            if (window == IntPtr.Zero) throw new InvalidOperationException($"{game} is not running, or has no window yet.");
            if (Native.IsIconic(window)) throw new InvalidOperationException($"The {game} window is minimized: restore it first.");

            // Physical pixels: without DPI awareness Windows reports a scaled-down client size on a scaled display.
            var previous = Native.SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            try
            {
                return Capture(window, path, maxWidth);
            }
            finally
            {
                Native.SetThreadDpiAwarenessContext(previous);
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>
    /// Where the PNG goes: <paramref name="outputPath"/>, or a new file under %LOCALAPPDATA%\mele-mcp\screenshots. A path of the
    /// caller's is a full path ending in .png (a relative one would land in the server's working folder, and any other name could
    /// replace a package or a YAML), outside the game install like every file the server writes.
    /// </summary>
    internal static string OutputPath(MEGame game, string? outputPath)
    {
        var path = outputPath ?? DefaultPath(game);
        if (!System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException($"path '{path}': give a full path, such as C:\\shots\\a.png or /mnt/c/shots/a.png.");
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"path '{path}': the screenshot is a PNG, so the file name must end in .png.");
        // The guard's roots are LEC's settings: empty until LEC is initialized, and then it refuses nothing.
        LecHost.EnsureInitialized();
        ConfigFiles.EnsureWritable(path);
        return path;
    }

    private static Screenshot Capture(IntPtr window, string path, int maxWidth)
    {
        if (!Native.GetClientRect(window, out var client) || client.Right <= 0 || client.Bottom <= 0)
            throw new InvalidOperationException("The game window has no client area.");

        using var image = new Bitmap(client.Right, client.Bottom, PixelFormat.Format32bppArgb);
        string method;
        using (var g = Graphics.FromImage(image))
        {
            var hdc = g.GetHdc();
            var printed = Native.PrintWindow(window, hdc, PwClientOnly | PwRenderFullContent);
            g.ReleaseHdc(hdc);
            method = "PrintWindow";
            if (!printed || IsBlack(image))
            {
                var origin = new Native.Point();
                Native.ClientToScreen(window, ref origin);
                g.CopyFromScreen(origin.X, origin.Y, 0, 0, image.Size);
                method = "screen copy";
            }
        }

        var note = IsBlack(image) ? "The image is black: a loading screen, or the game is in exclusive fullscreen (use windowed or borderless)."
            : method == "screen copy" ? "Copied from the screen: anything covering the game window is in the picture." : null;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var saved = maxWidth > 0 && image.Width > maxWidth ? Scale(image, maxWidth) : new Bitmap(image);
        // Encoded in memory, then written beside the file and moved over it (AtomicFile), like every whole file the server writes.
        using var png = new MemoryStream();
        saved.Save(png, ImageFormat.Png);
        AtomicFile.WriteAllBytes(path, png.ToArray());
        return new Screenshot(path, GamePaths.ToWslPath(path), saved.Width, saved.Height, method, note);
    }

    private static string DefaultPath(MEGame game) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mele-mcp", "screenshots",
                               $"{game}_{DateTime.Now:yyyyMMdd_HHmmss}.png");

    /// <summary>True when a 32×18 grid of samples is all near-black.</summary>
    private static bool IsBlack(Bitmap image)
    {
        for (var y = 0; y < 18; y++)
        for (var x = 0; x < 32; x++)
        {
            var c = image.GetPixel((2 * x + 1) * image.Width / 64, (2 * y + 1) * image.Height / 36);
            if (c.R > 8 || c.G > 8 || c.B > 8) return false;
        }
        return true;
    }

    private static Bitmap Scale(Bitmap image, int width)
    {
        var height = (int)Math.Round(image.Height * (double)width / image.Width);
        var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, 0, 0, width, height);
        return scaled;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X, Y; }

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    }
}
