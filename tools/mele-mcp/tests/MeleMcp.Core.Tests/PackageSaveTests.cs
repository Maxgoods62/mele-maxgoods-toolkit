using System.Diagnostics;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core.Tests;

/// <summary>
/// A package save that fails. LEC's Save catches every exception outside its DEBUG builds, passes a message to a callback and
/// returns, so a file another program held open, or a full disk, ended in a reply that named a backup and said the file was saved.
/// Every file is made under <see cref="TestSetup.Temp"/>.
/// </summary>
[TestClass]
public class PackageSaveTests
{
    /// <summary>A new empty LE1 package in a folder of its own. LEC compresses an LE package with the game's Oodle DLL: no game, no test.</summary>
    private static string NewPackage(string folder, string name)
    {
        TestSetup.RequireGame();
        var path = Path.Combine(TestSetup.NewFolder(folder), name);
        MEPackageHandler.CreateAndSavePackage(path, MEGame.LE1);
        return path;
    }

    /// <summary>Open for reading, shared for reading only: while this is open nobody can write, replace or delete the file.</summary>
    private static FileStream HeldOpen(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [TestMethod]
    public void Save_OnAFileItCannotWrite_Throws_NamesTheFileAndItsBackup_AndLeavesTheBytes()
    {
        var file = NewPackage("save-held", "SaveHeld.pcc");
        var before = File.ReadAllBytes(file);
        using var pcc = MEPackageHandler.OpenMEPackage(file, forceLoadFromDisk: true);
        pcc.FindNameOrAdd("MeleSaveTest");

        PackageSaveException e;
        using (HeldOpen(file)) e = Assert.ThrowsExactly<PackageSaveException>(() => PackageWriter.Save(pcc));

        var backup = Directory.GetFiles(PackageWriter.BackupRoot(), "*_SaveHeld.pcc").Single();
        StringAssert.Contains(e.Message, file);
        StringAssert.Contains(e.Message, backup);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
        CollectionAssert.AreEqual(new[] { file }, Directory.GetFiles(Path.GetDirectoryName(file)!), "a temporary file was left beside the package");
    }

    [TestMethod]
    public void Edit_OnAFileItCannotWrite_Throws_AndLeavesTheBytes()
    {
        var file = NewPackage("edit-held", "EditHeld.pcc");
        var before = File.ReadAllBytes(file);

        using (HeldOpen(file))
        {
            var e = Assert.ThrowsExactly<PackageSaveException>(() => PackageWriter.Edit(MEGame.LE1, file, pcc => pcc.FindNameOrAdd("MeleSaveTest")));
            StringAssert.Contains(e.Message, file);
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
        CollectionAssert.AreEqual(new[] { file }, Directory.GetFiles(Path.GetDirectoryName(file)!), "a temporary file was left beside the package");
    }

    /// <summary>LEC does not create folders: Create returned normally and left no file.</summary>
    [TestMethod]
    public void Create_WhereNoFileCanBeWritten_Throws_AndLeavesNoFile()
    {
        TestSetup.RequireGame();
        var path = Path.Combine(TestSetup.NewFolder("create-nowhere"), "no-such-folder", "New.pcc");

        var e = Assert.ThrowsExactly<PackageSaveException>(() => PackageWriter.Create(path, MEGame.LE1));

        StringAssert.Contains(e.Message, path);
        Assert.IsFalse(File.Exists(path));
    }

    /// <summary>
    /// Written beside the file and moved, a package must be the file LEC's own Save writes, byte for byte. A plot package from
    /// plot_map_new: two maps and a referencer, so there are exports to serialize and compress.
    /// </summary>
    [TestMethod]
    public void Save_WritesTheBytesLecsOwnSaveWrites_AndLeavesNoTemporaryFile()
    {
        TestSetup.RequireGame();
        var lecs = Path.Combine(TestSetup.NewFolder("save-bytes-lec"), "Same.pcc");
        var ours = Path.Combine(TestSetup.NewFolder("save-bytes"), "Same.pcc");
        PlotMaps.NewPackage(MEGame.LE1, lecs);
        File.Copy(lecs, ours);

        using (var pcc = MEPackageHandler.OpenMEPackage(lecs, forceLoadFromDisk: true))
        {
            for (var i = 0; i < 200; i++) pcc.FindNameOrAdd($"MeleSaveTest_{i}");
            pcc.Save(); // LEC's own save, into the file: tests are outside the ban of src/BannedSymbols.txt
        }
        using (var pcc = MEPackageHandler.OpenMEPackage(ours, forceLoadFromDisk: true))
        {
            for (var i = 0; i < 200; i++) pcc.FindNameOrAdd($"MeleSaveTest_{i}");
            PackageWriter.Save(pcc, backup: false);
        }

        CollectionAssert.AreEqual(File.ReadAllBytes(lecs), File.ReadAllBytes(ours));
        CollectionAssert.AreEqual(new[] { ours }, Directory.GetFiles(Path.GetDirectoryName(ours)!));
    }

    [TestMethod]
    public void Create_WritesTheBytesLecWrites_AndLeavesNoTemporaryFile()
    {
        TestSetup.RequireGame();
        var lecs = Path.Combine(TestSetup.NewFolder("create-bytes-lec"), "New.pcc");
        var ours = Path.Combine(TestSetup.NewFolder("create-bytes"), "New.pcc");
        MEPackageHandler.CreateAndSavePackage(lecs, MEGame.LE1);

        PackageWriter.Create(ours, MEGame.LE1);

        CollectionAssert.AreEqual(File.ReadAllBytes(lecs), File.ReadAllBytes(ours));
        CollectionAssert.AreEqual(new[] { ours }, Directory.GetFiles(Path.GetDirectoryName(ours)!));
    }

    /// <summary>level_new: the level is named after its file and opens. (Each level gets a new GUID, so its bytes are not compared.)</summary>
    [TestMethod]
    public void LevelNew_WritesALevelNamedAfterItsFile_AndLeavesNoTemporaryFile()
    {
        TestSetup.RequireGame();
        var path = Path.Combine(TestSetup.NewFolder("level-new"), "BIOA_T00.pcc");

        var made = LevelCreator.Create(MEGame.LE1, path);

        Assert.AreEqual("BIOA_T00", made.LevelName);
        Assert.IsGreaterThan(0, made.ExportCount);
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    /// <summary>
    /// A hard link gives one file a second name. LEC wrote into the file, so saving a package through one name changed what the
    /// other name holds: a link in a mod folder to a game file would let a save reach the game's file, and the write guard cannot
    /// see such a link. Moved over the name, the new package replaces the link only.
    /// </summary>
    [TestMethod]
    public void Save_OverAHardLink_ReplacesTheLink_AndLeavesTheOtherName()
    {
        var other = NewPackage("save-link", "Other.pcc");
        var link = Path.Combine(Path.GetDirectoryName(other)!, "Link.pcc");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{other}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        mklink!.WaitForExit();
        if (!File.Exists(link)) Assert.Inconclusive("mklink /H failed: no hard link to test with.");
        var before = File.ReadAllBytes(other);

        using (var pcc = MEPackageHandler.OpenMEPackage(link, forceLoadFromDisk: true))
        {
            pcc.FindNameOrAdd("MeleSaveTest");
            PackageWriter.Save(pcc, backup: false);
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(other));
        CollectionAssert.AreNotEqual(before, File.ReadAllBytes(link));
    }
}
