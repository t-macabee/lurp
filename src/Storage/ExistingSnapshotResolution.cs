namespace Lurp.Storage;

/// <summary>
///     Outcome of <see cref="SqliteIndexStore.ResolveExistingSnapshot" />: whether an
///     existing row with the same deterministic identity blocks, permits, or
///     precedes the current write attempt.
/// </summary>
public enum ExistingSnapshotDisposition
{
    /// <summary>No row exists with this identity; proceed to write.</summary>
    Fresh,

    /// <summary>A complete snapshot with this identity already exists; reuse it, do not write again.</summary>
    Reuse,

    /// <summary>A non-complete row with this identity existed and has been deleted; proceed to write.</summary>
    Retry
}

/// <summary>Resolution of the deterministic-identity reuse/retry decision.</summary>
public sealed record ExistingSnapshotResolution(ExistingSnapshotDisposition Disposition, string? ExistingStatus);
