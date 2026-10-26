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
        var loaded = KismetPrefabs.LevelLoaded(sequence, cache);
        var (start, startOutput) = (loaded, "Loaded and Visible");
        if (levels.Count > 0)
        {
            var wait = KismetPrefabs.Next(loaded, "Loaded and Visible", WaitForLevels) ?? SequenceObjectCreator.CreateSequenceObject(sequence, WaitForLevels, cache);
            wait.WriteProperty(new ArrayProperty<NameProperty>(levels.Select(l => new NameProperty(l)), "LevelNames"));
            KismetPrefabs.Link(loaded, "Loaded and Visible", wait, changes, $"Kismet: level loaded → wait for {string.Join(", ", levels)}");
            (start, startOutput) = (wait, "Finished");
        }
        var cinematicOn = KismetPrefabs.Next(start, startOutput, CinematicMode) ?? SequenceObjectCreator.CreateSequenceObject(sequence, CinematicMode, cache);
        KismetPrefabs.Link(start, startOutput, cinematicOn, changes, "Kismet: → cinematic mode on", Enable);
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

    /// <summary>The SequenceReference whose sequence holds the party selection screen: in BIOA_FRE31, the one with input "Level Startup".</summary>
    private static ExportEntry Prefab(IMEPackage source, string package, int line)
    {
        var screen = source.Exports.FirstOrDefault(e => e.ClassName == PartySelection)
                     ?? throw new ArgumentException($"line {line}: {package} has no party selection screen ({PartySelection}), so no squad prefab.");
        if (screen.Parent?.Parent is ExportEntry { ClassName: "SequenceReference" } reference) return reference;
        throw new ArgumentException($"line {line}: the party selection screen in {package} is not inside a prefab (SequenceReference).");
    }
}
