using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>New LE2 and LE3 mods, and merge mods for the files a mod may not ship whole.</summary>
[McpServerToolType]
public static class ScaffoldTools
{
    [McpServerTool(Name = "dlc_new", Destructive = false),
     Description("Starts an LE2 or LE3 mod in an empty folder, normally mods\\<game>\\<name> in M3's library: moddesc.ini (cmmver 9.2), DLC_MOD_<Name> at the mod root as M3's [CUSTOMDLC] "
                 + "expects, Mount.dlc, one .tlk per language and the config skeleton, in the forms of M3's Starter Kit. LE2: BIOEngine.ini with [Engine.DLCModules] and DLC_<module>_<LANG>.tlk. "
                 + "LE3: Default_DLC_MOD_<Name>.bin and DLC_MOD_<Name>_<LANG>.tlk. Refuses LE1 (there M3's DLC Mod Starter Kit starts the mod and mod_build fills it), a folder that holds anything, the game install, a name M3 refuses "
                 + "(letters, digits, _; 25 characters after DLC_MOD_; LE3 no capital MP), a folder name, mount priority or LE2 module already installed (each named), and a mount outside M3's range "
                 + "(LE2 over 2500, LE3 over 4000, under 32000). Every file is read back with LEC, and a mismatch removes the output. Compared with vanilla DLC and M3's template. "
                 + "In LE2, M3 parsed its output and the game loaded the mod's .tlk; no LE3 output has been loaded by the game.")]
    public static DlcNewResult DlcNew(
        [Description("Game: LE2 or LE3.")] string game,
        [Description("The mod root, empty or missing (Windows or /mnt/<drive> path).")] string folder,
        [Description("DLC name: Name or DLC_MOD_Name.")] string name,
        [Description("Mount priority, unique among installed DLC. Higher wins a file or string two DLC share.")] int mountPriority,
        [Description("The mod's name shown in M3, and the DLC's name string.")] string displayName,
        [Description("moddev in moddesc.ini.")] string author,
        [Description("LE2: the number that names the .tlk files, 1 to 65534. Default: the mount priority, as the Unofficial LE2 Patch's settings DLC does.")] int? moduleId = null,
        [Description("modver: X.Y, X.Y.Z or W.X.Y.Z.")] string version = "1.0",
        [Description("Languages to write a .tlk for: INT, DEU, ESN, FRA, ITA, JPN, POL, RUS. Default: all.")] string[]? languages = null,
        [Description("moddesc in moddesc.ini; line breaks become <br>. Default: displayName.")] string? description = null,
        [Description("Strref of the DLC's name (Mount.dlc TLKID). Default: the first free id from mount priority x 1000.")] int? nameStrRef = null)
        => ToolErrors.Run(() => DlcScaffold.New(PackageTools.ParseGame(game), folder, name, mountPriority, moduleId, displayName, author, version, languages, description, nameStrRef));

    [McpServerTool(Name = "mergemod_build", Destructive = true, Idempotent = true),
     Description("Builds an M3 merge mod, the supported route to patch SFXGame, Engine, Startup and the other core files, in LE1, LE2 or LE3. Give changes with targetFile and name, or an existing manifest. "
                 + "The mod must exist: dlc_new starts an LE2 or LE3 one, M3's DLC Mod Starter Kit an LE1 one. First a dry check on a scratch copy of the target, in order, as M3 will apply them: function text and class members compile with "
                 + "script_compile's route (errors keep Line and Column), each entry is found and normalized to its instanced full path, each property is checked against the export and its class, "
                 + "and a manifest is checked against M3's schema; on any error Stage is check-failed, nothing is written and M3 is not touched. Then the manifest and sources go in the mod's MergeMods "
                 + "folder, M3 compiles them (--compilemergemod to the running window, waiting on its log and the output) and the .m3m is added to moddesc.ini [BASEGAME] mergemods=. compile=false stops "
                 + "after the manifest. M3 opens a dialog on a compile failure; that is reported, never clicked. M3 is never closed. Compiling reads no game file. Applied through M3 and proved in LE2 once: M3 patches the core file in place, nothing undoes that but M3's Restore on the file's row, and merges stack on the file on disk.")]
    public static MergeBuildResult MergeModBuild(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("The mod root holding moddesc.ini (Windows or /mnt/<drive> path).")] string modRoot,
        [Description("Merge mod name: the file is MergeMods\\<name>.m3m. Letters, digits, _ and -. With manifest: default its file name.")] string? name = null,
        [Description("The file the changes go in: SFXGame.pcc, Engine.pcc, Startup_INT.pcc (LE3: Startup.pcc)...")] string? targetFile = null,
        [Description("Changes, in order. Each has entry (UIndex, instanced full path, or a unique name) and one of: properties (each name, value, optional type), script or scriptFile "
                     + "(function text; for a Class entry, one var, struct, enum, function or state), assetFile with assetEntry.")] MergeChange[]? changes = null,
        [Description("An existing manifest .json (Windows or /mnt/<drive> path), instead of changes. Its sources are copied in from beside it.")] string? manifest = null,
        [Description("Apply to every localized copy of targetFile. Only the named copy is checked.")] bool applyToAllLocalizations = false,
        [Description("Run M3. false writes the manifest and its sources only.")] bool compile = true,
        [Description("Seconds to wait for M3.")] int timeoutSeconds = 180)
        => ToolErrors.Run(() => MergeMods.Build(PackageTools.ParseGame(game), modRoot, name, targetFile, changes, manifest, applyToAllLocalizations, compile, timeoutSeconds));
}
