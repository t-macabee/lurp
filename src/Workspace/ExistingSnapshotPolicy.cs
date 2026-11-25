using Lurp.Storage;

namespace Lurp.Workspace;

internal enum ExistingSnapshotDisposition
{
    Fresh,
    Retry,
    Reuse,
    Rebuild
}

/// <summary>
///     The single rule for what an index run does when the computed snapshot id already exists (D5, D42, D47, D158.1).
/// </summary>
internal static class ExistingSnapshotPolicy
{
    internal static ExistingSnapshotDisposition Resolve(
        SqliteIndexStore store,
        string snapshotId,
        string workspaceId,
        string strategy,
        bool force,
        IOutputSink sink)
    {
        var existing = store.GetExistingSnapshotState(snapshotId, workspaceId);
        if (existing is null)
            return ExistingSnapshotDisposition.Fresh;

        var (status, buildStrategy) = existing.Value;
        if (!string.Equals(status, SnapshotStatusValues.Complete, StringComparison.Ordinal))
        {
            sink.WriteLine($"Snapshot {snapshotId} exists with status '{status}'; removing it and retrying {strategy} index.");
            return ExistingSnapshotDisposition.Retry;
        }

        var reuse = !force && (
            string.Equals(strategy, SnapshotBuildStrategies.Incremental, StringComparison.Ordinal) ||
            string.Equals(buildStrategy, SnapshotBuildStrategies.Full, StringComparison.Ordinal));
        if (reuse)
        {
            sink.WriteLine($"Identical complete snapshot {snapshotId} already exists for this workspace; reusing it.");
            return ExistingSnapshotDisposition.Reuse;
        }

        if (store.IsPinnedSnapshot(snapshotId))
        {
            var message = force
                ? $"snapshot '{snapshotId}' is pinned; --force cannot rebuild it. Clear the pin with --mode=pin-snapshot --clear, or run without --force."
                : $"snapshot '{snapshotId}' is pinned and an incremental run built it; --strategy=full cannot rebuild it. Clear the pin with --mode=pin-snapshot --clear, or run with --strategy=incremental.";
            throw new InvalidOperationException(message);
        }

        if (force)
            sink.WriteLine($"Identical complete snapshot {snapshotId} already exists; --force given, rebuilding it while the existing snapshot remains readable.");
        else
            sink.WriteLine($"Snapshot {snapshotId} exists and an incremental run built it; --strategy=full rebuilds it while the existing snapshot remains readable.");

        return ExistingSnapshotDisposition.Rebuild;
    }
}
