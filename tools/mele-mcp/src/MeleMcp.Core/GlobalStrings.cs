using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK.ME1;

namespace MeleMcp.Core;

/// <summary>Game 1 global strings for one language: the base game's GlobalTlk and every installed DLC's GlobalTalkTable.</summary>
public static class GlobalStrings
{
    /// <summary>
    /// Strings of <c>Startup_&lt;LANG&gt;.pcc</c> GlobalTlk, then of each enabled DLC's GlobalTalkTable package
    /// (<c>&lt;pkg&gt;_&lt;LANG&gt;.pcc</c>, or <c>&lt;pkg&gt;.pcc</c> for INT). A DLC string overrides a base one with the same id.
    /// Only the female/default copy (<c>GlobalTlk_tlk</c>) is read.
    /// </summary>
    public static Dictionary<int, string> Load(MEGame game, string language)
    {
        LecHost.EnsureInitialized();
        var lang = language.Trim().ToUpperInvariant();
        var strings = new Dictionary<int, string>();
        var cooked = MEDirectories.GetCookedPath(game);
        Read(Path.Combine(cooked, $"Startup_{lang}.pcc"), strings);

        foreach (var dlc in MELoadedDLC.GetEnabledDLCFolders(game).OrderBy(d => MELoadedDLC.GetMountPriority(d, game)))
        {
            var ini = Path.Combine(dlc, "AutoLoad.ini");
            if (!File.Exists(ini)) continue;
            foreach (var entry in IdAllocator.ReadPackages(ini).Where(e => e.Key.StartsWith("GlobalTalkTable", StringComparison.OrdinalIgnoreCase)))
            {
                var file = IdAllocator.FindPackage(dlc, lang == "INT" ? entry.Package : $"{entry.Package}_{lang}")
                           ?? IdAllocator.FindPackage(dlc, entry.Package);
                if (file is not null) Read(file, strings);
            }
        }
        return strings;
    }

    private static void Read(string path, Dictionary<int, string> strings)
    {
        if (!File.Exists(path)) return;
        using var pcc = MEPackageHandler.UnsafePartialLoad(path, IsDefaultTlk);
        foreach (var tlk in pcc.Exports.Where(IsDefaultTlk))
        {
            foreach (var s in new ME1TalkFile(tlk).StringRefs) strings[s.StringID] = s.Data ?? "";
        }
    }

    private static bool IsDefaultTlk(ExportEntry e) => e.ClassName == "BioTlkFile" && e.ObjectName.Name == "GlobalTlk_tlk";
}
