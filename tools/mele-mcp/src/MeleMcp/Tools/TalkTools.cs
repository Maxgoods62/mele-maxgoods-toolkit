using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Game 1 strings stored in packages (BioTlkFile exports).</summary>
[McpServerToolType]
public static class TalkTools
{
    private const string PackageDescription =
        "Package with BioTlkFile exports, e.g. the mod's DLC_MOD_<Name>_GlobalTlk_FR.pcc or a conversation's _LOC_ file. File name or full path.";

    [McpServerTool(Name = "tlk_get", ReadOnly = true),
     Description("Reads strings from the BioTlkFile exports of an LE1 package (each file is listed: GlobalTlk packages have a male and a female copy).")]
    public static IReadOnlyList<TlkFileStrings> TlkGet(
        [Description(PackageDescription)] string package,
        [Description("String id to look up. Omit to list.")] int? id = null,
        [Description("Case-insensitive text filter.")] string? textContains = null,
        [Description("One BioTlkFile export (UIndex or path). Default: all of them.")] string? export = null,
        [Description("Maximum strings per file.")] int limit = 50,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => TalkFiles.Get(PackageTools.ParseGame(game), package, export, id, textContains, limit));

    [McpServerTool(Name = "conv_read", ReadOnly = true),
     Description("Reads an LE1 BioConversation as a text graph: start entries, entries (NPC lines, speaker -1 = owner) with their wheel choices, "
                 + "replies (Shepard) with the entries they lead to, plot conditions/transitions, nExportID (link to the node's camera data). "
                 + "Text comes from the conversation's own TLK for the language.")]
    public static string ConvRead(
        [Description("Package holding the conversation, usually <area>_LOC_<LANG>.pcc. File name or full path.")] string package,
        [Description("BioConversation export (UIndex, path or unique name).")] string export,
        [Description("TLK language: INT, FR, DE, IT, PLPC, RA...")] string language = "FR",
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => ConversationReader.Read(PackageTools.ParseGame(game), package, export, language));

    [McpServerTool(Name = "facefx_get", ReadOnly = true),
     Description("Lists the lip-sync lines inside a FaceFXAnimSet (e.g. a conversation's FXA_<conv>_Owner_F): name, id, path, curve and key counts, length.")]
    public static FaceFxSetLines FaceFxGet(
        [Description("Package holding the FaceFXAnimSet. File name or full path.")] string package,
        [Description("FaceFXAnimSet export (UIndex, path or unique name).")] string export,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => FaceFxSets.Get(PackageTools.ParseGame(game), package, export));

    [McpServerTool(Name = "facefx_copy_lines", Destructive = true),
     Description("Copies FaceFX lines of a FaceFXAnimSet under new names. LE1 plays the line named FXA_<strref> (female Shepard sets) "
                 + "or FXA_<strref>_M (male sets) for a VO track, so copying FXA_152036 to FXA_74210010 gives a node with new strref 74210010 "
                 + "the template's lip movement. By default the copy drops its sound cue, so the template voice does not play. "
                 + "Backs the package up first and refuses files inside the game install.")]
    public static FaceFxCopyResult FaceFxCopyLines(
        [Description("Package holding the FaceFXAnimSet. File name or full path.")] string package,
        [Description("FaceFXAnimSet export (UIndex, path or unique name).")] string export,
        [Description("Copies: each has source (existing line name) and name (new line name).")] FaceFxLineCopy[] lines,
        [Description("Keep the source line's sound cue (plays the template voice).")] bool withSound = false,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => FaceFxSets.CopyLines(PackageTools.ParseGame(game), package, export, lines, withSound));

    [McpServerTool(Name = "conv_set_ids", Destructive = true),
     Description("Gives an LE1 BioConversation a new m_nResRefID and renumbers its node ids (entry/reply nExportID and the matching "
                 + "BioSeqEvt_ConvNode m_nNodeID/m_nConvResRefID), as LEX's ChangeConvoIDandConvNodeIDs. Run it on a conversation copied "
                 + "with export_import so its node data does not fire for the original. Backs the package up first and refuses files inside the game install.")]
    public static ConvIdsResult ConvSetIds(
        [Description("Package holding the conversation. File name or full path.")] string package,
        [Description("BioConversation export (UIndex, path or unique name).")] string export,
        [Description("New m_nResRefID, unique among loaded conversations.")] int resRefId,
        [Description("First new node id; nodes get nodeIdBase, nodeIdBase+1...")] int nodeIdBase,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => ConversationEditor.SetIds(PackageTools.ParseGame(game), package, export, resRefId, nodeIdBase));

    [McpServerTool(Name = "tlk_set", Destructive = true),
     Description("Adds or replaces strings in an LE1 package's BioTlkFile exports (all of them by default, so male and female stay in sync). "
                 + "Backs the package up first and refuses files inside the game install. Use one id block per mod (mount x 10000).")]
    public static TlkEdits TlkSet(
        [Description(PackageDescription)] string package,
        [Description("Strings to write: each has id and text.")] TlkString[] strings,
        [Description("One BioTlkFile export (UIndex or path). Default: all of them.")] string? export = null,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => TalkFiles.Set(PackageTools.ParseGame(game), package, strings, export));
}
