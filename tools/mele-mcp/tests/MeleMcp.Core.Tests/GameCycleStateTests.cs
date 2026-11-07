using System.Text;
using System.Text.Json.Nodes;

namespace MeleMcp.Core.Tests;

/// <summary>
/// The game_cycle state the 2026-11-07 sweep found implicit: log lines read while the game writes them, and the lock that keeps
/// two sessions from cycling, closing or installing one game at the same time. No game runs: the log is a file under
/// <see cref="TestSetup.Temp"/>.
/// </summary>
[TestClass]
public class GameCycleStateTests
{
    private static string Log(string name, string text)
    {
        var path = Path.Combine(TestSetup.NewFolder("debuglog-" + name), "DebugLogger.log");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// The DebugLogger flushes its buffer wherever it is full, so the log can end inside a line. LinesAfter used to return that
    /// part as a line: "Loading package synchronously: BIOA_NO" matched game_cycle's map pattern as the map BIOA_NO.
    /// </summary>
    [TestMethod]
    public void LinesAfter_LeavesALineStillBeingWritten_ForTheNextRead()
    {
        var path = Log("partial", "[10:00:00.000 info] one\n[10:00:01.000 info] Loading package synchronously: BIOA_NO");
        CollectionAssert.AreEqual(new[] { "[10:00:00.000 info] one" }, DebugLog.LinesAfter(path, 0));

        File.AppendAllText(path, "R10\n");
        CollectionAssert.AreEqual(new[] { "[10:00:00.000 info] one", "[10:00:01.000 info] Loading package synchronously: BIOA_NOR10" },
                                  DebugLog.LinesAfter(path, 0));
    }

    /// <summary>An offset taken with Length while a line was half written falls inside it: that line began before, so it is not "after".</summary>
    [TestMethod]
    public void LinesAfter_SkipsTheRestOfALineThatBeganBeforeTheOffset()
    {
        var path = Log("inside", "[10:00:00.000 info] old\n");
        File.AppendAllText(path, "[10:00:01.000 error] appLogErrorf: Bio");
        var offset = DebugLog.Length(path);
        File.AppendAllText(path, "WorldInfo.bHasPathNodes = True\n[10:00:02.000 info] new\n");

        CollectionAssert.AreEqual(new[] { "[10:00:02.000 info] new" }, DebugLog.LinesAfter(path, offset));
    }

    [TestMethod]
    public void LinesAfter_KeepsALineThatStartsAtTheOffset_AndReadsCrLf()
    {
        var path = Log("crlf", "a\r\nb\r\nc\r\n");
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, DebugLog.LinesAfter(path, 0));
        CollectionAssert.AreEqual(new[] { "b", "c" }, DebugLog.LinesAfter(path, 3), "offset 3 is where b starts");
        CollectionAssert.AreEqual(new[] { "b", "c" }, DebugLog.LinesAfter(path, 2), "offset 2 is inside a's line end");
        Assert.IsEmpty(DebugLog.LinesAfter(path, 9), "at the end");
        Assert.IsEmpty(DebugLog.LinesAfter(path, 50), "past the end: a log the next game start rewrote shorter");
    }

    [TestMethod]
    public void LinesAfter_NoNewlineYet_GivesNothing()
    {
        Assert.IsEmpty(DebugLog.LinesAfter(Log("empty", ""), 0));
        Assert.IsEmpty(DebugLog.LinesAfter(Log("none", "[10:00:00.000 info] still writ"), 0));
        Assert.IsEmpty(DebugLog.LinesAfter(Log("tail", "x\ny"), 1), "offset inside the first line, second not ended");
    }

    /// <summary>A name of this run only: the real game locks may be held by a server running on this machine.</summary>
    private static string LockName() => "MeleMcp.Test." + Guid.NewGuid().ToString("N");

    /// <summary>Two sessions' game_cycle on one game: the second waits, then is refused with the message, while the first runs.</summary>
    [TestMethod]
    public void GameLock_SecondTaker_IsRefusedWhileTheFirstHoldsIt()
    {
        var name = LockName();
        using var taken = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (GameLock.Take(name, TimeSpan.Zero, "first"))
            {
                taken.Set();
                release.Wait();
            }
        });
        holder.Start();
        try
        {
            Assert.IsTrue(taken.Wait(5000));
            var refused = Assert.Throws<InvalidOperationException>(() => GameLock.Take(name, TimeSpan.FromMilliseconds(200), "busy now"));
            Assert.AreEqual("busy now", refused.Message);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
        using (GameLock.Take(name, TimeSpan.FromSeconds(1), "free again")) { }
    }

    /// <summary>A server stopped with the lock held (stop-server.ps1 kills it): the next taker gets it, not a refusal.</summary>
    [TestMethod]
    public void GameLock_LeftByAThreadThatEnded_IsTakenOver()
    {
        var name = LockName();
        var holder = new Thread(() => GameLock.Take(name, TimeSpan.Zero, "first"));
        holder.Start();
        holder.Join();
        using (GameLock.Take(name, TimeSpan.FromSeconds(1), "abandoned lock refused")) { }
    }

    /// <summary>game_cycle holds the game's lock and its mod_install takes it again on the same thread.</summary>
    [TestMethod]
    public void GameLock_SameThread_TakesItAgain_AndBothReleasesFreeIt()
    {
        var name = LockName();
        using (GameLock.Take(name, TimeSpan.Zero, "first"))
        using (GameLock.Take(name, TimeSpan.Zero, "nested refused")) { }

        var other = Task.Run(() => { using (GameLock.Take(name, TimeSpan.FromSeconds(1), "still held")) { } });
        Assert.IsTrue(other.Wait(5000));
    }

    private static JsonObject Loaded() => new()
    {
        ["map"] = "BIOA_NOR10",
        ["begunPlay"] = true,
        ["pawn"] = "BIOA_NOR10.TheWorld.PersistentLevel.SFXPawn_Player_0",
        ["streaming"] = new JsonObject { ["levels"] = 40, ["pending"] = 0 },
        ["loadingMovie"] = new JsonObject { ["playing"] = false, ["state"] = "LMS_NotPlaying" },
        ["bPlayersOnly"] = true,
    };

    /// <summary>
    /// When game_cycle may send console input after a load: MeleBridge's status, read as scenario.py's wait_loaded reads it,
    /// except bPlayersOnly (an unfocused LE1 holds it), and an unknown loading movie does not hold the wait.
    /// </summary>
    [TestMethod]
    public void LoadProblem_ReadsTheStatusFields()
    {
        Assert.IsNull(GameCycle.LoadProblem(Loaded(), "bioa_nor10"), "loaded, paused by focus loss, map case ignored");

        var cases = new (Action<JsonObject> Change, string Why)[]
        {
            (s => s["map"] = "EntryMenu", "the map is EntryMenu"),
            (s => s["map"] = null, "the map is none"),
            (s => s["begunPlay"] = false, "the world has not begun play"),
            (s => s["begunPlay"] = null, "the world has not begun play"),
            (s => s["pawn"] = null, "no pawn"),
            (s => s["streaming"]!["pending"] = 3, "3 streaming levels pending"),
            (s => s["loadingMovie"]!["playing"] = true, "loading movie LMS_NotPlaying"),
            (s => s["loadingMovie"]!["state"] = "LMS_FadingOut", "loading movie LMS_FadingOut"),
        };
        foreach (var (change, why) in cases)
        {
            var status = Loaded();
            change(status);
            Assert.AreEqual(why, GameCycle.LoadProblem(status, "BIOA_NOR10"));
        }

        var unknown = Loaded();
        unknown["loadingMovie"] = null;
        unknown["streaming"] = null;
        Assert.IsNull(GameCycle.LoadProblem(unknown, "BIOA_NOR10"), "sections the bridge could not read");
    }
}
