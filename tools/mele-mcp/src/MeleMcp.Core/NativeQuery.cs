using System.Globalization;
using System.Text;
using Iced.Intel;
using LegendaryExplorerCore.Packages;
using Decoder = Iced.Intel.Decoder;

namespace MeleMcp.Core;

public sealed record NativeSection(string Name, int Rva, int VirtualSize, int RawOffset, int RawSize, bool Executable);

/// <summary>The PE headers of a mapped image.</summary>
public static class PeImage
{
    public const int ImageBaseField = 24;
    public const int SizeOfImageField = 56;

    public static int OptionalHeader(byte[] image)
    {
        if (image.Length < 0x400 || image[0] != 'M' || image[1] != 'Z') throw new InvalidDataException("Not a PE image: no MZ header.");
        var pe = BitConverter.ToInt32(image, 0x3C);
        if (pe <= 0 || pe > image.Length - 0x200 || BitConverter.ToUInt32(image, pe) != 0x4550) throw new InvalidDataException("Not a PE image: no PE header.");
        if (BitConverter.ToUInt16(image, pe + 4) != 0x8664) throw new InvalidDataException("Not an x64 image.");
        return pe + 24;
    }

    /// <summary>
    /// A mapped image's bytes under headers that describe them: each section's raw offset becomes its RVA, so a PE loader finds .text and
    /// .pdata again. The certificate and debug directories hold file offsets of the original layout, so they go.
    /// </summary>
    public static byte[] ToFileLayout(byte[] mapped)
    {
        const int sectionAlignmentField = 32;
        const int fileAlignmentField = 36;
        const int directories = 112;
        const int certificateDirectory = 4;
        const int debugDirectory = 6;
        var image = (byte[])mapped.Clone();
        var optional = OptionalHeader(image);
        var alignment = BitConverter.ToInt32(image, optional + sectionAlignmentField);
        BitConverter.TryWriteBytes(image.AsSpan(optional + fileAlignmentField), alignment);
        image.AsSpan(optional + directories + certificateDirectory * 8, 8).Clear();
        image.AsSpan(optional + directories + debugDirectory * 8, 8).Clear();
        var fileHeader = optional - 20;
        var table = optional + BitConverter.ToUInt16(image, fileHeader + 16);
        for (var i = 0; i < BitConverter.ToUInt16(image, fileHeader + 2); i++)
        {
            var entry = table + i * 40;
            var size = BitConverter.ToInt32(image, entry + 8);
            BitConverter.TryWriteBytes(image.AsSpan(entry + 16), (size + alignment - 1) & ~(alignment - 1));
            BitConverter.TryWriteBytes(image.AsSpan(entry + 20), BitConverter.ToInt32(image, entry + 12));
        }
        return image;
    }

    /// <summary>Whether a file is a mapped dump: the bytes run to SizeOfImage, where an exe file stops at its last raw section.</summary>
    public static void RequireMapped(byte[] image, string path)
    {
        var sizeOfImage = BitConverter.ToInt32(image, OptionalHeader(image) + SizeOfImageField);
        if (sizeOfImage != image.Length)
        {
            throw new InvalidDataException($"{path} is {image.Length} bytes but its PE header says the image maps to {sizeOfImage}: this is an exe file, not a mapped dump. "
                                           + "Use exe_dump: the games' exes keep their code encrypted on disk.");
        }
    }

    public static IReadOnlyList<NativeSection> Sections(byte[] image)
    {
        var optional = OptionalHeader(image);
        var fileHeader = optional - 20;
        var count = BitConverter.ToUInt16(image, fileHeader + 2);
        var table = optional + BitConverter.ToUInt16(image, fileHeader + 16);
        var sections = new List<NativeSection>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = table + i * 40;
            var name = Encoding.ASCII.GetString(image, entry, 8).TrimEnd('\0');
            sections.Add(new NativeSection(name, BitConverter.ToInt32(image, entry + 12), BitConverter.ToInt32(image, entry + 8), BitConverter.ToInt32(image, entry + 20),
                                           BitConverter.ToInt32(image, entry + 16), (BitConverter.ToUInt32(image, entry + 36) & 0x20000000) != 0));
        }
        return sections;
    }
}

public sealed record DumpInfo(string Dump, string Base, string ImageSize, IReadOnlyList<DumpSectionInfo> Sections, int FunctionEntries, DumpSidecar? Sidecar);
public sealed record DumpSectionInfo(string Name, string Rva, string Size, bool Executable);
public sealed record DisasmResult(string Dump, string Start, string? Function, string? FunctionEnd, int Instructions, string Text);

/// <param name="Verified">False when the instruction was not reached decoding from a function's start: no .pdata entry there (a leaf), or a false hit.</param>
public sealed record CodeRef(string At, string Kind, string Instruction, string? Function, bool Verified);

/// <param name="Next">The qword after the pointer when it points into code: tables of {name, function} pairs hold the native behind a string there.</param>
public sealed record PointerRef(string At, string Section, string? Next);
public sealed record XrefResult(string Dump, string Target, string? TargetFunction, int CodeCount, IReadOnlyList<CodeRef> Code, int PointerCount, IReadOnlyList<PointerRef> Pointers);
public sealed record StringHit(string At, string Encoding, string Value, int CodeCount, IReadOnlyList<CodeRef> Code, int PointerCount, IReadOnlyList<PointerRef> Pointers);
public sealed record StringResult(string Dump, string Text, int Count, IReadOnlyList<StringHit> Hits);
public sealed record PatternHit(string At, string? Function);
public sealed record PatternResult(string Dump, string Pattern, string Section, int Count, IReadOnlyList<PatternHit> Hits);
public sealed record VtableSlot(int Slot, string Function, int? Size, string? FirstInstruction);

/// <param name="Slots">Slots up to the next vtable that code refers to, or to the end of the run of code pointers.</param>
/// <param name="RunSlots">Code pointers in a row from the vtable's start. Vtables have no RTTI between them, so a run can hold the next vtable too.</param>
public sealed record VtableResult(string Dump, string Vtable, string? Class, int Slots, int RunSlots, IReadOnlyList<VtableSlot> Entries);

/// <param name="Vtable">The nearest table start at or before the slot that code takes the address of, else <paramref name="RunStart"/>.</param>
/// <param name="RunStart">Where the run of code pointers around the slot begins; <paramref name="RunSlot"/> counts from there, as a walk back over the run does.</param>
public sealed record SlotHit(string Vtable, int Slot, string RunStart, int RunSlot, bool VtableReferenced);
public sealed record SlotOfResult(string Dump, string Function, int Count, IReadOnlyList<SlotHit> Hits);
public sealed record SecondaryVtable(int Offset, string Vtable);

/// <param name="Registrar">The function that builds the class object, found through the class name's literal.</param>
/// <param name="InstanceSize">The native object's size, from the registrar.</param>
/// <param name="Config">The class's config file name, from the registrar.</param>
public sealed record ClassResult(string Dump, string Class, string Literal, string Registrar, string InternalConstructor, string Constructor, string Vtable, int Slots, int RunSlots,
                                 string? InstanceSize, string? Config, IReadOnlyList<SecondaryVtable> SecondaryVtables);
public sealed record NameEntry(string Entry, string Name, IReadOnlyList<string> Globals);
public sealed record NameResult(string Dump, string? Global, int Count, IReadOnlyList<NameEntry> Entries, string? Note);

/// <summary>A dump held in memory: a few queries in a row should not read 30 MB each.</summary>
internal sealed class NativeImage
{
    private static readonly int[] TrailingImmediates = [0, 1, 2, 4];
    private static readonly object Gate = new();
    private static (string Path, DateTime Stamp, long Length, NativeImage Image)? last;

    public required string Path { get; init; }
    public required byte[] Data { get; init; }
    public required ulong Base { get; init; }
    public required IReadOnlyList<NativeSection> Sections { get; init; }
    public required (int Begin, int End, int Unwind)[] Functions { get; init; }
    public required DumpSidecar? Sidecar { get; init; }

    public static NativeImage Open(string path)
    {
        var full = System.IO.Path.GetFullPath(GamePaths.ToWindowsPath(path));
        var file = new FileInfo(full);
        if (!file.Exists) throw new FileNotFoundException($"No dump at {full}.");
        lock (Gate)
        {
            if (last is { } held && held.Path == full && held.Stamp == file.LastWriteTimeUtc && held.Length == file.Length) return held.Image;
            var image = Load(full);
            last = (full, file.LastWriteTimeUtc, file.Length, image);
            return image;
        }
    }

    private static NativeImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        PeImage.RequireMapped(data, path);
        var optional = PeImage.OptionalHeader(data);
        var sections = PeImage.Sections(data);
        var pdata = sections.FirstOrDefault(s => s.Name == ".pdata");
        var functions = new List<(int, int, int)>();
        if (pdata != null)
        {
            for (var at = pdata.Rva; at + 12 <= pdata.Rva + pdata.VirtualSize; at += 12)
            {
                var begin = BitConverter.ToInt32(data, at);
                if (begin != 0) functions.Add((begin, BitConverter.ToInt32(data, at + 4), BitConverter.ToInt32(data, at + 8)));
            }
        }
        functions.Sort();
        var sidecar = GameDump.SidecarOf(path);
        var imageBase = sidecar != null ? ParseHex(sidecar.BaseAddress) : BitConverter.ToUInt64(data, optional + PeImage.ImageBaseField);
        return new NativeImage { Path = path, Data = data, Base = imageBase, Sections = sections, Functions = functions.ToArray(), Sidecar = sidecar };
    }

    private static ulong ParseHex(string text) => ulong.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public NativeSection? SectionOf(long rva) => Sections.FirstOrDefault(s => s.Rva <= rva && rva < s.Rva + s.VirtualSize);

    public bool InCode(long rva) => SectionOf(rva)?.Executable == true;

    public ulong Qword(long rva) => rva >= 0 && rva + 8 <= Data.Length ? BitConverter.ToUInt64(Data, (int)rva) : 0;

    /// <summary>The RVA a pointer in the image points at, or -1 when it points outside the image.</summary>
    public long ToRva(ulong pointer) => pointer >= Base && pointer - Base < (ulong)Data.Length ? (long)(pointer - Base) : -1;

    /// <summary>The .pdata entry holding an RVA. Leaf functions have none.</summary>
    public (int Begin, int End, int Unwind)? FragmentOf(int rva)
    {
        int low = 0, high = Functions.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var f = Functions[middle];
            if (rva < f.Begin) high = middle - 1;
            else if (rva >= f.End) low = middle + 1;
            else return f;
        }
        return null;
    }

    /// <summary>Follows chained unwind info from a cold fragment to the function that owns it.</summary>
    public (int Begin, int End)? FunctionOf(int rva)
    {
        var fragment = FragmentOf(rva);
        for (var hops = 0; fragment is { } f && hops < 16; hops++)
        {
            if (f.Unwind <= 0 || f.Unwind + 4 > Data.Length) return (f.Begin, f.End);
            const int chainInfo = 4;
            if ((Data[f.Unwind] >> 3 & chainInfo) == 0) return (f.Begin, f.End);
            var codes = Data[f.Unwind + 2];
            var chained = f.Unwind + 4 + ((codes + 1) & ~1) * 2;
            fragment = (BitConverter.ToInt32(Data, chained), BitConverter.ToInt32(Data, chained + 4), BitConverter.ToInt32(Data, chained + 8));
        }
        return null;
    }

    /// <summary>A 64-bit decoder that reads <paramref name="length"/> bytes at an RVA, with the RVA as its instruction pointer.</summary>
    public Decoder DecoderAt(int rva, int length) =>
        Decoder.Create(64, new ByteArrayCodeReader(Data, rva, Math.Min(length, Data.Length - rva)), (ulong)rva);

    /// <summary>
    /// Positions in code whose rel32 points into [low, high]: the byte scan the scripts ran, here from one pass. The displacement is the
    /// last field of a branch or plain memory operand, but an immediate can follow it (`cmp dword ptr [rip+x], 0`), so those lengths count too.
    /// </summary>
    public IEnumerable<(int At, long Target)> Rel32Hits(long low, long high)
    {
        foreach (var section in Sections.Where(s => s.Executable))
        {
            var end = section.Rva + section.VirtualSize - 4;
            for (var at = section.Rva; at < end; at++)
            {
                long target = at + 4L + BitConverter.ToInt32(Data, at);
                if (target > high || target + 4 < low) continue;
                foreach (var extra in TrailingImmediates)
                {
                    if (target + extra >= low && target + extra <= high) yield return (at, target + extra);
                }
            }
        }
    }

    /// <summary>Addresses in data sections that hold a pointer to the given RVAs, aligned as the compiler lays them out.</summary>
    public List<(int At, int Target)> PointersTo(IReadOnlySet<int> targets)
    {
        var found = new List<(int, int)>();
        foreach (var section in Sections.Where(s => !s.Executable))
        {
            var end = section.Rva + section.VirtualSize - 8;
            for (var at = (section.Rva + 7) & ~7; at <= end; at += 8)
            {
                var rva = ToRva(Qword(at));
                if (rva >= 0 && targets.Contains((int)rva)) found.Add((at, (int)rva));
            }
        }
        return found;
    }

    /// <summary>
    /// <see cref="EntryText"/> for a pointer of unknown meaning. Entries sit end to end, so a NUL comes before one: that keeps a pointer to
    /// any other text from reading as a name. Only a chunk's first entry fails this.
    /// </summary>
    public string? NameAt(long entry) => entry >= 1 && entry < Data.Length && Data[entry - 1] == 0 ? EntryText(entry) : null;

    /// <summary>Text at an RVA if it reads as a printable literal, UTF-16 first: the engine's literals are wide.</summary>
    public string? Literal(long rva)
    {
        if (rva < 0 || rva + 8 >= Data.Length) return null;
        var at = (int)rva;
        var wide = new StringBuilder();
        for (var i = at; i + 1 < Data.Length && wide.Length < 80; i += 2)
        {
            if (Data[i + 1] != 0 || Data[i] < 0x20 || Data[i] > 0x7E) break;
            wide.Append((char)Data[i]);
        }
        if (wide.Length >= 3) return wide.ToString();
        var plain = new StringBuilder();
        for (var i = at; i < Data.Length && plain.Length < 80; i++)
        {
            if (Data[i] < 0x20 || Data[i] > 0x7E) break;
            plain.Append((char)Data[i]);
        }
        return plain.Length >= 4 ? plain.ToString() : null;
    }

    /// <summary>
    /// A name table entry: { int32 flags; int64 next in hash; char text[] }, the text at +0xC. The table's first chunk is in the image; the
    /// engine keeps pointers to entries in globals for the names it compares.
    /// </summary>
    public string? EntryText(long entry)
    {
        const int textOffset = 0xC;
        const uint ansiFlag = 0x80000000;
        const ulong userSpaceEnd = 0x800000000000;
        if (entry < 0 || entry + textOffset + 1 >= Data.Length) return null;
        if ((BitConverter.ToUInt32(Data, (int)entry) & ansiFlag) == 0) return null;
        if (Qword(entry + 4) >= userSpaceEnd) return null;
        var text = new StringBuilder();
        for (var i = (int)entry + textOffset; i < Data.Length && text.Length < 256; i++)
        {
            var b = Data[i];
            if (b == 0) return text.Length > 0 ? text.ToString() : null;
            if (b < 0x20 || b > 0x7E) return null;
            text.Append((char)b);
        }
        return null;
    }
}

/// <summary>
/// Queries on a mapped image dump: what the research scripts redid each time (disassembly, callers, string and data references, byte
/// patterns, the walk from a class name to its vtable, vtable slots, name entries). Decompiled views and call graphs stay with REA.
/// </summary>
public static class NativeQuery
{
    private const int MaxInstruction = 15;
    private const int MaxWalkBytes = 0x4000;
    private const int MaxWalkDepth = 3;
    private const int MaxRunSlots = 2000;
    private const int MaxDisasm = 4000;

    private static readonly Formatter Syntax = NewFormatter();

    public static object Run(MEGame game, string? dump, string op, string? rva, string? text, string? className, string? pattern, string? section, int count, bool whole, int limit, int first)
    {
        var image = NativeImage.Open(dump ?? GameDump.CachedImage(game));
        return op.Trim().ToLowerInvariant() switch
        {
            "info" => Info(image),
            "disasm" => Disasm(image, Rva(image, rva), count, whole),
            "callers" => Xrefs(image, Rva(image, rva), limit, branchesOnly: true),
            "xrefs" => Xrefs(image, Rva(image, rva), limit, branchesOnly: false),
            "strings" => Strings(image, Required(text, "text"), limit),
            "pattern" => Pattern(image, Required(pattern, "pattern"), section, limit),
            "class" => Class(image, Required(className, "className")),
            "vtable" => Vtable(image, rva, className, first, limit),
            "slot_of" => SlotOf(image, Rva(image, rva), limit),
            "name" => Name(image, rva, text),
            _ => throw new ArgumentException($"Unknown op '{op}'. Use info, disasm, callers, xrefs, strings, pattern, class, vtable, slot_of or name."),
        };
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException($"This op needs {name}.") : value;

    /// <summary>A 0x-prefixed RVA or address: hex and decimal look alike in a bare number, and the docs write these in hex.</summary>
    private static int Rva(NativeImage image, string? text)
    {
        var value = Required(text, "rva").Trim();
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException($"'{value}' is not an RVA: write it as 0x24afc0.");
        }
        if (number >= image.Base) number -= image.Base;
        return number < (ulong)image.Data.Length ? (int)number : throw new ArgumentException($"{value} is outside the image (size 0x{image.Data.Length:x}, base 0x{image.Base:x}).");
    }

    private static string Hex(long value) => $"0x{value:x}";

    private static Formatter NewFormatter()
    {
        var formatter = new IntelFormatter();
        var options = formatter.Options;
        options.HexPrefix = "0x";
        options.HexSuffix = "";
        options.UppercaseHex = false;
        options.LeadingZeros = false;
        options.BranchLeadingZeros = false;
        options.DisplacementLeadingZeros = false;
        options.RipRelativeAddresses = false;
        options.SmallHexNumbersInDecimal = false;
        options.SpaceAfterOperandSeparator = true;
        options.SpaceBetweenMemoryAddOperators = true;
        options.ShowBranchSize = false;
        return formatter;
    }

    private static string Format(in Instruction instruction)
    {
        var output = new StringOutput();
        lock (Syntax) Syntax.Format(instruction, output);
        return output.ToStringAndReset();
    }

    private static DumpInfo Info(NativeImage image) => new(
        image.Path, Hex((long)image.Base), Hex(image.Data.Length),
        image.Sections.Select(s => new DumpSectionInfo(s.Name, Hex(s.Rva), Hex(s.VirtualSize), s.Executable)).ToList(),
        image.Functions.Length, image.Sidecar);

    private static DisasmResult Disasm(NativeImage image, int rva, int count, bool whole)
    {
        var function = image.FunctionOf(rva);
        var fragment = whole ? image.FragmentOf(rva) : null;
        var start = fragment?.Begin ?? rva;
        var end = fragment?.End ?? image.Data.Length;
        var most = fragment != null ? MaxDisasm : Math.Clamp(count, 1, MaxDisasm);
        var decoder = image.DecoderAt(start, end - start);
        var lines = new StringBuilder();
        var written = 0;
        while (written < most && decoder.IP < (ulong)end)
        {
            decoder.Decode(out var i);
            lines.Append($"{i.IP:x8}  {(i.IsInvalid ? "(bad)" : Format(i))}");
            if (Note(image, i) is { } note) lines.Append("  ; ").Append(note);
            lines.Append('\n');
            written++;
        }
        return new DisasmResult(image.Path, Hex(start), function is { } h ? Hex(h.Begin) : null, function is { } j ? Hex(j.End) : null, written, lines.ToString());
    }

    /// <summary>What a rip-relative operand points at, when it reads as a literal or a global holding a name entry.</summary>
    private static string? Note(NativeImage image, in Instruction i)
    {
        if (!i.IsIPRelativeMemoryOperand) return null;
        var target = (long)i.IPRelativeMemoryAddress;
        if (image.SectionOf(target) is not { Executable: false }) return null;
        if (target + 8 <= image.Data.Length && image.NameAt(image.ToRva(image.Qword((int)target))) is { } name) return $"name {name}";
        return image.Literal(target) is { } literal ? $"\"{literal}\"" : null;
    }

    private static XrefResult Xrefs(NativeImage image, int target, int limit, bool branchesOnly)
    {
        var (count, code) = CodeRefs(image, new HashSet<int> { target }, limit, branchesOnly)[target];
        var pointers = Pointers(image, new HashSet<int> { target }, limit)[target];
        return new XrefResult(image.Path, Hex(target), image.FunctionOf(target) is { } f ? Hex(f.Begin) : null, count, code, pointers.Count, pointers.Refs);
    }

    /// <summary>For each target, the instructions that reach it: the count, and details of the first <paramref name="limit"/>.</summary>
    private static Dictionary<int, (int Count, List<CodeRef> Refs)> CodeRefs(NativeImage image, HashSet<int> targets, int limit, bool branchesOnly)
    {
        var result = targets.ToDictionary(t => t, _ => (Count: 0, Refs: new List<CodeRef>()));
        foreach (var (at, wide) in image.Rel32Hits(targets.Min(), targets.Max()))
        {
            if (wide > int.MaxValue || !targets.Contains((int)wide)) continue;
            var target = (int)wide;
            if (FindInstruction(image, at, target) is not { } instruction) continue;
            if (branchesOnly && !IsBranch(instruction)) continue;
            var entry = result[target];
            entry.Count++;
            if (entry.Refs.Count < limit)
            {
                var function = image.FunctionOf(at);
                var verified = Reached(image, at, target, out var exact);
                entry.Refs.Add(new CodeRef(Hex((long)(verified ? exact : instruction).IP), (verified ? exact : instruction).Mnemonic.ToString().ToLowerInvariant(),
                                           Format(verified ? exact : instruction), function is { } f ? Hex(f.Begin) : null, verified));
            }
            result[target] = entry;
        }
        return result;
    }

    private static bool IsBranch(in Instruction i) =>
        i.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch;

    /// <summary>
    /// The instruction whose rel32 sits at <paramref name="at"/> and reaches <paramref name="target"/>, tried from the longest encoding down:
    /// the shorter tries can drop a REX prefix and read as a different instruction. Null for bytes that only look like a reference.
    /// </summary>
    private static Instruction? FindInstruction(NativeImage image, int at, int target)
    {
        for (var start = Math.Max(0, at - MaxInstruction + 4); start < at; start++)
        {
            var decoder = image.DecoderAt(start, MaxInstruction);
            decoder.Decode(out var i);
            if (Reaches(decoder, i, start, at, target)) return i;
        }
        return null;
    }

    private static bool Reaches(Decoder decoder, in Instruction i, int start, int at, int target)
    {
        if (i.IsInvalid) return false;
        var offsets = decoder.GetConstantOffsets(i);
        if (i.IsIPRelativeMemoryOperand) return (long)i.IPRelativeMemoryAddress == target && offsets.HasDisplacement && start + offsets.DisplacementOffset == at;
        return IsBranch(i) && i.Op0Kind is OpKind.NearBranch64 && (long)i.NearBranch64 == target && (int)i.NextIP == at + 4;
    }

    /// <summary>Decodes the function holding <paramref name="at"/> from its start, which proves the bytes are an instruction there.</summary>
    private static bool Reached(NativeImage image, int at, int target, out Instruction found)
    {
        found = default;
        if (image.FragmentOf(at) is not { } fragment) return false;
        var decoder = image.DecoderAt(fragment.Begin, fragment.End - fragment.Begin);
        while (decoder.IP < (ulong)fragment.End)
        {
            var start = (int)decoder.IP;
            decoder.Decode(out var i);
            if (i.IsInvalid) return false;
            if ((int)i.NextIP > at)
            {
                if (start > at || !Reaches(decoder, i, start, at, target)) return false;
                found = i;
                return true;
            }
        }
        return false;
    }

    private static Dictionary<int, (int Count, List<PointerRef> Refs)> Pointers(NativeImage image, HashSet<int> targets, int limit)
    {
        var result = targets.ToDictionary(t => t, _ => (Count: 0, Refs: new List<PointerRef>()));
        foreach (var (at, target) in image.PointersTo(targets))
        {
            var entry = result[target];
            entry.Count++;
            if (entry.Refs.Count < limit)
            {
                var next = at + 16 <= image.Data.Length ? image.ToRva(image.Qword(at + 8)) : -1;
                entry.Refs.Add(new PointerRef(Hex(at), image.SectionOf(at)?.Name ?? "?", next >= 0 && image.InCode(next) ? Hex(next) : null));
            }
            result[target] = entry;
        }
        return result;
    }

    private static StringResult Strings(NativeImage image, string text, int limit)
    {
        var hits = new List<(int At, string Encoding, string Value, int Start)>();
        var total = 0;
        foreach (var (bytes, encoding, unit) in new[] { (Encoding.ASCII.GetBytes(text), "ascii", 1), (Encoding.Unicode.GetBytes(text), "utf-16", 2) })
        {
            foreach (var section in image.Sections.Where(s => !s.Executable))
            {
                var span = image.Data.AsSpan(section.Rva, Math.Min(section.VirtualSize, image.Data.Length - section.Rva));
                for (var offset = 0; offset < span.Length;)
                {
                    var found = span[offset..].IndexOf(bytes);
                    if (found < 0) break;
                    var at = section.Rva + offset + found;
                    offset += found + bytes.Length;
                    total++;
                    if (hits.Count < limit)
                    {
                        var start = LiteralStart(image, at, unit);
                        hits.Add((at, encoding, ReadLiteral(image, start, unit), start));
                    }
                }
            }
        }
        if (hits.Count == 0) return new StringResult(image.Path, text, 0, []);
        // A literal's address is taken at its start, or one character in: class names are written L"UName" + 1.
        var targets = hits.SelectMany(h => new[] { h.At, h.Start, h.Start + (h.Encoding == "ascii" ? 1 : 2) }).ToHashSet();
        var code = CodeRefs(image, targets, limit, branchesOnly: false);
        var pointers = Pointers(image, targets, limit);
        var seen = new HashSet<int>();
        var list = new List<StringHit>();
        foreach (var hit in hits)
        {
            var mine = new[] { hit.Start, hit.Start + (hit.Encoding == "ascii" ? 1 : 2), hit.At }.Distinct().Where(seen.Add).ToList();
            list.Add(new StringHit(Hex(hit.At), hit.Encoding, hit.Value, mine.Sum(t => code[t].Count), mine.SelectMany(t => code[t].Refs).Take(limit).ToList(),
                                   mine.Sum(t => pointers[t].Count), mine.SelectMany(t => pointers[t].Refs).Take(limit).ToList()));
        }
        return new StringResult(image.Path, text, total, list);
    }

    private static int LiteralStart(NativeImage image, int at, int unit)
    {
        bool Printable(int p) => image.Data[p] is >= 0x20 and <= 0x7E && (unit == 1 || image.Data[p + 1] == 0);
        var start = at;
        while (start - unit >= 0 && Printable(start - unit)) start -= unit;
        return start;
    }

    private static string ReadLiteral(NativeImage image, int start, int unit)
    {
        var text = new StringBuilder();
        for (var p = start; p + unit <= image.Data.Length && text.Length < 200; p += unit)
        {
            if (image.Data[p] == 0 || (unit == 2 && image.Data[p + 1] != 0)) break;
            text.Append((char)image.Data[p]);
        }
        return text.ToString();
    }

    private static PatternResult Pattern(NativeImage image, string pattern, string? section, int limit)
    {
        var (bytes, mask) = ParsePattern(pattern);
        var anchor = Array.IndexOf(mask, true);
        if (anchor < 0) throw new ArgumentException("A pattern needs one byte that is not a wildcard.");
        var sections = section == null ? image.Sections : image.Sections.Where(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sections.Count == 0) throw new ArgumentException($"No section '{section}'. This image has {string.Join(", ", image.Sections.Select(s => s.Name))}.");
        var count = 0;
        var hits = new List<PatternHit>();
        foreach (var s in sections)
        {
            var data = image.Data;
            var end = Math.Min(s.Rva + s.VirtualSize, data.Length) - bytes.Length;
            for (var at = s.Rva; at <= end; at++)
            {
                var next = Array.IndexOf(data, bytes[anchor], at + anchor, end + anchor - (at + anchor) + 1);
                if (next < 0) break;
                at = next - anchor;
                var same = true;
                for (var i = 0; i < bytes.Length && same; i++) same = !mask[i] || data[at + i] == bytes[i];
                if (!same) continue;
                count++;
                if (hits.Count < limit) hits.Add(new PatternHit(Hex(at), image.FunctionOf(at) is { } f ? Hex(f.Begin) : null));
            }
        }
        return new PatternResult(image.Path, pattern, section ?? "all", count, hits);
    }

    private static (byte[] Bytes, bool[] Mask) ParsePattern(string pattern)
    {
        var parts = pattern.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        var mask = new bool[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "?" or "??") continue;
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new ArgumentException($"'{parts[i]}' in the pattern is not a hex byte or ??. Write 33 D2 39 15 ?? ?? ?? ??.");
            }
            mask[i] = true;
        }
        return bytes.Length > 0 ? (bytes, mask) : throw new ArgumentException("The pattern is empty.");
    }

    private static ClassResult Class(NativeImage image, string name) => FindClass(image, name);

    /// <summary>
    /// No RTTI, so a class's vtable comes from its registration: the name's literal leads to the function that builds the class object,
    /// which stores the InternalConstructor at [rsp+0x48]; that function, or the constructor it jumps to, writes the vtable to the object.
    /// </summary>
    private static ClassResult FindClass(NativeImage image, string name)
    {
        var literals = new List<(int At, string Literal)>();
        var spellings = new[] { "U" + name, "A" + name }.Concat(name.Length > 1 && name[0] is 'U' or 'A' && char.IsUpper(name[1]) ? [name] : Array.Empty<string>());
        foreach (var spelling in spellings)
        {
            var bytes = Encoding.Unicode.GetBytes(spelling + "\0");
            foreach (var section in image.Sections.Where(s => !s.Executable))
            {
                var span = image.Data.AsSpan(section.Rva, Math.Min(section.VirtualSize, image.Data.Length - section.Rva));
                for (var offset = 0; offset < span.Length;)
                {
                    var found = span[offset..].IndexOf(bytes);
                    if (found < 0) break;
                    literals.Add((section.Rva + offset + found, spelling));
                    offset += found + 2;
                }
            }
        }
        if (literals.Count == 0) throw new InvalidOperationException($"No wide literal U{name} or A{name} in {image.Path}: not a native class of this game.");

        var targets = literals.Select(l => l.At + 2).ToHashSet();
        var refs = CodeRefs(image, targets, 32, branchesOnly: false);
        foreach (var (at, literal) in literals)
        {
            foreach (var reference in refs[at + 2].Refs)
            {
                var registrar = image.FunctionOf(Convert.ToInt32(reference.At, 16));
                if (registrar is not { } r || ReadRegistrar(image, r.Begin, r.End, Convert.ToInt32(reference.At, 16)) is not { } facts) continue;
                if (VtableWrites(image, facts.InternalConstructor, 0) is not { } walk) continue;
                var primary = walk.Writes.LastOrDefault(w => w.Offset == 0);
                if (primary.Vtable == 0) continue;
                var (slots, run) = SlotCount(image, primary.Vtable);
                return new ClassResult(image.Path, name, literal, Hex(r.Begin), Hex(facts.InternalConstructor), Hex(walk.Function), Hex(primary.Vtable), slots, run,
                                       facts.Size is { } size ? Hex(size) : null, facts.Config,
                                       walk.Writes.Where(w => w.Offset != 0).Select(w => new SecondaryVtable(w.Offset, Hex(w.Vtable))).ToList());
            }
        }
        throw new InvalidOperationException($"Found {name}'s literal at {string.Join(", ", literals.Select(l => Hex(l.At)))}, but no reference to it led to a registrar and a vtable "
                                            + $"({refs.Values.Sum(v => v.Count)} references). Try strings on the name, then disasm.");
    }

    /// <summary>
    /// The arguments of the class object's constructor call that takes the name at <paramref name="literalAt"/>. One registrar can build a
    /// whole package's classes in a row, so only the stores just before that literal, and the size just after it, are this class's.
    /// </summary>
    private static (int InternalConstructor, long? Size, string? Config)? ReadRegistrar(NativeImage image, int begin, int end, int literalAt)
    {
        const int internalConstructorSlot = 0x48;
        const int configSlot = 0x38;
        var loaded = new Dictionary<Register, int>();
        int? constructor = null;
        long? size = null;
        string? config = null;
        var decoder = image.DecoderAt(begin, end - begin);
        while (decoder.IP < (ulong)end)
        {
            decoder.Decode(out var i);
            var past = (int)i.IP >= literalAt;
            if (past && i.FlowControl == FlowControl.Call) break;
            if (i.Mnemonic == Mnemonic.Lea && i.IsIPRelativeMemoryOperand)
            {
                loaded[i.Op0Register.GetFullRegister()] = (int)i.IPRelativeMemoryAddress;
            }
            else if (!past && i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase == Register.RSP && i.Op1Kind == OpKind.Register
                     && loaded.TryGetValue(i.Op1Register, out var value))
            {
                if (i.MemoryDisplacement64 == internalConstructorSlot) constructor = value;
                else if (i.MemoryDisplacement64 == configSlot) config = image.Literal(value);
            }
            else if (past && i.Mnemonic == Mnemonic.Mov && i.Op0Register == Register.R8D && i.Op1Kind == OpKind.Immediate32)
            {
                size = i.Immediate32;
            }
        }
        return constructor is { } found ? (found, size, config) : null;
    }

    /// <summary>
    /// Follows a constructor to the writes of `lea reg, [vtable]; mov [this+offset], reg`. Constructors run the parent's first, so in one body
    /// the last write to an offset is the class's own. A body with no write hands on to the jump or call it holds.
    /// </summary>
    private static (int Function, List<(int Offset, int Vtable)> Writes)? VtableWrites(NativeImage image, int start, int depth)
    {
        var loaded = new Dictionary<Register, int>();
        var self = new HashSet<Register> { Register.RCX };
        var writes = new List<(int Offset, int Vtable)>();
        var onward = new List<int>();
        var decoder = image.DecoderAt(start, MaxWalkBytes);
        while (decoder.IP < (ulong)(start + MaxWalkBytes))
        {
            decoder.Decode(out var i);
            if (i.IsInvalid || i.Mnemonic == Mnemonic.Int3 || i.FlowControl == FlowControl.Return) break;
            if (IsBranch(i) && i.Op0Kind is OpKind.NearBranch64) onward.Add((int)i.NearBranch64);
            if (i.FlowControl == FlowControl.Call)
            {
                loaded.Clear();
                self.ExceptWith(new[] { Register.RCX, Register.RDX, Register.R8, Register.R9, Register.RAX, Register.R10, Register.R11 });
            }
            else if (i.Mnemonic == Mnemonic.Lea && i.IsIPRelativeMemoryOperand)
            {
                loaded[i.Op0Register.GetFullRegister()] = (int)i.IPRelativeMemoryAddress;
            }
            else if (i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register && self.Contains(i.Op1Register))
            {
                self.Add(i.Op0Register);
            }
            else if (i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.Op1Kind == OpKind.Register && i.MemoryIndex == Register.None && self.Contains(i.MemoryBase)
                     && loaded.TryGetValue(i.Op1Register, out var table) && image.SectionOf(table) is { Executable: false } && image.InCode(image.ToRva(image.Qword(table))))
            {
                writes.Add(((int)i.MemoryDisplacement64, table));
            }
            if (i.FlowControl == FlowControl.UnconditionalBranch) break;
        }
        if (writes.Count > 0) return (start, writes);
        if (depth >= MaxWalkDepth) return null;
        foreach (var next in onward)
        {
            if (VtableWrites(image, next, depth + 1) is { } found) return found;
        }
        return null;
    }

    /// <summary>The slots of the vtable at <paramref name="vtable"/>, and the whole run of code pointers it starts.</summary>
    private static (int Slots, int Run) SlotCount(NativeImage image, int vtable)
    {
        var run = RunLength(image, vtable);
        var next = NextTable(image, vtable, run);
        return (next ?? run, run);
    }

    private static int RunLength(NativeImage image, int vtable)
    {
        var run = 0;
        while (run < MaxRunSlots && vtable + (run + 1) * 8 <= image.Data.Length && image.InCode(image.ToRva(image.Qword(vtable + run * 8)))) run++;
        return run;
    }

    /// <summary>The first slot past the start that code takes the address of: there another vtable begins.</summary>
    private static int? NextTable(NativeImage image, int vtable, int run)
    {
        int? next = null;
        foreach (var (at, target) in image.Rel32Hits(vtable + 8, vtable + (run - 1L) * 8))
        {
            if (target % 8 != vtable % 8 || FindInstruction(image, at, (int)target) == null) continue;
            var slot = (int)(target - vtable) / 8;
            if (next == null || slot < next) next = slot;
        }
        return next;
    }

    private static VtableResult Vtable(NativeImage image, string? rva, string? className, int first, int limit)
    {
        var known = className != null ? FindClass(image, className) : null;
        var vtable = known != null ? Convert.ToInt32(known.Vtable, 16) : Rva(image, rva);
        var (slots, run) = SlotCount(image, vtable);
        if (run == 0) throw new ArgumentException($"{Hex(vtable)} does not hold a pointer into code: not a vtable.");
        var entries = new List<VtableSlot>();
        for (var slot = Math.Max(first, 0); slot < slots && entries.Count < limit; slot++)
        {
            var function = (int)image.ToRva(image.Qword(vtable + slot * 8));
            var decoder = image.DecoderAt(function, MaxInstruction);
            decoder.Decode(out var i);
            entries.Add(new VtableSlot(slot, Hex(function), image.FragmentOf(function) is { } f ? f.End - f.Begin : null, i.IsInvalid ? null : Format(i)));
        }
        return new VtableResult(image.Path, Hex(vtable), className, slots, run, entries);
    }

    private static SlotOfResult SlotOf(NativeImage image, int function, int limit)
    {
        // A shared stub can sit in thousands of slots, so the tables' references come from one pass over the code.
        var slots = image.PointersTo(new HashSet<int> { function }).Select(p => p.At).ToList();
        var runs = slots.Select(at =>
        {
            var start = at;
            while (start - 8 >= 0 && image.InCode(image.ToRva(image.Qword(start - 8)))) start -= 8;
            return (At: at, Start: start);
        }).ToList();
        var referenced = new SortedSet<int>();
        if (runs.Count > 0)
        {
            foreach (var (code, target) in image.Rel32Hits(runs.Min(r => r.Start), runs.Max(r => r.At)))
            {
                if (target % 8 == 0 && !referenced.Contains((int)target) && FindInstruction(image, code, (int)target) != null) referenced.Add((int)target);
            }
        }
        var hits = runs.Take(limit).Select(run =>
        {
            var tables = referenced.GetViewBetween(run.Start, run.At);
            var table = tables.Count > 0 ? tables.Max : run.Start;
            return new SlotHit(Hex(table), (run.At - table) / 8, Hex(run.Start), (run.At - run.Start) / 8, tables.Count > 0);
        }).ToList();
        return new SlotOfResult(image.Path, Hex(function), slots.Count, hits);
    }

    private static NameResult Name(NativeImage image, string? rva, string? text)
    {
        if (rva != null)
        {
            var global = Rva(image, rva);
            var pointer = image.Qword(global);
            var entry = image.ToRva(pointer);
            if (image.EntryText(entry) is { } name)
            {
                return new NameResult(image.Path, Hex(global), 1, [new NameEntry(Hex(entry), name, [Hex(global)])], null);
            }
            return new NameResult(image.Path, Hex(global), 0, [],
                                  $"The qword there is 0x{pointer:x}: no name entry in the image. Later names live in heap chunks, which a dump of the image does not hold.");
        }
        var wanted = Required(text, "text or rva");
        var bytes = Encoding.ASCII.GetBytes(wanted + "\0");
        var entries = new List<int>();
        foreach (var section in image.Sections.Where(s => !s.Executable))
        {
            var span = image.Data.AsSpan(section.Rva, Math.Min(section.VirtualSize, image.Data.Length - section.Rva));
            for (var offset = 0; offset < span.Length;)
            {
                var found = span[offset..].IndexOf(bytes);
                if (found < 0) break;
                var entry = section.Rva + offset + found - 0xC;
                offset += found + 1;
                if (image.EntryText(entry) == wanted) entries.Add(entry);
            }
        }
        var holders = entries.Count > 0 ? image.PointersTo(entries.ToHashSet()) : [];
        return new NameResult(image.Path, null, entries.Count,
                              entries.Select(e => new NameEntry(Hex(e), wanted, holders.Where(h => h.Target == e).Select(h => Hex(h.At)).ToList())).ToList(),
                              entries.Count == 0 ? "No name entry in the image: the name may live in a heap chunk, which a dump of the image does not hold." : null);
    }
}
