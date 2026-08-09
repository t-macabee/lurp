using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class SnapshotReuseResolutionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-reuse-{Guid.NewGuid():N}.db");
    private const string WorkspaceId = "w1";
    private const string SnapshotId = "s1";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private SqliteIndexStore OpenStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        store.SaveWorkspace(WorkspaceId, "gitroot", "solution.sln", DateTime.UtcNow);
        return store;
    }

    private static SnapshotRow NewSnapshot() => new()
    {
        SnapshotId = SnapshotId,
        WorkspaceId = WorkspaceId,
        GitRoot = "gitroot",
        SolutionPath = "solution.sln",
        CreatedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public void NoExistingRow_ResolvesFresh_AndWritesNothing()
    {
        using var store = OpenStore();

        var resolution = store.ResolveExistingSnapshot(SnapshotId, WorkspaceId);

        Assert.Equal(ExistingSnapshotDisposition.Fresh, resolution.Disposition);
        Assert.Null(resolution.ExistingStatus);
        Assert.Null(store.GetSnapshotStatus(SnapshotId, WorkspaceId));
    }

    [Fact]
    public void CompleteExistingRow_ResolvesReuse_AndIsNotDeleted()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot());
        store.MarkSnapshotComplete(SnapshotId);

        var resolution = store.ResolveExistingSnapshot(SnapshotId, WorkspaceId);

        Assert.Equal(ExistingSnapshotDisposition.Reuse, resolution.Disposition);
        Assert.Equal(SnapshotStatusValues.Complete, resolution.ExistingStatus);
        Assert.Equal(SnapshotStatusValues.Complete, store.GetSnapshotStatus(SnapshotId, WorkspaceId));
    }

    [Fact]
    public void IncompleteExistingRow_ResolvesRetry_AndIsDeleted()
    {
        using var store = OpenStore();
        store.SaveSnapshot(NewSnapshot());
        store.MarkSnapshotInProgress(SnapshotId);

        var resolution = store.ResolveExistingSnapshot(SnapshotId, WorkspaceId);

        Assert.Equal(ExistingSnapshotDisposition.Retry, resolution.Disposition);
        Assert.Equal(SnapshotStatusValues.InProgress, resolution.ExistingStatus);
        Assert.Null(store.GetSnapshotStatus(SnapshotId, WorkspaceId));
    }
}
