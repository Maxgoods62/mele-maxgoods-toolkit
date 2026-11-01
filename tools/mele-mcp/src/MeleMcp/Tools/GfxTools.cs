using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Read-only inspection of the GFx (Flash) movies in packages.</summary>
[McpServerToolType]
public static class GfxTools
{
    [McpServerTool(Name = "gfx_scan", ReadOnly = true),
     Description("Reads a GFx movie (GFxMovieInfo RawData) without Flash tools. mode=movies: the package's movies with size, SWF and exporter version, tag and sprite counts, "
                 + "SHA-1 (same hash = same movie in two games), and referenced textures. tags: tag counts (sprites included), frame labels, placements, linkage exports, imports, "
                 + "fonts, edit texts, and the top-level tag list with file offsets. images: GFx image and gradient tags, each matched to the package's Texture2D, bitmap tags. "
                 + "as2: ActionScript 2 of the whole movie, one sprite or one frame, constant pools resolved, as a disassembly or rough pseudo-code. "
                 + "Output checked line by line against separate Python scanners on 196 LE1, LE2 and LE3 movies. Never modifies anything.")]
    public static GfxScanResult Scan(
        [Description("Package file name (resolved to the highest mounted copy), full path, or a loose .gfx/.swf file.")] string package,
        [Description("movies, tags, images or as2.")] string mode = "movies",
        [Description("GFxMovieInfo export: UIndex, path or unique name, e.g. GUI_SF_PhotoMode.PhotoMode. Needed for tags, images and as2 when the package has several movies.")] string? movie = null,
        [Description("tags, as2: root, a sprite id (12 or sprite12), or a linkage export name. Omit for the whole movie.")] string? sprite = null,
        [Description("as2: frame on the timeline, counted from 0 as the f<n> in the block headers.")] int? frame = null,
        [Description("as2: disasm (one line per action, jump targets as offsets) or pseudo (stack-simulated statements; branches stay goto).")] string format = "disasm",
        [Description("Start of the returned text, in chars: the Note of a capped call gives the next value.")] int offset = 0,
        [Description("Largest text returned.")] int maxChars = 60000,
        [Description("Game: LE1, LE2 or LE3.")] string game = "LE1")
        => ToolErrors.Run(() => GfxScan.Run(PackageTools.ParseGame(game), package, mode, movie, sprite, frame, format, offset, maxChars));
}
