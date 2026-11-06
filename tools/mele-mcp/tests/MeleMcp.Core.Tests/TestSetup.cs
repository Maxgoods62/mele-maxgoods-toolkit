using MeleMcp.Core;

// LecHost initializes once per process and the MELE_* variables are process-wide, so the tests run one at a time.
[assembly: DoNotParallelize]

namespace MeleMcp.Core.Tests;

[TestClass]
public static class TestSetup
{
    /// <summary>A fresh folder for this run. Every file a test writes goes under it.</summary>
    public static string Temp { get; private set; } = "";

    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Temp = Path.Combine(Path.GetTempPath(), "mele-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Temp);
        // Before any Core call: without these, a write tool backs up into %LOCALAPPDATA%\mele-mcp\backups and level tools
        // read and write MeleBridge's real levels.json.
        Environment.SetEnvironmentVariable(PackageWriter.BackupDirEnvVar, Path.Combine(Temp, "backups"));
        Environment.SetEnvironmentVariable("MELE_LEVELS_JSON", Path.Combine(Temp, "levels.json"));
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(Temp, recursive: true); }
        catch (IOException) { } // a junction or a file still open: the folder is in %TEMP%, Windows cleans it later
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A new empty folder under <see cref="Temp"/>.</summary>
    public static string NewFolder(string name)
    {
        var dir = Path.Combine(Temp, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The Legendary Edition folder of this machine, found as mele-mcp finds it (MELE_LE_DIR, then the registry), or the
    /// test stops as inconclusive. Tests open game files read-only and never write under this folder.
    /// </summary>
    public static string RequireGame()
    {
        var status = LecHost.EnsureInitialized();
        if (status.LegendaryEditionDir is not { } dir || !Directory.Exists(dir)) Assert.Inconclusive("No Legendary Edition install found.");
        return status.LegendaryEditionDir!;
    }
}
