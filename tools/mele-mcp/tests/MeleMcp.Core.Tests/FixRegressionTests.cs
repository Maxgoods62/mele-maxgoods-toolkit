using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MeleMcp.Core.Tests;

/// <summary>Bugs found by the 2026-11-06 review, one test each, so they stay fixed.</summary>
[TestClass]
public class FixRegressionTests
{
    // A vanilla LE1 conversation file. The FaceFX tests open it read-only and never save it.
    private const string ConversationFile = "BIOA_NOR10_04_DSG_LOC_INT.pcc";

    private static List<FaceFXLine> Lines(ExportEntry set) => ObjectBinary.From<FaceFXAnimSet>(set).Lines;

    /// <summary>
    /// conv_build gives a node a new strref and copies FXA_&lt;old&gt; to FXA_&lt;new&gt; without sound, for lip sync. That copy first
    /// removed any line already named FXA_&lt;new&gt;, so a voiced line of that strref lost its sound. This goes through
    /// conv_build's own call (CopyFaceFx), on nor20_trig03_callhome, whose owner already has voiced lines for 140066 and 140049.
    /// </summary>
    [TestMethod]
    public void ConvBuild_LipSyncCopy_KeepsTheNewStrrefsVoicedLines()
    {
        TestSetup.RequireGame();
        using var pcc = MEPackageHandler.OpenMEPackage(GamePaths.ResolvePackage(MEGame.LE1, ConversationFile));
        var conv = pcc.Exports.Single(e => e.ClassName == "BioConversation" && e.ObjectName.Instanced == "nor20_trig03_callhome_dlg");
        var props = conv.GetProperties();
        // The owner, speaker -1, is entry 1 of both lists (CopyFaceFx: speaker + 2).
        var targets = new[] { ("m_aMaleFaceSets", "FXA_140049_M"), ("m_aFemaleFaceSets", "FXA_140049") }
            .Select(t => (Set: (ExportEntry)props.GetProp<ArrayProperty<ObjectProperty>>(t.Item1)[1].ResolveToEntry(pcc), Name: t.Item2))
            .Select(t => (t.Set, t.Name, Before: Lines(t.Set).Single(l => l.NameAsString == t.Name)))
            .ToList();
        Assert.IsTrue(targets.All(t => t.Before.Index >= 0), "the fixture's lines should be voiced");

        var notes = new List<string>();
        var copied = ConversationEditor.CopyFaceFx(pcc, props, speaker: -1, oldStrRef: 140066, strRef: 140049, notes).ToList();

        Assert.IsEmpty(copied, string.Join(", ", copied));
        Assert.HasCount(2, notes.Where(n => n.Contains("kept as it is")), string.Join("\n", notes));
        foreach (var (set, name, before) in targets)
        {
            var now = Lines(set).Single(l => l.NameAsString == name);
            Assert.AreEqual(before.Index, now.Index, $"{name} lost its sound cue");
            Assert.AreEqual(before.Path, now.Path, name);
            Assert.AreEqual(before.ID, now.ID, name);
        }
    }

    /// <summary>
    /// CopyIn's other rules: a copy onto its own name does nothing (it used to strip the line's sound), and without
    /// keepExisting (facefx_copy_lines) an existing line is still replaced, as that tool says.
    /// </summary>
    [TestMethod]
    public void FaceFx_CopyIn_SelfCopyIsANoOp_AndTheToolStillReplaces()
    {
        TestSetup.RequireGame();
        using var pcc = MEPackageHandler.OpenMEPackage(GamePaths.ResolvePackage(MEGame.LE1, ConversationFile));
        var set = pcc.Exports.First(e => e.ClassName == "FaceFXAnimSet" && Lines(e).Count(l => l.Index >= 0) >= 2);
        var voiced = Lines(set).Where(l => l.Index >= 0).Take(2).ToList();
        var (a, b) = (voiced[0].NameAsString, voiced[1].NameAsString);
        FaceFXLine Line(string name) => Lines(set).Single(l => l.NameAsString == name);
        void AssertVoiced(string name, FaceFXLine before)
        {
            var now = Line(name);
            Assert.AreEqual(before.Index, now.Index, $"{name} lost its sound cue");
            Assert.AreEqual(before.Path, now.Path, name);
            Assert.AreEqual(before.ID, now.ID, name);
        }

        var (created, replaced, _, _) = FaceFxSets.CopyIn(set, [new FaceFxLineCopy(a, a)], withSound: false, skipMissing: false);
        Assert.IsEmpty(created);
        Assert.IsEmpty(replaced);
        AssertVoiced(a, voiced[0]);
        AssertVoiced(b, voiced[1]);

        // facefx_copy_lines still replaces an existing line, as its description says.
        (created, replaced, _, _) = FaceFxSets.CopyIn(set, [new FaceFxLineCopy(a, b)], withSound: false, skipMissing: false);
        CollectionAssert.AreEqual(new[] { b }, replaced);
        Assert.AreEqual(-1, Line(b).Index);
    }

    /// <summary>
    /// mod_check's id clash check decided "ours" with StartsWith(folder), so a mod DLC_MOD_X took DLC_MOD_XB's ids for its own
    /// and never reported "also defined by ... one overrides the other". Sources come from IdAllocator.Collect.
    /// </summary>
    [TestMethod]
    [DataRow("DLC_MOD_X: BIOG_DLC_MOD_X_Plot.pcc BioStateEventMap", true)]
    [DataRow("dlc_mod_x: BIOG_DLC_MOD_X_Plot.pcc BioStateEventMap", true)]
    [DataRow(@"DLC_MOD_X (D:\Mods\DLC_MOD_X): BIOG_DLC_MOD_X_Plot.pcc BioStateEventMap", true)]
    [DataRow("DLC_MOD_XB: BIOG_DLC_MOD_XB_Plot.pcc BioStateEventMap", false)]
    [DataRow(@"DLC_MOD_X_Extra (D:\Mods\DLC_MOD_X_Extra): BIOG_Extra.pcc BioStateEventMap", false)]
    [DataRow("base game: BIOC_Materials.pcc PlotManagerAuto.StateTransitionMap", false)]
    [DataRow("LEX mod plot database DLC_MOD_X", false)]
    public void ModCheck_IdSource_IsOwnOnlyForTheExactFolder(string source, bool own) =>
        Assert.AreEqual(own, ModChecker.IsOwnSource(source, ["DLC_MOD_X"]), source);

    /// <summary>
    /// mergemod_build named an array literal's file &lt;stem&gt;_&lt;property&gt;.uc, so two exports that set an array of the same
    /// name shared one file and both got the second literal.
    /// </summary>
    [TestMethod]
    public void MergeArrayFiles_TwoExportsSameProperty_KeepTheirOwnLiteral()
    {
        var sources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] a = "(1,2)"u8.ToArray(), b = "(3)"u8.ToArray();

        var fileA = MergeMods.ArrayAssetFile(sources, "MyMerge", "Default__SFXPawn_A", "Items", a);
        var fileB = MergeMods.ArrayAssetFile(sources, "MyMerge", "Default__SFXPawn_B", "Items", b);
        Assert.AreNotEqual(fileA, fileB);
        CollectionAssert.AreEqual(a, sources[fileA]);
        CollectionAssert.AreEqual(b, sources[fileB]);

        // Exports whose names differ only in characters the file name drops (. and _) still get a file per literal.
        byte[] c = "(4)"u8.ToArray();
        var fileC = MergeMods.ArrayAssetFile(sources, "MyMerge", "Default__SFXPawn.A", "Items", b);
        var fileD = MergeMods.ArrayAssetFile(sources, "MyMerge", "Default__SFXPawn_A", "Items", c);
        Assert.AreEqual(4, new[] { fileA, fileB, fileC, fileD }.Distinct(StringComparer.OrdinalIgnoreCase).Count(), string.Join(", ", sources.Keys));
        CollectionAssert.AreEqual(a, sources[fileA], "a later literal overwrote an earlier file");

        // The same literal again (CheckProperty runs once per check) reuses its file.
        Assert.AreEqual(fileA, MergeMods.ArrayAssetFile(sources, "MyMerge", "Default__SFXPawn_A", "Items", a));
        Assert.HasCount(4, sources);
    }
}
