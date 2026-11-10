using System.Globalization;
using System.Text;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core.Tests;

/// <summary>
/// The state the tools keep between calls about files on disk: LEC's file list, rescanned when the game's folders change
/// (<see cref="GamePaths.LoadedFiles(MEGame, bool)"/>), and a merge mod's listing in moddesc.ini, which follows its manifest.
/// </summary>
[TestClass]
public class CacheStateTests
{
    private const string Cooked = "CookedPCConsole";

    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A fake BioGame: CookedPCConsole with one file, and an empty DLC folder.</summary>
    private static (string BioGame, string Dlc) FakeGame(string name)
    {
        var bioGame = TestSetup.NewFolder(Path.Combine(name, "BioGame"));
        Directory.CreateDirectory(Path.Combine(bioGame, Cooked));
        File.WriteAllBytes(Path.Combine(bioGame, Cooked, "SFXGame.pcc"), [1]);
        var dlc = Path.Combine(bioGame, "DLC");
        Directory.CreateDirectory(dlc);
        return (bioGame, dlc);
    }

    /// <summary>
    /// Every file and folder under the root set to an old write time, so a change made next gets a time of its own even when
    /// the clock has not ticked since the tree was made.
    /// </summary>
    private static void Settle(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, Old);
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(dir, Old);
        Directory.SetLastWriteTimeUtc(root, Old);
    }

    /// <summary>Whether the change moves the folders' signature, from a settled tree.</summary>
    private static bool Moves(string bioGame, string dlc, Action change)
    {
        Settle(bioGame);
        var before = GamePaths.FolderSignature(bioGame, dlc, Cooked);
        change();
        return before != GamePaths.FolderSignature(bioGame, dlc, Cooked);
    }

    [TestMethod]
    public void FolderSignature_MovesOnInstallUninstallAndMountChanges_Le2Le3()
    {
        var (bioGame, dlc) = FakeGame("signature-le23");
        var cooked = Path.Combine(dlc, "DLC_MOD_A", Cooked);
        Directory.CreateDirectory(Path.Combine(cooked, "Sub"));
        File.WriteAllBytes(Path.Combine(cooked, "Mount.dlc"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(cooked, "BIOA_A.pcc"), [1]);

        Assert.IsFalse(Moves(bioGame, dlc, () => { }), "nothing changed: the cached list is served");
        Assert.IsTrue(Moves(bioGame, dlc, () =>
        {
            Directory.CreateDirectory(Path.Combine(dlc, "DLC_MOD_B", Cooked));
            File.WriteAllBytes(Path.Combine(dlc, "DLC_MOD_B", Cooked, "Mount.dlc"), [5]);
        }), "a DLC folder installed");
        Assert.IsTrue(Moves(bioGame, dlc, () => Directory.Delete(Path.Combine(dlc, "DLC_MOD_B"), recursive: true)), "a DLC folder uninstalled");
        Assert.IsTrue(Moves(bioGame, dlc, () => File.WriteAllBytes(Path.Combine(cooked, "Mount.dlc"), [9, 9, 9, 9])), "Mount.dlc rewritten (a new mount priority)");
        Assert.IsTrue(Moves(bioGame, dlc, () => File.WriteAllBytes(Path.Combine(cooked, "BIOA_B.pcc"), [1])), "a file copied into a DLC's cooked folder");
        Assert.IsTrue(Moves(bioGame, dlc, () => File.Move(Path.Combine(cooked, "BIOA_B.pcc"), Path.Combine(cooked, "BIOA_C.pcc"))), "a file renamed in a DLC's cooked folder");
        Assert.IsTrue(Moves(bioGame, dlc, () => File.Delete(Path.Combine(bioGame, Cooked, "SFXGame.pcc"))), "a base game file removed");

        // Neither changes a name → path pair, so the list stays right without a rescan; the second is the limit le_refresh_files covers.
        Assert.IsFalse(Moves(bioGame, dlc, () => File.WriteAllBytes(Path.Combine(cooked, "BIOA_A.pcc"), [7, 7])), "a file rewritten in place");
        Assert.IsFalse(Moves(bioGame, dlc, () => File.WriteAllBytes(Path.Combine(cooked, "Sub", "BIOA_D.pcc"), [1])), "a file added in a cooked subfolder");
    }

    [TestMethod]
    public void FolderSignature_MovesWhenAnAutoLoadIniChanges_Le1()
    {
        var (bioGame, dlc) = FakeGame("signature-le1");
        var mod = Path.Combine(dlc, "DLC_MOD_C");
        Directory.CreateDirectory(Path.Combine(mod, Cooked));
        var autoLoad = Path.Combine(mod, "AutoLoad.ini");
        File.WriteAllText(autoLoad, "[ME1DLCMOUNT]\nModMount = 100\n");

        // Same size: only the write time tells.
        Assert.IsTrue(Moves(bioGame, dlc, () => File.WriteAllText(autoLoad, "[ME1DLCMOUNT]\nModMount = 200\n")), "a new mount");
        Assert.IsTrue(Moves(bioGame, dlc, () => File.Delete(autoLoad)), "no AutoLoad.ini: LEC no longer counts the folder");
        Assert.IsTrue(Moves(bioGame, dlc, () => File.WriteAllText(autoLoad, "[ME1DLCMOUNT]\nModMount = 200\n")), "AutoLoad.ini back");
    }

    [TestMethod]
    public void FolderSignature_MissingFolders_GiveOneStableValue()
    {
        var missing = Path.Combine(TestSetup.Temp, "signature-missing", "BioGame");
        Assert.AreEqual(GamePaths.FolderSignature(missing, Path.Combine(missing, "DLC"), Cooked), GamePaths.FolderSignature(missing, Path.Combine(missing, "DLC"), Cooked));
        Assert.AreEqual(GamePaths.FolderSignature(null, null, Cooked), GamePaths.FolderSignature(null, null, Cooked));
    }

    /// <summary>
    /// LoadedFiles hands out LEC's cached list while the folders' signature holds, and a new scan once it moves (an M3 install the
    /// tools never heard of) or when asked. Before, the list stayed as first scanned until le_refresh_files. Scans the LE1 install's
    /// folders, read-only; the signatures are made up, so nothing is written there.
    /// </summary>
    [TestMethod]
    public void LoadedFiles_RescansOnlyWhenTheSignatureMoves()
    {
        TestSetup.RequireGame();
        try
        {
            var first = GamePaths.LoadedFiles(MEGame.LE1, 1, rescan: false);
            Assert.AreSame(first, GamePaths.LoadedFiles(MEGame.LE1, 1, rescan: false), "same folders: the cached list");
            var moved = GamePaths.LoadedFiles(MEGame.LE1, 2, rescan: false);
            Assert.AreNotSame(first, moved, "the folders changed: a new scan");
            Assert.AreSame(moved, GamePaths.LoadedFiles(MEGame.LE1, 2, rescan: false), "then cached again");
            var forced = GamePaths.LoadedFiles(MEGame.LE1, 2, rescan: true);
            Assert.AreNotSame(moved, forced, "rescan: true");
            GamePaths.RefreshLoadedFiles();
            Assert.AreNotSame(forced, GamePaths.LoadedFiles(MEGame.LE1, 2, rescan: false), "after RefreshLoadedFiles");
        }
        finally
        {
            // Back to the real signature for the tests after this one.
            GamePaths.LoadedFiles(MEGame.LE1, rescan: true);
        }
    }

    private static string Read(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static string NewModDesc(string name, string text)
    {
        var path = Path.Combine(TestSetup.NewFolder(name), "moddesc.ini");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
        return path;
    }

    [TestMethod]
    public void ModDesc_UnlistUndoesList()
    {
        const string original = "[ModManager]\r\ncmmver = 9.0\r\n\r\n[ModInfo]\r\ngame = LE2\r\n";
        var modDesc = NewModDesc("moddesc-roundtrip", original);

        Assert.IsTrue(MergeMods.ListInModDesc(modDesc, "MyMerge.m3m").Changed);
        StringAssert.Contains(Read(modDesc), "[BASEGAME]\r\nmoddir = .\r\nmergemods = MyMerge.m3m\r\n");
        var (backup, changed) = MergeMods.UnlistInModDesc(modDesc, "MyMerge.m3m");
        Assert.IsTrue(changed);
        Assert.AreEqual(original, Read(modDesc));
        StringAssert.Contains(Read(backup!), "mergemods = MyMerge.m3m", "the backup is the listed file");

        // Not listed: the file is left alone, with no backup.
        var (none, again) = MergeMods.UnlistInModDesc(modDesc, "MyMerge.m3m");
        Assert.IsFalse(again);
        Assert.IsNull(none);
        Assert.AreEqual(original, Read(modDesc));
    }

    [TestMethod]
    [DataRow("[ModInfo]\ngame = LE2\n\n[BASEGAME]\nmoddir = .\nmergemods = A.m3m; MyMerge.m3m ;B.m3m\n\n[CUSTOMDLC]\nsourcedirs = DLC_MOD_X\n",
             "[ModInfo]\ngame = LE2\n\n[BASEGAME]\nmoddir = .\nmergemods = A.m3m;B.m3m\n\n[CUSTOMDLC]\nsourcedirs = DLC_MOD_X\n", DisplayName = "other merge mods stay")]
    [DataRow("[BASEGAME]\nmoddir = .\nreplacefiles = x\nmergemods = mymerge.M3M\n",
             "[BASEGAME]\nmoddir = .\nreplacefiles = x\n", DisplayName = "a job with other files keeps its section")]
    [DataRow("[ModInfo]\ngame = LE2\n\n[BASEGAME]\nmoddir = .\nmergemods = MyMerge.m3m\n\n[CUSTOMDLC]\nsourcedirs = DLC_MOD_X\n",
             "[ModInfo]\ngame = LE2\n\n[CUSTOMDLC]\nsourcedirs = DLC_MOD_X\n", DisplayName = "a section left with moddir only goes")]
    public void ModDesc_Unlist_TakesOutOnlyThatFile(string before, string after)
    {
        var modDesc = NewModDesc("moddesc-unlist-" + Guid.NewGuid().ToString("N")[..6], before);
        Assert.IsTrue(MergeMods.UnlistInModDesc(modDesc, "MyMerge.m3m").Changed);
        Assert.AreEqual(after, Read(modDesc));
    }

    /// <summary>
    /// A build that does not compile its new manifest (here compile=false) after an earlier good build: moddesc.ini used to keep
    /// listing the .m3m, the earlier manifest's compile, so the mod installed the old merge, and the note said it was not listed.
    /// Checks against an LE1 core file, read-only; the mod and its scratch copy are written under %TEMP%.
    /// </summary>
    [TestMethod]
    public void MergeBuild_NotCompiled_TakesTheEarlierM3mOutOfModDesc()
    {
        TestSetup.RequireGame();
        var (target, entry, property, value) = SimpleIntProperty(MEGame.LE1, "IpDrv.pcc", "GFxUI.pcc", "Engine.pcc");
        var root = TestSetup.NewFolder("merge-not-compiled");
        var modDesc = Path.Combine(root, "moddesc.ini");
        File.WriteAllText(modDesc, "[ModManager]\r\ncmmver = 9.0\r\n\r\n[ModInfo]\r\ngame = LE1\r\nmodname = Cache test\r\n\r\n"
                                   + "[BASEGAME]\r\nmoddir = .\r\nmergemods = Other.m3m;CacheTest.m3m\r\n");
        // The earlier good build: its compile, listed above.
        Directory.CreateDirectory(Path.Combine(root, "MergeMods"));
        File.WriteAllBytes(Path.Combine(root, "MergeMods", "CacheTest.m3m"), "M3MM\u0001"u8.ToArray());
        MergeChange[] changes = [new(entry, [new MergeProperty(property, null, value)])];

        var first = MergeMods.Build(MEGame.LE1, root, "CacheTest", target, changes, null, false, compile: false, timeoutSeconds: 60);
        Assert.AreEqual("written", first.Stage, string.Join("; ", first.Checks.SelectMany(c => c.Errors).Select(e => e.Text)));
        Assert.IsTrue(first.ModDescChanged);
        StringAssert.Contains(Read(first.ModDescBackup!), "CacheTest.m3m", "the backup is the listed file");
        var text = Read(modDesc);
        StringAssert.Contains(text, "mergemods = Other.m3m\r\n");
        Assert.IsFalse(text.Contains("CacheTest", StringComparison.Ordinal), text);
        Assert.IsTrue(first.Notes.Any(n => n.Contains("CacheTest.m3m is taken out of moddesc.ini")), string.Join("\n", first.Notes));
        // The manifest written is the one the check finished: it filled in the property's type.
        StringAssert.Contains(File.ReadAllText(first.Manifest!), "\"propertytype\": \"IntProperty\"");

        var second = MergeMods.Build(MEGame.LE1, root, "CacheTest", target, changes, null, false, compile: false, timeoutSeconds: 60);
        Assert.AreEqual("written", second.Stage);
        Assert.IsFalse(second.ModDescChanged);
        Assert.IsNull(second.ModDescBackup);
        Assert.AreEqual(text, Read(modDesc));
        Assert.IsTrue(second.Notes.Any(n => n.Contains("CacheTest.m3m is not listed in moddesc.ini")), string.Join("\n", second.Notes));
    }

    /// <summary>A class default object with a plain IntProperty in one of the files, and its value, to set it to itself.</summary>
    internal static (string File, string Entry, string Property, string Value) SimpleIntProperty(MEGame game, params string[] files)
    {
        foreach (var file in files)
        {
            using var pcc = MEPackageHandler.OpenMEPackage(GamePaths.ResolvePackage(game, file));
            foreach (var export in pcc.Exports.Where(e => e.IsDefaultObject))
            {
                if (export.GetProperties().OfType<IntProperty>().FirstOrDefault(p => p.StaticArrayIndex == 0) is { } p)
                {
                    return (file, export.InstancedFullPath, p.Name.Instanced, p.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        Assert.Inconclusive($"No class default object with an IntProperty in {string.Join(", ", files)}.");
        return default;
    }
}
