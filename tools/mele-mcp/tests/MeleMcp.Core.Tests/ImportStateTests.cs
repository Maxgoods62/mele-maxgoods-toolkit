using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core.Tests;

/// <summary>
/// editor_import's and npc_place's states from the 2026-11-07 implicit-state-machine sweep (M8): what a session entry is, the
/// receipt when the build fails after the YAML is written, the YAML's backup, a lights entry's kind, and a vanilla actor twice in
/// a session. Each test writes its YAML and session under <see cref="TestSetup.Temp"/>; none opens a game file.
/// </summary>
[TestClass]
public class ImportStateTests
{
    private static readonly JsonObject NpcLight = new()
    {
        ["kind"] = "npc", ["radius"] = 200, ["brightness"] = 0.5, ["color"] = new JsonArray(255, 255, 255), ["falloff"] = 2, ["enabled"] = true,
    };

    private static JsonObject Light(string kind, double radius) => new()
    {
        ["kind"] = kind, ["radius"] = radius, ["brightness"] = 0.5, ["color"] = new JsonArray(255, 255, 255), ["falloff"] = 2, ["enabled"] = true,
    };

    private static JsonObject State(double[] location, JsonObject? light = null, JsonObject? npc = null)
    {
        var state = new JsonObject
        {
            ["location"] = new JsonArray(location.Select(v => (JsonNode?)v).ToArray()), ["rotation"] = new JsonArray(0, 0, 0), ["scale"] = 1,
            ["scale3d"] = new JsonArray(1, 1, 1), ["deleted"] = false,
        };
        if (light is not null) state["light"] = light.DeepClone();
        if (npc is not null) state["npc"] = npc.DeepClone();
        return state;
    }

    /// <summary>A level actor's session entry (not spawned): its state, and its base (null: none).</summary>
    private static JsonObject Entry(string tag, string level, string actor, string @class, double[] location, JsonObject? @base, JsonObject? light = null, JsonObject? npc = null)
    {
        var entry = State(location, light, npc);
        entry["tag"] = tag;
        entry["path"] = $"{level}.TheWorld.PersistentLevel.{actor}";
        entry["level"] = level;
        entry["class"] = @class;
        entry["spawned"] = false;
        entry["base"] = @base;
        return entry;
    }

    private static string Session(string folder, string map, int version, params JsonObject[] actors)
    {
        var path = Path.Combine(folder, map + ".json");
        File.WriteAllText(path, new JsonObject { ["version"] = version, ["map"] = map, ["actors"] = new JsonArray(actors) }.ToJsonString());
        return path;
    }

    private static string Yaml(string folder, string text)
    {
        var path = Path.Combine(folder, "level.yaml");
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        return path;
    }

    /// <summary>A lights entry level_build refuses (kind is props or room): its build fails at parsing, before it writes anything.</summary>
    private const string RefusedLight = """
        lights:
          lamp:
            tag: tst_lamp
            kind: bogus
            location: [0, 0, 100]
            radius: 300
            brightness: 0.5
        """;

    /// <summary>
    /// M8e: an NPC light built from an item (not spawned) whose item is gone. Without a base, whether the editor changed it is
    /// unknown, and the reason says so; it used to say "the editor changed it since". With a base it still has, it is dropped
    /// (into the dropped file); with one it moved from, it stays as changed.
    /// </summary>
    [TestMethod]
    public void NpcLight_ItemGone_ReasonFollowsTheEntrysState()
    {
        var folder = TestSetup.NewFolder("import-npc-light-gone");
        var yaml = Yaml(folder, """
            package: BIOA_TST01.pcc
            npcs:
              medic:
                tag: tst_medic
                location: [0, 0, 0]
                yaw: 0
            """);
        var npc = new JsonObject { ["tag"] = "tst_medic", ["offset"] = new JsonArray(10, 0, 50) };
        double[] at = [10, 0, 50];
        var session = Session(folder, "BIOA_TST01", 3,
            Entry("tst_unbased", "BIOA_TST01", "PointLight_1", "PointLight", at, null, NpcLight, npc),
            Entry("tst_unchanged", "BIOA_TST01", "PointLight_2", "PointLight", at, State(at, NpcLight, npc), NpcLight, npc),
            Entry("tst_changed", "BIOA_TST01", "PointLight_3", "PointLight", at, State(at, Light("npc", 100), npc), NpcLight, npc));
        var yamlBefore = File.ReadAllText(yaml);

        var result = EditorImport.Run(MEGame.LE1, yaml, session, build: false);

        var byKey = result.Actors.ToDictionary(a => a.Key);
        Assert.AreEqual("skipped", byKey["tst_unbased"].Action);
        Assert.Contains("has no base", byKey["tst_unbased"].Reason!);
        Assert.DoesNotContain("Not added again: the editor changed it since", byKey["tst_unbased"].Reason!);
        Assert.Contains("is dropped", byKey["tst_unchanged"].Reason!);
        Assert.Contains("Not added again: the editor changed it since", byKey["tst_changed"].Reason!);

        Assert.IsFalse(result.Written);
        Assert.IsNull(result.Backup);
        Assert.AreEqual(yamlBefore, File.ReadAllText(yaml));
        var left = File.ReadAllText(session);
        Assert.DoesNotContain("tst_unchanged", left);
        Assert.Contains("tst_unbased", left);
        Assert.Contains("tst_changed", left);
        Assert.Contains("tst_unchanged", File.ReadAllText(Path.Combine(folder, "BIOA_TST01.dropped.json")));
        Assert.IsEmpty(Directory.GetFiles(folder, "*.tmp"));
    }

    /// <summary>
    /// M8b, M8c: the build fails after the import wrote the YAML and marked the session. The result still comes back, with the
    /// merge, the marked count and the build's error (it used to be the error alone), and the YAML's old text is in the backup.
    /// </summary>
    [TestMethod]
    public void EditorImport_BuildFails_ReceiptAndBackupComeBack()
    {
        var folder = TestSetup.NewFolder("import-build-fails");
        var yaml = Yaml(folder, $$"""
            package: BIOA_TST02.pcc
            props:
              crate:
                tag: tst_crate
                mesh: { package: BIOA_TST02.pcc, export: Crates.Crate_01 }
                location: [100, 0, 0]
                yaw: 0
            {{RefusedLight}}
            """);
        double[] was = [100, 0, 0];
        var session = Session(folder, "BIOA_TST02", 2, Entry("tst_crate", "BIOA_TST02", "StaticMeshActor_1", "StaticMeshActor", [150, 0, 0], State(was)));
        var yamlBefore = File.ReadAllText(yaml);

        var result = EditorImport.Run(MEGame.LE1, yaml, session, build: true);

        Assert.IsNotNull(result.BuildError);
        Assert.Contains("kind", result.BuildError);
        Assert.IsNull(result.Build);
        Assert.IsTrue(result.Notes.Any(n => n.StartsWith("The build failed after the import")), string.Join("\n", result.Notes));
        Assert.AreEqual("updated", result.Actors.Single().Action);
        Assert.IsTrue(result.Written);
        Assert.AreEqual(1, result.Marked);
        Assert.Contains("150", File.ReadAllText(yaml));
        Assert.IsNotNull(result.Backup);
        Assert.AreEqual(yamlBefore, File.ReadAllText(result.Backup));
        Assert.Contains("\"imported\"", File.ReadAllText(session));
        Assert.IsEmpty(Directory.GetFiles(folder, "*.tmp"));
    }

    /// <summary>
    /// M8f: a lights entry whose kind is misspelt fails, and the YAML stays as it is. It used to be read as props, so the
    /// editor's room overwrote it as a change of kind.
    /// </summary>
    [TestMethod]
    public void MergeLight_UnknownYamlKind_FailsAndWritesNothing()
    {
        var folder = TestSetup.NewFolder("import-light-kind");
        var yaml = Yaml(folder, """
            package: BIOA_TST03.pcc
            lights:
              lamp:
                tag: tst_lamp
                kind: rooom
                location: [0, 0, 100]
                radius: 300
                brightness: 0.5
            """);
        double[] was = [0, 0, 100];
        var session = Session(folder, "BIOA_TST03", 2,
            Entry("tst_lamp", "BIOA_TST03", "PointLight_0", "PointLight", [0, 0, 120], State(was, Light("props", 300)), Light("room", 300)));
        var yamlBefore = File.ReadAllText(yaml);

        var result = EditorImport.Run(MEGame.LE1, yaml, session, build: false);

        var lamp = result.Actors.Single();
        Assert.AreEqual("failed", lamp.Action, lamp.Reason);
        Assert.Contains("'rooom'", lamp.Reason!);
        Assert.AreEqual(yamlBefore, File.ReadAllText(yaml));
        Assert.AreEqual(0, result.Marked);
    }

    /// <summary>
    /// M8d: a vanilla actor twice in a session (an old session keyed it by its fork's path and its file's). When the first one
    /// fails, the second says so; it used to say "that one was merged". The file is one no install has, so its lookup fails
    /// without opening a game file (it lists the install's folders, read only).
    /// </summary>
    [TestMethod]
    public void MergeVanilla_Duplicate_SaysWhatBecameOfTheFirst()
    {
        TestSetup.RequireGame();
        var folder = TestSetup.NewFolder("import-vanilla-twice");
        var yaml = Yaml(folder, """
            package: BIOA_TST04.pcc
            streaming:
              levels: [BIOA_ZZZ99_00_LAY]
            """);
        double[] at = [10, 20, 30];
        var session = Session(folder, "BIOA_TST04", 2,
            Entry("StaticMeshActor", "BIOA_ZZZ99_00_LAY", "StaticMeshActor_7", "StaticMeshActor", at, null),
            Entry("StaticMeshActor", "BIOA_ZZZ99_00_LAY", "StaticMeshActor_7", "StaticMeshActor", at, null));

        var result = EditorImport.Run(MEGame.LE1, yaml, session, build: false);

        Assert.HasCount(2, result.Actors);
        Assert.AreEqual("failed", result.Actors[0].Action, result.Actors[0].Reason);
        Assert.AreEqual("skipped", result.Actors[1].Action);
        Assert.Contains("that one was not merged (failed", result.Actors[1].Reason!);
        Assert.DoesNotContain("that one was merged", result.Actors[1].Reason!);
    }

    /// <summary>npc_place, as M8b and M8c: the YAML is backed up before the move is written, and a failed build still returns the placement with its error.</summary>
    [TestMethod]
    public void NpcPlace_BuildFails_PlacementAndBackupComeBack()
    {
        var folder = TestSetup.NewFolder("npc-place-build-fails");
        var yaml = Yaml(folder, $$"""
            package: BIOA_TST05.pcc
            npcs:
              medic:
                tag: tst_medic
                location: [0, 0, 0]
                yaw: 0
            {{RefusedLight}}
            """);
        var yamlBefore = File.ReadAllText(yaml);

        var placed = NpcPlacer.Place(MEGame.LE1, yaml, null, 10, 20, 30, 90, 0, build: true, live: false);

        Assert.IsNotNull(placed.BuildError);
        Assert.IsNull(placed.Build);
        Assert.IsNotEmpty(placed.YamlChanges);
        Assert.Contains("[10, 20, 30]", File.ReadAllText(yaml));
        Assert.IsNotNull(placed.Backup);
        Assert.AreEqual(yamlBefore, File.ReadAllText(placed.Backup));
        Assert.IsEmpty(Directory.GetFiles(folder, "*.tmp"));
    }
}
