namespace MeleMcp.Core;

/// <summary>
/// What a build that writes several files has put on disk so far, each file with its backup. The builds have no rollback (a
/// package is saved in place, see <see cref="PackageWriter"/>), so when a later step fails the caller must still learn which
/// files already changed and where their backups are. <see cref="Guard{T}"/> turns such a failure into a
/// <see cref="PartialBuildException"/> that names them; while nothing is written yet the original exception goes through as it is.
/// </summary>
public sealed class BuildWrites
{
    private readonly List<(string File, string? Backup, bool Rebuilt)> written = [];

    /// <summary>Each file in write order, with its backup; <c>Rebuilt</c>: one written over with no backup (see <see cref="Wrote"/>).</summary>
    public IReadOnlyList<(string File, string? Backup, bool Rebuilt)> Written => written;

    /// <summary>The backups so far, in write order. A file the build created, or rebuilt over an earlier one, has none.</summary>
    public List<string> Backups => written.Select(w => w.Backup).OfType<string>().ToList();

    /// <summary>
    /// Records a file once it is on disk: its backup, or null for a file the build created. <paramref name="rebuilt"/>: a file with
    /// no backup that was there before, which the build wrote again from the vanilla file (a fork, a fork's localized copy), so its
    /// earlier content is gone but can be made again; it is never named as created.
    /// </summary>
    public void Wrote(string file, string? backup = null, bool rebuilt = false) => written.Add((file, backup, rebuilt && backup is null));

    public T Guard<T>(Func<T> build)
    {
        try
        {
            return build();
        }
        catch (Exception ex) when (written.Count > 0 && ex is not PartialBuildException and not OperationCanceledException)
        {
            throw new PartialBuildException(ex, [.. written]);
        }
    }
}

/// <summary>A build that failed after it had written files: the message names each one and its backup.</summary>
public sealed class PartialBuildException(Exception failure, IReadOnlyList<(string File, string? Backup, bool Rebuilt)> written)
    : Exception(Describe(failure, written), failure)
{
    public IReadOnlyList<(string File, string? Backup, bool Rebuilt)> Written { get; } = written;

    private static string Describe(Exception failure, IReadOnlyList<(string File, string? Backup, bool Rebuilt)> written) =>
        $"{failure.GetType().Name}: {failure.Message} The build stopped after writing {written.Count} file{(written.Count == 1 ? "" : "s")}, " +
        $"which stay changed: {string.Join("; ", written.Select(Named))}. " +
        "Fix the cause and build again (the builds converge), or put the backups back.";

    private static string Named((string File, string? Backup, bool Rebuilt) w) =>
        w.Backup is not null ? $"{w.File} (backup {w.Backup})"
        : w.Rebuilt ? $"{w.File} (rewritten; rebuilt from the vanilla file, no backup)"
        : $"{w.File} (created)";
}
