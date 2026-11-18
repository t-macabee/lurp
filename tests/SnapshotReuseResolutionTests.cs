using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class SnapshotReuseResolutionTests : IDisposable
{
    private const string WorkspaceId = "w1";
    private const string SnapshotId = "s1";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-reuse-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestDatabase.Delete(_dbPath);
    }

    private SqliteIndexStore OpenStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        store.SaveWorkspace(WorkspaceId, "gitroot", "solution.sln");
        return store;
    }

    private static SnapshotRow NewSnapshot(string buildStrategy = SnapshotBuildStrategies.Incremental)
    {
        return new SnapshotRow
        {
            SnapshotId = SnapshotId,
            WorkspaceId = WorkspaceId,
            GitRoot = "gitroot",
            SolutionPath = "solution.sln",
            CreatedAtUtc = DateTime.UtcNow,
            BuildStrategy = buildStrategy
        };
    }

    [Fact]
    public void NoExistingRow_FullNoForce_ResolvesFresh_AndWritesNothing()
    {
        using var store = OpenStore();
        var sink = new CapturingOutputSink();

        var disposition = ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Full, force: false, sink);

        Assert.Equal(ExistingSnapshotDisposition.Fresh, disposition);
        Assert.Null(store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(string.Empty, sink.Output.ToString());
    }

    [Fact]
    public void IncompleteExistingRow_ResolvesRetry_AndIsDeleted()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot());
        var sink = new CapturingOutputSink();

        var disposition = ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Full, force: false, sink);

        Assert.Equal(ExistingSnapshotDisposition.Retry, disposition);
        Assert.Null(store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(
            $"Snapshot {SnapshotId} exists with status 'in_progress'; removing it and retrying full index.",
            sink.Output.ToString().TrimEnd());
    }

    [Fact]
    public void CompleteIncrementalRow_IncrementalNoForce_ResolvesReuse()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot(SnapshotBuildStrategies.Incremental));
        store.MarkSnapshotComplete(SnapshotId);
        var sink = new CapturingOutputSink();

        var disposition = ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Incremental, force: false, sink);

        Assert.Equal(ExistingSnapshotDisposition.Reuse, disposition);
        Assert.Equal(SnapshotStatusValues.Complete, store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(
            $"Identical complete snapshot {SnapshotId} already exists for this workspace; reusing it.",
            sink.Output.ToString().TrimEnd());
    }

    [Fact]
    public void CompleteFullRow_FullNoForce_ResolvesReuse()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot(SnapshotBuildStrategies.Full));
        store.MarkSnapshotComplete(SnapshotId);
        var sink = new CapturingOutputSink();

        var disposition = ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Full, force: false, sink);

        Assert.Equal(ExistingSnapshotDisposition.Reuse, disposition);
        Assert.Equal(SnapshotStatusValues.Complete, store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(
            $"Identical complete snapshot {SnapshotId} already exists for this workspace; reusing it.",
            sink.Output.ToString().TrimEnd());
    }

    [Fact]
    public void CompleteIncrementalRow_FullNoForce_ResolvesRebuild_AndIsDeleted()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot(SnapshotBuildStrategies.Incremental));
        store.MarkSnapshotComplete(SnapshotId);
        var sink = new CapturingOutputSink();

        var disposition = ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Full, force: false, sink);

        Assert.Equal(ExistingSnapshotDisposition.Rebuild, disposition);
        Assert.Null(store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(
            $"Snapshot {SnapshotId} exists and an incremental run built it; --strategy=full deletes it and rebuilds it.",
            sink.Output.ToString().TrimEnd());
    }

    [Fact]
    public void CompleteFullRow_FullForce_ResolvesRebuild_AndIsDeleted()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot(SnapshotBuildStrategies.Full));
        store.MarkSnapshotComplete(SnapshotId);
        var sink = new CapturingOutputSink();

        var disposition = ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Full, force: true, sink);

        Assert.Equal(ExistingSnapshotDisposition.Rebuild, disposition);
        Assert.Null(store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(
            $"Identical complete snapshot {SnapshotId} already exists; --force given, deleting it and rebuilding it.",
            sink.Output.ToString().TrimEnd());
    }

    [Fact]
    public void CompleteIncrementalRow_IncrementalForce_ResolvesRebuild_AndIsDeleted()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot(SnapshotBuildStrategies.Incremental));
        store.MarkSnapshotComplete(SnapshotId);
        var sink = new CapturingOutputSink();

        var disposition = ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Incremental, force: true, sink);

        Assert.Equal(ExistingSnapshotDisposition.Rebuild, disposition);
        Assert.Null(store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(
            $"Identical complete snapshot {SnapshotId} already exists; --force given, deleting it and rebuilding it.",
            sink.Output.ToString().TrimEnd());
    }

    [Fact]
    public void CompleteFullRow_FullForce_WhenPinned_ThrowsBeforeDeleteOrLine()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot(SnapshotBuildStrategies.Full));
        store.MarkSnapshotComplete(SnapshotId);
        store.SetPinnedSnapshot(SnapshotId);
        var sink = new CapturingOutputSink();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Full, force: true, sink));

        Assert.Equal(
            $"snapshot '{SnapshotId}' is pinned; --force cannot rebuild it. Clear the pin with --mode=pin-snapshot --clear, or run without --force.",
            ex.Message);
        Assert.Equal(SnapshotStatusValues.Complete, store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(SnapshotId, store.GetPinnedSnapshot(WorkspaceId)?.PinnedSnapshotId);
        Assert.Equal(string.Empty, sink.Output.ToString());
    }

    [Fact]
    public void CompleteIncrementalRow_FullNoForce_WhenPinned_ThrowsBeforeDeleteOrLine()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot(SnapshotBuildStrategies.Incremental));
        store.MarkSnapshotComplete(SnapshotId);
        store.SetPinnedSnapshot(SnapshotId);
        var sink = new CapturingOutputSink();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ExistingSnapshotPolicy.Resolve(store, SnapshotId, WorkspaceId, SnapshotBuildStrategies.Full, force: false, sink));

        Assert.Equal(
            $"snapshot '{SnapshotId}' is pinned and an incremental run built it; --strategy=full cannot rebuild it. Clear the pin with --mode=pin-snapshot --clear, or run with --strategy=incremental.",
            ex.Message);
        Assert.Equal(SnapshotStatusValues.Complete, store.GetSnapshotStatus(SnapshotId, WorkspaceId));
        Assert.Equal(SnapshotId, store.GetPinnedSnapshot(WorkspaceId)?.PinnedSnapshotId);
        Assert.Equal(string.Empty, sink.Output.ToString());
    }
}
