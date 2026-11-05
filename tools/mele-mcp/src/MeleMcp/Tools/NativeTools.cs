using System.ComponentModel;
using MeleMcp.Core;
using ModelContextProtocol.Server;

namespace MeleMcp.Tools;

/// <summary>Native code research on a dump of the running game: the exes keep their code encrypted on disk.</summary>
[McpServerToolType]
public static class NativeTools
{
    [McpServerTool(Name = "exe_dump", Destructive = false, Idempotent = true),
     Description("Copies the running game's mapped exe image, read-only, to a per-game cache: %LOCALAPPDATA%\\mele-mcp\\dumps\\<game>\\image.bin (MELE_DUMP_DIR replaces the folder), "
                 + "with image.json (exe SHA-256, size, base address, unreadable pages). The exes keep their code encrypted on disk, so native_query works from this copy. "
                 + "Nothing is read when the cache already holds the exe on disk (Skipped, the game need not run). Errors when the game is not running, or still has its code encrypted. "
                 + "Never writes in the game install, and refuses a MELE_DUMP_DIR inside it. The dump shows the code as patched in memory: installed ASI mods' hooks are in it. "
                 + "Dumps are game code: keep them out of any repo. "
                 + "Run on LE2, not on LE1 or LE3.")]
    public static ExeDumpResult ExeDump(
        [Description("Game: LE1, LE2 or LE3.")] string game,
        [Description("Read the game again even when the cached dump matches, e.g. to see hooks.")] bool force = false)
        => ToolErrors.Run(() => GameDump.Dump(PackageTools.ParseGame(game), force));

    [McpServerTool(Name = "exe_dump_pe", Destructive = false, Idempotent = true),
     Description("Writes a dump as a PE that REA can open (open_binary): each section's raw offset set to its RVA, certificate and debug directories removed. "
                 + "REA's Ghidra cannot load image.bin itself, whose headers describe the file layout. Tested on an LE2 dump: Ghidra's analysis took about 10 minutes, redone each session "
                 + "(REA snapshots keep no analysis database); function assembly, callers, xrefs and strings then matched native_query's facts. REA's addresses are Base plus an RVA. "
                 + "The output holds game code: keep it out of any repo. Refuses a target inside the game install, and any existing file it did not write.")]
    public static FileLayoutResult ExeDumpPe(
        [Description("Game whose cached dump to convert: LE1, LE2 or LE3.")] string game,
        [Description("An image.bin (Windows or /mnt/<drive> path) to convert instead of the cache.")] string? dump = null,
        [Description("Output file. Default: image-pe.exe next to the dump.")] string? outPath = null)
        => ToolErrors.Run(() => GameDump.WriteFileLayout(PackageTools.ParseGame(game), dump, outPath));

    [McpServerTool(Name = "native_query", ReadOnly = true),
     Description("Queries a mapped image dump (exe_dump's, or any image.bin through dump). Ops: "
                 + "info (base, sections, .pdata function count); "
                 + "disasm (rva, count; whole = the .pdata fragment; notes literals and name globals); "
                 + "callers (rva: calls and jumps to it, plus data pointers to it); "
                 + "xrefs (rva: every instruction that references it, plus data pointers); "
                 + "strings (text: ASCII and UTF-16 substring in data, with the code and pointers that reach each literal; Next on a pointer is the native in a {name, function} table); "
                 + "pattern (pattern '33 D2 39 15 ?? ?? ?? ??', section optional, returns Count and hits); "
                 + "class (className: native class name to vtable, through its registration, as exes have no RTTI); "
                 + "vtable (className or rva: slots, first and limit page them); "
                 + "slot_of (rva of a function: the vtables and slots that hold it); "
                 + "name (rva of a global holding a name entry, or text: the entry and the globals that hold it. Only names in the image: heap name chunks and GObjects are not in a dump). "
                 + "RVAs are written 0x24afc0; an address of the dump's run also works. "
                 + "Reproduced on LE1, LE2 and LE3 dumps: BioPlayerInput's vtable and InputKey slots, the movie input bytes and key names, the DOF gate. "
                 + "Answers take under a second and need no analysis; REA's Ghidra takes about 10 minutes per session on a dump. "
                 + "Use REA for pseudocode, cross-function comparison and annotation: give it the file from exe_dump_pe, its addresses are the dump's base plus an RVA.")]
    public static object NativeQueryTool(
        [Description("Game whose cached dump to read: LE1, LE2 or LE3.")] string game,
        [Description("info, disasm, callers, xrefs, strings, pattern, class, vtable, slot_of or name.")] string op,
        [Description("An image.bin (Windows or /mnt/<drive> path) to read instead of the cache.")] string? dump = null,
        [Description("0x-prefixed RVA, or an address of the dump's run.")] string? rva = null,
        [Description("strings: substring. name: exact name.")] string? text = null,
        [Description("class, vtable: native class name, e.g. BioPlayerInput.")] string? className = null,
        [Description("pattern: hex bytes, ?? for a wildcard.")] string? pattern = null,
        [Description("pattern: one section, e.g. .text. Default: all.")] string? section = null,
        [Description("disasm: instructions to decode.")] int count = 40,
        [Description("disasm: the whole .pdata fragment holding rva, instead of count.")] bool whole = false,
        [Description("Maximum items returned per list (counts still count all).")] int limit = 50,
        [Description("vtable: first slot listed.")] int first = 0)
        => ToolErrors.Run(() => NativeQuery.Run(PackageTools.ParseGame(game), dump, op, rva, text, className, pattern, section, count, whole, limit, first));
}
