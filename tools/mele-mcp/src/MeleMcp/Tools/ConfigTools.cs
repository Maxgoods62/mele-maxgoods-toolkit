using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Game config in each game's own layout.</summary>
[McpServerToolType]
public static class ConfigTools
{
    [McpServerTool(Name = "config_get", ReadOnly = true),
     Description("Reads game config file by file, in load order: the base Coalesced, then each mounted DLC by rising mount, so a key lists the value "
                 + "of every file that holds it. LE1: Coalesced_INT.bin, then ConfigDelta-*.m3cd (M3 merges these into the bin at install, so a "
                 + "modded install's base rows already hold them). LE2: Coalesced_<language>.bin, then the DLC's loose inis. LE3: Coalesced.bin, "
                 + "then Default_<DLC>.bin. Each row gives file, mount, operation (set, add, addunique, remove, clear) and the file's native form "
                 + "(key prefix, or LE3 type 0-4). Effective replays a key's rows when more than one file holds it (LE1: the base file's rows, all the game "
                 + "loads); the replay follows the formats' rules and was not checked against the running game. Read on real files of all three games; "
                 + "nothing tested in game.")]
    public static ConfigGetResult ConfigGet(
        [Description("Config asset, e.g. BioEngine, BioGame, BioWeapon (case does not matter).")] string? asset = null,
        [Description("Only files whose path under BioGame contains this, e.g. DLC_HEN_MT, BIOWeapon.ini or Coalesced. Rows of other files are left out, and with them Effective.")] string? file = null,
        [Description("Case-insensitive substring of the section, e.g. Core.System or sfxweapon_.")] string? section = null,
        [Description("Key name (no + - ! . > prefix), or a wildcard with * and ?. Case does not matter.")] string? key = null,
        [Description("Case-insensitive text in a value. A key with a match is listed with all its rows.")] string? textContains = null,
        [Description("LE2 base file: INT, FRA, DEU, ITA or POL (FR, DE, IT, PL too). LE1 and LE3 have one base file.")] string language = "INT",
        [Description("Maximum rows returned (TotalKeys and TotalRows still count all).")] int limit = 100,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => ConfigFiles.Get(PackageTools.ParseGame(game), asset, file, section, key, textContains, language, limit));

    [McpServerTool(Name = "config_set", Destructive = true, Idempotent = true),
     Description("Writes config entries into a mod's DLC folder in the game's native form, merging with what the folder holds: LE1 a ConfigDelta-*.m3cd "
                 + "(the file that already holds the key or section, else ConfigDelta-<mod>.m3cd), LE2 the loose ini of the asset (BIOEngine.ini...), LE3 "
                 + "Default_<DLC>.bin. Text files change only on the entry's lines. Operations: set (the key holds this one value in this file; its other "
                 + "rows there go), add, addunique, remove (also drops this file's own add of that value), clear (placed before the key's rows in this "
                 + "file). An entry already there changes nothing. Backs up each file it changes and refuses anything inside the game install. "
                 + "LE2 and LE3 output was read back with LEC, and a real DLC config round-tripped; nothing tested in game.")]
    public static ConfigSetResult ConfigSet(
        [Description("The mod's DLC_<name> folder or its CookedPCConsole, usually in the M3 library (Windows or /mnt/<drive> path).")] string folder,
        [Description("Entries: each has asset (BioEngine, BioGame...), section, key (no prefix), value (not for clear) and operation (default set).")] ConfigEntry[] entries,
        [Description("LE1 only: write to ConfigDelta-<this>.m3cd instead of choosing the file.")] string? deltaName = null,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => ConfigFiles.SetEntries(PackageTools.ParseGame(game), folder, entries, deltaName));
}
