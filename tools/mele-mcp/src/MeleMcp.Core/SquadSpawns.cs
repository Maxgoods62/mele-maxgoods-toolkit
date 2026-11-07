using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

/// <summary>
/// The player's squad in a level of our own. Mission maps get it from Kismet, with BioWare's squad prefab (the same in
/// BIOA_FRE3x and BIOA_UNCxx): its remote event Hench_SetupSquad spawns the henchmen the "in squad" plot bools name, by tag
/// next to the player, then ends with "Setup Done". Around it, the order of the freighter's arrival Kismet
/// (BIOA_FRE31_00_DSG): once the streamed files are in, cinematic mode on, black screen, party selection screen (which sets
/// the plot bools), cinematic mode off, the spawn, and the screen back on.
/// </summary>
internal static class SquadSpawns
{
    private const string SetupSquad = "Hench_SetupSquad";
    private const string CinematicMode = "BioSeqAct_BioToggleCinematicMode";
    private const string BlackScreen = "BioSeqAct_BlackScreen";
    private const string PartySelection = "BioSeqAct_ShowPartySelectionGUI";
    private const string WaitForLevels = "SeqAct_WaitForLevelsVisible";
    // The outputs a chain starts on: the level loaded event's, or the wait's.
    private const string Loaded = "Loaded and Visible", Finished = "Finished";

    // Inputs of SeqAct_ToggleCinematicMode.
    private const int Enable = 0, Disable = 1;

    /// <param name="levels">The files the level streams, by name: the squad waits until they are visible.</param>
    internal static void Set(MEGame game, IMEPackage pcc, ExportEntry sequence, string package, IReadOnlyList<string> levels, int line, PackageCache cache, List<string> changes)
    {
        var prefab = KismetPrefabs.Find(sequence, e => e.ClassName == PartySelection);
        if (prefab is null)
        {
            prefab = KismetPrefabs.Import(game, pcc, sequence, package, source => Prefab(source, package, line));
            changes.Add($"squad (prefab from {package})");
        }
        if (!KismetHelper.GetOutputLinkNames(prefab).Contains("Setup Done")) throw new InvalidOperationException($"line {line}: the squad prefab has no 'Setup Done' output.");

        // It waits for the streamed files (their floor, paths and lights), as the freighter's arrival Kismet does. That one
        // starts on BioSeqEvt_ForegroundStreamingDone, which a stream volume fires and which did not fire in our map: the engine's wait instead.
        // The chain an earlier build made starts on the wait or on the level loaded event, as that build had 'streaming' or not: it is
        // found from either, so a 'streaming' added or removed since moves it instead of building a second one beside it. One that
        // Remove unlinked (the file had no 'squad' for a while) is linked again.
        var loaded = KismetPrefabs.LevelLoaded(sequence, cache);
        var wait = KismetPrefabs.Next(loaded, Loaded, WaitForLevels);
        var heads = (levels.Count > 0 ? Heads(wait, Finished).Concat(Heads(loaded, Loaded)) : Heads(loaded, Loaded).Concat(Heads(wait, Finished)))
            .Concat(Unlinked(sequence));
        var cinematicOn = heads.FirstOrDefault() ?? SequenceObjectCreator.CreateSequenceObject(sequence, CinematicMode, cache);
        var (start, startOutput) = (loaded, Loaded);
        if (levels.Count > 0)
        {
            wait ??= SequenceObjectCreator.CreateSequenceObject(sequence, WaitForLevels, cache);
            wait.WriteProperty(new ArrayProperty<NameProperty>(levels.Select(l => new NameProperty(l)), "LevelNames"));
            KismetPrefabs.Link(loaded, Loaded, wait, changes, $"Kismet: level loaded → wait for {string.Join(", ", levels)}");
            (start, startOutput) = (wait, Finished);
        }
        KismetPrefabs.Link(start, startOutput, cinematicOn, changes, "Kismet: → cinematic mode on", Enable);
        // Exactly one chain fires (one black screen, one party selection screen, one Hench_SetupSquad): the other start's edge
        // goes, and any second chain an older build left beside this one.
        Cut(loaded, wait, start, cinematicOn, changes);
        var black = KismetPrefabs.Next(cinematicOn, "Out", BlackScreen) ?? SequenceObjectCreator.CreateSequenceObject(sequence, BlackScreen, cache);
        KismetPrefabs.Link(cinematicOn, "Out", black, changes, "Kismet: → black screen");
        var selection = KismetPrefabs.Next(black, "Finished", PartySelection) ?? SequenceObjectCreator.CreateSequenceObject(sequence, PartySelection, cache);
        foreach (var output in new[] { "Finished", "Aborted", "Cancelled" }) KismetPrefabs.Link(black, output, selection, changes, $"Kismet: black screen {output.ToLowerInvariant()} → party selection");
        var cinematicOff = KismetPrefabs.Next(selection, "Finished", CinematicMode) ?? SequenceObjectCreator.CreateSequenceObject(sequence, CinematicMode, cache);
        KismetPrefabs.Link(selection, "Finished", cinematicOff, changes, "Kismet: → cinematic mode off", Disable);
        var setup = KismetPrefabs.Next(cinematicOff, "Out", "SeqAct_ActivateRemoteEvent");
        if (setup is null)
        {
            setup = SequenceObjectCreator.CreateSequenceObject(sequence, "SeqAct_ActivateRemoteEvent", cache);
            setup.WriteProperty(new NameProperty(SetupSquad, "EventName"));
        }
        KismetPrefabs.Link(cinematicOff, "Out", setup, changes, $"Kismet: → {SetupSquad}");

        var screenOn = KismetPrefabs.Next(prefab, "Setup Done", BlackScreen);
        if (screenOn is null)
        {
            screenOn = SequenceObjectCreator.CreateSequenceObject(sequence, BlackScreen, cache);
            screenOn.WriteProperty(new EnumProperty("BlackScreenAction_TurnBlackOff", "BlackScreenActionSet", game, "m_eBlackScreenAction"));
        }
        KismetPrefabs.Link(prefab, "Setup Done", screenOn, changes, "Kismet: squad set up → screen back on");
    }

    /// <summary>
    /// A level whose YAML has no 'squad' (any more): the chain <see cref="Set"/> built loses its edge from the level loaded event or
    /// its wait, so no party selection screen opens and Hench_SetupSquad never fires. Its objects and the prefab stay in Main_Sequence,
    /// unlinked; a 'squad' added again links the same chain. Only a chain of the shape Set builds is cut, never other Kismet.
    /// </summary>
    internal static void Remove(ExportEntry sequence, List<string> changes, List<string> notes)
    {
        if (KismetPrefabs.FindLevelLoaded(sequence) is not { } loaded) return;
        if (Cut(loaded, KismetPrefabs.Next(loaded, Loaded, WaitForLevels), null, null, changes) == 0) return;
        notes.Add("The file has no 'squad': the level no longer opens the party selection screen or spawns the squad. "
                  + "Its Kismet stays in Main_Sequence, unlinked from the level loaded event.");
    }

    /// <summary>
    /// Cuts the edges into squad chains from the level loaded event and from its wait, all but <paramref name="keep"/>'s from
    /// <paramref name="start"/> (both null: every one). A wait that led into a chain and now leads nowhere goes too: it is only there for
    /// the squad, and a later 'streaming' would make another beside it. Returns how many edges it cut.
    /// </summary>
    private static int Cut(ExportEntry loaded, ExportEntry? wait, ExportEntry? start, ExportEntry? keep, List<string> changes)
    {
        var cut = KismetPrefabs.Unlink(loaded, Loaded, o => IsHead(o) && !(start == loaded && o == keep)).Count;
        if (cut > 0) changes.Add("Kismet: level loaded → squad chain unlinked");
        if (wait is null) return cut;
        var fromWait = KismetPrefabs.Unlink(wait, Finished, o => IsHead(o) && !(start == wait && o == keep)).Count;
        if (fromWait == 0) return cut;
        changes.Add("Kismet: wait for levels → squad chain unlinked");
        if (start != wait && KismetHelper.GetOutputLinksOfNode(wait).All(l => l.Count == 0))
        {
            KismetHelper.RemoveFromSequence(wait, trash: true);
            changes.Add("Kismet: wait for levels removed");
        }
        return cut + fromWait;
    }

    /// <summary>The squad chains an output starts, in link order.</summary>
    private static IEnumerable<ExportEntry> Heads(ExportEntry? from, string output)
    {
        if (from is null) return [];
        var index = KismetHelper.GetOutputLinkNames(from).IndexOf(output);
        if (index < 0) throw new InvalidOperationException($"#{from.UIndex} {from.ClassName} has no output '{output}'.");
        return KismetHelper.GetOutputLinksOfNode(from)[index].Select(o => o.LinkedOp).OfType<ExportEntry>().Where(IsHead).Distinct().ToList();
    }

    /// <summary>The squad chains of the sequence nothing links to (lazily: only read when no linked chain is found).</summary>
    private static IEnumerable<ExportEntry> Unlinked(ExportEntry sequence)
    {
        var objects = KismetHelper.GetSequenceObjects(sequence).OfType<ExportEntry>().ToList();
        foreach (var head in objects.Where(IsHead))
        {
            if (KismetHelper.FindOutputConnectionsToNode(head, objects).Count == 0) yield return head;
        }
    }

    /// <summary>
    /// The head of a chain as <see cref="Set"/> builds it: cinematic mode on, then a black screen, then the party selection screen. The
    /// shape tells it from anything else on the same output, which is never moved or cut.
    /// </summary>
    private static bool IsHead(ExportEntry o) =>
        o.ClassName == CinematicMode && After(o, "Out", BlackScreen) is { } black && After(black, "Finished", PartySelection) is not null;

    /// <summary><see cref="KismetPrefabs.Next"/>, or null when <paramref name="from"/> has no such output.</summary>
    private static ExportEntry? After(ExportEntry from, string output, string className) =>
        KismetHelper.GetOutputLinkNames(from).Contains(output) ? KismetPrefabs.Next(from, output, className) : null;

    /// <summary>The SequenceReference whose sequence holds the party selection screen: in BIOA_FRE31, the one with input "Level Startup".</summary>
    private static ExportEntry Prefab(IMEPackage source, string package, int line)
    {
        var screen = source.Exports.FirstOrDefault(e => e.ClassName == PartySelection)
                     ?? throw new ArgumentException($"line {line}: {package} has no party selection screen ({PartySelection}), so no squad prefab.");
        if (screen.Parent?.Parent is ExportEntry { ClassName: "SequenceReference" } reference) return reference;
        throw new ArgumentException($"line {line}: the party selection screen in {package} is not inside a prefab (SequenceReference).");
    }
}
