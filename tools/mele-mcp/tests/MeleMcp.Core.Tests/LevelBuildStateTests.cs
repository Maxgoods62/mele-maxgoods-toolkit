using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MeleMcp.Core.Tests;

/// <summary>
/// level_build's pass order and its streaming and light states, from the 2026-11-07 implicit-state-machine sweep: a refusal or a
/// check that fails writes nothing (M3a, M3b), what describes the level is written with it (M3c, M3d), a pair's other name, a
/// file that left streaming.levels and a changed volume leave no fork or volume behind (M20), and a light whose tag moves from
/// lights: to an NPC takes its template's channels back (M21). The levels are LEC's empty level, written under
/// <see cref="TestSetup.Temp"/> with the actors a test needs; class infos, templates and vanilla files are read from the game, read-only.
/// </summary>
[TestClass]
public class LevelBuildStateTests
{
    // Internal, with the helpers below: BuildRecordStateTests builds its levels the same way.
    internal const string LevelPath = "TheWorld.PersistentLevel";
    internal const string Lay = "BIOA_NOR10_09_LAY";
    internal const string Fork = "T00_NOR10_09_LAY";

    /// <summary>M3b: a check in the level threw after LevelCreator.Create had written the level, so a failed first build left an empty level.</summary>
    [TestMethod]
    public void CheckFailingInTheLevel_CreatesNoLevel()
    {
        LecHost.EnsureInitialized();
        var map = MapPath("build-check-new");
        var yaml = Yaml(map, """
            npcs:
              nurse: { location: [0, 0, 0] }
            """);

        var e = Assert.Throws<ArgumentException>(() => LevelBuilder.Build(MEGame.LE1, yaml, null));
        StringAssert.Contains(e.Message, "needs a 'tag'");
        Assert.IsFalse(File.Exists(map), "a build that failed created the level");
    }

    /// <summary>M3a: the refusal said "(nothing written)" after the level had been created.</summary>
    [TestMethod]
    public void RefusedVanillaEntry_CreatesNoLevel()
    {
        TestSetup.RequireGame();
        var map = MapPath("build-refused");
        var yaml = Yaml(map, $$"""
            streaming:
              volume: { package: BIOA_NOR00.pcc, actor: BioTriggerStream_27 }
              levels: [{{Lay}}]
            vanilla:
              {{Lay}}.StaticMeshActor_NoSuchActor: { location: [0, 0, 0] }
            """);

        var e = Assert.Throws<ArgumentException>(() => LevelBuilder.Build(MEGame.LE1, yaml, null));
        StringAssert.Contains(e.Message, "(nothing written)");
        StringAssert.Contains(e.Message, "has no actor StaticMeshActor_NoSuchActor");
        Assert.IsFalse(File.Exists(map), "the refused build created the level");
    }

    /// <summary>
    /// M3b: the forks were written before the level's checks ran, so a check that failed left a new fork beside the old map, which
    /// streams it.
    /// </summary>
    [TestMethod]
    public void CheckFailingInTheLevel_WritesNoFork()
    {
        TestSetup.RequireGame();
        string actor;
        using (var vanilla = PackageInspector.Open(MEGame.LE1, Lay + ".pcc"))
        {
            var held = ForkBuilder.KismetHeld(vanilla);
            actor = vanilla.Exports.First(e => e.Parent?.InstancedFullPath == LevelPath && e.IsA("Actor") && ForkBuilder.Refusal(e, held, false) is null).ObjectName.Instanced;
        }
        var map = Level("build-check-fork", (pcc, cache) => Streams(pcc, cache, Lay));
        var bytes = File.ReadAllBytes(map);
        var yaml = Yaml(map, $$"""
            streaming:
              volume: { package: BIOA_NOR00.pcc, actor: BioTriggerStream_27 }
              levels: [{{Lay}}]
            vanilla:
              {{Lay}}.{{actor}}: { location: [0, 0, 0] }
            npcs:
              nurse: { location: [0, 0, 0] }
            """);

        var e = Assert.Throws<ArgumentException>(() => LevelBuilder.Build(MEGame.LE1, yaml, null));
        StringAssert.Contains(e.Message, "needs a 'tag'");
        Assert.IsFalse(File.Exists(Beside(map, Fork)), "the fork was written before the level's checks");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(map), "the level changed");
    }

    /// <summary>
    /// M3c, M3d: the GlobalTlk came before the Sideloader config and levels.json, so a refused GlobalTlk left the level saved with
    /// no config (a first build is never sideloaded) and levels.json describing the build before, and the error named no file.
    /// </summary>
    [TestMethod]
    public void RefusedGlobalTlk_LeavesTheLevelDescribed_AndNamesWhatWasWritten()
    {
        TestSetup.RequireGame();
        var map = Level("build-tlk", (pcc, cache) =>
        {
            var level = pcc.FindExport(LevelPath)!;
            var behavior = Add(pcc, cache, "BioPawnBehavior", "BioPawnBehavior_1", level);
            Add(pcc, cache, "BioUseable", "BioUseable_1", level, new NameProperty("t_use", "Tag"), new ObjectProperty(behavior, "m_oBehavior"));
        });
        // A package with no BioTlkFile export: TalkFiles.Set refuses it, after the level is saved.
        var tlk = Beside(map, "DLC_MOD_T_GlobalTlk");
        MEPackageHandler.CreateAndSavePackage(tlk, MEGame.LE1);
        var yaml = Yaml(map, $$"""
            globalTlk: ['{{tlk}}']
            sideloader: [BIOA_T00X]
            usables:
              terminal:
                tag: t_use
                name: { strref: 74210061, en: "Terminal" }
            """);

        var e = Assert.Throws<PartialBuildException>(() => LevelBuilder.Build(MEGame.LE1, yaml, null));
        StringAssert.Contains(e.Message, "has no BioTlkFile export");
        var config = Path.Combine(Path.GetDirectoryName(map)!, "ConfigDelta-BIOA_T00.m3cd");
        StringAssert.Contains(File.ReadAllText(config), "LevelName=\"BIOA_T00\"");
        var levels = JsonNode.Parse(File.ReadAllText(LevelBuilder.LevelsFilePath()))!["levels"]!["BIOA_T00"]!;
        Assert.AreEqual("DLC_MOD_T", (string?)levels["dlc"]);
        var files = e.Written.Select(w => w.File).ToList();
        CollectionAssert.IsSubsetOf(new[] { map, config, LevelBuilder.LevelsFilePath() }, files, string.Join("; ", files));
        Assert.IsNotNull(e.Written.First(w => w.File == map).Backup);
    }

    /// <summary>
    /// M20a: with entries for both names of a pair, only the list entry of the other name was removed. Its export still named the fork,
    /// and the build kept the fork forever, saying a streaming entry still named it.
    /// </summary>
    [TestMethod]
    public void BothNamesOfAPair_TheForkGoes()
    {
        TestSetup.RequireGame();
        var map = Level("streams-both", (pcc, cache) => Streams(pcc, cache, Lay, Fork));
        File.WriteAllText(Beside(map, Fork), "an earlier build's fork");
        var yaml = Yaml(map, $$"""
            streaming:
              volume: { package: BIOA_NOR00.pcc, actor: BioTriggerStream_27 }
              levels: [{{Lay}}]
            """);

        var result = LevelBuilder.Build(MEGame.LE1, yaml, null);
        Assert.IsFalse(File.Exists(Beside(map, Fork)), string.Join("\n", result.Notes));
        Assert.IsTrue(result.Forks.Any(f => f.Fork == Fork && f.Action == "deleted"));
        Assert.IsFalse(result.Notes.Any(n => n.Contains("is kept")), string.Join("\n", result.Notes));
        CollectionAssert.AreEquivalent(new[] { Lay }, Listed(map));
    }

    /// <summary>
    /// M20b: a file that left streaming.levels stayed in StreamingLevels under its fork's name, and only current levels' forks were
    /// ever deleted, so its fork stayed for good. A sub-level of the mod's own whose name only looks like a fork stays as it is.
    /// </summary>
    [TestMethod]
    public void LevelLeftStreaming_ItsForkGoes_AndTheEntryNamesTheFileAgain()
    {
        TestSetup.RequireGame();
        var map = Level("streams-left", (pcc, cache) => Streams(pcc, cache, Fork, "T00_OWN"));
        File.WriteAllText(Beside(map, Fork), "an earlier build's fork");
        File.WriteAllText(Beside(map, "T00_OWN"), "the mod's own sub-level");
        var yaml = Yaml(map, """
            streaming:
              volume: { package: BIOA_NOR00.pcc, actor: BioTriggerStream_27 }
              levels: [BIOA_NOR10_09_ART]
            """);

        var result = LevelBuilder.Build(MEGame.LE1, yaml, null);
        Assert.IsFalse(File.Exists(Beside(map, Fork)), string.Join("\n", result.LevelChanges));
        Assert.IsTrue(result.Forks.Any(f => f.Fork == Fork && f.Action == "deleted"));
        Assert.IsTrue(File.Exists(Beside(map, "T00_OWN")), "a file of the mod's own was taken for a fork");
        CollectionAssert.AreEquivalent(new[] { Lay, "T00_OWN", "BIOA_NOR10_09_ART" }, Listed(map), "an entry left StreamingLevels, or kept the fork's name");
        CollectionAssert.Contains(result.LevelChanges.ToList(), $"no longer streams {Fork} ({Lay} left streaming.levels)");
    }

    /// <summary>
    /// M20b, the whole section: a YAML with no 'streaming' any more skipped the streaming pass, so the level went on streaming an
    /// earlier build's fork, with that build's edits, and the fork was never deleted. The mod's own sub-level stays as it is.
    /// </summary>
    [TestMethod]
    public void StreamingSectionRemoved_ItsForksGo_AndTheEntriesNameTheFilesAgain()
    {
        TestSetup.RequireGame();
        var map = Level("streams-none", (pcc, cache) => Streams(pcc, cache, Fork, "T00_OWN"));
        File.WriteAllText(Beside(map, Fork), "an earlier build's fork");
        File.WriteAllText(Beside(map, "T00_OWN"), "the mod's own sub-level");
        var yaml = Yaml(map, "");

        var result = LevelBuilder.Build(MEGame.LE1, yaml, null);
        Assert.IsFalse(File.Exists(Beside(map, Fork)), string.Join("\n", result.LevelChanges));
        Assert.IsTrue(result.Forks.Any(f => f.Fork == Fork && f.Action == "deleted"));
        Assert.IsTrue(File.Exists(Beside(map, "T00_OWN")), "a file of the mod's own was taken for a fork");
        CollectionAssert.AreEquivalent(new[] { Lay, "T00_OWN" }, Listed(map), "an entry left StreamingLevels, or kept the fork's name");
        CollectionAssert.Contains(result.LevelChanges.ToList(), $"no longer streams {Fork} (the YAML has no streaming section)");
    }

    /// <summary>M20c: a changed volume imported a second BioTriggerStream, and the earlier build's copy went on showing its levels.</summary>
    [TestMethod]
    public void ChangedVolume_ReplacesTheEarlierBuildsCopy_Only()
    {
        TestSetup.RequireGame();
        var map = Level("streams-volume", (pcc, cache) =>
        {
            List(pcc, cache, Lay);
            var level = pcc.FindExport(LevelPath)!;
            // An earlier build's copy, in the shape SetStreaming writes, and a volume of another shape.
            Add(pcc, cache, "BioTriggerStream", "BioTriggerStream_99", level, States(State(Lay)));
            Add(pcc, cache, "BioTriggerStream", "BioTriggerStream_98", level, States(State(Lay), State("BIOA_NOR10_09_ART")));
        });
        var yaml = Yaml(map, $$"""
            streaming:
              volume: { package: BIOA_NOR00.pcc, actor: BioTriggerStream_27 }
              levels: [{{Lay}}]
            """);

        var result = LevelBuilder.Build(MEGame.LE1, yaml, null);
        using var built = MEPackageHandler.OpenMEPackage(map, forceLoadFromDisk: true);
        Assert.IsNotNull(built.FindExport($"{LevelPath}.BioTriggerStream_27"));
        Assert.IsNull(built.FindExport($"{LevelPath}.BioTriggerStream_99"), string.Join("\n", result.LevelChanges));
        Assert.IsNotNull(built.FindExport($"{LevelPath}.BioTriggerStream_98"), "a volume the build did not write was removed");
        Assert.IsTrue(result.LevelChanges.Any(c => c.StartsWith("stream volume BioTriggerStream_99 removed")), string.Join("\n", result.LevelChanges));
    }

    /// <summary>
    /// M21: a light whose tag moved from lights: to an NPC's lights kept the entry's channels (Gameplay_4 alone), no shadows and
    /// forced dynamic light: it lit props and not her. A new NPC light has its template's.
    /// </summary>
    [TestMethod]
    public void LightMovedFromLightsToAnNpc_TakesItsTemplatesChannelsBack()
    {
        TestSetup.RequireGame();
        var map = MapPath("light-to-npc");
        LevelBuilder.Build(MEGame.LE1, Yaml(map, """
            lights:
              lamp: { tag: t_lamp, kind: props, location: [0, 0, 100], radius: 100, brightness: 1 }
            """), null);
        Edit(map, (pcc, cache) =>
        {
            var level = pcc.FindExport(LevelPath)!;
            var behavior = Add(pcc, cache, "BioPawnBehavior", "BioPawnBehavior_1", level);
            Add(pcc, cache, "BioPawn", "BioPawn_1", level, new NameProperty("t_nurse", "Tag"), new ObjectProperty(behavior, "m_oBehavior"));
        });

        var result = LevelBuilder.Build(MEGame.LE1, Yaml(map, """
            npcs:
              nurse:
                tag: t_nurse
                lights:
                  - { tag: t_lamp, offset: [0, 0, 100], radius: 100, brightness: 1 }
            """), null);
        var changes = result.Npcs.Single().Changes;
        Assert.IsTrue(changes.Any(c => c.Contains("kind props → NPC light")), string.Join("\n", changes));

        PropertyCollection template;
        using (var source = PackageInspector.Open(MEGame.LE1, "BIOA_NOR10_07_DSG.pcc"))
        {
            var light = source.FindExport($"{LevelPath}.PointLightToggleable_7")!;
            template = LevelBuilder.Effective((ExportEntry)light.GetProperty<ObjectProperty>("LightComponent")!.ResolveToEntry(source)!);
        }
        using var built = MEPackageHandler.OpenMEPackage(map, forceLoadFromDisk: true);
        var lamp = built.Exports.Single(e => e.Parent?.InstancedFullPath == LevelPath && e.IsA("Light") && e.GetProperty<NameProperty>("Tag")?.Value.Instanced == "t_lamp");
        var component = ((ExportEntry)lamp.GetProperty<ObjectProperty>("LightComponent")!.ResolveToEntry(built)!).GetProperties();
        CollectionAssert.AreEquivalent(On(template), On(component), "the light kept the lights: entry's channels");
        foreach (var flag in new[] { "CastShadows", "bForceDynamicLight" })
        {
            Assert.AreEqual(template.GetProp<BoolProperty>(flag)?.Value, component.GetProp<BoolProperty>(flag)?.Value, flag);
        }
    }

    /// <summary>The channels on, without bInitialized; none for no LightingChannels of its own.</summary>
    private static List<string> On(PropertyCollection component) =>
        component.GetProp<StructProperty>("LightingChannels")?.Properties.OfType<BoolProperty>().Where(b => b.Value && b.Name.Instanced != "bInitialized")
            .Select(b => b.Name.Instanced).ToList() ?? [];

    /// <summary>Where a test's level goes, in a DLC folder of its own; not created.</summary>
    internal static string MapPath(string name)
    {
        var cooked = Path.Combine(TestSetup.NewFolder(name), "DLC_MOD_T", "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        return Path.Combine(cooked, "BIOA_T00.pcc");
    }

    /// <summary>An empty LE1 level LEC writes, with what <paramref name="edit"/> adds.</summary>
    internal static string Level(string name, Action<IMEPackage, PackageCache> edit)
    {
        var map = MapPath(name);
        MEPackageHandler.CreateEmptyLevel(map, MEGame.LE1);
        Edit(map, edit);
        return map;
    }

    private static void Edit(string map, Action<IMEPackage, PackageCache> edit)
    {
        using var pcc = MEPackageHandler.OpenMEPackage(map, forceLoadFromDisk: true);
        using var cache = new PackageCache();
        edit(pcc, cache);
        pcc.Save();
    }

    internal static string Beside(string map, string name) => Path.Combine(Path.GetDirectoryName(map)!, name + ".pcc");

    internal static string Yaml(string map, string body)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(map)!)!)!, $"level-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, $"package: '{map}'\n{body.ReplaceLineEndings("\n")}\n");
        return path;
    }

    internal static ExportEntry Add(IMEPackage pcc, PackageCache cache, string className, string name, IEntry parent, params Property[] props)
    {
        var export = new ExportEntry(pcc, parent, NameReference.FromInstancedString(name))
        {
            Class = EntryImporter.EnsureClassIsInFile(pcc, className, new RelinkerOptionsPackage { Cache = cache }),
        };
        pcc.AddExport(export);
        if (props.Length > 0)
        {
            var collection = new PropertyCollection();
            foreach (var p in props) collection.Add(p);
            export.WriteProperties(collection);
        }
        return export;
    }

    /// <summary>
    /// The level streams these names, each a LevelStreamingKismet on its StreamingLevels, inside a volume named as the YAML's (found by
    /// its name, so nothing is copied from BIOA_NOR00).
    /// </summary>
    internal static void Streams(IMEPackage pcc, PackageCache cache, params string[] names)
    {
        List(pcc, cache, names);
        Add(pcc, cache, "BioTriggerStream", "BioTriggerStream_27", pcc.FindExport(LevelPath)!, States(State(names[0])));
    }

    /// <summary>A LevelStreamingKismet for each name, each on the level's StreamingLevels.</summary>
    private static void List(IMEPackage pcc, PackageCache cache, params string[] names)
    {
        var level = pcc.FindExport(LevelPath)!;
        var world = pcc.FindExport("TheWorld")!;
        var listed = new ArrayProperty<ObjectProperty>("StreamingLevels");
        foreach (var name in names) listed.Add(new ObjectProperty(Add(pcc, cache, "LevelStreamingKismet", $"LevelStreamingKismet_{listed.Count}", world, new NameProperty(name, "PackageName"))));
        pcc.Exports.First(e => e.Parent == level && e.ClassName == "BioWorldInfo").WriteProperty(listed);
    }

    private static StructProperty State(string shown) =>
        new("BioStreamingState", [new ArrayProperty<NameProperty>([new NameProperty(shown)], "VisibleChunkNames"), new NameProperty("Default", "StateName"), new NameProperty(shown, "InChunkName")]);

    private static ArrayProperty<StructProperty> States(params StructProperty[] states) => new(states, "StreamingStates");

    /// <summary>The names the saved level's StreamingLevels lists.</summary>
    private static List<string> Listed(string map)
    {
        using var pcc = MEPackageHandler.OpenMEPackage(map, forceLoadFromDisk: true);
        return LevelStreams.Streamed(pcc, pcc.FindExport(LevelPath)!).ToList();
    }
}
