using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Coalesced;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK.ME2ME3;

namespace MeleMcp.Core;

/// <param name="ModuleId">LE2 only: the number in BIOEngine.ini [Engine.DLCModules], which names the .tlk files.</param>
/// <param name="NameStrRef">Mount.dlc's TLKID: the strref of the DLC's name, present in every .tlk written.</param>
/// <param name="Files">Files written, relative to the mod root.</param>
/// <param name="Checks">What was read back with LEC; a mismatch fails the call and removes the output.</param>
public sealed record DlcNewResult(string Game, string ModRoot, string ModDesc, string DlcFolder, int MountPriority, int? ModuleId, int NameStrRef, int MountFlags,
    IReadOnlyList<string> Languages, IReadOnlyList<string> Files, IReadOnlyList<string> Checks, IReadOnlyList<string> Notes);

/// <summary>
/// A new LE2 or LE3 mod M3 accepts: moddesc.ini, a DLC_MOD_* folder at the mod root (M3's [CUSTOMDLC] sourcedirs, as its Starter Kit and real
/// mods lay it out; BioGame\DLC is where M3 installs it), Mount.dlc, the config skeleton and one .tlk per language. The per-game forms follow
/// M3's Starter Kit (ME3TweaksCore DLCModGenerator.cs). LE1 has no Mount.dlc: mod_build makes those mods.
/// </summary>
public static partial class DlcScaffold
{
    private const string ModDescVersion = "9.2";
    private const string Prefix = "DLC_MOD_";
    private const string MountName = "Mount.dlc";

    // M3's Starter Kit limits. Its LE2 and LE3 minimums sit above every vanilla mount; 25 is its limit on what follows DLC_MOD_.
    private const int MaxNameLength = 25;
    private const int MaxMount = 32000;
    private const int Le2MinMount = 2500;
    private const int Le3MinMount = 4000;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex FolderName();

    [GeneratedRegex(@"^\d+\.\d+(\.\d+){0,2}$")]
    private static partial Regex ModVersion();

    public static DlcNewResult New(MEGame game, string modRoot, string name, int mountPriority, int? moduleId, string displayName, string author,
        string version, IReadOnlyList<string>? languages, string? description, int? nameStrRef)
    {
        LecHost.EnsureInitialized();
        if (game == MEGame.LE1) throw new ArgumentException("dlc_new makes LE2 and LE3 mods. An LE1 mod has no Mount.dlc: build it from YAML with mod_build, which writes AutoLoad.ini and the GlobalTlk.");
        if (game is not (MEGame.LE2 or MEGame.LE3)) throw new ArgumentException("dlc_new makes LE2 and LE3 mods.");

        var folder = DlcFolder(game, name);
        var title = OneLine(displayName, "displayName");
        var dev = OneLine(author, "author");
        var modVersion = version.Trim();
        if (!ModVersion().IsMatch(modVersion)) throw new ArgumentException($"Version '{version}': M3 takes X.Y, X.Y.Z or W.X.Y.Z.");
        if (game == MEGame.LE2 && moduleId is <= 0 or >= ushort.MaxValue) throw new ArgumentException($"Module id {moduleId}: M3 takes 1 to {ushort.MaxValue - 1}.");
        if (game == MEGame.LE3 && moduleId is not null) throw new ArgumentException("moduleId is LE2 only: LE3 names its .tlk files after the DLC folder.");
        if (nameStrRef is <= 0) throw new ArgumentException($"String id {nameStrRef}: ids are positive.");

        var lang = languages is { Count: > 0 } ? languages.Select(TalkTables.Language).Distinct().ToList() : TalkTables.AllLanguages.ToList();
        var root = RootFolder(modRoot);
        RefuseInstalled(game, folder, mountPriority);
        var module = game == MEGame.LE2 ? moduleId ?? mountPriority : (int?)null;
        if (module is { } number) RefuseModule(game, number);

        var notes = new List<string>();
        var (male, female) = TalkTables.Load(game, "INT");
        var strRef = nameStrRef ?? FreeStrRef(mountPriority, male, female);
        if (nameStrRef is { } given && male.TryGetValue(given, out var taken)) notes.Add($"String {given} already reads '{taken}' in this install: the highest mount wins it.");
        var missing = TalkTables.AllLanguages.Except(lang).ToList();
        if (missing.Count > 0) notes.Add($"No .tlk for {string.Join(", ", missing)}: a game running in one of these languages shows no text of this mod.");

        var flags = game == MEGame.LE3 ? new MountFlag(EME3MountFileFlag.LoadsInSingleplayer) : new MountFlag(0, true);
        var dlcRoot = Path.Combine(root, folder);
        var cooked = Path.Combine(dlcRoot, game.CookedDirName());
        var moddesc = Path.Combine(root, "moddesc.ini");
        var rootExisted = Directory.Exists(root);
        var files = new List<string>();
        var checks = new List<string>();
        try
        {
            Directory.CreateDirectory(cooked);
            var mount = new MountFile
            {
                Game = game,
                MountFlags = flags,
                MountPriority = mountPriority,
                TLKID = strRef,
                ME2Only_DLCFolderName = folder,
                ME2Only_DLCHumanName = title,
            };
            var mountPath = Path.Combine(cooked, MountName);
            mount.WriteMountFile(mountPath);
            files.Add(mountPath);

            // Config first: TalkTables reads a LE2 folder's module from BIOEngine.ini.
            files.AddRange(ConfigFiles.SetEntries(game, cooked, Config(game, folder, module, flags.FlagValue, strRef), null).Written);
            foreach (var code in lang)
            {
                var tlk = Path.Combine(cooked, $"{(game == MEGame.LE2 ? $"DLC_{module}" : folder)}_{code}.tlk");
                notes.AddRange(TalkTables.Set(game, tlk, [new TlkString(strRef, title)]).Notes);
                files.Add(tlk);
            }

            File.WriteAllText(moddesc, ModDesc(game, title, description, modVersion, dev, folder), new UTF8Encoding(false));
            files.Add(moddesc);

            checks.Add(CheckMount(game, mountPath, folder, title, mountPriority, strRef, flags.FlagValue));
            checks.Add(game == MEGame.LE2 ? CheckLe2Config(cooked, folder, module!.Value, strRef) : CheckLe3Config(cooked, folder));
            checks.Add(CheckTlk(game, files.Where(f => f.EndsWith(".tlk", StringComparison.OrdinalIgnoreCase)).ToList(), game == MEGame.LE2 ? $"DLC_{module}" : folder, strRef, title));
            checks.Add(CheckModDesc(moddesc, game, title, folder));
        }
        catch
        {
            // Nothing of this call stays behind: the folder was empty or missing when it started.
            if (Directory.Exists(dlcRoot)) Directory.Delete(dlcRoot, recursive: true);
            File.Delete(moddesc);
            if (!rootExisted && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            throw;
        }

        return new DlcNewResult(game.ToString(), root, moddesc, folder, mountPriority, module, strRef, flags.FlagValue, lang,
            files.Select(f => Path.GetRelativePath(root, f)).ToList(), checks, notes);
    }

    /// <summary>DLC_MOD_&lt;Name&gt;, from the bare name or the whole folder name, by M3's rules.</summary>
    private static string DlcFolder(MEGame game, string name)
    {
        var bare = name?.Trim() ?? "";
        if (bare.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) bare = bare[Prefix.Length..];
        if (bare.Length == 0 || !FolderName().IsMatch(bare)) throw new ArgumentException($"DLC name '{name}': M3 takes letters, digits and _ only.");
        if (bare.Length > MaxNameLength) throw new ArgumentException($"DLC name '{bare}' has {bare.Length} characters: M3 takes {MaxNameLength} at most after {Prefix}.");
        var folder = Prefix + bare;
        // M3's Starter Kit: "LE3 rejects DLC foldernames containing case sensitive 'MP'".
        if (game == MEGame.LE3 && bare.Contains("MP", StringComparison.Ordinal)) throw new ArgumentException($"{folder}: LE3 refuses a DLC folder name with a capital 'MP' in it.");
        return folder;
    }

    private static string OneLine(string text, string what)
    {
        var line = text?.Trim() ?? "";
        if (line.Length == 0 || line.Contains('\n') || line.Contains('\r')) throw new ArgumentException($"{what} must be one line of text.");
        return line;
    }

    /// <summary>The mod root: outside the game install, and a folder that holds nothing yet.</summary>
    private static string RootFolder(string modRoot)
    {
        var windows = GamePaths.ToWindowsPath(modRoot.Trim());
        if (!Path.IsPathFullyQualified(windows)) throw new ArgumentException($"'{modRoot}': give the mod's full path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windows));
        ConfigFiles.EnsureWritable(Path.Combine(root, "moddesc.ini"));
        if (File.Exists(root)) throw new ArgumentException($"{root} is a file.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).FirstOrDefault() is { } first)
        {
            throw new ArgumentException($"{root} already holds {Path.GetFileName(first)}: dlc_new starts a new mod and never writes over one.");
        }
        return root;
    }

    private static void RefuseInstalled(MEGame game, string folder, int mountPriority)
    {
        var dlcPath = MEDirectories.GetDLCPath(game);
        var installed = Directory.Exists(dlcPath) ? Directory.GetDirectories(dlcPath) : [];
        foreach (var dlc in installed)
        {
            if (Path.GetFileName(dlc).Equals(folder, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{game} already has a DLC folder {folder}.");
        }

        var (min, max) = (game == MEGame.LE2 ? Le2MinMount : Le3MinMount, MaxMount);
        foreach (var dlc in installed)
        {
            var path = Path.Combine(dlc, game.CookedDirName(), MountName);
            if (File.Exists(path) && new MountFile(path).MountPriority == mountPriority)
            {
                throw new ArgumentException($"Mount priority {mountPriority} is taken by {Path.GetFileName(dlc)}: two DLC on one mount load in no fixed order.");
            }
        }
        if (mountPriority <= min || mountPriority >= max) throw new ArgumentException($"Mount priority {mountPriority}: M3 takes {min + 1} to {max - 1} for {game}.");
    }

    /// <summary>Two LE2 folders with one module number name the same .tlk files.</summary>
    private static void RefuseModule(MEGame game, int module)
    {
        var dlcPath = MEDirectories.GetDLCPath(game);
        if (!Directory.Exists(dlcPath)) return;
        foreach (var dlc in Directory.GetDirectories(dlcPath))
        {
            var ini = Path.Combine(dlc, game.CookedDirName(), "BIOEngine.ini");
            if (!File.Exists(ini)) continue;
            var value = DuplicatingIni.LoadIni(ini).GetValue("Engine.DLCModules", Path.GetFileName(dlc))?.Value;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) && used == module)
            {
                throw new ArgumentException($"Module id {module} is taken by {Path.GetFileName(dlc)}: its DLC_{module}_<LANG>.tlk files would clash.");
            }
        }
    }

    /// <summary>mount × 1000: above the game's strings and the mods' ranges seen, and tied to a number already unique in the install.</summary>
    private static int FreeStrRef(int mountPriority, Dictionary<int, string> male, Dictionary<int, string> female)
    {
        var id = mountPriority * 1000;
        while (male.ContainsKey(id) || female.ContainsKey(id)) id++;
        return id;
    }

    /// <summary>LE2: M3's BIOEngine.ini, as the Unofficial LE2 Patch ships it. LE3: M3's Default_DLC_MOD_StarterKit.bin, with the folder name in its paths.</summary>
    private static List<ConfigEntry> Config(MEGame game, string folder, int? module, int flags, int strRef)
    {
        const string engine = "BioEngine", system = "Core.System";
        if (game == MEGame.LE2)
        {
            return
            [
                new(engine, system, "CookPaths", null, "clear"),
                new(engine, system, "SeekFreePCPaths", $@"..\BIOGame\DLC\{folder}\CookedPC", "addunique"),
                new(engine, "Engine.DLCModules", folder, module!.Value.ToString(CultureInfo.InvariantCulture)),
                new(engine, "DLCInfo", "Version", "0"),
                new(engine, "DLCInfo", "Flags", flags.ToString(CultureInfo.InvariantCulture)),
                new(engine, "DLCInfo", "Name", strRef.ToString(CultureInfo.InvariantCulture)),
            ];
        }

        string Dlc(string sub) => $@"..\..\BIOGame\DLC\{folder}\{sub}";
        string Shared(string sub) => $@"..\..\BIOGame\DLC\DLC_Shared\{sub}";
        ConfigEntry Add(string key, string value) => new(engine, system, key, value, "add");
        const string basedOn = "BasedOn", configuration = "Configuration";
        const string difficulty = @"..\..\BIOGame\DLC\DLC_Shared_MP\Config\DefaultDifficulty.ini";
        return
        [
            new("BioCredits", configuration, basedOn, difficulty),
            new("BioDifficulty", configuration, basedOn, difficulty),
            new(engine, configuration, basedOn, Dlc(@"Config\DefaultEngine.ini"), "add"),
            new(engine, system, "CookPaths", null, "clear"),
            Add("CookPaths", Dlc("Content")),
            Add("CookPaths", Dlc("Script")),
            Add("CookPaths", Dlc("ScriptFinalRelease")),
            Add("FRScriptPaths", Shared("ScriptFinalRelease")),
            Add("FRScriptPaths", Dlc("ScriptFinalRelease")),
            Add("Paths", Shared("__Trashcan")),
            Add("Paths", Shared("Content")),
            Add("Paths", Shared("TestContent")),
            Add("Paths", Dlc("__Trashcan")),
            Add("Paths", Dlc("Content")),
            Add("Paths", Dlc("TestContent")),
            Add("ScriptPaths", Shared("Script")),
            Add("ScriptPaths", Dlc("Script")),
            Add("SeekFreePCPaths", Shared("CookedPCConsole")),
            Add("SeekFreePCPaths", Dlc("CookedPCConsole")),
            new(engine, "UnrealEd.EditorEngine", "EditPackages", "SFXGameContentDLC_Shared", "add"),
            new(engine, "UnrealEd.EditorEngine", "EditPackages", $"SFXGameContentDLC_MOD_{folder[Prefix.Length..]}", "add"),
            new("BioInput", configuration, basedOn, Shared(@"Config\DefaultInput.ini")),
        ];
    }

    private static string ModDesc(MEGame game, string title, string? description, string version, string author, string folder)
    {
        var text = string.IsNullOrWhiteSpace(description) ? title : description.Trim().ReplaceLineEndings("<br>");
        return string.Join("\r\n",
            "[ModManager]", $"cmmver = {ModDescVersion}", "",
            "[ModInfo]", $"game = {game}", $"modname = {title}", $"moddesc = {text}", $"modver = {version}", $"moddev = {author}", "",
            "[CUSTOMDLC]", $"sourcedirs = {folder}", $"destdirs = {folder}", "");
    }

    private static string CheckMount(MEGame game, string path, string folder, string title, int mountPriority, int strRef, int flags)
    {
        var mount = new MountFile(path);
        var same = mount.Game == game && mount.MountPriority == mountPriority && mount.TLKID == strRef && mount.MountFlags.FlagValue == flags
                   && (game != MEGame.LE2 || (mount.ME2Only_DLCFolderName == folder && mount.ME2Only_DLCHumanName == title));
        if (!same) throw new InvalidOperationException($"{MountName} reads back as {mount.Game}, mount {mount.MountPriority}, TLKID {mount.TLKID}, flags {mount.MountFlags.FlagValue}.");
        return $"{MountName}: {mount.Game}, mount {mount.MountPriority}, TLKID {mount.TLKID}, flags 0x{mount.MountFlags.FlagValue:X}";
    }

    private static string CheckLe2Config(string cooked, string folder, int module, int strRef)
    {
        var ini = DuplicatingIni.LoadIni(Path.Combine(cooked, "BIOEngine.ini"));
        var (read, name) = (ini.GetValue("Engine.DLCModules", folder)?.Value, ini.GetValue("DLCInfo", "Name")?.Value);
        if (read != module.ToString(CultureInfo.InvariantCulture) || name != strRef.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException($"BIOEngine.ini reads back as module {read}, name {name}.");
        }
        return $"BIOEngine.ini: [Engine.DLCModules] {folder}={read}, [DLCInfo] Name={name}";
    }

    private static string CheckLe3Config(string cooked, string folder)
    {
        var file = $"Default_{folder}.bin";
        var coal = new CoalescedFileXml();
        using (var input = File.OpenRead(Path.Combine(cooked, file))) coal.Deserialize(input);
        var engine = coal.Files.First(f => Path.GetFileNameWithoutExtension(f.Name).Equals("bioengine", StringComparison.OrdinalIgnoreCase));
        var seekFree = engine.Sections["core.system"]["seekfreepcpaths"];
        if (!seekFree.Any(v => v.Value == $@"..\..\BIOGame\DLC\{folder}\CookedPCConsole")) throw new InvalidOperationException($"{file} has no seek-free path of {folder}.");
        return $"{file}: {coal.Files.Count} assets, {coal.Files.Sum(f => f.Sections.Values.Sum(s => s.Values.Sum(v => v.Count)))} rows, seek-free path of {folder} present";
    }

    private static string CheckTlk(MEGame game, List<string> tlks, string module, int strRef, string title)
    {
        foreach (var path in tlks)
        {
            var refs = new ME2ME3TalkFile(path).StringRefs;
            var read = refs.FirstOrDefault(r => r.StringID == TalkTables.ModuleStringId)?.Data;
            if (read != module) throw new InvalidOperationException($"{Path.GetFileName(path)} names module '{read}', not {module}.");
            read = refs.FirstOrDefault(r => r.StringID == strRef)?.Data;
            if (read != title) throw new InvalidOperationException($"{Path.GetFileName(path)}: string {strRef} reads '{read}'.");
        }
        return $"{tlks.Count} .tlk files: module {module}, string {strRef} = '{title}'";
    }

    private static string CheckModDesc(string path, MEGame game, string title, string folder)
    {
        var ini = DuplicatingIni.LoadIni(path);
        var (read, name, source) = (ini.GetValue("ModInfo", "game")?.Value, ini.GetValue("ModInfo", "modname")?.Value, ini.GetValue("CUSTOMDLC", "sourcedirs")?.Value);
        if (read != game.ToString() || name != title || source != folder) throw new InvalidOperationException($"moddesc.ini reads back as {read}, '{name}', {source}.");
        return $"moddesc.ini: cmmver {ini.GetValue("ModManager", "cmmver")?.Value}, {read}, '{name}', {source}";
    }
}
