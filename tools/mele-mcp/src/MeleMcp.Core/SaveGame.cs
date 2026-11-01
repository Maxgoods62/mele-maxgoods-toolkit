using System.Globalization;
using System.Text;
using LegendaryExplorerCore.Coalesced;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <summary>
/// One career save of LE2 (format 30) or LE3 (format 59), read field by field from the first byte to the checksum. A file
/// that does not end exactly on its checksum is refused, so an edit never starts from a misread layout. Nothing in a save
/// holds a file offset: a field can change size, the checksum is the only thing to redo. Layout: docs/formats.md §7.
/// </summary>
public sealed class SaveGame
{
    private const int Le2Format = 30, Le3Format = 59, ChecksumSize = 4, GuidSize = 16;
    private static readonly string[] Le2Difficulties = ["Casual", "Normal", "Veteran", "Hardcore", "Insanity"];
    private static readonly string[] Le3Difficulties = ["Narrative", "Casual", "Normal", "Hardcore", "Insanity"];

    private readonly byte[] bytes;
    private readonly MEGame game;
    private readonly int end;
    private readonly Dictionary<string, (int Offset, bool IsFloat)> fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> mappedInts = [], mappedFloats = [];
    private readonly List<string> dlc = [];
    private int pos, headFlagAt, headEnd;
    private int plotBoolsAt, plotBoolWords, plotIntsAt, plotIntCount, plotFloatsAt, plotFloatCount;
    private int intMapCountAt, intMapEnd, floatMapCountAt, floatMapEnd;

    private SaveGame(byte[] bytes, MEGame game)
    {
        this.bytes = bytes;
        this.game = game;
        end = bytes.Length - ChecksumSize;
        Read();
        if (pos != end) throw new InvalidDataException($"The save's fields end at byte {pos}, its checksum sits at {end}.");
    }

    public static SaveGame Parse(byte[] bytes, MEGame game)
    {
        if (bytes.Length < 8) throw new InvalidDataException($"{bytes.Length} bytes is no save.");
        var stored = BitConverter.ToUInt32(bytes, bytes.Length - ChecksumSize);
        var computed = Crc32.Compute(bytes, 0, bytes.Length - ChecksumSize);
        if (stored != computed) throw new InvalidDataException($"Checksum {stored:X8} in the file, {computed:X8} computed.");
        return new SaveGame(bytes, game);
    }

    private bool Le3 => game == MEGame.LE3;

    public byte[] Bytes => bytes;
    public int Format { get; private set; }
    public double SecondsPlayed { get; private set; }
    public string Map { get; private set; } = "";
    public string Difficulty { get; private set; } = "";
    public string Saved { get; private set; } = "";
    public bool IsFemale { get; private set; }
    public string ClassName { get; private set; } = "";
    public string FirstName { get; private set; } = "";
    public string FaceCode { get; private set; } = "";
    public SaveHead Head { get; private set; } = new("default", null, 0, 0, 0);
    public IReadOnlyList<string> Dlc => dlc;
    public int PlotBoolCount => plotBoolWords * 32;
    public int PlotIntCount => Le3 ? mappedInts.Count : plotIntCount;
    public int PlotFloatCount => Le3 ? mappedFloats.Count : plotFloatCount;
    public IEnumerable<string> FieldNames => fields.Keys;

    /// <summary>A field by name, or a plot value (bool:id, int:id, float:id). Null: LE3 holds no value for this id, the game reads 0.</summary>
    public string? Get(string name)
    {
        if (fields.TryGetValue(name, out var field)) return ReadAt(field.Offset, field.IsFloat);
        var (kind, id) = PlotName(name);
        switch (kind)
        {
            case "bool":
                return ((BitConverter.ToUInt32(bytes, BoolWordAt(id)) >> (id % 32)) & 1) == 1 ? "True" : "False";
            case "int":
                if (Le3) return mappedInts.TryGetValue(id, out var intAt) ? ReadAt(intAt, false) : null;
                return ReadAt(ListSlot(id, plotIntsAt, plotIntCount, "int"), false);
            default:
                if (Le3) return mappedFloats.TryGetValue(id, out var floatAt) ? ReadAt(floatAt, true) : null;
                return ReadAt(ListSlot(id, plotFloatsAt, plotFloatCount, "float"), true);
        }
    }

    /// <summary>The save with this value set. LE3 keeps int and float plot values as id/value pairs, so a new id adds a pair.</summary>
    public SaveGame With(string name, string value)
    {
        if (fields.TryGetValue(name, out var field)) return Overwritten(field.Offset, Encode(value, field.IsFloat));
        var (kind, id) = PlotName(name);
        switch (kind)
        {
            case "bool":
                var wordAt = BoolWordAt(id);
                var word = BitConverter.ToUInt32(bytes, wordAt);
                var mask = 1u << (id % 32);
                return Overwritten(wordAt, BitConverter.GetBytes(ParseBool(value) ? word | mask : word & ~mask));
            case "int":
                if (!Le3) return Overwritten(ListSlot(id, plotIntsAt, plotIntCount, "int"), Encode(value, false));
                return mappedInts.TryGetValue(id, out var intAt) ? Overwritten(intAt, Encode(value, false)) : Inserted(intMapCountAt, intMapEnd, id, Encode(value, false));
            default:
                if (!Le3) return Overwritten(ListSlot(id, plotFloatsAt, plotFloatCount, "float"), Encode(value, true));
                return mappedFloats.TryGetValue(id, out var floatAt) ? Overwritten(floatAt, Encode(value, true)) : Inserted(floatMapCountAt, floatMapEnd, id, Encode(value, true));
        }
    }

    /// <summary>The save with the head flag cleared and the morph block gone: LoadAppearance then leaves the pawn its default head.</summary>
    public SaveGame WithoutMorphHead()
    {
        if (Head.Kind != "morph") return this;
        var bodyStart = headFlagAt + sizeof(int);
        var output = new byte[bytes.Length - (headEnd - bodyStart)];
        bytes.AsSpan(0, headFlagAt).CopyTo(output);
        BitConverter.TryWriteBytes(output.AsSpan(headFlagAt), 0);
        bytes.AsSpan(headEnd).CopyTo(output.AsSpan(bodyStart));
        return Sealed(output);
    }

    private SaveGame Overwritten(int offset, byte[] value)
    {
        var output = (byte[])bytes.Clone();
        value.CopyTo(output, offset);
        return Sealed(output);
    }

    private SaveGame Inserted(int countAt, int at, int id, byte[] value)
    {
        var output = new byte[bytes.Length + sizeof(int) + value.Length];
        bytes.AsSpan(0, at).CopyTo(output);
        BitConverter.TryWriteBytes(output.AsSpan(at), id);
        value.CopyTo(output, at + sizeof(int));
        bytes.AsSpan(at).CopyTo(output.AsSpan(at + sizeof(int) + value.Length));
        BitConverter.TryWriteBytes(output.AsSpan(countAt), BitConverter.ToInt32(bytes, countAt) + 1);
        return Sealed(output);
    }

    private SaveGame Sealed(byte[] output)
    {
        BitConverter.TryWriteBytes(output.AsSpan(output.Length - ChecksumSize), Crc32.Compute(output, 0, output.Length - ChecksumSize));
        return Parse(output, game);
    }

    private (string Kind, int Id) PlotName(string name)
    {
        var parts = name.Split(':');
        if (parts.Length == 2 && parts[0].ToLowerInvariant() is "bool" or "int" or "float"
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return (parts[0].ToLowerInvariant(), id);
        }
        throw new ArgumentException($"'{name}' is no value: use bool:<id>, int:<id>, float:<id> or one of {string.Join(", ", fields.Keys)}.");
    }

    private int BoolWordAt(int id)
    {
        if (id / 32 >= plotBoolWords) throw new ArgumentException($"Plot bool {id} is past this save's table ({PlotBoolCount} bools).");
        return plotBoolsAt + id / 32 * sizeof(int);
    }

    private static int ListSlot(int id, int start, int count, string kind)
    {
        if (id >= count) throw new ArgumentException($"Plot {kind} {id} is past this save's table ({count} values).");
        return start + id * sizeof(int);
    }

    private string ReadAt(int offset, bool isFloat) => isFloat
        ? BitConverter.ToSingle(bytes, offset).ToString("R", CultureInfo.InvariantCulture)
        : BitConverter.ToInt32(bytes, offset).ToString(CultureInfo.InvariantCulture);

    private static byte[] Encode(string value, bool isFloat)
    {
        if (isFloat)
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return BitConverter.GetBytes(f);
        }
        else if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
        {
            return BitConverter.GetBytes(i);
        }
        throw new ArgumentException($"'{value}' is no {(isFloat ? "number" : "whole number")}.");
    }

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => throw new ArgumentException($"'{value}' is no bool: use True, False, 1 or 0."),
    };

    private void Read()
    {
        Format = Int();
        var expected = game switch
        {
            MEGame.LE2 => Le2Format,
            MEGame.LE3 => Le3Format,
            _ => throw new ArgumentException($"{game} saves are not supported."),
        };
        if (Format != expected) throw new InvalidDataException($"Save format {Format}: {game} saves are format {expected}.");
        Str(); // debug name
        SecondsPlayed = Single();
        Skip(sizeof(int)); // disc
        Map = Str();
        if (Le3) Str(); // map name override
        var difficulties = Le3 ? Le3Difficulties : Le2Difficulties;
        var difficulty = Byte();
        Difficulty = difficulty < difficulties.Length ? difficulties[difficulty] : difficulty.ToString(CultureInfo.InvariantCulture);
        Skip(sizeof(int)); // end game state
        var (seconds, day, month, year) = (Int(), Int(), Int(), Int());
        Saved = $"{year:D4}-{month:D2}-{day:D2} {seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}";
        Skip(3 * sizeof(float) + 3 * sizeof(int) + sizeof(int)); // location, rotation, loading tip
        Each(() => { Str(); Bool(); Bool(); }); // levels
        Each(() => { Str(); Bool(); }); // streaming states
        Each(() => { Skip(GuidSize); Bool(); }); // Kismet records
        Each(() => Skip(GuidSize + 2)); // doors
        if (Le3) Each(() => Skip(GuidSize + 2)); // placeables
        Each(() => Skip(GuidSize)); // pawns
        ReadPlayer();
        ReadSquad();
        ReadPlot();
        ReadJournal();
        Ints(); // ME1 plot table: bools, ints, floats
        Ints();
        Ints();
        if (Le3) Each(() => { Str(); Skip(sizeof(int)); }); // player variables
        ReadGalaxy();
        Each(() => { Skip(sizeof(int)); dlc.Add(Str()); if (Le3) Str(); });
        if (!Le3) return;
        Each(() => { Str(); Skip(2 * sizeof(int)); Each(() => Str()); }); // treasures
        Each(() => Skip(GuidSize)); // use modules
        Skip(1); // conversation mode
        Each(() => { Str(); Skip(3 * sizeof(float) + sizeof(int)); Str(); Skip(1); }); // objective markers
        Skip(sizeof(int)); // saved objective text
    }

    private void ReadPlayer()
    {
        IsFemale = Bool();
        ClassName = Str();
        if (Le3) { Bool(); Bool(); Bool(); } // combat, injured, casual
        Field("level");
        FloatField("xp");
        FirstName = Str();
        Skip(sizeof(int) + 2); // last name string, origin, notoriety
        Field("talentPoints");
        Str(); Str(); Str(); // mapped powers
        Skip(1 + 13 * sizeof(int)); // combat appearance, then part, tint, pattern and helmet ids
        ReadHead();
        if (Le3)
        {
            Skip(sizeof(int)); // emissive id
            Each(Le3Power);
            Each(() => Skip(2 * sizeof(int))); // war assets
            Each(Le3Weapon);
            Each(Le3WeaponMod);
            Strs(6 + 2); // loadout, primary and secondary weapon
            Ints(); // loadout weapon groups
            Each(() => { Str(); Str(); }); // hotkeys
            FloatField("health");
            ReadResources();
            FloatField("fuel");
            Field("grenades");
            FaceCode = Str();
            Skip(sizeof(int) + GuidSize); // class name string, character guid
        }
        else
        {
            Each(Le2Power);
            Each(() => { Str(); Skip(2 * sizeof(int)); Bool(); Bool(); Str(); }); // weapons
            Strs(6); // loadout
            Each(() => { Str(); Skip(sizeof(int)); }); // hotkeys
            ReadResources();
            FloatField("fuel");
            FaceCode = Str();
            Skip(sizeof(int)); // class name string
            Skip(2 * sizeof(int) + 5 * sizeof(float)); // ME1 import bonus
        }
    }

    private void ReadHead()
    {
        headFlagAt = pos;
        if (Bool())
        {
            var start = pos;
            var hair = Str();
            Each(() => Str()); // accessory meshes
            var features = Count();
            for (var i = 0; i < features; i++) { Str(); Skip(sizeof(float)); }
            Each(() => { Str(); Skip(3 * sizeof(float)); }); // offset bones
            var vertices = Count();
            Skip(checked(vertices * 3 * sizeof(float)));
            for (var lod = 1; lod < 4; lod++) Skip(checked(Count() * 3 * sizeof(float)));
            Each(() => { Str(); Skip(sizeof(float)); }); // scalar parameters
            Each(() => { Str(); Skip(4 * sizeof(float)); }); // vector parameters
            Each(() => { Str(); Str(); }); // texture parameters
            Head = new SaveHead("morph", hair, features, vertices, pos - start);
        }
        headEnd = pos;
    }

    private void ReadResources()
    {
        foreach (var name in new[] { "credits", "medigel", "eezo", "iridium", "palladium", "platinum", "probes" }) Field(name);
    }

    private void ReadSquad() => Each(() =>
    {
        Str(); // tag
        Each(Le3 ? Le3Power : Le2Power);
        Skip(2 * sizeof(int)); // level, talent points
        Strs(6 + 1); // loadout, mapped power
        if (!Le3) return;
        Each(Le3WeaponMod);
        Skip(sizeof(int)); // grenades
        Each(Le3Weapon);
    });

    private void ReadPlot()
    {
        plotBoolWords = Count();
        plotBoolsAt = pos;
        Skip(checked(plotBoolWords * sizeof(int)));
        if (Le3)
        {
            intMapCountAt = pos;
            Each(() => { var id = Int(); mappedInts[id] = pos; Skip(sizeof(int)); });
            intMapEnd = pos;
            floatMapCountAt = pos;
            Each(() => { var id = Int(); mappedFloats[id] = pos; Skip(sizeof(float)); });
            floatMapEnd = pos;
        }
        else
        {
            plotIntCount = Count();
            plotIntsAt = pos;
            Skip(checked(plotIntCount * sizeof(int)));
            plotFloatCount = Count();
            plotFloatsAt = pos;
            Skip(checked(plotFloatCount * sizeof(float)));
        }
    }

    private void ReadJournal()
    {
        Skip(sizeof(int)); // quest progress counter
        Each(() => { Skip(sizeof(int)); Bool(); if (Le3) Skip(sizeof(int)); Ints(); }); // quests
        Ints(); // quest ids
        Each(() => Each(() => { Skip(sizeof(int)); Bool(); })); // codex entries and their pages
        Ints(); // codex ids
    }

    private void ReadGalaxy()
    {
        Each(() =>
        {
            Skip(sizeof(int));
            Bool();
            Each(() => Skip(2 * sizeof(float))); // probes
            if (Le3) Bool(); // show as scanned
        });
        if (Le3) Each(() => { Skip(sizeof(int) + sizeof(float)); Bool(); }); // systems
    }

    private void Le2Power() { Str(); Skip(sizeof(float)); Str(); Skip(sizeof(int)); }

    private void Le3Power() { Str(); Skip(sizeof(float) + 6 * sizeof(int)); Str(); Skip(sizeof(int)); }

    private void Le3Weapon() { Str(); Skip(2 * sizeof(int)); Bool(); Bool(); Str(); Str(); }

    private void Le3WeaponMod() { Str(); Each(() => Str()); }

    private void Field(string name)
    {
        fields[name] = (pos, false);
        Skip(sizeof(int));
    }

    private void FloatField(string name)
    {
        fields[name] = (pos, true);
        Skip(sizeof(float));
    }

    private void Need(long count)
    {
        if (count < 0 || pos + count > end) throw new InvalidDataException($"Byte {pos}: {count} bytes do not fit before the checksum.");
    }

    private void Skip(int count)
    {
        Need(count);
        pos += count;
    }

    private byte Byte()
    {
        Need(1);
        return bytes[pos++];
    }

    private int Int()
    {
        Need(sizeof(int));
        var value = BitConverter.ToInt32(bytes, pos);
        pos += sizeof(int);
        return value;
    }

    private float Single()
    {
        Need(sizeof(float));
        var value = BitConverter.ToSingle(bytes, pos);
        pos += sizeof(float);
        return value;
    }

    private bool Bool()
    {
        var value = Int();
        return value is 0 or 1 ? value == 1 : throw new InvalidDataException($"Byte {pos - sizeof(int)}: {value} is no bool.");
    }

    /// <summary>A list's length. Every item takes at least a byte, so a misread count fails here.</summary>
    private int Count()
    {
        var count = Int();
        Need(count);
        return count;
    }

    /// <summary>Unreal string: a positive length is Latin-1, a negative one UTF-16, both counting their terminator.</summary>
    private string Str()
    {
        long length = Int();
        if (length == 0) return "";
        var size = length > 0 ? length : -length * 2;
        Need(size);
        var text = length > 0 ? Encoding.Latin1.GetString(bytes, pos, (int)size - 1) : Encoding.Unicode.GetString(bytes, pos, (int)size - 2);
        var terminated = bytes[pos + (int)size - 1] == 0 && (length > 0 || bytes[pos + (int)size - 2] == 0);
        if (!terminated) throw new InvalidDataException($"Byte {pos}: unterminated string.");
        pos += (int)size;
        return text;
    }

    private void Strs(int count)
    {
        for (var i = 0; i < count; i++) Str();
    }

    /// <summary>A list of 4-byte values.</summary>
    private void Ints() => Skip(checked(Count() * sizeof(int)));

    private void Each(Action item)
    {
        var count = Count();
        for (var i = 0; i < count; i++) item();
    }
}
