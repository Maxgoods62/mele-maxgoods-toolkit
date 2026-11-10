using System.Text;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core.Tests;

/// <summary>
/// ME3Tweaks Mod Manager as the tools drive it: its log read while M3 writes it, a merge compile's failure line tied to its
/// manifest, and the lock around a merge compile. M3 never runs: the log is a file under <see cref="TestSetup.Temp"/> and the
/// compile is a stand-in.
/// </summary>
[TestClass]
public class M3StateTests
{
    /// <summary>A log folder with one empty log in it, as M3 has on a new day before it writes.</summary>
    private static (string Dir, string File) EmptyLog(string name)
    {
        var dir = TestSetup.NewFolder("m3log-" + name);
        var file = Path.Combine(dir, "modmanagerlog-20261110.txt");
        File.WriteAllBytes(file, []);
        return (dir, file);
    }

    private static void Append(string file, byte[] bytes)
    {
        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }

    /// <summary>
    /// M3 writes its log while the tools read it, so a read can end inside a line. ReadNew used to return that part, and then
    /// moved its offset to a length asked for after the read, which skipped whatever M3 appended in between.
    /// </summary>
    [TestMethod]
    public void ReadNew_LeavesALineM3IsStillWriting_ForTheNextRead()
    {
        var (dir, file) = EmptyLog("partial");
        var offsets = ModInstaller.LogOffsets(dir);

        File.AppendAllText(file, "one\ntwo");
        Assert.AreEqual("one\n", ModInstaller.ReadNew(dir, offsets));
        File.AppendAllText(file, " more\n");
        Assert.AreEqual("two more\n", ModInstaller.ReadNew(dir, offsets));
        Assert.AreEqual("", ModInstaller.ReadNew(dir, offsets));
    }

    /// <summary>A read that ends inside a UTF-8 character used to decode its half as a broken character, and the other half as another.</summary>
    [TestMethod]
    public void ReadNew_ACharacterCutInTwo_IsReadWhole()
    {
        var (dir, file) = EmptyLog("utf8");
        var offsets = ModInstaller.LogOffsets(dir);
        var bytes = Encoding.UTF8.GetBytes("mod: Café\n");

        Append(file, bytes[..^2]);
        Assert.AreEqual("", ModInstaller.ReadNew(dir, offsets));
        Append(file, bytes[^2..]);
        Assert.AreEqual("mod: Café\n", ModInstaller.ReadNew(dir, offsets));
    }

    /// <summary>M3 starts a new log file each day: one that was not there when the offsets were taken is read from its start.</summary>
    [TestMethod]
    public void ReadNew_AFileThatAppearsLater_IsReadFromItsStart()
    {
        var (dir, _) = EmptyLog("newday");
        var offsets = ModInstaller.LogOffsets(dir);

        File.WriteAllText(Path.Combine(dir, "modmanagerlog-20261111.txt"), "new day\n", new UTF8Encoding(false));
        Assert.AreEqual("new day\n", ModInstaller.ReadNew(dir, offsets));
    }

    private const string One = @"C:\Mods\One\MergeMods\One.json";
    private const string Two = @"C:\Mods\Two\MergeMods\Two.json";
    private const string Failure = "2026-11-10 10:00:02.000 +01:00 [ERR] [M3] Error compiling m3m mod file: bad manifest";

    /// <summary>The line M3 logs when it begins to compile <paramref name="manifest"/>: it names the path it was given.</summary>
    private static string Begin(string manifest) =>
        $"2026-11-10 10:00:01.000 +01:00 [INF] [M3] M3MCompiler: Serializing {manifest} to {Path.ChangeExtension(manifest, ".m3m")}";

    private static string Log(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    [TestMethod]
    public void CompileFailed_ItsOwnFailure_Counts()
    {
        Assert.IsTrue(MergeMods.CompileFailed(Log(Begin(One), Failure), One));
    }

    /// <summary>
    /// Another session's compile fails while this one runs: the failure line names no file, and it used to count as this build's,
    /// which then took its own good .m3m out of moddesc.ini.
    /// </summary>
    [TestMethod]
    public void CompileFailed_AnotherManifestsFailure_DoesNotCount()
    {
        Assert.IsFalse(MergeMods.CompileFailed(Log(Begin(Two), Failure), One));
    }

    /// <summary>A failure M3 logged before it began this manifest's compile belongs to an earlier one.</summary>
    [TestMethod]
    public void CompileFailed_AFailureBeforeItsBeginLine_DoesNotCount()
    {
        var log = Log(Begin(Two), Failure, Begin(One), "2026-11-10 10:00:03.000 +01:00 [INF] [M3] M3MCompiler: Serializing merge mod object");
        Assert.IsFalse(MergeMods.CompileFailed(log, One));
    }

    /// <summary>Windows paths ignore case, so a begin line that spells this manifest in another case is still its begin line.</summary>
    [TestMethod]
    public void CompileFailed_ThePathsCaseDoesNotMatter()
    {
        Assert.IsTrue(MergeMods.CompileFailed(Log(Begin(One.ToUpperInvariant()), Failure), One));
    }

    [TestMethod]
    public void CompileFailed_NoFailureLine_DoesNotCount()
    {
        var log = Log(Begin(One), @"2026-11-10 10:00:02.000 +01:00 [INF] [M3] M3MCompiler: Writing final result to C:\Mods\One\MergeMods\One.m3m");
        Assert.IsFalse(MergeMods.CompileFailed(log, One));
    }

    /// <summary>A name of this run only: the real M3 lock may be held by a server running on this machine.</summary>
    private static string LockName() => "MeleMcp.Test." + Guid.NewGuid().ToString("N");

    /// <summary>
    /// An LE1 mod with no merge mod yet, and one change to a class default in a core file, set to its own value. Checks against
    /// the game's files, read-only; the mod is written under <see cref="TestSetup.Temp"/>.
    /// </summary>
    private static (string Root, string ModDesc, string Target, MergeChange[] Changes) NewMod(string folder)
    {
        TestSetup.RequireGame();
        var (target, entry, property, value) = CacheStateTests.SimpleIntProperty(MEGame.LE1, "IpDrv.pcc", "GFxUI.pcc", "Engine.pcc");
        var root = TestSetup.NewFolder(folder);
        var modDesc = Path.Combine(root, "moddesc.ini");
        File.WriteAllText(modDesc, "[ModManager]\r\ncmmver = 9.0\r\n\r\n[ModInfo]\r\ngame = LE1\r\nmodname = Lock test\r\n\r\n"
                                   + "[BASEGAME]\r\nmoddir = .\r\n");
        MergeChange[] changes = [new(entry, [new MergeProperty(property, null, value)])];
        return (root, modDesc, target, changes);
    }

    /// <summary>
    /// mergemod_build while another session installs or compiles through M3: it took no lock, so the two read each other's lines
    /// in M3's one log. Now it waits for the lock, and a busy M3 refuses the build before it has written anything.
    /// </summary>
    [TestMethod]
    public void MergeBuild_WhileAnotherCallHoldsM3_IsRefusedBeforeAnyWrite()
    {
        var (root, modDesc, target, changes) = NewMod("merge-lock-busy");
        var before = File.ReadAllText(modDesc);
        var name = LockName();
        var compiled = false;
        var route = new MergeMods.M3Route(() => GameLock.Take(name, TimeSpan.FromMilliseconds(200), "M3 is busy"),
            (_, _, _, _, _) => { compiled = true; return ("compiled", 0d, Array.Empty<string>()); });

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
            var refused = Assert.Throws<InvalidOperationException>(() =>
                MergeMods.Build(MEGame.LE1, root, "LockTest", target, changes, null, false, compile: true, timeoutSeconds: 60, route));
            Assert.AreEqual("M3 is busy", refused.Message);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
        Assert.IsFalse(compiled, "the compile ran without the lock");
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "MergeMods")), "the manifest was written before the lock was taken");
        Assert.AreEqual(before, File.ReadAllText(modDesc));
    }

    /// <summary>The compile runs with the M3 lock held, and the build lets go of it when it returns.</summary>
    [TestMethod]
    public void MergeBuild_Compiles_WithTheM3LockHeld_AndLetsGoAfter()
    {
        var (root, modDesc, target, changes) = NewMod("merge-lock-held");
        var name = LockName();
        var heldDuringCompile = false;
        var route = new MergeMods.M3Route(() => GameLock.Take(name, TimeSpan.Zero, "M3 is busy"), (_, m3m, _, _, _) =>
        {
            // From another thread: this one holds the lock and may take it again.
            var probe = new Thread(() =>
            {
                try { using (GameLock.Take(name, TimeSpan.FromMilliseconds(100), "held")) { } }
                catch (InvalidOperationException) { heldDuringCompile = true; }
            });
            probe.Start();
            probe.Join();
            File.WriteAllBytes(m3m, "M3MM\u0001"u8.ToArray());
            return ("compiled", 0.1, Array.Empty<string>());
        });

        var result = MergeMods.Build(MEGame.LE1, root, "LockTest2", target, changes, null, false, compile: true, timeoutSeconds: 60, route);
        Assert.AreEqual("compiled", result.Stage, string.Join("; ", result.Checks.SelectMany(c => c.Errors).Select(e => e.Text)));
        Assert.IsTrue(heldDuringCompile, "the compile ran without the M3 lock");
        StringAssert.Contains(File.ReadAllText(modDesc), "LockTest2.m3m");

        // Caught here: an exception nobody catches on a raw thread ends the whole test run.
        var free = false;
        var after = new Thread(() =>
        {
            try
            {
                using (GameLock.Take(name, TimeSpan.FromSeconds(1), "still held")) { }
                free = true;
            }
            catch (InvalidOperationException) { }
        });
        after.Start();
        after.Join();
        Assert.IsTrue(free, "the build kept the M3 lock after it returned");
    }
}
