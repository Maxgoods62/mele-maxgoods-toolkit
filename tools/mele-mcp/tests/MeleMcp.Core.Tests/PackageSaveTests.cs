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
}
