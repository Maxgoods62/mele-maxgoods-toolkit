using System.Diagnostics;

namespace MeleMcp.Core.Tests;

/// <summary>
/// lectool run as a process, as the mods' build.sh run it. scripts/test.sh builds it and sets LECTOOL to the exe; without
/// LECTOOL these tests are inconclusive. Each run gets MELE_LE_DIR pointing at a fake install in %TEMP%, so even a broken
/// guard could only write there.
/// </summary>
[TestClass]
public class LectoolTests
{
    private static string exe = "", fakeLe = "", gameFile = "", work = "";

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        exe = Environment.GetEnvironmentVariable("LECTOOL") ?? "";
        work = TestSetup.NewFolder("lectool");
        fakeLe = Path.Combine(work, "Mass Effect Legendary Edition");
        gameFile = Path.Combine(fakeLe, @"Game\ME1\BioGame\CookedPCConsole\BIOG_2DA_Fake.pcc");
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
    }

    [TestInitialize]
    public void RequireLectool()
    {
        if (!File.Exists(exe)) Assert.Inconclusive("Set LECTOOL to a built lectool.exe (scripts/test.sh does).");
        File.WriteAllText(gameFile, "vanilla");
    }

    private static (int Exit, string Out) Run(params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["MELE_LE_DIR"] = fakeLe;
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail($"lectool {string.Join(' ', args)} did not exit in 2 minutes.");
        }
        return (p.ExitCode, output.Result + err.Result);
    }

    private static void AssertError(string expected, params string[] args)
    {
        var (exit, output) = Run(args);
        Assert.AreEqual(1, exit, output);
        Assert.Contains(expected, output);
    }

    /// <summary>An unknown command exited 0 with no output, so a build script that called a newer command passed.</summary>
    [TestMethod]
    public void UnknownCommand_ExitsOne() => AssertError("ERROR unknown command", "nosuchcommand", "x");

    /// <summary>An old lectool skipped --unique-wave and exited 0: every variant cue then shared one wave (ai-docs/36).</summary>
    [TestMethod]
    public void BuildSound_UnknownFlag_ExitsOne() =>
        AssertError("unknown option --unqiue-wave", "buildsound", "donor.pcc", "cue", "x.ogg", "1", "1", "1", Path.Combine(work, "out.pcc"), "new", "title", "1.0", "--unqiue-wave");

    [TestMethod]
    public void CubeShrink_BadFormat_ExitsOne() =>
        AssertError("usage: cubeshrink", "cubeshrink", Path.Combine(work, "x.pcc"), "Pkg.Cube", "256", "BC4");

    /// <summary>lectool had no install guard: 2dapart deleted its output before it even opened its source.</summary>
    [TestMethod]
    public void WriteCommands_RefuseAFileInTheInstall_AndLeaveItAlone()
    {
        var tsv = Path.Combine(work, "strings.tsv");
        File.WriteAllText(tsv, "1\tHello\n");
        string Missing(string name) => Path.Combine(work, name); // inputs that do not exist: the guard comes before they are read
        // The commands whose guard runs before they open an input. The others (swf, coalesced, texpng, ogg, dumpwave,
        // portmesh, addtexture, 2damerge) read an input first and need real game data to reach it.
        string[][] calls = [
            ["2dapart", Missing("src.pcc"), Missing("recipe.yaml"), gameFile],
            ["tlkpackage", gameFile, tsv],
            ["compileclass", gameFile, "Pkg", Missing("A.uc")],
            ["addactor", gameFile, "Pkg", "Cls"],
            ["morphface", Missing("donor.pcc"), "Face", gameFile, "Face"],
            ["gfxpackage", Missing("src.pcc"), "Movie", gameFile],
            ["actortype", Missing("r.yaml"), Missing("t.pcc"), Missing("l.pcc"), gameFile],
            ["buildsound", Missing("donor.pcc"), "cue", Missing("x.ogg"), "1", "1", "1", gameFile, "new", "title"],
            ["cubeshrink", gameFile, "Pkg.Cube", "256", "DXT1"],
            ["forcepackage", gameFile, "Pkg"],
        ];
        foreach (var call in calls)
        {
            AssertError("Refusing to modify a file inside the game install", call);
            Assert.AreEqual("vanilla", File.ReadAllText(gameFile), $"{call[0]} changed the install's file");
        }
        // The same through a WSL spelling of the fake install.
        var wsl = $"/mnt/{char.ToLowerInvariant(gameFile[0])}/{gameFile[3..].Replace('\\', '/')}";
        AssertError("Refusing to modify a file inside the game install", "tlkpackage", wsl, tsv);
        Assert.AreEqual("vanilla", File.ReadAllText(gameFile));
    }

    /// <summary>2dapart and tlkpackage delete their output first, so an output that is also an input destroyed the input.</summary>
    [TestMethod]
    public void OutputThatIsAnInput_IsRefused_AndKept()
    {
        var src = Path.Combine(work, "src.pcc");
        File.WriteAllText(src, "source");
        AssertError("the output is also an input", "2dapart", src, Path.Combine(work, "recipe.yaml"), src);
        Assert.AreEqual("source", File.ReadAllText(src));

        var tsv = Path.Combine(work, "strings2.tsv");
        File.WriteAllText(tsv, "1\tHello\n");
        AssertError("the output is also an input", "tlkpackage", tsv.ToUpperInvariant(), tsv);
        Assert.AreEqual("1\tHello\n", File.ReadAllText(tsv));

        // Output and --ids-from swapped by mistake: both files have the same ids, so only the input check stops it.
        var ids = Path.Combine(work, "INT.tsv");
        File.WriteAllText(ids, "1\tHi\n");
        AssertError("the output is also an input", "tlkpackage", ids, tsv, "--ids-from", ids);
        Assert.AreEqual("1\tHi\n", File.ReadAllText(ids));
    }
}
