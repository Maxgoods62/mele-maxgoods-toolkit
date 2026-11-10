using System.IO.MemoryMappedFiles;
using System.Text;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <summary>
/// What every mele-mcp server on this machine has to agree on about a game's LEX Interop link. Replies carry no request id, and
/// the game reports the player's position once, to whichever server asked. So when two sessions take turns, one server's memory
/// is not enough: the late reply to a request that timed out in one would answer the next request of its kind in the other, and
/// a position reported to one would be missing in the other. Kept in a named memory section that every server opens. It lives
/// while one of them has it open, and starts zeroed (settled, no position) when the first one creates it, as a single server
/// always started. Each game's slot belongs to one game process (<see cref="Bind"/>). Read and written only while holding the
/// reply window (<see cref="GameBridge.HoldReplies"/>), which is one lock for the whole machine.
/// </summary>
internal sealed class LexLinkShared : IDisposable
{
    public const string SectionName = @"Local\MeleMcp.LexLink.1";

    // One slot per game: the game's process id (int), its start (long), unsettled (int), the position's length in bytes (int),
    // the position's text (UTF-8).
    private const int SlotSize = 512, IdAt = 0, StartedAt = 4, UnsettledAt = 12, LengthAt = 16, TextAt = 20, MaxText = SlotSize - TextAt;

    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;

    /// <param name="name"><see cref="SectionName"/> everywhere but in tests, which use a name of their own.</param>
    public LexLinkShared(string name)
    {
        map = MemoryMappedFile.CreateOrOpen(name, 3 * SlotSize);
        view = map.CreateViewAccessor();
    }

    private static long Slot(MEGame game) => game switch
    {
        MEGame.LE1 => 0,
        MEGame.LE2 => SlotSize,
        MEGame.LE3 => 2 * SlotSize,
        _ => throw new ArgumentException($"No live link for {game}."),
    };

    /// <summary>
    /// Gives the game's slot to this game process. A slot left by another process (an earlier run of the game) is cleared first:
    /// its late replies and its positions went with it.
    /// </summary>
    public void Bind(MEGame game, int processId, long started)
    {
        var at = Slot(game);
        if (view.ReadInt32(at + IdAt) == processId && view.ReadInt64(at + StartedAt) == started) return;
        view.Write(at + LengthAt, 0);
        view.Write(at + UnsettledAt, 0);
        view.Write(at + IdAt, processId);
        view.Write(at + StartedAt, started);
    }

    /// <summary>A request to this game got no reply in time, from any server, and its reply may still come.</summary>
    public bool GetUnsettled(MEGame game) => view.ReadInt32(Slot(game) + UnsettledAt) != 0;

    public void SetUnsettled(MEGame game, bool value) => view.Write(Slot(game) + UnsettledAt, value ? 1 : 0);

    /// <summary>The last player position this game process reported to any server, or null.</summary>
    public string? GetPlayerGps(MEGame game)
    {
        var at = Slot(game);
        var length = view.ReadInt32(at + LengthAt);
        if (length <= 0 || length > MaxText) return null;
        var bytes = new byte[length];
        view.ReadArray(at + TextAt, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The length is written last: a server stopped half way leaves "no position", never half a text.</summary>
    public void SetPlayerGps(MEGame game, string reply)
    {
        var at = Slot(game);
        var bytes = Encoding.UTF8.GetBytes(reply);
        view.Write(at + LengthAt, 0);
        if (bytes.Length > MaxText) return;
        view.WriteArray(at + TextAt, bytes, 0, bytes.Length);
        view.Write(at + LengthAt, bytes.Length);
    }

    public void Dispose()
    {
        view.Dispose();
        map.Dispose();
    }
}
