using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core.Tests;

/// <summary>
/// What a build, or a game_cycle, says it did when it stops part way, from the review of the 2026-11-07 sweep's fixes. A fork
/// written over an earlier build's is named as rewritten, not created. mod_build names a level file's writes by their own paths
/// (a level it created, levels.json), where it used to rebuild them from the result's backups. An out= copy's note does not
/// count a file a build deleted from the copy. A console command game_cycle could not send says whether it ran. Files go under
/// <see cref="TestSetup.Temp"/>; the level tests read class infos and vanilla files from the game, read-only.
/// </summary>
[TestClass]
public class BuildRecordStateTests
{
    private const string Lay = LevelBuildStateTests.Lay;

    /// <summary>A file with no backup was always "(created)", a fork that replaced an earlier build's too.</summary>
    [TestMethod]
    public void PartialBuild_NamesCreatedBackedUpAndRebuiltFiles()
    {
        var writes = new BuildWrites();
        writes.Wrote(@"C:\m\A.pcc");
        writes.Wrote(@"C:\m\B.pcc", @"C:\b\x_B.pcc");
        writes.Wrote(@"C:\m\F.pcc", null, rebuilt: true);
        // A backup says more than "rebuilt": the file can be put back.
        writes.Wrote(@"C:\m\G.pcc", @"C:\b\x_G.pcc", rebuilt: true);

        var e = Assert.Throws<PartialBuildException>(() => writes.Guard<int>(() => throw new IOException("disk full")));
        StringAssert.Contains(e.Message, @"C:\m\A.pcc (created)");
        StringAssert.Contains(e.Message, @"C:\m\B.pcc (backup C:\b\x_B.pcc)");
        StringAssert.Contains(e.Message, @"C:\m\F.pcc (rewritten; rebuilt from the vanilla file, no backup)");
        StringAssert.Contains(e.Message, @"C:\m\G.pcc (backup C:\b\x_G.pcc)");
        CollectionAssert.AreEqual(new[] { @"C:\b\x_B.pcc", @"C:\b\x_G.pcc" }, writes.Backups);
    }

    /// <summary>
    /// A build into out= deletes a fork with no entries left, and stale localized copies, from its copy. Each later build then
    /// counted them as source files missing from the copy and said to delete the copy, which the next build undid again.
    /// </summary>
    [TestMethod]
    public void OutCopy_FilesABuildDeletedFromTheCopy_AreNoSourceChange()
    {
        var root = TestSetup.NewFolder("out-copy-deleted");
        var source = Path.Combine(root, "src", "DLC_MOD_T");
        var cooked = Path.Combine(source, "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        foreach (var name in new[] { "BIOA_T00.pcc", "T00_NOR10_09_LAY.pcc", "T00_NOR10_09_LAY_LOC_INT.pcc" }) File.WriteAllText(Path.Combine(cooked, name), name);
        var copy = Path.Combine(root, "out", "DLC_MOD_T");
        YamlFiles.CopyFolder(source, copy);

        File.Delete(Path.Combine(copy, "CookedPCConsole", "T00_NOR10_09_LAY.pcc"));
        File.Delete(Path.Combine(copy, "CookedPCConsole", "T00_NOR10_09_LAY_LOC_INT.pcc"));
        var stale = YamlFiles.Stale(source, copy);
        Assert.IsEmpty(stale, string.Join(", ", stale));

        // A file added to the source after the copy is still named. Its creation time is set: the file system's clock may give it
        // the copy's own tick.
        var added = Path.Combine(cooked, "DLC_MOD_T_GlobalTlk.pcc");
        File.WriteAllText(added, "tlk");
        File.SetCreationTimeUtc(added, DateTime.UtcNow.AddMinutes(10));
        CollectionAssert.AreEqual(new[] { @"CookedPCConsole\DLC_MOD_T_GlobalTlk.pcc" }, YamlFiles.Stale(source, copy));
    }

    /// <summary>
    /// ForkBuilder.Write replaces a fork with no backup. A build that failed after writing one named it "(created)", also when it
    /// had written over an earlier build's fork.
    /// </summary>
    [TestMethod]
    public void ForkWrittenOverAnEarlierBuilds_IsNamedRebuilt_NotCreated()
    {
        TestSetup.RequireGame();
        string actor;
        using (var vanilla = PackageInspector.Open(MEGame.LE1, Lay + ".pcc"))
        {
            var held = ForkBuilder.KismetHeld(vanilla);
            actor = vanilla.Exports.First(e => e.Parent?.InstancedFullPath == LevelBuildStateTests.LevelPath && e.IsA("Actor")
                                               && ForkBuilder.Refusal(e, held, false) is null).ObjectName.Instanced;
        }
        var map = LevelBuildStateTests.Level("fork-rebuilt", (pcc, cache) =>
        {
            LevelBuildStateTests.Streams(pcc, cache, Lay);
            var level = pcc.FindExport(LevelBuildStateTests.LevelPath)!;
            var behavior = LevelBuildStateTests.Add(pcc, cache, "BioPawnBehavior", "BioPawnBehavior_1", level);
            LevelBuildStateTests.Add(pcc, cache, "BioUseable", "BioUseable_1", level, new NameProperty("t_use", "Tag"), new ObjectProperty(behavior, "m_oBehavior"));
        });
        // A package with no BioTlkFile export: TalkFiles.Set refuses it, after the fork and the level are written.
        var tlk = LevelBuildStateTests.Beside(map, "DLC_MOD_T_GlobalTlk");
        MEPackageHandler.CreateAndSavePackage(tlk, MEGame.LE1);
        var yaml = LevelBuildStateTests.Yaml(map, $$"""
            globalTlk: ['{{tlk}}']
            streaming:
              volume: { package: BIOA_NOR00.pcc, actor: BioTriggerStream_27 }
              levels: [{{Lay}}]
            vanilla:
              {{Lay}}.{{actor}}: { location: [0, 0, 0] }
            usables:
              terminal:
                tag: t_use
                name: { strref: 74210061, en: "Terminal" }
            """);
        var fork = LevelBuildStateTests.Beside(map, LevelBuildStateTests.Fork);

        var first = Assert.Throws<PartialBuildException>(() => LevelBuilder.Build(MEGame.LE1, yaml, null));
        Assert.IsTrue(first.Written.Any(w => w.File == fork && w.Backup is null && !w.Rebuilt), string.Join("; ", first.Written));
        StringAssert.Contains(first.Message, $"{fork} (created)");

        File.WriteAllText(fork, "an earlier build's fork");
        var second = Assert.Throws<PartialBuildException>(() => LevelBuilder.Build(MEGame.LE1, yaml, null));
        Assert.IsTrue(second.Written.Any(w => w.File == fork && w.Backup is null && w.Rebuilt), string.Join("; ", second.Written));
        StringAssert.Contains(second.Message, $"{fork} (rewritten; rebuilt from the vanilla file, no backup)");
        Assert.IsFalse(second.Message.Contains($"{fork} (created)"), second.Message);
    }

    /// <summary>
    /// mod_build recorded a finished level build from its result's backups: a level it created (no backup) was left out, and
    /// levels.json was named by its bare file name. The level build now records its own writes into mod_build's record.
    /// </summary>
    [TestMethod]
    public void ModBuild_ALevelFilesWrites_AreNamedByTheirOwnPaths()
    {
        TestSetup.RequireGame();
        var root = TestSetup.NewFolder("mod-level-partial");
        var cooked = Path.Combine(root, "DLC_MOD_T", "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        var created = Path.Combine(cooked, "BIOA_T00.pcc");
        File.WriteAllText(Path.Combine(root, "a.yaml"), $$"""
            package: '{{created}}'
            streaming:
              volume: { package: BIOA_NOR00.pcc, actor: BioTriggerStream_27 }
              levels: [BIOA_NOR10_09_ART]
            """.ReplaceLineEndings("\n"));
        // Refused before this file writes anything.
        File.WriteAllText(Path.Combine(root, "b.yaml"), $$"""
            package: '{{Path.Combine(cooked, "BIOA_T01.pcc")}}'
            npcs:
              nurse: { location: [0, 0, 0] }
            """.ReplaceLineEndings("\n"));

        var e = Assert.Throws<PartialBuildException>(() => ModBuilder.Build(MEGame.LE1, root));
        StringAssert.Contains(e.Message, "b.yaml: ");
        StringAssert.Contains(e.Message, "needs a 'tag'");
        Assert.IsTrue(e.Written.Any(w => w.File == created && w.Backup is null && !w.Rebuilt), string.Join("; ", e.Written));
        StringAssert.Contains(e.Message, $"{created} (created)");
        Assert.IsTrue(e.Written.Any(w => w.File == LevelBuilder.LevelsFilePath()), string.Join("; ", e.Written));
        Assert.IsFalse(e.Written.Any(w => w.File == "levels.json"), string.Join("; ", e.Written));
        Assert.IsFalse(File.Exists(Path.Combine(cooked, "BIOA_T01.pcc")));
    }

    /// <summary>
    /// game_cycle said "was not sent" for every failure, also for a request MeleBridge took and did not finish in time, which
    /// may still run (a QuickLoad that goes on loading).
    /// </summary>
    [TestMethod]
    public void CycleCommand_SaysNotRunOnlyForARequestTheGameThreadNeverTook()
    {
        var never = new InvalidOperationException("the game thread did not take the request in time (game loading, or not running script?)");
        Assert.AreEqual($"QuickLoad did not run: {never.Message}", GameCycle.Failed(MEGame.LE2, "QuickLoad", never));

        var late = new IOException("MeleBridge did not answer 'console QuickLoad' in time: it started in the game and was still running when "
                                   + "the bridge stopped waiting for it, so it may still complete.");
        Assert.AreEqual($"QuickLoad failed: {late.Message}", GameCycle.Failed(MEGame.LE2, "QuickLoad", late));
        Assert.AreEqual($"QuickLoad failed: {never.Message}", GameCycle.Failed(MEGame.LE1, "QuickLoad", never), "LE1's console is not MeleBridge's");
    }
}
