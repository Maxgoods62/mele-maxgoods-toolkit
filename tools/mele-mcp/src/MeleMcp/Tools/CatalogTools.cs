using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>The in-game editor's prop catalog (MeleBridge, F8; docs/07-ingame-editor.md §3.5): which meshes its menu offers, and the package it loads them from. Offline.</summary>
[McpServerToolType]
public static class CatalogTools
{
    [McpServerTool(Name = "catalog_index", ReadOnly = true),
     Description("Indexes every StaticMesh of the files LE1 loads (LE1CP's copy of a file wins; our own mods' files are left out), one row per "
                 + "path: the smallest file that has it (the one to copy it from), its size, triangles, how often the game's levels place it, and "
                 + "what the curation rules make of it (apl, apl-large and levelprop are the editor's default prop list, by category; arch-*, "
                 + "excluded-* and unknown are left out of it). Returns the stats; with search, category or curatedOnly, the matching meshes. "
                 + "About 3 s; reused while no game file changes. Offline: the game need not run.")]
    public static CatalogIndexResult CatalogIndex(
        [Description("Only meshes whose path contains this (case-insensitive), e.g. crate, chair, BIOA_NOR10_S.")] string? search = null,
        [Description("Only meshes of this category: Crates, Furniture, Medical, Consoles, Lights, Industrial, Debris, Alien, Small items, Signs, Plants, Other.")] string? category = null,
        [Description("Only meshes in the default list (curated).")] bool curatedOnly = false,
        [Description("Maximum meshes returned (Matches still counts all).")] int limit = 50,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => Core.CatalogIndex.Query(PackageTools.ParseGame(game), search, category, curatedOnly, limit));

    [McpServerTool(Name = "catalog_build", Destructive = true),
     Description("Builds the in-game editor's prop catalog from catalog_index: MeleCatalog_Core.pcc (the meshes copied from their source files, "
                 + "each under a group per category, so the bridge loads one at a time with DynamicLoadObject 'MeleCatalog_Core.<Group>.<Name>') "
                 + "in the dev mod 'Mele Catalog' of M3's library (moddesc.ini and AutoLoad.ini written when missing; the old package backed up), "
                 + "and for the bridge's menu catalog.json with 128x128 thumbnails (rendered offline: diffuse texture and tint, simple light) "
                 + "under %LOCALAPPDATA%\\MeleBridge\\LE1\\catalog. Meshes whose material has no shader in the game are left out. Refuses to "
                 + "write inside the game install. Offline; install the mod (game closed: mod_install mod='Mele Catalog') for the game to load it.")]
    public static CatalogBuildResult CatalogBuild(
        [Description("Start from the default list (catalog_index's curated meshes).")] bool curated = true,
        [Description("More meshes by path, as catalog_index lists them, e.g. [\"BIOA_FRE10_S.CARGO_NET\"]; whatever the rules made of them (category 'Other' when none).")] string[]? meshes = null,
        [Description("Render thumbnails; false keeps the ones already rendered for the same entries.")] bool thumbs = true,
        [Description("Build MeleCatalog_Core.pcc; false only writes catalog.json (and thumbnails).")] bool package = true,
        [Description("The dev mod's folder (Windows or /mnt/<drive> path); default M3's library, mods\\LE1\\Mele Catalog.")] string? modFolder = null,
        [Description("Where catalog.json and thumbs\\ go (Windows or /mnt/<drive> path); default %LOCALAPPDATA%\\MeleBridge\\LE1\\catalog, where the bridge reads them.")] string? outDir = null,
        [Description("Game: LE1.")] string game = "LE1")
        => ToolErrors.Run(() => CatalogBuilder.Build(PackageTools.ParseGame(game),
            new CatalogBuilder.Options(curated, meshes ?? [], thumbs, package, modFolder, outDir)));
}
