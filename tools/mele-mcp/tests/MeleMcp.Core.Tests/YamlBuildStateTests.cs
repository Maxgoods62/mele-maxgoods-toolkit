using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core.Tests;

/// <summary>
/// The YAML builds' and id_alloc's states from the 2026-11-07 implicit-state-machine sweep: the out= DLC copy (M11), the files a
/// build has written when a later step fails (M23), backups in one millisecond, and id_alloc's reservations (M22). Every file is
/// made under <see cref="TestSetup.Temp"/>: the packages are empty ones LEC writes, and no test opens a game file.
/// </summary>
[TestClass]
public class YamlBuildStateTests
{
    private static string Yaml(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        return path;
    }

    /// <summary>A source DLC folder with three files, for out= to copy.</summary>
    private static string SourceDlc(string root)
    {
        var cooked = Path.Combine(root, "src", "DLC_MOD_T", "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        foreach (var name in new[] { "A.pcc", "B.pcc", "C.pcc" }) File.WriteAllText(Path.Combine(cooked, name), name);
        File.WriteAllText(Path.Combine(root, "src", "DLC_MOD_T", "AutoLoad.ini"), "[Packages]\n");
        return cooked;
    }

    /// <summary>
    /// M11a: the copy made the DLC folder under out= before its files. A copy that stopped half way left a folder that every later
    /// build took as done (it copies only when the folder is missing), so the files it lacked were never copied, and a missing
    /// package was then created empty in out=.
    /// </summary>
    [TestMethod]
    public void OutCopy_StoppedHalfWay_IsRedoneByTheNextBuild()
    {
        LecHost.EnsureInitialized();
        var root = TestSetup.NewFolder("out-copy-partial");
        var source = SourceDlc(root);
        var outRoot = Path.Combine(root, "out");
        var copy = Path.Combine(outRoot, "DLC_MOD_T");
        var package = Path.Combine(source, "BIOG_DLC_MOD_T_Plot.pcc");
        var notes = new List<string>();

        // A file another program holds (LEX, an antivirus) stops File.Copy.
        using (new FileStream(Path.Combine(source, "B.pcc"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => YamlFiles.Target(MEGame.LE1, package, root, outRoot, notes));
        }
        Assert.IsFalse(Directory.Exists(copy), "a copy that stopped left a DLC folder the next build takes as done");
        Assert.IsFalse(Directory.Exists(YamlFiles.PartialCopy(copy)), "the half copy was left beside it");

        var (path, missing) = YamlFiles.Target(MEGame.LE1, package, root, outRoot, notes);
        Assert.AreEqual(Path.Combine(copy, "CookedPCConsole", "BIOG_DLC_MOD_T_Plot.pcc"), path);
        Assert.IsTrue(missing);
        foreach (var name in new[] { @"CookedPCConsole\A.pcc", @"CookedPCConsole\B.pcc", @"CookedPCConsole\C.pcc", "AutoLoad.ini" })
        {
            Assert.IsTrue(File.Exists(Path.Combine(copy, name)), $"{name} is not in the copy");
        }
        Assert.IsTrue(notes.Any(n => n.Contains("copied")), string.Join("\n", notes));
    }

    /// <summary>
    /// M11b: the copy is a snapshot (yaml-reference.md, Building into out). A source file changed or added after it never
    /// reached out=, and nothing said so: a GlobalTlk added later was refused with no word of the copy.
    /// </summary>
    [TestMethod]
    public void OutCopy_IsASnapshot_AndSaysWhichSourceFilesChangedSince()
    {
        LecHost.EnsureInitialized();
        var root = TestSetup.NewFolder("out-copy-stale");
        var source = SourceDlc(root);
        var outRoot = Path.Combine(root, "out");
        var copy = Path.Combine(outRoot, "DLC_MOD_T");
        var package = Path.Combine(source, "BIOG_DLC_MOD_T_Plot.pcc");
        var notes = new List<string>();
        YamlFiles.Target(MEGame.LE1, package, root, outRoot, notes);

        // A build changes the copy (newer than its source): that is no source change.
        File.SetLastWriteTimeUtc(Path.Combine(copy, "CookedPCConsole", "C.pcc"), DateTime.UtcNow.AddMinutes(5));
        notes.Clear();
        YamlFiles.Target(MEGame.LE1, package, root, outRoot, notes);
        Assert.IsEmpty(notes, string.Join("\n", notes));

        File.WriteAllText(Path.Combine(source, "A.pcc"), "A.pcc changed");
        File.SetLastWriteTimeUtc(Path.Combine(source, "A.pcc"), DateTime.UtcNow.AddMinutes(10));
        var added = Path.Combine(source, "DLC_MOD_T_GlobalTlk.pcc");
        File.WriteAllText(added, "tlk");
        // A missing file counts when it was created after the copy: the file system's clock may give both the same tick.
        File.SetCreationTimeUtc(added, DateTime.UtcNow.AddMinutes(10));
        YamlFiles.Target(MEGame.LE1, package, root, outRoot, notes);
        var note = notes.Single();
        StringAssert.Contains(note, "2 source file(s)");
        StringAssert.Contains(note, @"CookedPCConsole\A.pcc");
        StringAssert.Contains(note, $"Delete {copy}");
        Assert.AreEqual("A.pcc", File.ReadAllText(Path.Combine(copy, "CookedPCConsole", "A.pcc")), "the copy was refreshed: a snapshot is kept as it is");

        var refused = Assert.Throws<ArgumentException>(() => YamlFiles.InOut(added, outRoot, "globalTlk"));
        StringAssert.Contains(refused.Message, $"{copy} is the copy an earlier build made");
    }

    /// <summary>
    /// A 'package' that climbs out of its DLC folder with '..' landed outside out=: the path from the DLC folder on was joined to
    /// out= before '..' was resolved. An out= build changes nothing outside out=.
    /// </summary>
    [TestMethod]
    public void Out_APackageThatClimbsOutOfItsDlcFolder_IsRefused()
    {
        LecHost.EnsureInitialized();
        var root = TestSetup.NewFolder("out-escape");
        // The DLC folder beside the YAML makes the YAML's folder the mod root, so M3's library is never asked.
        Directory.CreateDirectory(Path.Combine(root, "DLC_MOD_T", "CookedPCConsole"));
        var outRoot = Path.Combine(root, "out");
        Directory.CreateDirectory(outRoot);
        var notes = new List<string>();

        foreach (var package in new[] { @"DLC_MOD_T\..\..\escaped.pcc", @"DLC_MOD_T\..\escaped.pcc", "DLC_MOD_T/CookedPCConsole/../../../escaped.pcc" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => { YamlFiles.Target(MEGame.LE1, package, root, outRoot, notes); }, package);
        }

        var (path, missing) = YamlFiles.Target(MEGame.LE1, @"DLC_MOD_T\CookedPCConsole\A.pcc", root, outRoot, notes);
        Assert.AreEqual(Path.Combine(outRoot, @"DLC_MOD_T\CookedPCConsole\A.pcc"), path);
        Assert.IsTrue(missing);
    }

    /// <summary>
    /// M23a: plot_build saves the package, then each GlobalTlk, then the AutoLoad.ini. A failure on a GlobalTlk threw the bare
    /// error: the package stayed saved and its backup path was lost with the exception.
    /// </summary>
    [TestMethod]
    public void PlotBuild_FailingAfterThePackageIsSaved_NamesItAndItsBackup()
    {
        LecHost.EnsureInitialized();
        var root = TestSetup.NewFolder("plot-partial");
        var cooked = Path.Combine(root, "DLC_MOD_T", "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        var package = Path.Combine(cooked, "BIOG_DLC_MOD_T_Plot.pcc");
        // A package with no BioTlkFile export: TalkFiles.Set refuses it, after the plot package is saved.
        var globalTlk = Path.Combine(cooked, "DLC_MOD_T_GlobalTlk.pcc");
        MEPackageHandler.CreateAndSavePackage(globalTlk, MEGame.LE1);
        var yaml = Yaml(root, "plot.yaml", $$"""
            package: '{{package}}'
            autoload: none
            globalTlk: ['{{globalTlk}}']
            transitions:
              8421: [bool 8421 = true]
            quests:
              8421:
                title: { strref: 74210001, en: "Test quest" }
                tasks:
                  - name: { strref: 74210003, en: "Test task" }
            """);

        var e = Assert.Throws<PartialBuildException>(() => PlotBuilder.Build(MEGame.LE1, yaml, null));
        Assert.IsInstanceOfType<ArgumentException>(e.InnerException);
        StringAssert.Contains(e.Message, "has no BioTlkFile export");
        Assert.IsTrue(e.Written.All(w => w.File == package), string.Join("; ", e.Written));
        Assert.IsTrue(e.Written.Any(w => w.Backup is null), "the package the build created is not named");
        var backup = e.Written.Single(w => w.Backup is not null).Backup!;
        Assert.IsTrue(File.Exists(backup), backup);
        StringAssert.Contains(e.Message, backup);
    }

    /// <summary>
    /// M23a across files: mod_build stops at the first failing file, and the files built before it stay saved. The error named
    /// only the failing file; it now names the earlier files' writes and their backups too.
    /// </summary>
    [TestMethod]
    public void ModBuild_FailingInALaterFile_NamesTheFilesEarlierFilesWrote()
    {
        LecHost.EnsureInitialized();
        var root = TestSetup.NewFolder("mod-partial");
        var cooked = Path.Combine(root, "DLC_MOD_T", "CookedPCConsole");
        var first = Path.Combine(cooked, "BIOG_DLC_MOD_T_Plot.pcc");
        Yaml(root, "a.yaml", $$"""
            package: '{{first}}'
            autoload: none
            transitions:
              8421: [bool 8421 = true]
            """);
        // Journal text with nowhere to write it: refused before this file writes anything.
        Yaml(root, "b.yaml", $$"""
            package: '{{Path.Combine(cooked, "BIOG_DLC_MOD_T_Plot2.pcc")}}'
            autoload: none
            quests:
              8422:
                title: { strref: 74210002, en: "Test quest" }
                tasks:
                  - name: { strref: 74210004, en: "Test task" }
            """);

        var e = Assert.Throws<PartialBuildException>(() => ModBuilder.Build(MEGame.LE1, root));
        StringAssert.Contains(e.Message, "b.yaml: The file gives journal text but no 'globalTlk'");
        Assert.IsTrue(e.Written.Any(w => w.File == first && w.Backup is not null && File.Exists(w.Backup)), string.Join("; ", e.Written));
        Assert.IsFalse(File.Exists(Path.Combine(cooked, "BIOG_DLC_MOD_T_Plot2.pcc")));
    }

    /// <summary>Two backups of one file name in the same millisecond: File.Copy threw on the second, after the build had saved other files.</summary>
    [TestMethod]
    public void Backup_ManyInOneMillisecond_EachGetsItsOwnFile()
    {
        var folder = TestSetup.NewFolder("backup-burst");
        var file = Path.Combine(folder, "DLC_MOD_T_GlobalTlk.pcc");
        File.WriteAllText(file, "tlk");

        var backups = Enumerable.Range(0, 50).Select(_ => PackageWriter.Backup(file)).ToList();
        Assert.HasCount(50, backups.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(backups.All(b => File.Exists(b) && Path.GetFileName(b).EndsWith("_DLC_MOD_T_GlobalTlk.pcc")), string.Join("\n", backups));
    }

    private static (List<int> Free, List<IdUse> Used, List<string> Notes) Choose(string ledger, int from, int count, string? owner, DateTime now,
                                                                                   params IdAllocator.Source[] sources)
    {
        var notes = new List<string>();
        var (free, used, _) = IdAllocator.Choose("bool", null, from, count, sources, owner, ledger, now, notes);
        return (free, used, notes);
    }

    /// <summary>M22c: from + 1,000,000 overflowed for a block near int.MaxValue, so the search ran no id and said "Only 0 free ids".</summary>
    [TestMethod]
    public void IdAlloc_FromNearIntMax_SearchesUpToIt()
    {
        var ledger = Path.Combine(TestSetup.NewFolder("ids-overflow"), "ids.json");
        var (free, _, notes) = Choose(ledger, int.MaxValue - 5, 10, "DLC_MOD_T", DateTime.UtcNow);

        CollectionAssert.AreEqual(Enumerable.Range(int.MaxValue - 5, 6).ToList(), free);
        Assert.IsTrue(notes.Any(n => n == $"Only 6 free ids between {int.MaxValue - 5} and {int.MaxValue}."), string.Join("\n", notes));
    }

    /// <summary>
    /// M22a: an id is seen by a scan only once installed, so two id_alloc calls before a build and install got the same ids. The
    /// ids handed out are now reserved: other owners skip them, the same owner gets them again.
    /// </summary>
    [TestMethod]
    public void IdAlloc_TwoCallsBeforeAnInstall_GetTheirOwnIds()
    {
        var ledger = Path.Combine(TestSetup.NewFolder("ids-reserve"), "ids.json");
        var now = DateTime.UtcNow;
        var used = new IdAllocator.Source("base game: BIOC_Materials.pcc", [8423]);

        var a = Choose(ledger, 8421, 3, "DLC_MOD_A", now, used);
        CollectionAssert.AreEqual(new[] { 8421, 8422, 8424 }, a.Free);
        Assert.IsTrue(a.Notes.Any(n => n.StartsWith("Reserved bool 8421-8422, 8424 for 'DLC_MOD_A'")), string.Join("\n", a.Notes));

        var b = Choose(ledger, 8421, 3, "DLC_MOD_B", now, used);
        CollectionAssert.AreEqual(new[] { 8425, 8426, 8427 }, b.Free);
        Assert.IsTrue(b.Used.Any(u => u.Id == 8421 && u.Sources.Single().StartsWith("reserved by id_alloc for 'DLC_MOD_A'")), string.Join("; ", b.Used.Select(u => $"{u.Id} {string.Join(", ", u.Sources)}")));

        CollectionAssert.AreEqual(a.Free, Choose(ledger, 8421, 3, "dlc_mod_a", now, used).Free, "the same owner (any case) gets its ids again");

        // No owner: held against every later call, its own too.
        var c = Choose(ledger, 8421, 1, null, now, used);
        CollectionAssert.AreEqual(new[] { 8428 }, c.Free);
        CollectionAssert.AreEqual(new[] { 8429 }, Choose(ledger, 8421, 1, null, now, used).Free);
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(ledger)!, Path.GetFileName(ledger) + ".*tmp"), "the ledger is written beside, then moved over");
    }

    /// <summary>
    /// A reservation ends when a source every call sees (an installed DLC) has the id, or after 30 days. A folder passed in
    /// (extraDlcFolders) does not end it: another call may not pass that folder.
    /// </summary>
    [TestMethod]
    public void IdAlloc_Reservations_EndOnceInstalledOrAfter30Days()
    {
        var ledger = Path.Combine(TestSetup.NewFolder("ids-release"), "ids.json");
        var now = DateTime.UtcNow;
        Choose(ledger, 100, 3, "DLC_MOD_A", now);

        var library = new IdAllocator.Source(@"DLC_MOD_A (D:\library\DLC_MOD_A): BIOG_DLC_MOD_A_Plot.pcc", [100, 101, 102], Installed: false);
        Choose(ledger, 200, 1, "DLC_MOD_B", now, library);
        using (var held = IdReservations.Open(ledger, now))
        {
            Assert.AreEqual("100-102", IdReservations.Text(held.Entries.Where(e => e.Owner == "DLC_MOD_A").SelectMany(e => Enumerable.Range(e.First, e.Last - e.First + 1))));
        }

        var installed = new IdAllocator.Source("DLC_MOD_A: BIOG_DLC_MOD_A_Plot.pcc", [100]);
        var b = Choose(ledger, 100, 1, "DLC_MOD_B", now, installed);
        CollectionAssert.AreEqual(new[] { 103 }, b.Free);
        Assert.IsTrue(b.Notes.Any(n => n.StartsWith("Reservations ended for bool 100")), string.Join("\n", b.Notes));
        using (var held = IdReservations.Open(ledger, now))
        {
            Assert.AreEqual("101-102", IdReservations.Text(held.Entries.Where(e => e.Owner == "DLC_MOD_A").SelectMany(e => Enumerable.Range(e.First, e.Last - e.First + 1))));
        }

        CollectionAssert.AreEqual(new[] { 101 }, Choose(ledger, 101, 1, "DLC_MOD_C", now + TimeSpan.FromDays(31)).Free, "a reservation outlived its 30 days");
    }

    /// <summary>Checked ids are reserved too when free: checking a block picked by hand claims it.</summary>
    [TestMethod]
    public void IdAlloc_CheckedIds_AreReservedWhenFree()
    {
        var ledger = Path.Combine(TestSetup.NewFolder("ids-check"), "ids.json");
        var now = DateTime.UtcNow;
        var notes = new List<string>();
        var (free, _, _) = IdAllocator.Choose("quest", [500, 501], null, 1, [], "DLC_MOD_A", ledger, now, notes);
        CollectionAssert.AreEqual(new[] { 500, 501 }, free);

        var (otherFree, otherUsed, _) = IdAllocator.Choose("quest", [501, 502], null, 1, [], "DLC_MOD_B", ledger, now, notes);
        CollectionAssert.AreEqual(new[] { 502 }, otherFree);
        Assert.AreEqual(501, otherUsed.Single().Id);
        // Another kind's ids are not held.
        CollectionAssert.AreEqual(new[] { 500 }, IdAllocator.Choose("bool", [500], null, 1, [], "DLC_MOD_B", ledger, now, notes).Free);
    }
}
