using Lurp.Handlers;
using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class PinSnapshotTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-pin-{Guid.NewGuid():N}.db");

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
            DatabaseSchemaVersion = Lurp.Workspace.VersionConstants.DatabaseSchemaVersion,
            OutputSchemaVersion = Lurp.Workspace.VersionConstants.OutputSchemaVersion,
            ExtractorVersion = Lurp.Workspace.VersionConstants.ExtractorVersion,
            ToolVersion = Lurp.Workspace.VersionConstants.ToolVersion,
            PreviousSnapshotId = null,
            Projects = [],
            Documents = [],
            SkippedAdapters = []
        };
    }

    private static void InsertSnapshot(SqliteIndexStore store, string id, string workspaceId, DateTime builtAt)
    {
        var row = MakeSnapshot(id, workspaceId, builtAt);
        store.SaveSnapshot(row);
        store.MarkSnapshotComplete(id);
    }

    [Fact]
    public void PinnedOlderSnapshot_IsEffectiveLatest()
    {
        using var store = CreateStore();
        var ws = "ws-pin-1";
        var older = DateTime.UtcNow.AddHours(-2);
        var newer = DateTime.UtcNow;
        InsertSnapshot(store, "snap-a", ws, older);
        InsertSnapshot(store, "snap-b", ws, newer);

        // Without pin, latest is snap-b (built_at latest)
        Assert.Equal("snap-b", store.GetBuiltAtLatestSnapshotId(ws));
        Assert.Equal("snap-b", store.GetLatestSnapshotId(ws));
        Assert.Equal("snap-b", store.LoadLatestSnapshot(ws)!.SnapshotId);
        Assert.Equal("snap-b", HandlerBootstrap.ResolveSnapshotId(store, null));
        Assert.Equal("snap-b", HandlerBootstrap.ResolveSnapshotId(store, "latest"));

        // Pin older
        store.SetPinnedSnapshot("snap-a");

        // Effective latest is now snap-a
        Assert.Equal("snap-a", store.GetLatestSnapshotId(ws));
        Assert.Equal("snap-a", store.LoadLatestSnapshot(ws)!.SnapshotId);
        Assert.Equal("snap-a", HandlerBootstrap.ResolveSnapshotId(store, null));
        // Explicit latest should still be built_at latest? The ResolveSnapshotId logic treats "latest" as pin-aware? Actually it maps "latest" to GetLatestSnapshotId which is pin-aware.
        // But explicit snapshot id should still work.
        Assert.Equal("snap-b", store.LoadSnapshot("snap-b")!.SnapshotId);
        // Built-at latest remains snap-b
        Assert.Equal("snap-b", store.GetBuiltAtLatestSnapshotId(ws));

        // Visible in status
        var pinned = store.GetPinnedSnapshot(ws);
        Assert.NotNull(pinned);
        Assert.Equal("snap-a", pinned.PinnedSnapshotId);
        Assert.NotEqual(default, pinned.PinnedAtUtc);
        Assert.NotNull(pinned.BuiltAtUtc);

        // Clear pin restores built_at
        Assert.True(store.ClearPinnedSnapshot(ws));
        Assert.Equal("snap-b", store.GetLatestSnapshotId(ws));
        Assert.Null(store.GetPinnedSnapshot(ws));

        store.Close();
    }

    [Fact]
    public void EveryReadMode_ResolvesToPinned_WhenSnapshotOmitted()
    {
        using var store = CreateStore();
        var ws = "ws-pin-2";
        InsertSnapshot(store, "snap-old", ws, DateTime.UtcNow.AddHours(-1));
        InsertSnapshot(store, "snap-new", ws, DateTime.UtcNow);
        store.SetPinnedSnapshot("snap-old");

        // All these funnel through ResolveSnapshotId -> GetLatestSnapshotId
        foreach (var arg in new string?[] { null, "latest" })
        {
            // Our current ResolveSnapshotId treats null and "latest" as pin-aware effective latest.
            // This is intentional: every read-mode's default is pin-aware.
            var resolved = HandlerBootstrap.ResolveSnapshotId(store, arg);
            Assert.Equal("snap-old", resolved);
        }
        // Explicit id bypasses pin
        Assert.Equal("snap-new", HandlerBootstrap.ResolveSnapshotId(store, "snap-new"));

        // Also verify GetLatestSnapshotId without workspace param (single workspace fallback)
        Assert.Equal("snap-old", store.GetLatestSnapshotId());
        Assert.Equal("snap-new", store.GetBuiltAtLatestSnapshotId());

        store.Close();
    }

    [Fact]
    public void PinProtectedFromPruning()
    {
        using var store = CreateStore();
        var ws = "ws-pin-3";
        var baseTime = DateTime.UtcNow.AddHours(-10);
        // Create 4 snapshots: snap-1 oldest, snap-4 newest
        for (int i = 1; i <= 4; i++)
            InsertSnapshot(store, $"snap-{i}", ws, baseTime.AddHours(i));

        // Pin oldest
        store.SetPinnedSnapshot("snap-1");
        // Prune keep=3 should keep snap-4, snap-3, snap-2 plus pinned snap-1 (not pruned)
        store.PruneOldSnapshots(3);
        var ids = store.GetSnapshotIds(ws);
        Assert.Contains("snap-1", ids);
        Assert.Equal(4, ids.Count); // none pruned because pinned protected

        // Clear pin and prune again -> now snap-1 should be pruned
        store.ClearPinnedSnapshot(ws);
        store.PruneOldSnapshots(3);
        ids = store.GetSnapshotIds(ws);
        Assert.DoesNotContain("snap-1", ids);
        Assert.Equal(3, ids.Count);

        store.Close();
    }

    [Fact]
    public void PinRejected_ForIncompleteSnapshot()
    {
        using var store = CreateStore();
        var ws = "ws-pin-4";
        var row = MakeSnapshot("snap-inprog", ws, DateTime.UtcNow);
        store.SaveSnapshot(row);
        // status is in_progress
        var ex = Assert.Throws<InvalidOperationException>(() => store.SetPinnedSnapshot("snap-inprog"));
        Assert.Contains("cannot be pinned", ex.Message);

        store.Close();
    }

    [Fact]
    public void DeletePinnedSnapshot_Throws()
    {
        using var store = CreateStore();
        var ws = "ws-pin-5";
        InsertSnapshot(store, "snap-x", ws, DateTime.UtcNow.AddHours(-1));
        InsertSnapshot(store, "snap-y", ws, DateTime.UtcNow);
        store.SetPinnedSnapshot("snap-x");

        var ex = Assert.Throws<InvalidOperationException>(() => store.DeleteSnapshotData("snap-x"));
        Assert.Contains("pinned", ex.Message);

        // After clear, delete succeeds
        store.ClearPinnedSnapshot(ws);
        store.DeleteSnapshotData("snap-x");
        Assert.Null(store.LoadSnapshot("snap-x"));

        store.Close();
    }

    [Fact]
    public void PinIsImmutable_SnapshotRowUnchanged()
    {
        using var store = CreateStore();
        var ws = "ws-pin-6";
        var t = DateTime.UtcNow.AddHours(-5);
        InsertSnapshot(store, "snap-orig", ws, t);
        InsertSnapshot(store, "snap-newer", ws, DateTime.UtcNow);
        var before = store.LoadSnapshot("snap-orig")!.CreatedAtUtc;

        store.SetPinnedSnapshot("snap-orig");

        var after = store.LoadSnapshot("snap-orig")!.CreatedAtUtc;
        Assert.Equal(before, after);

        // built_at not rewritten
        using var conn = TestDatabase.Open(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT built_at_utc FROM snapshots WHERE snapshot_id='snap-orig';";
        var built = (string)cmd.ExecuteScalar()!;
        Assert.Equal(t.ToString("O"), built);

        store.Close();
    }

    [Fact]
    public void Prune_RemovesASnapshotThatWasThePreviousPin()
    {
        using var store = CreateStore();
        var ws = "ws-pin-repin";
        var baseTime = DateTime.UtcNow.AddHours(-10);
        for (var i = 1; i <= 5; i++)
            InsertSnapshot(store, $"snap-{i}", ws, baseTime.AddHours(i));

        store.SetPinnedSnapshot("snap-1");
        store.SetPinnedSnapshot("snap-5");

        store.PruneOldSnapshots(3);

        Assert.Equal(["snap-3", "snap-4", "snap-5"], store.GetSnapshotIds(ws));
        var pin = store.GetPinnedSnapshot(ws);
        Assert.NotNull(pin);
        Assert.Equal("snap-5", pin.PinnedSnapshotId);
        Assert.Null(pin.PreviousPinnedSnapshotId);

        store.Close();
    }

    [Fact]
    public void Prune_KeepsThePreviousPin_WhileItIsInsideTheKeepWindow()
    {
        using var store = CreateStore();
        var ws = "ws-pin-repin-kept";
        var baseTime = DateTime.UtcNow.AddHours(-10);
        for (var i = 1; i <= 4; i++)
            InsertSnapshot(store, $"snap-{i}", ws, baseTime.AddHours(i));

        store.SetPinnedSnapshot("snap-2");
        store.SetPinnedSnapshot("snap-4");

        store.PruneOldSnapshots(3);

        Assert.Equal(["snap-2", "snap-3", "snap-4"], store.GetSnapshotIds(ws));
        Assert.Equal("snap-2", store.GetPinnedSnapshot(ws)!.PreviousPinnedSnapshotId);
        store.Close();
    }

    [Fact]
    public void Prune_FailsWithTheRealError_WhenThePinsCannotBeRead()
    {
        using var store = CreateStore();
        var ws = "ws-pin-unreadable";
        var baseTime = DateTime.UtcNow.AddHours(-10);
        for (var i = 1; i <= 4; i++)
            InsertSnapshot(store, $"snap-{i}", ws, baseTime.AddHours(i));
        DropPinsTable();

        var ex = Assert.Throws<SqliteException>(() => store.PruneOldSnapshots(3));

        Assert.Contains("snapshot_pins", ex.Message);
        Assert.Equal(4, store.GetSnapshotIds(ws).Count);
        store.Close();
    }

    [Fact]
    public void DeleteSnapshotData_FailsWithTheRealError_WhenThePinsCannotBeRead()
    {
        using var store = CreateStore();
        InsertSnapshot(store, "snap-a", "ws-pin-unreadable-2", DateTime.UtcNow);
        DropPinsTable();

        var ex = Assert.Throws<SqliteException>(() => store.DeleteSnapshotData("snap-a"));

        Assert.Contains("snapshot_pins", ex.Message);
        Assert.NotNull(store.LoadSnapshot("snap-a"));
        store.Close();
    }

    [Fact]
    public void GetPinnedSnapshot_FailsWithTheRealError_WhenThePinsTableIsMissing()
    {
        using var store = CreateStore();
        DropPinsTable();

        var ex = Assert.Throws<SqliteException>(() => store.GetPinnedSnapshot("ws-none"));

        Assert.Contains("snapshot_pins", ex.Message);
        store.Close();
    }

    [Fact]
    public void GetPinnedSnapshot_FailsWithTheRealError_WhenAStalePinCannotBeCleared()
    {
        using var store = CreateStore();
        InsertSnapshot(store, "snap-a", "ws-stale", DateTime.UtcNow);
        store.SetPinnedSnapshot("snap-a");
        ExecuteSql("""
            UPDATE snapshots SET status = 'failed' WHERE snapshot_id = 'snap-a';
            CREATE TRIGGER block_pin_delete BEFORE DELETE ON snapshot_pins BEGIN SELECT RAISE(ABORT, 'pin delete blocked'); END;
            """);

        var ex = Assert.Throws<SqliteException>(() => store.GetPinnedSnapshot("ws-stale"));

        Assert.Contains("pin delete blocked", ex.Message);
        store.Close();
    }

    [Fact]
    public void GetPinnedSnapshot_ReturnsNull_ForAStalePin_OnAReadOnlyStore()
    {
        using (var store = CreateStore())
        {
            InsertSnapshot(store, "snap-a", "ws-stale", DateTime.UtcNow);
            store.SetPinnedSnapshot("snap-a");
            store.Close();
        }
        ExecuteSql("UPDATE snapshots SET status = 'failed' WHERE snapshot_id = 'snap-a';");

        using var readOnly = new SqliteIndexStore(_dbPath);
        readOnly.OpenReadOnly();

        Assert.Null(readOnly.GetPinnedSnapshot("ws-stale"));
        readOnly.Close();
    }

    private void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void DropPinsTable() => ExecuteSql("DROP TABLE snapshot_pins;");
}
