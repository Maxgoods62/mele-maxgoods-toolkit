using System.ComponentModel;
using LegendaryExplorerCore.Packages;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Strings (LE1: BioTlkFile exports in packages; LE2/LE3: loose .tlk files) and conversations.</summary>
[McpServerToolType]
public static class TalkTools
{
    private const string PackageDescription =
        "LE1: package with BioTlkFile exports, e.g. the mod's DLC_MOD_<Name>_GlobalTlk_FR.pcc or a conversation's _LOC_ file. File name or full path. "
        + "LE2/LE3: a .tlk file, one per module and language: BIOGame_INT.tlk, DLC_CON_DH1_INT.tlk, DLC_3_INT.tlk (LE2 names DLC files by the module number in "
        + "BIOEngine.ini [Engine.DLCModules]: 3 is DLC_HEN_MT). File name (highest mounted copy) or full path.";

    [McpServerTool(Name = "tlk_get", ReadOnly = true),
     Description("Reads strings. LE1: from the BioTlkFile exports of a package (each file is listed: GlobalTlk packages have a male and a female copy). "
                 + "LE2/LE3: from one loose .tlk file; female shows the file's female table text for an id (lines that differ for a female Shepard: "
                 + "almost none in INT, hundreds to thousands in other languages). Tested offline on shipped files, not in game.")]
    public static object TlkGet(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description(PackageDescription)] string package,
        [Description("String id to look up. Omit to list.")] int? id = null,
        [Description("Case-insensitive text filter.")] string? textContains = null,
        [Description("LE1: one BioTlkFile export (UIndex or path). Default: all of them.")] string? export = null,
        [Description("Maximum strings per file.")] int limit = 50)
        => ToolErrors.Run<object>(() =>
        {
            var parsed = PackageTools.ParseGame(game);
            if (parsed.IsGame1()) return TalkFiles.Get(parsed, package, export, id, textContains, limit);
            if (export is not null) throw new ArgumentException(".tlk files have no exports: leave export out.");
            return TalkTables.Get(parsed, package, id, textContains, limit);
        });

    [McpServerTool(Name = "conv_read", ReadOnly = true),
     Description("Reads a BioConversation as a text graph: start entries, entries (NPC lines, speaker -1 = owner) with their wheel choices, "
                 + "replies (Shepard) with the entries they lead to, plot conditions/transitions, nExportID (link to the node's camera data), "
                 + "and the VO track's strref. Text comes from the conversation's own TLK for the language (LE1) or from the game's .tlk files, base game "
                 + "and DLC, highest mount winning, with female: \"...\" after a line that differs for a female Shepard (LE2/LE3). "
                 + "LE2/LE3 keep conversations in _LOC_<LANG> packages only.")]
    public static string ConvRead(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("Package holding the conversation, usually <area>_LOC_<LANG>.pcc. File name or full path.")] string package,
        [Description("BioConversation export (UIndex, path or unique name).")] string export,
        [Description("TLK language. LE1: INT, FR, DE, IT, PLPC, RA... LE2/LE3: INT, FRA, DEU, ESN, ITA, JPN, POL, RUS (the LE1 names work too).")] string language = "FR")
        => ToolErrors.Run(() => ConversationReader.Read(PackageTools.ParseGame(game), package, export, language));

    [McpServerTool(Name = "conv_find", ReadOnly = true),
     Description("Searches every conversation the game loads (base game, DLC, mods) for lines by speaker tag, text or plot id, e.g. "
                 + "every henchman comment gated by conditional 415, every line Wrex (hench_krogan) says about Saren, or a template to "
                 + "copy. Returns file, conversation, node (E<n>/R<n> as conv_read names them), speaker, strref, text and plot gate. "
                 + "Filters combine; a search reads every package (a few seconds, LE3 the slowest).")]
    public static ConvFindResult ConvFind(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("Substring of the speaker tag (e.g. hench_asari, hench_krogan, nor_chakwas), or owner / player.")] string? speaker = null,
        [Description("Case-insensitive substring of the line or its wheel paraphrase.")] string? text = null,
        [Description("A conditional, plot bool or transition id the line uses, e.g. 415.")] int? plotId = null,
        [Description("Substring of the conversation name, e.g. _dlg or nor10.")] string? conversation = null,
        [Description("Wildcard on package file names, e.g. BIOA_NOR*.")] string fileFilter = "*",
        [Description("Language of the text and of the _LOC_ packages read: INT, FR, DE, IT... LE2/LE3 also take FRA, DEU, ESN, ITA, JPN, POL, RUS.")] string language = "FR",
        [Description("Maximum lines returned (Matches still counts all).")] int limit = 100)
        => ToolErrors.Run(() => ConversationFinder.Find(PackageTools.ParseGame(game), speaker, text, plotId, conversation, fileFilter, language, limit));

    [McpServerTool(Name = "conv_audio", ReadOnly = true),
     Description("Lists LE1 conversation lines that have no voice yet: for each spoken line, follows its VO track's strref to the speaker's "
                 + "FaceFX line FXA_<strref> (female Shepard) and FXA_<strref>_M (male), its sound cue, SoundNodeWave and ISACT sound. "
                 + "Status, worst first: broken, other-voice (plays a wave named for another line: wrong words), no-vo-track, no-set, "
                 + "no-facefx (no lips, no voice), silent (lips move, no sound: our new lines), unchecked, voiced. Give a mod (all its packages) or one package.")]
    public static ConvAudioResult ConvAudio(
        [Description("Game: LE1.")] string game,
        [Description("moddesc.ini, a mod folder, or a mod's folder name in M3's library, e.g. 'My Mod'.")] string? mod = null,
        [Description("One package instead of a mod, e.g. BIOA_NOR10_11_DSG_LOC_FR.pcc. File name or full path.")] string? package = null,
        [Description("Substring of the conversation name, e.g. req_officer.")] string? conversation = null,
        [Description("Language of the text and of the _LOC_ packages read (each has its own audio): INT, FR, DE, IT...")] string language = "FR",
        [Description("Also list voiced lines.")] bool includeVoiced = false,
        [Description("Also list lines no link leads to (template leftovers).")] bool includeUnreachable = false,
        [Description("Maximum lines returned (Statuses still counts all).")] int limit = 200)
        => ToolErrors.Run(() => ConversationAudio.Check(PackageTools.ParseGame(game), mod, package, conversation, language, includeVoiced, includeUnreachable, limit));

    [McpServerTool(Name = "conv_add_node", Destructive = true),
     Description("Adds an LE1 conversation line by copying a template node (E<n> NPC line or R<n> Shepard line): new text strref and "
                 + "export id, no links, plot cleared. Also copies the template's node data (ConvNode, Interp with cameras/gestures, "
                 + "EndCurrentConvNode) with the VO track set to the strref, and its FaceFX lines to FXA_<strref>[_M] without sound, "
                 + "so lips move. Write the text with tlk_set and link the node with conv_link. Backs the package up first and refuses files inside the game install.")]
    public static ConvNodeAdded ConvAddNode(
        [Description("Game: LE1.")] string game,
        [Description("Package holding the conversation. File name or full path.")] string package,
        [Description("BioConversation export (UIndex, path or unique name).")] string export,
        [Description("Node to copy, E<n> or R<n> as conv_read prints them. Pick one whose length and gestures fit the new line.")] string template,
        [Description("String id of the new line's text (subtitle, VO track and FaceFX name).")] int strRef,
        [Description("Line duration in seconds (InterpData length). Default: the template's.")] float? length = null,
        [Description("Keep the template's plot condition and transition.")] bool keepPlot = false)
        => ToolErrors.Run(() => ConversationEditor.AddNode(PackageTools.ParseGame(game), package, export, template, strRef, length, keepPlot));

    [McpServerTool(Name = "conv_link", Destructive = true),
     Description("Adds or removes one LE1 conversation link: from 'start' to E<n> (starting list), E<n> to R<n> (wheel choice with "
                 + "paraphrase strref and category), R<n> to E<n> (what the NPC answers). Re-adding an E → R link updates it. "
                 + "Backs the package up first and refuses files inside the game install.")]
    public static ConvLinkResult ConvLink(
        [Description("Game: LE1.")] string game,
        [Description("Package holding the conversation. File name or full path.")] string package,
        [Description("BioConversation export (UIndex, path or unique name).")] string export,
        [Description("start, E<n> or R<n>.")] string from,
        [Description("E<n> or R<n>.")] string to,
        [Description("E → R only: wheel text strref.")] int? paraphrase = null,
        [Description("E → R only: wheel slot category: DEFAULT, AGREE, DISAGREE, FRIENDLY, HOSTILE, INVESTIGATE...")] string? category = null,
        [Description("Remove the link instead.")] bool remove = false)
        => ToolErrors.Run(() => ConversationEditor.Link(PackageTools.ParseGame(game), package, export, from, to, paraphrase, category, remove));

    [McpServerTool(Name = "conv_build", Destructive = true),
     Description("Builds an LE1 conversation from a YAML file. A missing conversation is copied from the file's 'from' template (with "
                 + "its node data, FaceFX and TLK set, under its own names, resref and node ids, vanilla plot removed). Then: new text strrefs for "
                 + "kept nodes, new lines copied from template nodes (node data and lip sync with them), text in every language of the conversation's TLK set (and optional GlobalTlk files), plot conditions and transitions, "
                 + "wheels, replies' next lines and the starting list, in one save. A new line is found again by its strref, so running "
                 + "the file twice changes nothing new; every list in the file replaces the conversation's. The format is in mele-maxgoods-toolkit's docs/yaml-reference.md, Conversation files. "
                 + "Backs the packages up first and refuses files inside the game install.")]
    public static ConvBuildResult ConvBuild(
        [Description("Game: LE1.")] string game,
        [Description("YAML file (Windows or /mnt/<drive> path).")] string file,
        [Description("Package holding the conversation, instead of the file's 'package'.")] string? package = null)
        => ToolErrors.Run(() => ConversationBuilder.Build(PackageTools.ParseGame(game), file, package));

    [McpServerTool(Name = "facefx_get", ReadOnly = true),
     Description("Lists the lip-sync lines inside a FaceFXAnimSet (e.g. a conversation's FXA_<conv>_Owner_F): name, id, path, curve and key counts, length.")]
    public static FaceFxSetLines FaceFxGet(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("Package holding the FaceFXAnimSet. File name or full path.")] string package,
        [Description("FaceFXAnimSet export (UIndex, path or unique name).")] string export)
        => ToolErrors.Run(() => FaceFxSets.Get(PackageTools.ParseGame(game), package, export));

    [McpServerTool(Name = "facefx_copy_lines", Destructive = true),
     Description("Copies FaceFX lines of a FaceFXAnimSet under new names. LE1 plays the line named FXA_<strref> (female Shepard sets) "
                 + "or FXA_<strref>_M (male sets) for a VO track, so copying FXA_152036 to FXA_74210010 gives a node with new strref 74210010 "
                 + "the template's lip movement. By default the copy drops its sound cue, so the template voice does not play. "
                 + "Backs the package up first and refuses files inside the game install.")]
    public static FaceFxCopyResult FaceFxCopyLines(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("Package holding the FaceFXAnimSet. File name or full path.")] string package,
        [Description("FaceFXAnimSet export (UIndex, path or unique name).")] string export,
        [Description("Copies: each has source (existing line name) and name (new line name).")] FaceFxLineCopy[] lines,
        [Description("Keep the source line's sound cue (plays the template voice).")] bool withSound = false)
        => ToolErrors.Run(() => FaceFxSets.CopyLines(PackageTools.ParseGame(game), package, export, lines, withSound));

    [McpServerTool(Name = "conv_set_ids", Destructive = true),
     Description("Gives an LE1 BioConversation a new m_nResRefID and renumbers its node ids (entry/reply nExportID and the matching "
                 + "BioSeqEvt_ConvNode m_nNodeID/m_nConvResRefID), as LEX's ChangeConvoIDandConvNodeIDs. Run it on a conversation copied "
                 + "with export_import so its node data does not fire for the original. Backs the package up first and refuses files inside the game install.")]
    public static ConvIdsResult ConvSetIds(
        [Description("Game: LE1.")] string game,
        [Description("Package holding the conversation. File name or full path.")] string package,
        [Description("BioConversation export (UIndex, path or unique name).")] string export,
        [Description("New m_nResRefID, unique among loaded conversations.")] int resRefId,
        [Description("First new node id; nodes get nodeIdBase, nodeIdBase+1...")] int nodeIdBase)
        => ToolErrors.Run(() => ConversationEditor.SetIds(PackageTools.ParseGame(game), package, export, resRefId, nodeIdBase));

    [McpServerTool(Name = "tlk_set", Destructive = true),
     Description("Adds or replaces strings. LE1: in a package's BioTlkFile exports (all of them by default, so male and female stay in sync). "
                 + "LE2/LE3: in one mod .tlk file, one file per language, created with the header strings every .tlk has if it is missing; an id in both "
                 + "tables gets the new text in both, as LEC's ReplaceString does, unless the string gives female too. Notes warn when the file name is not one the DLC folder loads. "
                 + "LE2/LE3 output reads back through LEC, non-ASCII text included. In LE2 and LE3 the game loaded .tlk files it wrote. "
                 + "Backs an existing file up first and refuses files inside the game install. Use one id block per mod (mount x 10000).")]
    public static object TlkSet(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("LE1: package with BioTlkFile exports, e.g. the mod's DLC_MOD_<Name>_GlobalTlk_FR.pcc or a conversation's _LOC_ file. File name or full path. "
                     + "LE2/LE3: full path of the mod's <module>_<LANG>.tlk: <folder>_FRA.tlk for LE3, DLC_<n>_FRA.tlk for LE2, n from its BIOEngine.ini [Engine.DLCModules].")] string package,
        [Description("Strings to write: each has id and text; LE2/LE3 also take female, the text for a female Shepard.")] TlkString[] strings,
        [Description("LE1: one BioTlkFile export (UIndex or path). Default: all of them.")] string? export = null)
        => ToolErrors.Run<object>(() =>
        {
            var parsed = PackageTools.ParseGame(game);
            if (parsed.IsGame1()) return TalkFiles.Set(parsed, package, strings, export);
            if (export is not null) throw new ArgumentException(".tlk files have no exports: leave export out.");
            return TalkTables.Set(parsed, package, strings);
        });
}
