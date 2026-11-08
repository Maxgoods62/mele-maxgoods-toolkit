using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <summary>Shepard's head in a save.</summary>
/// <param name="Kind">morph (a face of its own), or default (the game's head for Shepard's sex).</param>
/// <param name="Hair">The morph's hair mesh; "None": the hair is part of the head.</param>
/// <param name="Features">Morph features.</param>
/// <param name="Vertices">Lod 0 vertices.</param>
/// <param name="Bytes">The morph's size in the file.</param>
public sealed record SaveHead(string Kind, string? Hair, int Features, int Vertices, int Bytes);

/// <param name="Saved">The save's own time, not the file's.</param>
/// <param name="Head">default or morph.</param>
/// <param name="Error">Why the file can't be read; the other fields are then empty.</param>
public sealed record SaveRow(string File, long Size, string? Player, string? Sex, string? ClassName, double? HoursPlayed, string? Map, string? Saved, string? Head, string? Error);

/// <param name="BackupFiles">.bak files beside the saves (Trilogy Save Editor's); no save tool reads or writes them.</param>
public sealed record SaveCareer(string Career, int Saves, int BackupFiles, int DefaultHeads, int MorphHeads, int Unreadable, string? Player);

/// <param name="Careers">Every career of the game; empty when a career was asked for.</param>
/// <param name="Saves">The career's saves; empty when none was asked for.</param>
public sealed record SaveListResult(string Game, string Folder, IReadOnlyList<SaveCareer> Careers, IReadOnlyList<SaveRow> Saves);

/// <param name="Fields">Every value save_set can write by name.</param>
/// <param name="Values">The values asked for; null: LE3 holds no value for this id, the game reads 0.</param>
public sealed record SaveDetails(string File, string Game, int Format, long Size, string Player, string Sex, string ClassName, double HoursPlayed,
    string Map, string Difficulty, string Saved, string FaceCode, SaveHead Head, int PlotBools, int PlotInts, int PlotFloats,
    IReadOnlyDictionary<string, string?> Fields, IReadOnlyDictionary<string, string?>? Values, IReadOnlyList<string> Dlc);

/// <param name="Outcome">changed, would change, already default or failed.</param>
public sealed record SaveHeadRow(string File, string Outcome, long SizeBefore, long SizeAfter, string? Error);

/// <param name="BackupFolder">Holds the file as it was, for each save changed. Null when nothing was written.</param>
public sealed record SaveHeadResult(string Game, string Target, bool DryRun, int Files, int Changed, int AlreadyDefault, int Failed, string? BackupFolder,
    IReadOnlyList<SaveHeadRow> Rows, IReadOnlyList<string> Notes);

/// <param name="Name">A name from save_read's Fields, or bool:id, int:id, float:id.</param>
/// <param name="Value">True/False or 1/0 for a bool, a number otherwise.</param>
public sealed record SaveValue(string Name, string Value);

/// <param name="Was">The value before; null: LE3 held none.</param>
public sealed record SaveValueChange(string Name, string? Was, string? Now);

public sealed record SaveSetResult(string Game, string File, bool DryRun, IReadOnlyList<SaveValueChange> Changes, string? BackupPath, IReadOnlyList<string> Notes);

/// <summary>
/// Career saves of LE2 and LE3 in Documents\BioWare. An edit parses the result again, backs the file up, checks that the file
/// has not changed since it was read, moves the new bytes over it, keeps its file times, and reads it back. .bak files stay as they are.
/// </summary>
public static class SaveFiles
{
    private const string Extension = ".pcsav", BackupExtension = ".bak";

    public static string Root(MEGame game)
    {
        var folder = game switch
        {
            MEGame.LE2 => "ME2",
            MEGame.LE3 => "ME3",
            _ => throw new ArgumentException($"{game} saves have another layout: the save tools read LE2 and LE3."),
        };
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BioWare", "Mass Effect Legendary Edition", "Save", folder);
    }

    public static SaveListResult List(MEGame game, string? career)
    {
        var root = Root(game);
        if (career is not null)
        {
            var folder = Existing(game, career);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"{career} is no career folder.");
            return new SaveListResult(game.ToString(), folder, [], Saves(folder).Select(path => Row(game, path)).ToList());
        }
        var careers = Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase)
            .Select(folder => Summary(game, folder)).Where(c => c.Saves > 0).ToList();
        return new SaveListResult(game.ToString(), root, careers, []);
    }

    public static SaveDetails Read(MEGame game, string save, IReadOnlyList<string>? values)
    {
        var path = OneSave(game, save);
        var parsed = SaveGame.Parse(File.ReadAllBytes(path), game);
        return new SaveDetails(path, game.ToString(), parsed.Format, parsed.Bytes.Length, parsed.FirstName, parsed.IsFemale ? "female" : "male", parsed.ClassName,
            Hours(parsed), parsed.Map, parsed.Difficulty, parsed.Saved, parsed.FaceCode, parsed.Head, parsed.PlotBoolCount, parsed.PlotIntCount, parsed.PlotFloatCount,
            parsed.FieldNames.ToDictionary(n => n, parsed.Get), values?.Distinct().ToDictionary(n => n, parsed.Get), parsed.Dlc);
    }

    /// <summary>Gives one save, or every save of a folder, the game's default head.</summary>
    public static SaveHeadResult HeadDefault(MEGame game, string target, bool dryRun, bool allowRunning)
    {
        Root(game);
        var notes = new List<string>();
        if (!dryRun) GuardRunning(game, allowRunning, notes);
        var path = Existing(game, target);
        var isFolder = Directory.Exists(path);
        var paths = isFolder ? Saves(path) : [OneSave(game, target)];
        var backupFolder = BackupFolder(game, paths.FirstOrDefault() ?? path);
        var rows = new List<SaveHeadRow>();
        var written = false;
        foreach (var save in paths)
        {
            var name = Path.GetFileName(save);
            var size = new FileInfo(save).Length;
            try
            {
                var before = SaveGame.Parse(File.ReadAllBytes(save), game);
                if (before.Head.Kind != "morph")
                {
                    rows.Add(new SaveHeadRow(name, "already default", size, size, null));
                    continue;
                }
                var after = before.WithoutMorphHead();
                if (after.Head.Kind == "morph") throw new InvalidDataException("The head is still there after the edit.");
                if (!dryRun)
                {
                    Replace(game, save, before.Bytes, after.Bytes, backupFolder);
                    written = true;
                }
                rows.Add(new SaveHeadRow(name, dryRun ? "would change" : "changed", size, after.Bytes.Length, null));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
            {
                rows.Add(new SaveHeadRow(name, "failed", size, size, ex.Message));
            }
        }
        if (isFolder)
        {
            var kept = Directory.GetFiles(path, "*" + Extension + BackupExtension).Length;
            if (kept > 0) notes.Add($"{kept} .bak files beside the saves stay as they are, with their old heads.");
        }
        return new SaveHeadResult(game.ToString(), path, dryRun, rows.Count, rows.Count(r => r.Outcome is "changed" or "would change"),
            rows.Count(r => r.Outcome == "already default"), rows.Count(r => r.Outcome == "failed"), written ? backupFolder : null, rows, notes);
    }

    public static SaveSetResult Set(MEGame game, string save, IReadOnlyList<SaveValue> values, bool dryRun, bool allowRunning)
    {
        if (values.Count == 0) throw new ArgumentException("No values to set.");
        var notes = new List<string>();
        if (!dryRun) GuardRunning(game, allowRunning, notes);
        var path = OneSave(game, save);
        var before = SaveGame.Parse(File.ReadAllBytes(path), game);
        var after = before;
        foreach (var value in values) after = after.With(value.Name, value.Value);
        var changes = values.Select(v => new SaveValueChange(v.Name, before.Get(v.Name), after.Get(v.Name))).ToList();
        string? backup = null;
        if (!dryRun && !before.Bytes.AsSpan().SequenceEqual(after.Bytes))
        {
            var folder = BackupFolder(game, path);
            Replace(game, path, before.Bytes, after.Bytes, folder);
            backup = Path.Combine(folder, Path.GetFileName(path));
        }
        notes.Add("Only the value changes: no plot transition, consequence or journal update runs.");
        return new SaveSetResult(game.ToString(), path, dryRun, changes, backup, notes);
    }

    private static void GuardRunning(MEGame game, bool allowRunning, List<string> notes)
    {
        if (!GameBridge.IsGameRunning(game)) return;
        if (!allowRunning)
        {
            throw new InvalidOperationException($"{game} is running. Close it first, or pass allowRunning: the game reads a save only when it loads it, "
                + "but its next save writes what it holds in memory.");
        }
        notes.Add($"{game} was running. A session already loaded keeps what it read, and its next save writes that back.");
    }

    private static void Replace(MEGame game, string path, byte[] original, byte[] edited, string backupFolder)
    {
        PackageWriter.EnsureWritable(game, path);
        var created = File.GetCreationTimeUtc(path);
        var modified = File.GetLastWriteTimeUtc(path);
        Directory.CreateDirectory(backupFolder);
        var backup = Path.Combine(backupFolder, Path.GetFileName(path));
        File.Copy(path, backup);
        if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(original)) throw new IOException("The file changed since it was read.");
        // The save keeps its own times: set on the new file before it is moved over the old one.
        AtomicFile.WriteAllBytes(path, edited, temporary =>
        {
            File.SetCreationTimeUtc(temporary, created);
            File.SetLastWriteTimeUtc(temporary, modified);
            return true;
        });
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(edited)) throw new IOException($"The file differs from what was written. The original is {backup}.");
    }

    /// <summary>...\backups\saves\LE2\&lt;career&gt;\&lt;time&gt;: one folder for one edit, so a save's backups keep their names.</summary>
    private static string BackupFolder(MEGame game, string path)
    {
        var career = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "saves";
        return Path.Combine(PackageWriter.BackupRoot(), "saves", game.ToString(), career, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
    }

    private static SaveCareer Summary(MEGame game, string folder)
    {
        var rows = Saves(folder).Select(path => Row(game, path)).ToList();
        return new SaveCareer(Path.GetFileName(folder), rows.Count, Directory.GetFiles(folder, "*" + Extension + BackupExtension).Length,
            rows.Count(r => r.Head == "default"), rows.Count(r => r.Head == "morph"), rows.Count(r => r.Error is not null), rows.FirstOrDefault(r => r.Player is not null)?.Player);
    }

    private static SaveRow Row(MEGame game, string path)
    {
        var size = new FileInfo(path).Length;
        try
        {
            var save = SaveGame.Parse(File.ReadAllBytes(path), game);
            return new SaveRow(Path.GetFileName(path), size, save.FirstName, save.IsFemale ? "female" : "male", save.ClassName, Hours(save), save.Map, save.Saved, save.Head.Kind, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        {
            return new SaveRow(Path.GetFileName(path), size, null, null, null, null, null, null, null, ex.Message);
        }
    }

    private static double Hours(SaveGame save) => Math.Round(save.SecondsPlayed / 3600, 1);

    private static string[] Saves(string folder) => Directory.GetFiles(folder, "*" + Extension)
        .Where(path => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string OneSave(MEGame game, string save)
    {
        var path = Existing(game, save);
        if (!File.Exists(path)) throw new FileNotFoundException($"No save at {path}.");
        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{path} is no {Extension} file.");
        return path;
    }

    /// <summary>A Windows or /mnt path, or a path under the game's save folder: John_12_Soldier_010121_1a2b3c4, or that\Save_0084 with or without its extension.</summary>
    private static string Existing(MEGame game, string target)
    {
        var path = GamePaths.ToWindowsPath(target.Trim()).Replace('/', '\\');
        if (!Path.IsPathRooted(path)) path = Path.Combine(Root(game), path);
        if (!File.Exists(path) && !Directory.Exists(path) && File.Exists(path + Extension)) path += Extension;
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException($"Nothing at {path}.");
        return path;
    }
}
