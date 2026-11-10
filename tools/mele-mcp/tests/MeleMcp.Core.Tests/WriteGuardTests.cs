using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core.Tests;

/// <summary>
/// PackageWriter's game-install guard, the one check between a write tool (or lectool) and the game folder. It was fixed
/// three times (3e17262, 2fca724, 06ceaa3), each time for a spelling of the folder that got past it. A fake install in
/// %TEMP% stands in for the game, so nothing here touches the real one except <see cref="RealInstall_IsRefused"/>, which
/// only asks the guard and writes nothing.
/// </summary>
[TestClass]
public class WriteGuardTests
{
    private static string root = "", file = "", outside = "";

    [ClassInitialize]
    public static void MakeFakeInstall(TestContext _)
    {
        var dir = TestSetup.NewFolder("guard");
        root = Path.Combine(dir, "Mass Effect Legendary Edition");
        file = Path.Combine(root, @"Game\ME1\BioGame\CookedPCConsole\BIOA_X.pcc");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "game file");
        // Folders whose names start like the install's: a prefix compare without the trailing \ would refuse them.
        foreach (var sibling in new[] { "Mass Effect Legendary Edition 2", "Mass Effect Legendary Edition.bak", "mod" })
        {
            Directory.CreateDirectory(Path.Combine(dir, sibling));
            File.WriteAllText(Path.Combine(dir, sibling, "x.pcc"), "mod file");
        }
        outside = dir;
    }

    private static bool Refused(string path, params string?[] roots)
    {
        try
        {
            PackageWriter.EnsureOutside(path, roots);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void AssertAllRefused(IEnumerable<(string Label, string Path)> cases)
    {
        var missed = cases.Where(c => !Refused(c.Path, root)).Select(c => $"{c.Label}: {c.Path}").ToList();
        Assert.IsEmpty(missed, "Not refused:\n" + string.Join("\n", missed));
    }

    [TestMethod]
    public void Spellings_OfAFileInsideTheInstall_AreRefused()
    {
        var rest = file[3..]; // without "C:\"
        var drive = char.ToLowerInvariant(file[0]);
        AssertAllRefused([
            ("plain", file),
            ("the install folder itself", root),
            ("a file that does not exist yet", Path.Combine(root, @"Game\ME1\new.pcc")),
            ("upper case", file.ToUpperInvariant()),
            ("forward slashes", file.Replace('\\', '/')),
            (".. segments", Path.Combine(root, @"Game\..\Game\ME1\BioGame\CookedPCConsole\BIOA_X.pcc")),
            (".. through a sibling", Path.Combine(outside, @"mod\..\Mass Effect Legendary Edition\Game\ME1\x.pcc")),
            ("trailing dot on a folder (Windows drops it)", Path.Combine(root + ".", @"Game\ME1\x.pcc")),
            (@"\\?\ prefix", @"\\?\" + file),
            (@"\\.\ prefix", @"\\.\" + file),
            ("WSL /mnt path", $"/mnt/{drive}/{rest.Replace('\\', '/')}"),
            (@"\\wsl$ share of the drive", $@"\\wsl$\Ubuntu\mnt\{drive}\{rest}"),
            (@"\\wsl.localhost share of the drive", $@"\\wsl.localhost\Ubuntu\mnt\{drive}\{rest}"),
        ]);
    }

    [TestMethod]
    public void FoldersNextToTheInstall_AreAllowed()
    {
        string[] allowed = [
            Path.Combine(outside, @"Mass Effect Legendary Edition 2\x.pcc"),
            Path.Combine(outside, @"Mass Effect Legendary Edition.bak\x.pcc"),
            Path.Combine(outside, @"mod\x.pcc"),
            Path.Combine(outside, @"mod\not-yet.pcc"),
            Path.Combine(outside, "x.pcc"),
        ];
        var refused = allowed.Where(p => Refused(p, root)).ToList();
        Assert.IsEmpty(refused, "Refused although outside the install:\n" + string.Join("\n", refused));
    }

    [TestMethod]
    public void NoKnownInstall_RefusesNothing()
    {
        // LEC has no LE folder when neither MELE_LE_DIR nor the registry gives one: the roots come in null or empty.
        Assert.IsFalse(Refused(Path.Combine(outside, @"mod\x.pcc"), null, ""));
        Assert.IsFalse(Refused(file, null, ""));
    }

    [TestMethod]
    public void Junction_IntoTheInstall_IsRefused()
    {
        var link = Path.Combine(outside, "junction");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = outside });
        mklink!.WaitForExit();
        if (!Directory.Exists(link)) Assert.Inconclusive("mklink /J failed: no junction to test with.");
        try
        {
            AssertAllRefused([
                ("existing file through the junction", Path.Combine(link, @"Game\ME1\BioGame\CookedPCConsole\BIOA_X.pcc")),
                ("new file through the junction", Path.Combine(link, @"Game\ME1\new.pcc")),
                ("the junction itself", link),
            ]);
        }
        finally
        {
            Directory.Delete(link); // removes the link, not the folder it points to
        }
    }

    [TestMethod]
    public void VolumeGuidPath_IntoTheInstall_IsRefused()
    {
        var volume = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(file[..3], volume, (uint)volume.Capacity)) Assert.Inconclusive($"No volume name for {file[..3]}.");
        // \\?\Volume{guid}\Users\...\BIOA_X.pcc
        AssertAllRefused([("volume GUID path", volume + file[3..])]);
    }

    [TestMethod]
    public void ShortName_OfTheInstall_IsRefused()
    {
        var shortName = new StringBuilder(1024);
        var length = GetShortPathNameW(file, shortName, (uint)shortName.Capacity);
        if (length == 0 || shortName.ToString().Equals(file, StringComparison.OrdinalIgnoreCase)) Assert.Inconclusive("This volume has no 8.3 names.");
        AssertAllRefused([("8.3 short path", shortName.ToString())]);
    }

    [TestMethod]
    public void AdminShare_OfTheInstall_IsRefused()
    {
        var rest = file[3..];
        var share = $@"\\localhost\{file[0]}$\{rest}";
        if (!File.Exists(share)) Assert.Inconclusive($@"\\localhost\{file[0]}$ is not reachable from this process.");
        AssertAllRefused([
            (@"\\localhost\C$ share", share),
            (@"\\127.0.0.1\C$ share", $@"\\127.0.0.1\{file[0]}$\{rest}"),
            (@"\\?\UNC\localhost\C$ share", $@"\\?\UNC\localhost\{file[0]}$\{rest}"),
        ]);
    }

    [TestMethod]
    public void RealInstall_IsRefused()
    {
        var le = TestSetup.RequireGame();
        var startup = Path.Combine(le, @"Game\ME1\BioGame\CookedPCConsole\Startup_INT.pcc");
        void Check(MEGame game, string path) =>
            Assert.ThrowsExactly<InvalidOperationException>(() => PackageWriter.EnsureWritable(game, path), path);

        Check(MEGame.LE1, startup);
        Check(MEGame.LE1, $"/mnt/{char.ToLowerInvariant(startup[0])}/{startup[3..].Replace('\\', '/')}");
        // The whole Legendary Edition folder, not only the game asked about: an LE1 call refuses an LE3 file, and the launcher.
        Check(MEGame.LE1, Path.Combine(le, @"Game\ME3\BioGame\CookedPCConsole\SFXGame.pcc"));
        Check(MEGame.LE1, Path.Combine(le, @"Game\Launcher\new.txt"));
        PackageWriter.EnsureWritable(MEGame.LE1, Path.Combine(outside, @"mod\x.pcc")); // and %TEMP% passes
    }

    /// <summary>
    /// The writers that save no package take the same guard: game_screenshot's PNG, and the level YAML that editor_import,
    /// npc_place and live_save rewrite. Each is asked about a folder of the real install that does not exist, so even a guard
    /// that let them through could write nothing there.
    /// </summary>
    [TestMethod]
    public void ScreenshotAndLevelYaml_InsideTheRealInstall_AreRefused()
    {
        var le = TestSetup.RequireGame();
        var inside = Path.Combine(le, @"Game\ME1\mele-tests-no-such-folder");
        Assert.IsFalse(Directory.Exists(inside));

        Assert.ThrowsExactly<InvalidOperationException>(() => GameScreenshot.OutputPath(MEGame.LE1, Path.Combine(inside, "shot.png")));
        Assert.ThrowsExactly<InvalidOperationException>(() => EditorImport.WriteYaml(Path.Combine(inside, "level.yaml"), "package: X.pcc\n"));
        Assert.IsFalse(Directory.Exists(inside), "a refusal created the folder");
    }

    /// <summary>A screenshot path of the caller's is a full path ending in .png: a relative one landed in the server's folder, and any other name could replace a package or a YAML.</summary>
    [TestMethod]
    public void Screenshot_Path_IsAFullPngPath()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GameScreenshot.OutputPath(MEGame.LE1, "shot.png"));
        Assert.ThrowsExactly<ArgumentException>(() => GameScreenshot.OutputPath(MEGame.LE1, Path.Combine(outside, @"mod\x.pcc")));
        var given = Path.Combine(outside, @"mod\shot.PNG");
        Assert.AreEqual(given, GameScreenshot.OutputPath(MEGame.LE1, given));
        Assert.IsTrue(GameScreenshot.OutputPath(MEGame.LE1, null).EndsWith(".png", StringComparison.Ordinal));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint length);
}
