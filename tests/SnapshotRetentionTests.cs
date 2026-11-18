using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class SnapshotRetentionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-retention-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestDatabase.Delete(_dbPath);
    }

    private SqliteIndexStore CreateStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        return store;
    }

    private static SnapshotRow MakeSnapshot(string id, string workspaceId, DateTime builtAt)
    {
        return new SnapshotRow
        {
            SnapshotId = id,
            WorkspaceId = workspaceId,
            GitRoot = "/repo",
            SolutionPath = "/repo/Test.slnx",
            SdkVersion = "10.0.100",
            CompilerVersion = "4.0",
            CreatedAtUtc = builtAt,
            DatabaseSchemaVersion = MigrationRunner.LatestVersion,
            OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
            ExtractorVersion = VersionConstants.ExtractorVersion,
            ToolVersion = VersionConstants.ToolVersion,
            PreviousSnapshotId = null,
            Projects = [],
            Documents = [],
            SkippedAdapters = []
        };
    }

    private static void InsertComplete(SqliteIndexStore store, string id, string workspaceId, DateTime builtAt)
    {
        store.SaveSnapshot(MakeSnapshot(id, workspaceId, builtAt));
        store.MarkSnapshotComplete(id);
    }

    private static void InsertFailed(SqliteIndexStore store, string id, string workspaceId, DateTime builtAt, string reason = "failed_test")
    {
        store.SaveSnapshot(MakeSnapshot(id, workspaceId, builtAt));
        store.MarkSnapshotFailed(id, reason, "test failure");
    }

    [Fact]
    public void Prune_KeepsTheSnapshotThatTheLastRunSelected()
    {
        using var store = CreateStore();
        const string ws = "ws-ret-1";
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        InsertComplete(store, "c1", ws, t);
        InsertComplete(store, "c2", ws, t.AddHours(1));
        InsertComplete(store, "c3", ws, t.AddHours(2));
        InsertComplete(store, "c4", ws, t.AddHours(3));

        store.MarkSnapshotSelected("c1", t.AddHours(4));
        store.PruneOldSnapshots(3);

        Assert.Equal(["c3", "c4", "c1"], store.GetSnapshotIds(ws));
    }

    [Fact]
    public void Status_DoesNotReportAFailureThatALaterRunSuperseded()
    {
        using var store = CreateStore();
        const string ws = "ws-ret-2";
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        InsertComplete(store, "c1", ws, t);
        InsertFailed(store, "f1", ws, t.AddHours(1));

        Assert.Equal("f1", store.GetLatestSnapshotFailure(ws)?.SnapshotId);

        store.MarkSnapshotSelected("c1", t.AddHours(2));
        Assert.Null(store.GetLatestSnapshotFailure(ws));
    }

    [Fact]
    public void Prune_FailedRunsDoNotTakeWindowSlots()
    {
        using var store = CreateStore();
        const string ws = "ws-ret-3";
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        InsertComplete(store, "c1", ws, t);
        InsertComplete(store, "c2", ws, t.AddHours(1));
        InsertComplete(store, "c3", ws, t.AddHours(2));
        InsertFailed(store, "f1", ws, t.AddHours(3));
        InsertFailed(store, "f2", ws, t.AddHours(4));
        store.DeleteIncompleteSnapshots();
        InsertComplete(store, "c4", ws, t.AddHours(5));

        store.PruneOldSnapshots(3);

        Assert.Equal(["c2", "c3", "c4"], store.GetSnapshotIds(ws));
        Assert.Equal(["c2", "c3", "c4"], ReadAllSnapshotIds(ws));
        Assert.Null(store.GetLatestSnapshotFailure(ws));
    }

    [Fact]
    public void Prune_KeepsOnlyTheNewestFailureThatIsNotSuperseded()
    {
        using var store = CreateStore();
        const string ws = "ws-ret-4";
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        InsertComplete(store, "c1", ws, t);
        InsertComplete(store, "c2", ws, t.AddHours(1));
        InsertComplete(store, "c3", ws, t.AddHours(2));
        InsertFailed(store, "f1", ws, t.AddHours(3));
        InsertFailed(store, "f2", ws, t.AddHours(4));
        store.DeleteIncompleteSnapshots();

        store.PruneOldSnapshots(3);

        Assert.Equal(["c1", "c2", "c3", "f2"], ReadAllSnapshotIds(ws));
        Assert.Equal("f2", store.GetLatestSnapshotFailure(ws)?.SnapshotId);
    }

    private List<string> ReadAllSnapshotIds(string workspaceId)
    {
        SqliteConnection.ClearAllPools();
        using var connection = TestDatabase.OpenReadOnly(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT snapshot_id FROM snapshots WHERE workspace_id = @ws ORDER BY selected_at_utc;";
        cmd.Parameters.AddWithValue("@ws", workspaceId);
        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }
}
