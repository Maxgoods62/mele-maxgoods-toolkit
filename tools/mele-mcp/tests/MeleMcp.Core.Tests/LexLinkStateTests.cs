using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core.Tests;

/// <summary>
/// What the servers of two sessions share about a game's LEX Interop link (<see cref="LexLinkShared"/>): a request that timed
/// out, and the last player position the game reported. Each test uses a memory section of its own name, so a server running on
/// this machine is not touched. No game.
/// </summary>
[TestClass]
public class LexLinkStateTests
{
    private const string Gps = "PATHFINDING_GPS PLAYERLOC=100,200,300, PLAYERROT=0,16384,0";

    /// <summary>A name of this run only: the real section may be open in a server running on this machine.</summary>
    private static string Name() => @"Local\MeleMcp.Test." + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Session A's request timed out and A saw where Shepard stood; session B asks next. B must fence first, and must find the
    /// position the game will not report again. Each game keeps its own slot.
    /// </summary>
    [TestMethod]
    public void ASecondServer_SeesWhatTheFirstLeft()
    {
        var name = Name();
        using var first = new LexLinkShared(name);
        using var second = new LexLinkShared(name);
        first.Bind(MEGame.LE1, 100, 5);
        first.SetUnsettled(MEGame.LE1, true);
        first.SetPlayerGps(MEGame.LE1, Gps);

        Assert.IsTrue(second.GetUnsettled(MEGame.LE1), "the timeout of the first server");
        Assert.AreEqual(Gps, second.GetPlayerGps(MEGame.LE1), "the position the first server got");
        Assert.IsFalse(second.GetUnsettled(MEGame.LE2), "another game's slot");
        Assert.IsNull(second.GetPlayerGps(MEGame.LE3), "another game's slot");
    }

    /// <summary>
    /// Binding the game process that already owns the slot keeps what it left. A new game process (a later start under the same
    /// id, or another id) clears it: the late replies and the positions of the earlier run went with it.
    /// </summary>
    [TestMethod]
    public void Bind_TheSameGameProcess_KeepsTheSlot_AndANewOneClearsIt()
    {
        using var shared = new LexLinkShared(Name());
        shared.Bind(MEGame.LE1, 100, 5);
        shared.SetUnsettled(MEGame.LE1, true);
        shared.SetPlayerGps(MEGame.LE1, Gps);

        shared.Bind(MEGame.LE1, 100, 5);
        Assert.IsTrue(shared.GetUnsettled(MEGame.LE1), "same process: kept");
        Assert.AreEqual(Gps, shared.GetPlayerGps(MEGame.LE1), "same process: kept");

        shared.Bind(MEGame.LE1, 100, 6);
        Assert.IsFalse(shared.GetUnsettled(MEGame.LE1), "same id, later start: a new game process");
        Assert.IsNull(shared.GetPlayerGps(MEGame.LE1), "same id, later start: a new game process");

        shared.SetUnsettled(MEGame.LE1, true);
        shared.SetPlayerGps(MEGame.LE1, Gps);
        shared.Bind(MEGame.LE1, 101, 6);
        Assert.IsFalse(shared.GetUnsettled(MEGame.LE1), "another id: a new game process");
        Assert.IsNull(shared.GetPlayerGps(MEGame.LE1), "another id: a new game process");
    }

    /// <summary>A text longer than the slot holds is not kept, and the earlier one is not handed back as if it were the latest.</summary>
    [TestMethod]
    public void PlayerGps_TooLongToKeep_IsNoPosition()
    {
        using var shared = new LexLinkShared(Name());
        shared.Bind(MEGame.LE1, 100, 5);
        shared.SetPlayerGps(MEGame.LE1, Gps);
        shared.SetPlayerGps(MEGame.LE1, new string('x', 600));

        Assert.IsNull(shared.GetPlayerGps(MEGame.LE1));
    }

    /// <summary>The section lives while a server has it open: with the last one gone, the next server starts settled and with no position, as a single server always did.</summary>
    [TestMethod]
    public void TheLastServerGone_TheNextStartsClean()
    {
        var name = Name();
        using (var gone = new LexLinkShared(name))
        {
            gone.Bind(MEGame.LE1, 100, 5);
            gone.SetUnsettled(MEGame.LE1, true);
            gone.SetPlayerGps(MEGame.LE1, Gps);
        }

        using var next = new LexLinkShared(name);
        Assert.IsFalse(next.GetUnsettled(MEGame.LE1));
        Assert.IsNull(next.GetPlayerGps(MEGame.LE1));
    }
}
