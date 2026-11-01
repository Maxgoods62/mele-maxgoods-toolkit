using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MeleMcp.Core;

public sealed record GfxMovieRow(
    int? UIndex,
    string Path,
    int Bytes,
    string? Sha1,
    string? Signature,
    int? SwfVersion,
    string? Stage,
    double? FrameRate,
    int? Frames,
    string? Exporter,
    int? Tags,
    int? Sprites,
    int? ActionBlocks,
    int? ExternalImages,
    int References,
    IReadOnlyList<string>? ReferencedExports,
    string? Problem);

public sealed record GfxScanResult(
    string Path,
    string Game,
    string Mode,
    string? Movie,
    IReadOnlyList<GfxMovieRow>? Movies,
    IReadOnlyDictionary<string, int>? TagCounts,
    IReadOnlyDictionary<string, int>? TopLevelTagCounts,
    string? Text,
    int TotalChars,
    bool Capped,
    string? Note);

/// <summary>
/// Read-only GFx inspector: SWF tags (with Scaleform's 1000+ tags), images, and ActionScript 2. LEC has no SWF parser; JPEXS, which LEX's
/// viewer uses, is a separate application.
/// </summary>
public static class GfxScan
{
    private const string MovieClass = "GFxMovieInfo";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Reference(int UIndex, string ClassName, string Path, string ObjectName);

    private sealed record MovieSource(int? UIndex, string Path, byte[]? Bytes, IReadOnlyList<Reference> References);

    public static GfxScanResult Run(MEGame game, string package, string mode, string? movie, string? sprite, int? frame, string format, int offset, int maxChars)
    {
        mode = mode.Trim().ToLowerInvariant();
        if (mode is not ("movies" or "tags" or "images" or "as2")) throw new ArgumentException($"Unsupported mode '{mode}'. Use movies, tags, images or as2.");
        format = format.Trim().ToLowerInvariant();
        if (format is not ("disasm" or "pseudo")) throw new ArgumentException($"Unsupported format '{format}'. Use disasm or pseudo.");
        if (offset < 0) throw new ArgumentException("offset cannot be negative.");
        if (maxChars < 1) throw new ArgumentException("maxChars must be positive.");

        if (LooseFile(package) is { } loose)
        {
            var source = new MovieSource(null, Path.GetFileName(loose), File.ReadAllBytes(loose), []);
            return mode == "movies"
                ? new GfxScanResult(loose, game.ToString(), mode, source.Path, [Row(source, wholeList: true)], null, null, null, 0, false, null)
                : Inspect(loose, game, mode, source, sprite, frame, format, offset, maxChars);
        }

        using var pcc = PackageInspector.Open(game, package);
        var exports = pcc.Exports.Where(e => e.ClassName == MovieClass).ToList();
        if (mode == "movies")
        {
            var rows = (movie is null ? exports : [Pick(pcc, exports, movie)])
                .Select(e => Row(Source(pcc, e), wholeList: exports.Count == 1 || movie is not null)).ToList();
            var note = rows.Count == 0 ? $"No {MovieClass} exports in this package." : rows.Count > 1 ? "Give movie to list one movie's referenced exports." : null;
            return new GfxScanResult(pcc.FilePath, game.ToString(), mode, movie, rows, null, null, null, 0, false, note);
        }
        return Inspect(pcc.FilePath, game, mode, Source(pcc, Pick(pcc, exports, movie)), sprite, frame, format, offset, maxChars);
    }

    private static string? LooseFile(string package)
    {
        var path = GamePaths.ToWindowsPath(package);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".gfx", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".swf", StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(path) ? Path.GetFullPath(path) : throw new FileNotFoundException($"'{package}' does not exist.");
    }

    private static ExportEntry Pick(IMEPackage pcc, List<ExportEntry> movies, string? movie)
    {
        if (movie is null)
        {
            return movies.Count switch
            {
                1 => movies[0],
                0 => throw new ArgumentException($"No {MovieClass} exports in this package."),
                _ => throw new ArgumentException($"{movies.Count} movies in this package, give movie: {string.Join(", ", movies.Take(20).Select(e => $"#{e.UIndex} {e.InstancedFullPath}"))}"),
            };
        }
        var picked = ExportInspector.Resolve(pcc, movie);
        return picked.ClassName == MovieClass ? picked : throw new ArgumentException($"#{picked.UIndex} {picked.InstancedFullPath} is a {picked.ClassName}, not a {MovieClass}.");
    }

    private static MovieSource Source(IMEPackage pcc, ExportEntry export)
    {
        var bytes = export.GetProperty<ImmutableByteArrayProperty>("RawData")?.Bytes
                    ?? export.GetProperty<ArrayProperty<ByteProperty>>("RawData")?.Select(b => b.Value).ToArray();
        var references = export.GetProperty<ArrayProperty<ObjectProperty>>("References")?
            .Where(o => o.Value != 0 && pcc.IsEntry(o.Value))
            .Select(o => pcc.GetEntry(o.Value))
            .Select(e => new Reference(e.UIndex, e.ClassName, e.InstancedFullPath, e.ObjectName.Instanced))
            .ToList() ?? [];
        return new MovieSource(export.UIndex, export.InstancedFullPath, bytes, references);
    }

    private static string Describe(Reference r) => $"#{r.UIndex} {r.ClassName} {r.Path}";

    private static GfxMovieRow Row(MovieSource source, bool wholeList)
    {
        var listed = wholeList ? source.References.Select(Describe).ToList() : null;
        if (source.Bytes is null)
        {
            return new GfxMovieRow(source.UIndex, source.Path, 0, null, null, null, null, null, null, null, null, null, null, null, source.References.Count, listed, "No RawData.");
        }
        var sha1 = Convert.ToHexString(SHA1.HashData(source.Bytes)).ToLowerInvariant();
        try
        {
            var swf = SwfMovie.Parse(source.Bytes);
            var scan = new SwfScan(swf);
            return new GfxMovieRow(source.UIndex, source.Path, source.Bytes.Length, sha1, swf.Signature, swf.Version, swf.StageText, swf.FrameRate, swf.Frames,
                swf.ExporterText(), scan.TagTotal, scan.Sprites.Count, scan.Blocks.Count, scan.ExternalImageCount, source.References.Count, listed, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return new GfxMovieRow(source.UIndex, source.Path, source.Bytes.Length, sha1, null, null, null, null, null, null, null, null, null, null, source.References.Count, listed,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static GfxScanResult Inspect(string path, MEGame game, string mode, MovieSource source, string? sprite, int? frame, string format, int offset, int maxChars)
    {
        if (source.Bytes is null) throw new InvalidDataException($"{source.Path} has no RawData.");
        var swf = SwfMovie.Parse(source.Bytes);
        var scan = new SwfScan(swf);
        var notes = new List<string>();
        if (scan.Po3ClipActions > 0) notes.Add($"{scan.Po3ClipActions} PlaceObject3 tag(s) carry clip actions, which are not read: none of the 196 movies this was built on has one.");

        int? spriteId = sprite is null ? null : scan.SpriteId(sprite);
        IReadOnlyDictionary<string, int>? counts = null, top = null;
        string text;
        switch (mode)
        {
            case "tags":
                if (spriteId is > 0)
                {
                    var tags = swf.FindSprite(spriteId.Value)?.Children ?? throw new ArgumentException($"No DefineSprite with id {spriteId}.");
                    counts = SwfScan.Count(tags, nested: true);
                    top = SwfScan.Count(tags, nested: false);
                    text = string.Join('\n', scan.TagList(tags, "")) + "\n";
                }
                else
                {
                    counts = scan.Counts;
                    top = SwfScan.Count(swf.Tags, nested: false);
                    text = string.Join('\n', scan.Summary.Concat(["tag list:"]).Concat(scan.TagList(swf.Tags, "  "))) + "\n";
                }
                break;
            case "images":
                text = string.Join('\n', scan.ImageLines(source.References.Select(r => (r.ObjectName, Describe(r))).ToList())) + "\n";
                break;
            default:
                var blocks = scan.Blocks.Where(b => b.Matches(spriteId, frame)).ToList();
                if (blocks.Count == 0) notes.Add("No ActionScript in that selection.");
                text = format == "pseudo" ? As2.Pseudo(swf.Data, blocks) : As2.Disassembly(swf.Data, blocks);
                break;
        }

        var kept = CrossGame.CutAtLine(text, offset, maxChars);
        var capped = offset + kept.Length < text.Length;
        if (capped) notes.Add($"Returned {kept.Length} of {text.Length} chars from {offset}: call again with offset={offset + kept.Length}.");
        return new GfxScanResult(path, game.ToString(), mode, source.Path, null, counts, top, kept, text.Length, capped, notes.Count > 0 ? string.Join(' ', notes) : null);
    }

    /// <summary>Always with a decimal point: 1280.0.</summary>
    internal static string PyFloat(double v)
    {
        var s = Num(v);
        return s.Contains('.') || s.Contains('e') ? s : s + ".0";
    }

    /// <summary>The shortest text that reads back as the same number, so a constant is never rounded.</summary>
    internal static string Num(double v) => Lower(v.ToString("R", Inv));

    internal static string Num(float v) => Lower(v.ToString("R", Inv));

    private static string Lower(string s) => s.Replace("E+", "e+").Replace("E-", "e-");

    internal static string Int(int v) => v.ToString(Inv);

    internal static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>A quoted string with control characters escaped, as Python's repr writes it.</summary>
    internal static string Repr(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' || c == quote) sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || (c >= 0x7f && c <= 0xa0) || c == 0xad) sb.Append($"\\x{(int)c:x2}");
            else if (c < 0x7f) sb.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) sb.Append(c).Append(s[++i]);
            else
            {
                switch (char.GetUnicodeCategory(c))
                {
                    case UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator:
                        sb.Append($"\\u{(int)c:x4}");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>A null-terminated string at <paramref name="q"/>, which moves past it.</summary>
    internal static string CStr(byte[] d, ref int q, int end)
    {
        var stop = Array.IndexOf(d, (byte)0, q, end - q);
        if (stop < 0) throw new InvalidDataException($"Unterminated string at 0x{q:x}.");
        var s = Encoding.UTF8.GetString(d, q, stop - q);
        q = stop + 1;
        return s;
    }

    /// <summary>A length-prefixed string, as GFx's own tags store them.</summary>
    internal static string PStr(byte[] d, ref int q)
    {
        int n = d[q];
        var s = Encoding.Latin1.GetString(d, q + 1, n);
        q += 1 + n;
        return s;
    }

    internal static int U16(byte[] d, int p) => BitConverter.ToUInt16(d, p);

    internal static int I32(byte[] d, int p) => BitConverter.ToInt32(d, p);
}

internal sealed class SwfTag(int code, int start, int length)
{
    public int Code { get; } = code;

    public int Start { get; } = start;

    public int Length { get; } = length;

    public int End => Start + Length;

    public int SpriteId { get; init; }

    public int SpriteFrames { get; init; }

    public List<SwfTag>? Children { get; init; }
}

/// <summary>A movie's header and tag tree. Offsets are into <see cref="Data"/>, the movie unpacked when it was compressed.</summary>
internal sealed class SwfMovie
{
    private SwfMovie(byte[] data, string signature, int fileLength, int[] rect, int bodyStart)
    {
        Data = data;
        Signature = signature;
        Version = data[3];
        FileLength = fileLength;
        Rect = rect;
        FrameRate = GfxScan.U16(data, bodyStart) / 256.0;
        Frames = GfxScan.U16(data, bodyStart + 2);
        Tags = ReadTags(data, bodyStart + 4, data.Length);
    }

    public byte[] Data { get; }

    public string Signature { get; }

    public int Version { get; }

    public int FileLength { get; }

    public int[] Rect { get; }

    public double FrameRate { get; }

    public int Frames { get; }

    public List<SwfTag> Tags { get; }

    public string StageText => $"{GfxScan.PyFloat((Rect[1] - Rect[0]) / 20.0)}x{GfxScan.PyFloat((Rect[3] - Rect[2]) / 20.0)}";

    public static SwfMovie Parse(byte[] raw)
    {
        if (raw.Length < 13) throw new InvalidDataException($"{raw.Length} bytes is too short for a movie.");
        var signature = Encoding.ASCII.GetString(raw, 0, 3);
        if (signature is not ("GFX" or "CFX" or "FWS" or "CWS")) throw new InvalidDataException($"Not a GFx or SWF movie (signature {GfxScan.Repr(signature)}).");

        var data = raw;
        if (signature[0] == 'C')
        {
            using var inflate = new ZLibStream(new MemoryStream(raw, 8, raw.Length - 8), CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            unpacked.Write(raw, 0, 8);
            inflate.CopyTo(unpacked);
            data = unpacked.ToArray();
        }
        var bits = new Bits(data, 8);
        var n = (int)bits.Unsigned(5);
        int[] rect = [bits.Signed(n), bits.Signed(n), bits.Signed(n), bits.Signed(n)];
        return new SwfMovie(data, signature, GfxScan.I32(data, 4), rect, bits.Align());
    }

    private static List<SwfTag> ReadTags(byte[] d, int p, int end, int depth = 0)
    {
        // Tag nesting recurses, and .NET can't catch a stack overflow: a bad file would end the whole server.
        if (depth > MaxSpriteDepth) throw new InvalidDataException($"Sprites nest deeper than {MaxSpriteDepth} levels at 0x{p:x}.");
        var tags = new List<SwfTag>();
        while (p < end)
        {
            var head = GfxScan.U16(d, p);
            p += 2;
            int code = head >> 6, n = head & 0x3f;
            if (n == 0x3f)
            {
                n = GfxScan.I32(d, p);
                p += 4;
            }
            if (n < 0 || n > end - p) throw new InvalidDataException($"Tag {code} at 0x{p:x} runs past its container ({n} bytes).");
            tags.Add(code == 39
                ? new SwfTag(code, p, n) { SpriteId = GfxScan.U16(d, p), SpriteFrames = GfxScan.U16(d, p + 2), Children = ReadTags(d, p + 4, p + n, depth + 1) }
                : new SwfTag(code, p, n));
            p += n;
            if (code == 0) break;
        }
        return tags;
    }

    private const int MaxSpriteDepth = 64;

    public SwfTag? FindSprite(int id) => Find(Tags, id);

    private static SwfTag? Find(List<SwfTag> tags, int id)
    {
        foreach (var tag in tags.Where(t => t.Children is not null))
        {
            if (tag.SpriteId == id) return tag;
            if (Find(tag.Children!, id) is { } nested) return nested;
        }
        return null;
    }

    public string? ExporterText()
    {
        var tag = Tags.FirstOrDefault(t => t.Code == 1000);
        return tag is null ? null : ExporterLine(tag);
    }

    public string ExporterLine(SwfTag tag)
    {
        var version = GfxScan.U16(Data, tag.Start);
        var q = tag.Start + 2;
        uint flags = 0;
        if (version >= 0x10a)
        {
            flags = BitConverter.ToUInt32(Data, q);
            q += 4;
        }
        var bitmapFormat = GfxScan.U16(Data, q);
        q += 2;
        var prefix = GfxScan.PStr(Data, ref q);
        var swf = GfxScan.PStr(Data, ref q);
        return $"ExporterInfo ver=0x{version:x} flags=0x{flags:x} bitmapFormat={bitmapFormat} prefix={GfxScan.Repr(prefix)} swf={GfxScan.Repr(swf)} rest={GfxScan.Hex(Data.AsSpan(q, tag.End - q))}";
    }
}

/// <summary>SWF bit fields, most significant bit first.</summary>
internal sealed class Bits(byte[] data, int position)
{
    private int bit;

    public int Position { get; private set; } = position;

    public uint Unsigned(int n)
    {
        uint v = 0;
        for (var i = 0; i < n; i++)
        {
            v = (v << 1) | (uint)((data[Position] >> (7 - bit)) & 1);
            if (++bit == 8)
            {
                bit = 0;
                Position++;
            }
        }
        return v;
    }

    public int Signed(int n)
    {
        var v = (long)Unsigned(n);
        return (int)(n > 0 && (v & (1L << (n - 1))) != 0 ? v - (1L << n) : v);
    }

    public int Align()
    {
        if (bit != 0)
        {
            bit = 0;
            Position++;
        }
        return Position;
    }
}

/// <summary>A run of AS2 bytecode. <paramref name="Target"/> is the sprite of a DoInitAction or the button of a button action; Base is what pseudo-code labels count from.</summary>
internal sealed record ActionBlock(string DisasmHeader, string PseudoHeader, int Owner, int Frame, int? Target, int Start, int End, int Base)
{
    public bool Matches(int? sprite, int? frame)
    {
        if (sprite is > 0 && Target == sprite) return frame is null; // not on the sprite's own timeline
        if (sprite is { } wanted && wanted != Owner) return false;
        return frame is null || frame == Frame;
    }
}

/// <summary>One walk over the tag tree: counts, placements, linkage, fonts, texts, images and the ActionScript blocks.</summary>
internal sealed class SwfScan
{
    private static readonly Dictionary<int, string> TagNames = new()
    {
        [0] = "End", [1] = "ShowFrame", [2] = "DefineShape", [4] = "PlaceObject", [5] = "RemoveObject", [6] = "DefineBits", [7] = "DefineButton",
        [9] = "SetBackgroundColor", [10] = "DefineFont", [11] = "DefineText", [12] = "DoAction", [13] = "DefineFontInfo", [14] = "DefineSound",
        [15] = "StartSound", [18] = "SoundStreamHead", [19] = "SoundStreamBlock", [20] = "DefineBitsLossless", [21] = "DefineBitsJPEG2", [22] = "DefineShape2",
        [24] = "Protect", [26] = "PlaceObject2", [28] = "RemoveObject2", [32] = "DefineShape3", [33] = "DefineText2", [34] = "DefineButton2",
        [35] = "DefineBitsJPEG3", [36] = "DefineBitsLossless2", [37] = "DefineEditText", [39] = "DefineSprite", [43] = "FrameLabel", [45] = "SoundStreamHead2",
        [46] = "DefineMorphShape", [48] = "DefineFont2", [56] = "ExportAssets", [57] = "ImportAssets", [58] = "EnableDebugger", [59] = "DoInitAction",
        [62] = "DefineFontInfo2", [64] = "EnableDebugger2", [65] = "ScriptLimits", [66] = "SetTabIndex", [69] = "FileAttributes", [70] = "PlaceObject3",
        [71] = "ImportAssets2", [73] = "DefineFontAlignZones", [74] = "CSMTextSettings", [75] = "DefineFont3", [76] = "SymbolClass", [77] = "Metadata",
        [78] = "DefineScalingGrid", [82] = "DoABC", [83] = "DefineShape4", [84] = "DefineMorphShape2", [86] = "DefineSceneAndFrameLabelData",
        [87] = "DefineBinaryData", [88] = "DefineFontName", [89] = "StartSound2", [1000] = "GFX_ExporterInfo", [1001] = "GFX_DefineExternalImage",
        [1002] = "GFX_FontTextureInfo", [1003] = "GFX_DefineExternalGradient", [1004] = "GFX_DefineGradientMap", [1005] = "GFX_DefineCompactedFont",
        [1006] = "GFX_DefineExternalSound", [1007] = "GFX_DefineExternalStreamSound", [1008] = "GFX_DefineSubImage", [1009] = "GFX_DefineExternalImage2",
    };

    /// <summary>Byte and bit of each event in a clip action's flags.</summary>
    private static readonly (int Byte, int Bit, string Name)[] ClipEvents =
    [
        (0, 7, "KeyUp"), (0, 6, "KeyDown"), (0, 5, "MouseUp"), (0, 4, "MouseDown"), (0, 3, "MouseMove"), (0, 2, "Unload"), (0, 1, "EnterFrame"), (0, 0, "Load"),
        (1, 7, "DragOver"), (1, 6, "RollOut"), (1, 5, "RollOver"), (1, 4, "ReleaseOutside"), (1, 3, "Release"), (1, 2, "Press"), (1, 1, "Initialize"), (1, 0, "Data"),
        (2, 2, "Construct"), (2, 1, "KeyPress"), (2, 0, "DragOut"),
    ];

    private readonly SwfMovie swf;
    private readonly byte[] d;
    private readonly List<string> head;
    private readonly Dictionary<int, string> exports = [];
    private readonly List<string> fonts = [];
    private readonly List<string> edits = [];
    private readonly List<string> images = [];

    public SwfScan(SwfMovie swf)
    {
        this.swf = swf;
        d = swf.Data;
        head = [$"{swf.Signature} v{swf.Version} len={swf.FileLength} stage={swf.StageText} rate={GfxScan.PyFloat(swf.FrameRate)} frames={swf.Frames}"];
        Walk(swf.Tags, "root", 0);
    }

    public SortedDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    public Dictionary<int, int> Sprites { get; } = [];

    public List<ActionBlock> Blocks { get; } = [];

    public int Po3ClipActions { get; private set; }

    public int TagTotal => Counts.Values.Sum();

    public int ExternalImageCount => images.Count;

    public static string TagName(int code) => TagNames.GetValueOrDefault(code) ?? GfxScan.Int(code);

    public static SortedDictionary<string, int> Count(List<SwfTag> tags, bool nested)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Add(List<SwfTag> list)
        {
            foreach (var t in list)
            {
                var name = TagName(t.Code);
                counts[name] = counts.GetValueOrDefault(name) + 1;
                if (nested && t.Children is not null) Add(t.Children);
            }
        }
        Add(tags);
        return counts;
    }

    /// <summary>root is 0, the timeline no character id uses.</summary>
    public int SpriteId(string sprite)
    {
        var s = sprite.Trim();
        if (s.Equals("root", StringComparison.OrdinalIgnoreCase)) return 0;
        var digits = s.StartsWith("sprite", StringComparison.OrdinalIgnoreCase) ? s[6..] : s;
        if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return id;
        foreach (var (key, name) in exports)
        {
            if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) return key;
        }
        throw new ArgumentException($"No sprite '{sprite}': give root, an id, or a linkage export name (mode=tags lists them).");
    }

    private void Walk(List<SwfTag> tags, string owner, int ownerId)
    {
        var frame = 0;
        foreach (var t in tags)
        {
            var name = TagName(t.Code);
            Counts[name] = Counts.GetValueOrDefault(name) + 1;
            var ctx = $"{owner} f{frame}";
            switch (t.Code)
            {
                case 1:
                    frame++;
                    break;
                case 39:
                    Sprites[t.SpriteId] = t.SpriteFrames;
                    Walk(t.Children!, $"sprite{t.SpriteId}", t.SpriteId);
                    break;
                case 12:
                    Blocks.Add(new ActionBlock($"== DoAction {ctx} (file 0x{t.Start:x}, {t.Length} bytes)", $"//==== DoAction {ctx}", ownerId, frame, null, t.Start, t.End, t.Start));
                    break;
                case 59:
                    var sid = GfxScan.U16(d, t.Start);
                    Blocks.Add(new ActionBlock($"== DoInitAction sprite{sid} (file 0x{t.Start:x}, {t.Length} bytes)", $"//==== DoInitAction sprite{sid}", ownerId, frame, sid, t.Start + 2, t.End, t.Start));
                    break;
                case 43:
                    var labelAt = t.Start;
                    head.Add($"  label {ctx}: {GfxScan.CStr(d, ref labelAt, t.End)}");
                    break;
                case 56:
                    ExportAssets(t);
                    break;
                case 26 or 70:
                    Place(t, ctx, ownerId, frame);
                    break;
                case 34:
                    ButtonActions(t, ctx, ownerId, frame);
                    break;
                case 37:
                    EditText(t);
                    break;
                case 1001 or 1009:
                    Image(t);
                    break;
                case 48 or 75 or 1005 or 10:
                    Font(t);
                    break;
                case 57 or 71:
                    Import(t);
                    break;
                case 1000:
                    head.Add("  GFX_" + swf.ExporterLine(t));
                    break;
            }
        }
    }

    private void ExportAssets(SwfTag t)
    {
        var q = t.Start + 2;
        for (var i = GfxScan.U16(d, t.Start); i > 0; i--)
        {
            var id = GfxScan.U16(d, q);
            q += 2;
            exports[id] = GfxScan.CStr(d, ref q, t.End);
        }
    }

    private void Place(SwfTag t, string ctx, int ownerId, int frame)
    {
        var q = t.Start;
        int flags = d[q], flags2 = 0;
        var po3 = t.Code == 70;
        if (po3) flags2 = d[++q];
        var depth = GfxScan.U16(d, q + 1);
        q += 3;

        string? className = null;
        if (po3 && ((flags2 & 0x08) != 0 || ((flags2 & 0x10) != 0 && (flags & 0x02) != 0))) className = GfxScan.CStr(d, ref q, t.End);
        int? character = null;
        if ((flags & 0x02) != 0)
        {
            character = GfxScan.U16(d, q);
            q += 2;
        }
        string? at = null;
        if ((flags & 0x04) != 0) at = Matrix(ref q);
        if ((flags & 0x08) != 0) SkipColorTransform(ref q);
        if ((flags & 0x10) != 0) q += 2;
        string? instance = null;
        if ((flags & 0x20) != 0) instance = GfxScan.CStr(d, ref q, t.End);
        if ((flags & 0x40) != 0) q += 2;

        if ((flags & 0x80) != 0)
        {
            if (po3) Po3ClipActions++;
            else ClipActions(ref q, t.End, ctx, depth, character, instance, ownerId, frame);
        }

        if (instance is not null || character is not null)
        {
            var target = character is { } c ? $"char{c}" : "move";
            var export = character is { } e && exports.TryGetValue(e, out var linkage) ? $" [{linkage}]" : "";
            head.Add($"  place {ctx} depth {depth} {target}{export} name={instance ?? "None"}{at}{(className is null ? "" : $" class={className}")}");
        }
    }

    private void ClipActions(ref int q, int end, string ctx, int depth, int? character, string? instance, int ownerId, int frame)
    {
        q += 6; // reserved, then the union of all event flags
        while (true)
        {
            var events = d.AsSpan(q, 4).ToArray();
            q += 4;
            if (BitConverter.ToUInt32(events) == 0) break;
            var size = GfxScan.I32(d, q);
            q += 4;
            if (size < 0 || size > end - q) throw new InvalidDataException($"Clip action at 0x{q:x} runs past its tag ({size} bytes).");
            var names = ClipEvents.Where(e => (events[e.Byte] & (1 << e.Bit)) != 0).Select(e => e.Name);
            var start = q;
            var key = "";
            if ((events[2] & 0x02) != 0)
            {
                key = $" key={d[start]}";
                start++;
            }
            var label = $"ClipAction {ctx} depth {depth} char {(character is { } c ? GfxScan.Int(c) : "None")} name {instance ?? "None"} on({string.Join(',', names)}){key}";
            Blocks.Add(new ActionBlock("== " + label, "//==== " + label, ownerId, frame, null, start, q + size, start));
            q += size;
            if (q >= end) break;
        }
    }

    private void ButtonActions(SwfTag t, string ctx, int ownerId, int frame)
    {
        var id = GfxScan.U16(d, t.Start);
        var offsetField = t.Start + 3;
        var offset = GfxScan.U16(d, offsetField);
        if (offset == 0) return;
        var q = offsetField + offset;
        while (q + 4 <= t.End)
        {
            var size = GfxScan.U16(d, q);
            var conditions = GfxScan.U16(d, q + 2);
            var stop = size == 0 ? t.End : q + size;
            if (stop > t.End) throw new InvalidDataException($"Button action at 0x{q:x} runs past its tag ({size} bytes).");
            var label = $"ButtonAction {ctx} button{id} conditions 0x{conditions:x4}";
            Blocks.Add(new ActionBlock("== " + label, "//==== " + label, ownerId, frame, id, q + 4, stop, q + 4));
            if (size == 0) break;
            q += size;
        }
    }

    private string Matrix(ref int q)
    {
        var r = new Bits(d, q);
        double scaleX = 1, scaleY = 1;
        if (r.Unsigned(1) != 0)
        {
            var n = (int)r.Unsigned(5);
            scaleX = r.Signed(n) / 65536.0;
            scaleY = r.Signed(n) / 65536.0;
        }
        if (r.Unsigned(1) != 0)
        {
            var n = (int)r.Unsigned(5);
            r.Signed(n);
            r.Signed(n);
        }
        var bits = (int)r.Unsigned(5);
        var x = r.Signed(bits) / 20.0;
        var y = r.Signed(bits) / 20.0;
        q = r.Align();
        return string.Create(CultureInfo.InvariantCulture, $" at ({x:F1},{y:F1}) scale ({scaleX:F3},{scaleY:F3})");
    }

    private void SkipColorTransform(ref int q)
    {
        var r = new Bits(d, q);
        bool add = r.Unsigned(1) != 0, multiply = r.Unsigned(1) != 0;
        var n = (int)r.Unsigned(4);
        for (var i = (add ? 4 : 0) + (multiply ? 4 : 0); i > 0; i--) r.Signed(n);
        q = r.Align();
    }

    private void EditText(SwfTag t)
    {
        var id = GfxScan.U16(d, t.Start);
        var r = new Bits(d, t.Start + 2);
        var n = (int)r.Unsigned(5);
        for (var i = 0; i < 4; i++) r.Signed(n);
        var q = r.Align();
        int f1 = d[q], f2 = d[q + 1];
        q += 2;
        bool hasText = (f1 & 0x80) != 0, hasColor = (f1 & 0x04) != 0, hasMax = (f1 & 0x02) != 0, hasFont = (f1 & 0x01) != 0;
        bool hasClass = (f2 & 0x80) != 0, hasLayout = (f2 & 0x20) != 0, html = (f2 & 0x02) != 0;
        int? font = null;
        if (hasFont)
        {
            font = GfxScan.U16(d, q);
            q += 2;
        }
        string? fontClass = null;
        if (hasClass) fontClass = GfxScan.CStr(d, ref q, t.End);
        if (hasFont || hasClass) q += 2;
        if (hasColor) q += 4;
        if (hasMax) q += 2;
        if (hasLayout) q += 9;
        var variable = GfxScan.CStr(d, ref q, t.End);
        var text = hasText ? GfxScan.CStr(d, ref q, t.End) : "";
        edits.Add($"  ({id}, {GfxScan.Repr(variable)}, {(font is { } f ? GfxScan.Int(f) : "None")}, {(fontClass is null ? "None" : GfxScan.Repr(fontClass))}, {(html ? "True" : "False")}, {GfxScan.Repr(text)})");
    }

    private void Image(SwfTag t)
    {
        if (t.Code == 1009)
        {
            var q = t.Start + 10;
            var export = GfxScan.PStr(d, ref q);
            var file = GfxScan.PStr(d, ref q);
            images.Add($"  ({GfxScan.I32(d, t.Start)}, {GfxScan.U16(d, t.Start + 6)}, {GfxScan.U16(d, t.Start + 8)}, {GfxScan.Repr(export)}, {GfxScan.Repr(file)})");
        }
        else
        {
            var q = t.Start + 8;
            var file = GfxScan.PStr(d, ref q);
            images.Add($"  ({GfxScan.U16(d, t.Start)}, {GfxScan.U16(d, t.Start + 4)}, {GfxScan.U16(d, t.Start + 6)}, '', {GfxScan.Repr(file)})");
        }
    }

    private void Font(SwfTag t)
    {
        var id = GfxScan.U16(d, t.Start);
        var name = "";
        if (t.Code is 48 or 75)
        {
            // Font id, flags, language, then the length-prefixed name.
            int n = d[t.Start + 4];
            name = Encoding.Latin1.GetString(d, t.Start + 5, n).TrimEnd('\0');
        }
        fonts.Add($"  ({id}, {GfxScan.Repr(TagName(t.Code))}, {GfxScan.Repr(name)})");
    }

    private void Import(SwfTag t)
    {
        var q = t.Start;
        var url = GfxScan.CStr(d, ref q, t.End);
        if (t.Code == 71) q += 2;
        var count = GfxScan.U16(d, q);
        q += 2;
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = GfxScan.U16(d, q);
            q += 2;
            names.Add($"{id}:{GfxScan.CStr(d, ref q, t.End)}");
        }
        head.Add($"  import from {url}: {string.Join(", ", names)}");
    }

    public IEnumerable<string> Summary
    {
        get
        {
            var lines = new List<string>(head)
            {
                "tag counts: " + string.Join(", ", Counts.Select(c => $"{c.Key}={c.Value}")),
                $"sprites: {Sprites.Count}",
                "exports (linkage):",
            };
            foreach (var (id, name) in exports.OrderBy(e => e.Key))
            {
                lines.Add($"  {id}: {name}" + (Sprites.TryGetValue(id, out var frames) ? $" (sprite, {frames} frames)" : ""));
            }
            lines.Add("fonts:");
            lines.AddRange(fonts);
            lines.Add("edit texts (id, variable, font, fontclass, html, initial text):");
            lines.AddRange(edits);
            lines.Add("external images (id, w, h, export name, file):");
            lines.AddRange(images);
            return lines;
        }
    }

    public IEnumerable<string> TagList(List<SwfTag> tags, string indent) => tags.Select(t =>
    {
        var line = $"{indent}0x{t.Start:x6} {TagName(t.Code)} len={t.Length}";
        if (t.Children is null) return line;
        var linkage = exports.TryGetValue(t.SpriteId, out var name) ? $" [{name}]" : "";
        return $"{line} id={t.SpriteId}{linkage} frames={t.SpriteFrames} tags={t.Children.Count}";
    });

    /// <summary>One line per image tag; external images and gradients also name the package texture of the same name.</summary>
    public List<string> ImageLines(List<(string ObjectName, string Text)> references)
    {
        var lines = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string Texture(string file)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            foreach (var r in references.Where(r => r.ObjectName.Equals(stem, StringComparison.OrdinalIgnoreCase)))
            {
                used.Add(r.ObjectName);
                return $" texture={r.Text}";
            }
            return references.Count > 0 ? " texture=none" : "";
        }

        void Add(List<SwfTag> tags)
        {
            foreach (var t in tags)
            {
                var b = t.Start;
                var q = b;
                switch (t.Code)
                {
                    case 1000:
                        lines.Add(swf.ExporterLine(t));
                        break;
                    case 1001:
                    {
                        q = b + 8;
                        var file = GfxScan.PStr(d, ref q);
                        lines.Add($"DefineExternalImage id={GfxScan.U16(d, b)} fmt={GfxScan.U16(d, b + 2)} {GfxScan.U16(d, b + 4)}x{GfxScan.U16(d, b + 6)} file={GfxScan.Repr(file)} rest={GfxScan.Hex(d.AsSpan(q, t.End - q))}{Texture(file)}");
                        break;
                    }
                    case 1009:
                    {
                        q = b + 10;
                        var export = GfxScan.PStr(d, ref q);
                        var file = GfxScan.PStr(d, ref q);
                        lines.Add($"DefineExternalImage2 id={GfxScan.I32(d, b)} fmt={GfxScan.U16(d, b + 4)} {GfxScan.U16(d, b + 6)}x{GfxScan.U16(d, b + 8)} export={GfxScan.Repr(export)} file={GfxScan.Repr(file)} rest={GfxScan.Hex(d.AsSpan(q, t.End - q))}{Texture(file)}");
                        break;
                    }
                    case 1003:
                    {
                        q = b + 6;
                        var file = GfxScan.PStr(d, ref q);
                        lines.Add($"DefineExternalGradient id={GfxScan.U16(d, b)} fmt={GfxScan.U16(d, b + 2)} size={GfxScan.U16(d, b + 4)} file={GfxScan.Repr(file)} rest={GfxScan.Hex(d.AsSpan(q, t.End - q))}{Texture(file)}");
                        break;
                    }
                    case 1002 or 1004 or 1005 or 1006 or 1007 or 1008:
                        lines.Add($"GFx tag {t.Code} len={t.Length} head={GfxScan.Hex(d.AsSpan(b, Math.Min(t.Length, 48)))}");
                        break;
                    case 6 or 20 or 21 or 35 or 36 or 90:
                        lines.Add($"bitmap tag {t.Code} id={GfxScan.U16(d, b)} len={t.Length}");
                        break;
                }
                if (t.Children is not null) Add(t.Children);
            }
        }

        Add(swf.Tags);
        var unmatched = references.Where(r => !used.Contains(r.ObjectName)).Select(r => r.Text).ToList();
        if (unmatched.Count > 0) lines.Add("references no tag names: " + string.Join(", ", unmatched));
        return lines;
    }
}

/// <summary>ActionScript 2 bytecode as text: a disassembly, or a rough pseudo-code from simulating the stack.</summary>
internal static class As2
{
    private static readonly Dictionary<int, string> ActionNames = new()
    {
        [0x00] = "End", [0x04] = "NextFrame", [0x05] = "PrevFrame", [0x06] = "Play", [0x07] = "Stop", [0x08] = "ToggleQuality", [0x09] = "StopSounds",
        [0x0A] = "Add", [0x0B] = "Subtract", [0x0C] = "Multiply", [0x0D] = "Divide", [0x0E] = "Equals", [0x0F] = "Less", [0x10] = "And", [0x11] = "Or",
        [0x12] = "Not", [0x13] = "StringEquals", [0x14] = "StringLength", [0x15] = "StringExtract", [0x17] = "Pop", [0x18] = "ToInteger", [0x1C] = "GetVariable",
        [0x1D] = "SetVariable", [0x20] = "SetTarget2", [0x21] = "StringAdd", [0x22] = "GetProperty", [0x23] = "SetProperty", [0x24] = "CloneSprite",
        [0x25] = "RemoveSprite", [0x26] = "Trace", [0x27] = "StartDrag", [0x28] = "EndDrag", [0x29] = "StringLess", [0x2A] = "Throw", [0x2B] = "CastOp",
        [0x2C] = "ImplementsOp", [0x30] = "RandomNumber", [0x31] = "MBStringLength", [0x32] = "CharToAscii", [0x33] = "AsciiToChar", [0x34] = "GetTime",
        [0x35] = "MBStringExtract", [0x36] = "MBCharToAscii", [0x37] = "MBAsciiToChar", [0x3A] = "Delete", [0x3B] = "Delete2", [0x3C] = "DefineLocal",
        [0x3D] = "CallFunction", [0x3E] = "Return", [0x3F] = "Modulo", [0x40] = "NewObject", [0x41] = "DefineLocal2", [0x42] = "InitArray", [0x43] = "InitObject",
        [0x44] = "TypeOf", [0x45] = "TargetPath", [0x46] = "Enumerate", [0x47] = "Add2", [0x48] = "Less2", [0x49] = "Equals2", [0x4A] = "ToNumber", [0x4B] = "ToString",
        [0x4C] = "PushDuplicate", [0x4D] = "StackSwap", [0x4E] = "GetMember", [0x4F] = "SetMember", [0x50] = "Increment", [0x51] = "Decrement", [0x52] = "CallMethod",
        [0x53] = "NewMethod", [0x54] = "InstanceOf", [0x55] = "Enumerate2", [0x60] = "BitAnd", [0x61] = "BitOr", [0x62] = "BitXor", [0x63] = "BitLShift",
        [0x64] = "BitRShift", [0x65] = "BitURShift", [0x66] = "StrictEquals", [0x67] = "Greater", [0x68] = "StringGreater", [0x69] = "Extends", [0x81] = "GotoFrame",
        [0x83] = "GetURL", [0x87] = "StoreRegister", [0x88] = "ConstantPool", [0x8A] = "WaitForFrame", [0x8B] = "SetTarget", [0x8C] = "GoToLabel",
        [0x8D] = "WaitForFrame2", [0x8E] = "DefineFunction2", [0x8F] = "Try", [0x94] = "With", [0x96] = "Push", [0x99] = "Jump", [0x9A] = "GetURL2",
        [0x9B] = "DefineFunction", [0x9D] = "If", [0x9E] = "Call", [0x9F] = "GotoFrame2",
    };

    private static readonly Dictionary<int, string> BinaryOperators = new()
    {
        [0x47] = "+", [0x0A] = "+", [0x0B] = "-", [0x0C] = "*", [0x0D] = "/", [0x3F] = "%", [0x48] = "<", [0x0F] = "<", [0x49] = "==", [0x0E] = "==", [0x66] = "===",
        [0x67] = ">", [0x10] = "&&", [0x11] = "||", [0x60] = "&", [0x61] = "|", [0x62] = "^", [0x63] = "<<", [0x64] = ">>", [0x65] = ">>>", [0x21] = "add", [0x13] = "eq",
        [0x29] = "lt", [0x68] = "gt", [0x54] = "instanceof",
    };

    public static string Disassembly(byte[] d, List<ActionBlock> blocks)
    {
        var o = new List<string>();
        foreach (var block in blocks)
        {
            o.Add(block.DisasmHeader);
            Guarded(o, "  ", () => Disassemble(d, block.Start, block.End, o));
        }
        return o.Count == 0 ? "" : string.Join('\n', o) + "\n";
    }

    public static string Pseudo(byte[] d, List<ActionBlock> blocks)
    {
        var o = new List<string>();
        foreach (var block in blocks)
        {
            o.Add("");
            o.Add(block.PseudoHeader);
            Guarded(o, "", () => Decompile(d, block.Start, block.End, block.Base, o, "", [], []));
        }
        return o.Count == 0 ? "" : string.Join('\n', o) + "\n";
    }

    /// <summary>One bad block should not cost the rest of the movie.</summary>
    private static void Guarded(List<string> o, string indent, Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            o.Add($"{indent}// block unreadable from here: {ex.Message}");
        }
    }

    private static byte[] Payload(byte[] d, ref int p, int end, int code)
    {
        if (code < 0x80) return [];
        var length = GfxScan.U16(d, p);
        p += 2;
        var data = d[p..Math.Min(p + length, end)];
        p += length;
        return data;
    }

    private static string Offset(int off) => off.ToString("x5", CultureInfo.InvariantCulture);

    private static List<string> ReadPool(byte[] data)
    {
        var pool = new List<string>();
        var q = 2;
        for (var i = GfxScan.U16(data, 0); i > 0; i--) pool.Add(GfxScan.CStr(data, ref q, data.Length));
        return pool;
    }

    private static IEnumerable<string> PushValues(byte[] data, List<string> pool, Func<int, string> register)
    {
        var values = new List<string>();
        var q = 0;
        while (q < data.Length)
        {
            int type = data[q++];
            switch (type)
            {
                case 0:
                    values.Add(GfxScan.Repr(GfxScan.CStr(data, ref q, data.Length)));
                    break;
                case 1:
                    values.Add(GfxScan.Num(BitConverter.ToSingle(data, q)));
                    q += 4;
                    break;
                case 2:
                    values.Add("null");
                    break;
                case 3:
                    values.Add("undefined");
                    break;
                case 4:
                    values.Add(register(data[q++]));
                    break;
                case 5:
                    values.Add(data[q++] != 0 ? "true" : "false");
                    break;
                case 6:
                    // Doubles are stored with their two 32-bit words swapped.
                    values.Add(GfxScan.Num(BitConverter.ToDouble([.. data.AsSpan(q + 4, 4), .. data.AsSpan(q, 4)])));
                    q += 8;
                    break;
                case 7:
                    values.Add(GfxScan.Int(GfxScan.I32(data, q)));
                    q += 4;
                    break;
                case 8 or 9:
                    var i = type == 8 ? data[q] : GfxScan.U16(data, q);
                    q += type == 8 ? 1 : 2;
                    values.Add(i < pool.Count ? GfxScan.Repr(pool[i]) : $"c{i}?");
                    break;
                default:
                    values.Add($"?type{type}");
                    return values;
            }
        }
        return values;
    }

    private static void Disassemble(byte[] d, int start, int end, List<string> o)
    {
        const string indent = "  ";
        var pool = new List<string>();
        var p = start;
        while (p < end)
        {
            var at = Offset(p - start);
            int c = d[p++];
            var data = Payload(d, ref p, end, c);
            var name = ActionNames.GetValueOrDefault(c) ?? $"op{c:x2}";
            var q = 0;
            switch (c)
            {
                case 0:
                    o.Add($"{indent}{at} End");
                    break;
                case 0x88:
                    pool = ReadPool(data);
                    o.Add($"{indent}{at} ConstantPool {pool.Count}: {string.Join(" | ", pool)}");
                    break;
                case 0x96:
                    o.Add($"{indent}{at} Push {string.Join(", ", PushValues(data, pool, r => $"r{r}"))}");
                    break;
                case 0x9B or 0x8E:
                    var function = GfxScan.CStr(data, ref q, data.Length);
                    var count = GfxScan.U16(data, q);
                    var parameters = new List<string>();
                    if (c == 0x9B)
                    {
                        q += 2;
                        for (var i = 0; i < count; i++) parameters.Add(GfxScan.CStr(data, ref q, data.Length));
                    }
                    else
                    {
                        q += 5;
                        for (var i = 0; i < count; i++)
                        {
                            int reg = data[q++];
                            var parameter = GfxScan.CStr(data, ref q, data.Length);
                            parameters.Add(reg != 0 ? $"{parameter}@r{reg}" : parameter);
                        }
                    }
                    o.Add($"{indent}{at} {name} \"{function}\"({string.Join(", ", parameters)}) size={GfxScan.U16(data, q)}");
                    break;
                case 0x99 or 0x9D:
                    o.Add($"{indent}{at} {name} -> {Offset(p - start + BitConverter.ToInt16(data, 0))}");
                    break;
                case 0x83:
                    var url = GfxScan.CStr(data, ref q, data.Length);
                    o.Add($"{indent}{at} GetURL {GfxScan.Repr(url)} {GfxScan.Repr(GfxScan.CStr(data, ref q, data.Length))}");
                    break;
                case 0x87:
                    o.Add($"{indent}{at} StoreRegister r{data[0]}");
                    break;
                case 0x8C or 0x8B:
                    o.Add($"{indent}{at} {name} {GfxScan.Repr(GfxScan.CStr(data, ref q, data.Length))}");
                    break;
                case 0x81:
                    o.Add($"{indent}{at} GotoFrame {GfxScan.U16(data, 0)}");
                    break;
                default:
                    o.Add(data.Length > 0 ? $"{indent}{at} {name} [{GfxScan.Hex(data)}]" : $"{indent}{at} {name}");
                    break;
            }
        }
    }

    /// <summary>A stack value: an expression, or an anonymous function with its statements.</summary>
    private sealed record Val(string S, string? Head = null, List<string>? Body = null)
    {
        public static Val Of(string s) => new(s);

        public static Val Function(string head, List<string> body) => new(head + " {...}", head, body);
    }

    private static string? Ident(Val v)
    {
        var s = v.S;
        if (v.Body is not null || s.Length < 3 || s[0] != '\'' || s[^1] != '\'') return null;
        return s[1..^1].All(c => c == '_' || c == '$' || char.IsLetterOrDigit(c)) ? s[1..^1] : null;
    }

    private static string Member(Val o, Val m) => Ident(m) is { } name ? $"{o.S}.{name}" : $"{o.S}[{m.S}]";

    // The stack is simulated linearly, so real movies do underflow it a little; a count far past it is a bad file.
    private const int ExtraPops = 16;
    private const int MaxFunctionDepth = 32;

    private static int Count(Val k) =>
        k.S.Length > 0 && k.S.TrimStart('-').Length > 0 && k.S.TrimStart('-').All(char.IsAsciiDigit) && int.TryParse(k.S, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>Every offset a Jump or If lands on, so goto targets get a label line.</summary>
    private static HashSet<int> JumpTargets(byte[] d, int start, int end)
    {
        var targets = new HashSet<int>();
        var p = start;
        while (p < end)
        {
            int c = d[p++];
            if (c == 0) break;
            var data = Payload(d, ref p, end, c);
            if (c is 0x99 or 0x9D) targets.Add(p + BitConverter.ToInt16(data, 0));
        }
        return targets;
    }

    private static void Decompile(byte[] d, int start, int end, int labelBase, List<string> o, string ind, List<string> pool, Dictionary<int, string> regs, int depth = 0)
    {
        var st = new List<Val>();
        var targets = JumpTargets(d, start, end);

        Val Pop()
        {
            if (st.Count == 0) return Val.Of("<?>");
            var v = st[^1];
            st.RemoveAt(st.Count - 1);
            return v;
        }

        void Push(string s) => st.Add(Val.Of(s));

        void Emit(string s) => o.Add(ind + s);

        void Assign(string lhs, Val v)
        {
            if (v.Body is not null)
            {
                Emit($"{lhs} = {v.Head} {{");
                o.AddRange(v.Body);
                Emit("}");
            }
            else
            {
                Emit($"{lhs} = {v.S}");
            }
        }

        string Label(int target) => "L" + Offset(target - labelBase);

        string Register(int r) => regs.TryGetValue(r, out var name) ? name : $"r{r}";

        List<string> Args(Val k)
        {
            var args = new List<string>();
            for (var i = Math.Min(Count(k), st.Count + ExtraPops); i > 0; i--) args.Add(Pop().S);
            return args;
        }

        var p = start;
        while (p < end)
        {
            if (targets.Remove(p)) o.Add($"{ind}{Label(p)}:");
            int c = d[p++];
            var data = Payload(d, ref p, end, c);
            if (c == 0) break;
            var q = 0;
            switch (c)
            {
                case 0x88:
                    pool.Clear();
                    pool.AddRange(ReadPool(data));
                    break;
                case 0x96:
                    foreach (var value in PushValues(data, pool, Register)) Push(value);
                    break;
                case 0x9B or 0x8E:
                {
                    var function = GfxScan.CStr(data, ref q, data.Length);
                    var count = GfxScan.U16(data, q);
                    var inner = new Dictionary<int, string>();
                    var parameters = new List<string>();
                    if (c == 0x9B)
                    {
                        q += 2;
                        for (var i = 0; i < count; i++) parameters.Add(GfxScan.CStr(data, ref q, data.Length));
                    }
                    else
                    {
                        int flags = data[q + 3], flags2 = data[q + 4];
                        q += 5;
                        for (var i = 0; i < count; i++)
                        {
                            int reg = data[q++];
                            var parameter = GfxScan.CStr(data, ref q, data.Length);
                            parameters.Add(parameter);
                            if (reg != 0) inner[reg] = parameter;
                        }
                        // Preloaded values fill registers from 1, in this order.
                        var next = 1;
                        foreach (var (bit, name) in new[] { (0x01, "this"), (0x04, "arguments"), (0x10, "super"), (0x40, "_root"), (0x80, "_parent") })
                        {
                            if ((flags & bit) != 0) inner[next++] = name;
                        }
                        if ((flags2 & 0x01) != 0) inner[next] = "_global";
                    }
                    var size = GfxScan.U16(data, q);
                    var head = $"function {function}({string.Join(", ", parameters)})";
                    var body = new List<string>();
                    if (depth < MaxFunctionDepth) Decompile(d, p, p + size, labelBase, body, ind + "    ", [.. pool], inner, depth + 1);
                    else body.Add($"{ind}    // {size} bytes not decompiled: functions nest deeper than {MaxFunctionDepth} levels");
                    p += size;
                    if (function.Length > 0)
                    {
                        Emit(head + " {");
                        o.AddRange(body);
                        Emit("}");
                    }
                    else
                    {
                        st.Add(Val.Function(head, body));
                    }
                    break;
                }
                case 0x1C:
                {
                    var n = Pop();
                    Push(Ident(n) ?? $"eval({n.S})");
                    break;
                }
                case 0x1D:
                {
                    var v = Pop();
                    var n = Pop();
                    Assign(Ident(n) ?? $"set({n.S})", v);
                    break;
                }
                case 0x3C:
                {
                    var v = Pop();
                    var n = Pop();
                    Assign("var " + (Ident(n) ?? n.S), v);
                    break;
                }
                case 0x41:
                    Emit("var " + (Ident(Pop()) ?? "?"));
                    break;
                case 0x4E:
                {
                    var m = Pop();
                    var obj = Pop();
                    Push(Member(obj, m));
                    break;
                }
                case 0x4F:
                {
                    var v = Pop();
                    var m = Pop();
                    var obj = Pop();
                    Assign(Member(obj, m), v);
                    break;
                }
                case 0x3D or 0x40:
                {
                    var n = Pop();
                    var args = Args(Pop());
                    Push($"{(c == 0x40 ? "new " : "")}{Ident(n) ?? n.S}({string.Join(", ", args)})");
                    break;
                }
                case 0x52 or 0x53:
                {
                    var m = Pop();
                    var obj = Pop();
                    var args = Args(Pop());
                    var target = m.S is "undefined" or "''" ? obj.S : Member(obj, m);
                    Push($"{(c == 0x53 ? "new " : "")}{target}({string.Join(", ", args)})");
                    break;
                }
                case 0x42:
                    Push("[" + string.Join(", ", Args(Pop())) + "]");
                    break;
                case 0x43:
                {
                    var items = new List<string>();
                    for (var i = Math.Min(Count(Pop()), st.Count + ExtraPops); i > 0; i--)
                    {
                        var v = Pop();
                        var n = Pop();
                        items.Add($"{Ident(n) ?? n.S}: {v.S}");
                    }
                    Push("{" + string.Join(", ", items) + "}");
                    break;
                }
                case 0x17:
                    Emit(Pop().S);
                    break;
                case 0x12:
                    Push($"!{Pop().S}");
                    break;
                case 0x50:
                    Push($"({Pop().S} + 1)");
                    break;
                case 0x51:
                    Push($"({Pop().S} - 1)");
                    break;
                case 0x4C:
                    st.Add(st.Count > 0 ? st[^1] : Val.Of("<?>"));
                    break;
                case 0x4D:
                    if (st.Count >= 2) (st[^1], st[^2]) = (st[^2], st[^1]);
                    break;
                case 0x87:
                    Emit($"{Register(data[0])} := {(st.Count > 0 ? st[^1].S : "<?>")}");
                    break;
                case 0x3E:
                    Emit($"return {Pop().S}");
                    break;
                case 0x9D:
                    Emit($"if ({Pop().S}) goto {Label(p + BitConverter.ToInt16(data, 0))}");
                    break;
                case 0x99:
                    Emit($"goto {Label(p + BitConverter.ToInt16(data, 0))}");
                    break;
                case 0x9A:
                {
                    var t = Pop();
                    var u = Pop();
                    Emit($"getURL({u.S}, {t.S})");
                    break;
                }
                case 0x83:
                {
                    var u = GfxScan.CStr(data, ref q, data.Length);
                    Emit($"getURL({GfxScan.Repr(u)}, {GfxScan.Repr(GfxScan.CStr(data, ref q, data.Length))})");
                    break;
                }
                case 0x44:
                    Push($"typeof {Pop().S}");
                    break;
                case 0x4A:
                    Push($"Number({Pop().S})");
                    break;
                case 0x4B:
                    Push($"String({Pop().S})");
                    break;
                case 0x18:
                    Push($"int({Pop().S})");
                    break;
                case 0x2B:
                {
                    var obj = Pop();
                    var t = Pop();
                    Push($"({t.S})({obj.S})");
                    break;
                }
                case 0x69:
                {
                    var super = Pop();
                    var sub = Pop();
                    Emit($"{sub.S} extends {super.S}");
                    break;
                }
                case 0x2C:
                {
                    var interfaces = Args(Pop());
                    Emit($"{Pop().S} implements {string.Join(", ", interfaces)}");
                    break;
                }
                case 0x3A:
                {
                    var m = Pop();
                    var obj = Pop();
                    Push($"delete {Member(obj, m)}");
                    break;
                }
                case 0x3B:
                    Push($"delete {Pop().S}");
                    break;
                case 0x26:
                    Emit($"trace({Pop().S})");
                    break;
                case 0x46 or 0x55:
                    Push($"enumerate({Pop().S})");
                    break;
                case 0x22:
                {
                    var i = Pop();
                    var t = Pop();
                    Push($"getProperty({t.S}, {i.S})");
                    break;
                }
                case 0x23:
                {
                    var v = Pop();
                    var i = Pop();
                    var t = Pop();
                    Emit($"setProperty({t.S}, {i.S}, {v.S})");
                    break;
                }
                case 0x34:
                    Push("getTimer()");
                    break;
                case 0x45:
                    Push($"targetPath({Pop().S})");
                    break;
                case 0x07:
                    Emit("stop()");
                    break;
                case 0x06:
                    Emit("play()");
                    break;
                case 0x9F:
                    Emit($"gotoFrame2({Pop().S}, play={data[0] & 1})");
                    break;
                case 0x8C:
                    Emit($"gotoLabel({GfxScan.Repr(GfxScan.CStr(data, ref q, data.Length))})");
                    break;
                case 0x81:
                    Emit($"gotoFrame({GfxScan.U16(data, 0)})");
                    break;
                case 0x8B:
                    Emit($"setTarget({GfxScan.Repr(GfxScan.CStr(data, ref q, data.Length))})");
                    break;
                case 0x20:
                    Emit($"setTarget2({Pop().S})");
                    break;
                case 0x27:
                {
                    var t = Pop();
                    var locked = Pop();
                    var constrain = Pop();
                    Emit($"startDrag({t.S}, lock={locked.S}, constrain={constrain.S} ...)");
                    break;
                }
                case 0x28:
                    Emit("stopDrag()");
                    break;
                case 0x2A:
                    Emit($"throw {Pop().S}");
                    break;
                case 0x30:
                    Push($"random({Pop().S})");
                    break;
                case 0x14:
                    Push($"length({Pop().S})");
                    break;
                default:
                    if (BinaryOperators.TryGetValue(c, out var op))
                    {
                        var right = Pop();
                        var left = Pop();
                        Push($"({left.S} {op} {right.S})");
                    }
                    else
                    {
                        Emit($"/* op 0x{c:x2} {GfxScan.Hex(data)} */");
                    }
                    break;
            }
        }
        if (targets.Contains(end)) o.Add($"{ind}{Label(end)}:");
        foreach (var v in st.Where(v => v.Body is not null)) Emit(v.S);
    }
}
