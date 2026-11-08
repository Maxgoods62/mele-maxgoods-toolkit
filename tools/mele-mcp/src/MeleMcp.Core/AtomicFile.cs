using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MeleMcp.Core;

/// <summary>
/// The one way the server writes a whole file (src/BannedSymbols.txt keeps File.WriteAll* here): the new content goes to a file
/// beside the target, which is then moved over it, so a crash, a full disk or a reader at the wrong moment finds the old file or
/// the new one, never part of one. Each write has its own temporary name, &lt;file&gt;.&lt;8 hex&gt;.tmp, so two writers of one file
/// never share it (MeleBridge uses &lt;file&gt;.tmp for its own), and every path that does not move it deletes it.
/// </summary>
public static class AtomicFile
{
    /// <summary>File.WriteAllText's encoding rules: UTF-8 without a byte order mark when none is given, else the encoding's preamble first.</summary>
    /// <returns>Whether the target was replaced: false only when <paramref name="beforeMove"/> returned false.</returns>
    [SuppressMessage("ApiDesign", "RS0030", Justification = "The one place whole files are written.")]
    public static bool WriteAllText(string path, string text, Encoding? encoding = null, Func<string, bool>? beforeMove = null) =>
        Write(path, temporary => File.WriteAllText(temporary, text, encoding ?? Utf8), beforeMove);

    /// <returns>Whether the target was replaced: false only when <paramref name="beforeMove"/> returned false.</returns>
    [SuppressMessage("ApiDesign", "RS0030", Justification = "The one place whole files are written.")]
    public static bool WriteAllBytes(string path, byte[] bytes, Func<string, bool>? beforeMove = null) =>
        Write(path, temporary => File.WriteAllBytes(temporary, bytes), beforeMove);

    /// <summary>For an API that writes the file at a path it is given (LEC's HuffmanCompression.SaveToTlkFile): it gets the temporary one.</summary>
    /// <param name="beforeMove">Runs once the temporary file is written and closed, with its path: to set its times, or to check that
    /// the target did not change meanwhile. False leaves the target as it was.</param>
    /// <returns>Whether the target was replaced: false only when <paramref name="beforeMove"/> returned false.</returns>
    public static bool Write(string path, Action<string> writeTemporary, Func<string, bool>? beforeMove = null)
    {
        var temporary = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        try
        {
            writeTemporary(temporary);
            if (beforeMove is not null && !beforeMove(temporary)) return false;
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            // Gone after the move; on any other way out it is ours to remove. Failing to remove it must not hide the failure that got here.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
}
