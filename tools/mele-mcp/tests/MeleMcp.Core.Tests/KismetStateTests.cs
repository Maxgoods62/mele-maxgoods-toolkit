using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core.Tests;

/// <summary>
/// The Kismet level_build links for a squad, an NPC's conversation and her idle follows the YAML both ways: an entry changed
/// since the last build leaves exactly one chain firing, and a removed one leaves none. KismetPrefabs.Link only adds, so before
/// these fixes a toggled 'streaming' built a second squad chain and a renamed conversation started beside the old one. Each test
/// builds twice into one empty LE1 level made in memory (nothing is saved); the sequence objects' class defaults and the prefabs
/// (BIOA_FRE31.pcc, BIOA_NOR10_03_DSG.pcc) are read from the game, read-only.
/// </summary>
[TestClass]
public class KismetStateTests
{
    private const string MainSequence = "TheWorld.PersistentLevel.Main_Sequence";
    private const string CinematicMode = "BioSeqAct_BioToggleCinematicMode";
    private const string PartySelection = "BioSeqAct_ShowPartySelectionGUI";
    private const string Wait = "SeqAct_WaitForLevelsVisible";
    private const string Loaded = "Loaded and Visible";

    [TestMethod]
    public void Squad_StreamingAddedThenRemoved_OneChainFires()
    {
        using var pcc = NewLevel(out var sequence);
        using var cache = new PackageCache();
        var changes = new List<string>();

        SquadSpawns.Set(MEGame.LE1, pcc, sequence, "BIOA_FRE31.pcc", [], 1, cache, changes);
        AssertSquad(sequence, fires: 1, viaWait: false);

        SquadSpawns.Set(MEGame.LE1, pcc, sequence, "BIOA_FRE31.pcc", ["BIOA_FRE31_00_LAY"], 1, cache, changes);
        AssertSquad(sequence, fires: 1, viaWait: true);
        CollectionAssert.Contains(changes, "Kismet: level loaded → squad chain unlinked");

        SquadSpawns.Set(MEGame.LE1, pcc, sequence, "BIOA_FRE31.pcc", [], 1, cache, changes);
        AssertSquad(sequence, fires: 1, viaWait: false);
        Assert.IsFalse(Objects(sequence).Any(o => o.ClassName == Wait), "the wait that only led into the squad chain should go");
    }

    [TestMethod]
    public void Squad_Removed_NothingFires_AndComesBackAsTheSameChain()
    {
        using var pcc = NewLevel(out var sequence);
        using var cache = new PackageCache();
        var (changes, notes) = (new List<string>(), new List<string>());

        SquadSpawns.Set(MEGame.LE1, pcc, sequence, "BIOA_FRE31.pcc", ["BIOA_FRE31_00_LAY"], 1, cache, changes);
        SquadSpawns.Remove(sequence, changes, notes);
        AssertSquad(sequence, fires: 0, viaWait: false);
        Assert.HasCount(1, notes, string.Join("\n", notes));

        // Nothing left to cut: no second note.
        SquadSpawns.Remove(sequence, changes, notes);
        Assert.HasCount(1, notes, string.Join("\n", notes));

        SquadSpawns.Set(MEGame.LE1, pcc, sequence, "BIOA_FRE31.pcc", ["BIOA_FRE31_00_LAY"], 1, cache, changes);
        AssertSquad(sequence, fires: 1, viaWait: true);
    }

    [TestMethod]
    public void Conversation_RenamedThenRemoved_UseStartsOnlyWhatTheFileNames()
    {
        using var pcc = NewLevel(out var sequence);
        using var cache = new PackageCache();
        var changes = new List<string>();
        var pawn = Pawn(pcc, cache);
        AddConversation(pcc, "conv_a", cache);
        AddConversation(pcc, "conv_b", cache);

        var a = LevelBuilder.LinkConversation(pcc, pawn, "conv_a", 1, cache, changes)!.Value;
        CollectionAssert.AreEqual(new[] { "conv_a" }, Started(a.Use));

        var b = LevelBuilder.LinkConversation(pcc, pawn, "conv_b", 1, cache, changes)!.Value;
        Assert.AreEqual(a.Use, b.Use);
        CollectionAssert.AreEqual(new[] { "conv_b" }, Started(b.Use));
        CollectionAssert.Contains(changes, "Kismet: use → start conv_a unlinked");

        // Renamed to one the level does not have yet: the old one stops starting too.
        Assert.IsNull(LevelBuilder.LinkConversation(pcc, pawn, "conv_c", 1, cache, changes));
        Assert.IsEmpty(Started(b.Use));

        // Back to conv_b: its start is found again, not made a second time.
        LevelBuilder.LinkConversation(pcc, pawn, "conv_b", 1, cache, changes);
        CollectionAssert.AreEqual(new[] { "conv_b" }, Started(b.Use));
        Assert.AreEqual(2, Objects(sequence).Count(o => o.ClassName == "BioSeqAct_StartConversation"));

        // No conversation in the file.
        CollectionAssert.AreEqual(new[] { "conv_b" }, LevelBuilder.UnlinkConversations(sequence, pawn, null, changes));
        Assert.IsEmpty(Started(b.Use));
    }

    [TestMethod]
    public void Idle_ConversationRenamedThenGone_ThenIdleRemoved()
    {
        using var pcc = NewLevel(out var sequence);
        using var cache = new PackageCache();
        var (changes, notes) = (new List<string>(), new List<string>());
        var pawn = Pawn(pcc, cache);
        AddConversation(pcc, "conv_a", cache);
        AddConversation(pcc, "conv_b", cache);
        var idle = new IdleStations.Spec("BIOA_NOR10_03_DSG.pcc", "HMM_DP_ArmsCrossed", new(), 1);

        var a = LevelBuilder.LinkConversation(pcc, pawn, "conv_a", 1, cache, changes)!.Value;
        IdleStations.Set(MEGame.LE1, pcc, sequence, pawn, idle, a.Use, a.Start, cache, changes);
        var station = Objects(sequence).Single(o => o.ClassName == "SequenceReference");
        var loaded = Objects(sequence).Single(o => o.ClassName == "SeqEvent_LevelLoaded");

        var b = LevelBuilder.LinkConversation(pcc, pawn, "conv_b", 1, cache, changes)!.Value;
        IdleStations.Set(MEGame.LE1, pcc, sequence, pawn, idle, b.Use, b.Start, cache, changes);
        CollectionAssert.AreEqual(new[] { b.Start }, StartsInto(sequence, station), "only the conversation the file names should start the pose again");
        Assert.IsTrue(Leaves(b.Use));

        // The file drops the conversation and keeps the idle: using her must not take her out of the pose for good.
        LevelBuilder.UnlinkConversations(sequence, pawn, null, changes);
        IdleStations.Set(MEGame.LE1, pcc, sequence, pawn, idle, null, null, cache, changes);
        Assert.IsEmpty(StartsInto(sequence, station));
        Assert.IsFalse(Leaves(b.Use));
        CollectionAssert.Contains(Targets(loaded, Loaded), station);

        // Back with a conversation, then the idle goes.
        b = LevelBuilder.LinkConversation(pcc, pawn, "conv_b", 1, cache, changes)!.Value;
        IdleStations.Set(MEGame.LE1, pcc, sequence, pawn, idle, b.Use, b.Start, cache, changes);
        Assert.AreEqual(station, Objects(sequence).Single(o => o.ClassName == "SequenceReference"), "the same pose should keep its prefab copy");
        IdleStations.Remove(sequence, pawn, "Npc", changes, notes);
        CollectionAssert.DoesNotContain(Targets(loaded, Loaded), station);
        Assert.IsEmpty(StartsInto(sequence, station));
        Assert.IsFalse(Leaves(b.Use));
        CollectionAssert.AreEqual(new[] { "conv_b" }, Started(b.Use), "removing the idle keeps her conversation");
        Assert.HasCount(1, notes, string.Join("\n", notes));
    }

    /// <summary>An empty LE1 level in memory, never saved, and its Main_Sequence.</summary>
    private static IMEPackage NewLevel(out ExportEntry sequence)
    {
        TestSetup.RequireGame();
        var pcc = MEPackageHandler.CreateMemoryEmptyLevel(Path.Combine(TestSetup.Temp, "KismetState.pcc"), MEGame.LE1);
        sequence = pcc.FindExport(MainSequence) ?? throw new AssertFailedException($"LEC's empty level has no {MainSequence}.");
        return pcc;
    }

    private static ExportEntry Pawn(IMEPackage pcc, PackageCache cache) => Add(pcc, "BioPawn", pcc.GetNextIndexedName("BioPawn"), cache);

    /// <summary>A BioConversation named exactly <paramref name="name"/>, as LinkConversation looks it up.</summary>
    private static void AddConversation(IMEPackage pcc, string name, PackageCache cache) => Add(pcc, "BioConversation", new NameReference(name), cache);

    private static ExportEntry Add(IMEPackage pcc, string className, NameReference name, PackageCache cache)
    {
        var export = new ExportEntry(pcc, pcc.FindExport("TheWorld.PersistentLevel"), name)
        {
            Class = EntryImporter.EnsureClassIsInFile(pcc, className, new RelinkerOptionsPackage { Cache = cache }),
        };
        pcc.AddExport(export);
        return export;
    }

    private static List<ExportEntry> Objects(ExportEntry sequence) => KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().ToList();

    private static List<ExportEntry> Targets(ExportEntry from, string output) =>
        KismetHelper.GetOutputLinksOfNode(from)[KismetHelper.GetOutputLinkNames(from).IndexOf(output)].Select(l => l.LinkedOp).OfType<ExportEntry>().ToList();

    /// <summary>The squad chains the level loaded event fires, directly and through its waits; and the chains built at all.</summary>
    private static void AssertSquad(ExportEntry sequence, int fires, bool viaWait)
    {
        var loaded = Objects(sequence).Single(o => o.ClassName == "SeqEvent_LevelLoaded");
        var direct = Targets(loaded, Loaded).Where(o => o.ClassName == CinematicMode).ToList();
        var waited = Targets(loaded, Loaded).Where(o => o.ClassName == Wait).SelectMany(w => Targets(w, "Finished")).Where(o => o.ClassName == CinematicMode).ToList();
        Assert.AreEqual(fires, direct.Count + waited.Count, $"chains fired: {direct.Count} on load, {waited.Count} after a wait");
        if (fires == 1) Assert.HasCount(viaWait ? 1 : 0, waited);
        // The prefab's own party selection screen is inside its SequenceReference, not in Main_Sequence.
        Assert.AreEqual(1, Objects(sequence).Count(o => o.ClassName == PartySelection), "one chain should be built, not one per build");
    }

    /// <summary>The conversations a use event starts.</summary>
    private static List<string> Started(ExportEntry use) =>
        Targets(use, "Out").Where(o => o.ClassName == "BioSeqAct_StartConversation")
            .Select(s => s.GetProperty<ObjectProperty>("Conv")!.ResolveToEntry(s.FileRef)!.ObjectName.Instanced).ToList();

    private static List<ExportEntry> StartsInto(ExportEntry sequence, ExportEntry station) =>
        Objects(sequence).Where(o => o.ClassName == "BioSeqAct_StartConversation" && Targets(o, "Out").Contains(station)).ToList();

    private static bool Leaves(ExportEntry use) => Targets(use, "Out").Any(o => o.ClassName == "BioSeqAct_LeaveActionStation");
}
