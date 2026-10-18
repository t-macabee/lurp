using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Storage;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

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
    public void GetLatestSnapshotId_StalePin_ReadsTheLatestCompleteSnapshotAndKeepsThePin()
    {
        using (var store = CreateStore())
        {
            const string ws = "ws-stale-read-2";
            InsertSnapshot(store, "snap-a", ws, DateTime.UtcNow.AddHours(-1));
            InsertSnapshot(store, "snap-b", ws, DateTime.UtcNow);
            store.SetPinnedSnapshot("snap-a");
        }
        ExecuteSql("UPDATE snapshots SET status = 'failed' WHERE snapshot_id = 'snap-a';");

        using var writable = new SqliteIndexStore(_dbPath);
        writable.Open();

        // R149: a pin read is a read. A stale pin is treated as absent and left in place.
        Assert.Equal("snap-b", writable.GetLatestSnapshotId("ws-stale-read-2"));
        Assert.Equal("snap-a", PinnedSnapshotIdInDb(_dbPath));
        writable.Close();
    }

    [Fact]
    public void ClearStalePin_ReturnsTheStaleIdAndRemovesTheRow()
    {
        using var store = CreateStore();
        const string ws = "ws-clear-stale";
        InsertSnapshot(store, "snap-a", ws, DateTime.UtcNow.AddHours(-1));
        InsertSnapshot(store, "snap-b", ws, DateTime.UtcNow);
        store.SetPinnedSnapshot("snap-a");
        ExecuteSql("UPDATE snapshots SET status = 'failed' WHERE snapshot_id = 'snap-a';");

        Assert.Equal("snap-a", store.ClearStalePin());
        Assert.Null(PinnedSnapshotIdInDb(_dbPath));
        Assert.Null(store.ClearStalePin());
    }

    [Fact]
    public void ClearStalePin_WithoutAWorkspaceId_RemovesOnlyThePinItReturns()
    {
        using var store = CreateStore();
        InsertSnapshot(store, "snap-a", "ws-one", DateTime.UtcNow.AddHours(-1));
        InsertSnapshot(store, "snap-b", "ws-two", DateTime.UtcNow);
        store.SetPinnedSnapshot("snap-a");
        store.SetPinnedSnapshot("snap-b");
        ExecuteSql("UPDATE snapshots SET status = 'failed' WHERE snapshot_id IN ('snap-a', 'snap-b');");

        var staleId = store.GetStalePinnedSnapshotId();
        Assert.NotNull(staleId);
        var pinsBefore = PinnedSnapshotIdsByWorkspace(_dbPath);
        var staleWorkspace = pinsBefore.Single(kv => kv.Value == staleId).Key;
        var otherWorkspace = pinsBefore.Keys.Single(k => k != staleWorkspace);

        // R151: the clear and the stale read are one rule and one row.
        Assert.Equal(staleId, store.ClearStalePin());

        var pinsAfter = PinnedSnapshotIdsByWorkspace(_dbPath);
        Assert.Single(pinsAfter);
        Assert.Equal(pinsBefore[otherWorkspace], pinsAfter[otherWorkspace]);
    }

    [Fact]
    public void ClearStalePin_KeepsAPinOnACompleteSnapshot()
    {
        using var store = CreateStore();
        const string ws = "ws-clear-complete";
        InsertSnapshot(store, "snap-a", ws, DateTime.UtcNow);
        store.SetPinnedSnapshot("snap-a");

        Assert.Null(store.ClearStalePin());
        Assert.Equal("snap-a", PinnedSnapshotIdInDb(_dbPath));
    }

    [Fact]
    public void ClearStalePin_ReturnsNull_WhenThereIsNoPin()
    {
        using var store = CreateStore();
        Assert.Null(store.ClearStalePin());
        Assert.Null(PinnedSnapshotIdInDb(_dbPath));
    }

    [Fact]
    public void GetPinnedSnapshot_ReturnsNull_ForAStalePin_WithoutTouchingThePin()
    {
        using var store = CreateStore();
        InsertSnapshot(store, "snap-a", "ws-stale", DateTime.UtcNow);
        store.SetPinnedSnapshot("snap-a");
        ExecuteSql("UPDATE snapshots SET status = 'failed' WHERE snapshot_id = 'snap-a';");

        Assert.Null(store.GetPinnedSnapshot("ws-stale"));
        Assert.Equal("snap-a", PinnedSnapshotIdInDb(_dbPath));
        store.Close();
    }

    // R152 (decision 29): the active pin is the most recently set pin whose snapshot
    // exists, has built_at_utc and is complete. A failed pin of another workspace
    // never hides it, whatever order the pin rows were inserted in.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetPinnedSnapshot_WithoutAWorkspaceId_SkipsAFailedPinOfAnotherWorkspace(bool pinBFirst)
    {
        using var store = CreateStore();
        var older = DateTime.UtcNow.AddMinutes(-30);
        var newer = DateTime.UtcNow.AddMinutes(-20);
        InsertSnapshot(store, "snap-fail-a", "ws-pin-a", older);
        InsertSnapshot(store, "snap-ok-b", "ws-pin-b", newer);

        if (pinBFirst)
        {
            store.SetPinnedSnapshot("snap-ok-b");
            store.SetPinnedSnapshot("snap-fail-a");
        }
        else
        {
            store.SetPinnedSnapshot("snap-fail-a");
            store.SetPinnedSnapshot("snap-ok-b");
        }

        SetPinnedAt("ws-pin-b", DateTime.UtcNow.AddMinutes(-10));
        // The failed pin is the most recently set pin; it is still the stale one.
        SetPinnedAt("ws-pin-a", DateTime.UtcNow);
        ExecuteSql("UPDATE snapshots SET status = 'failed' WHERE snapshot_id = 'snap-fail-a';");

        var active = store.GetPinnedSnapshot();
        Assert.NotNull(active);
        Assert.Equal("snap-ok-b", active.PinnedSnapshotId);
        Assert.Equal("ws-pin-b", active.WorkspaceId);
        Assert.Equal("snap-ok-b", store.GetLatestSnapshotId());
        Assert.Equal("snap-fail-a", store.GetStalePinnedSnapshotId());
    }

    [Fact]
    public void GetPinnedSnapshot_WithoutAWorkspaceId_ChoosesTheMostRecentlySetCompletePin()
    {
        using var store = CreateStore();
        InsertSnapshot(store, "snap-a", "ws-order-a", DateTime.UtcNow.AddMinutes(-2));
        InsertSnapshot(store, "snap-b", "ws-order-b", DateTime.UtcNow.AddMinutes(-1));
        store.SetPinnedSnapshot("snap-a");
        store.SetPinnedSnapshot("snap-b");

        SetPinnedAt("ws-order-a", DateTime.UtcNow.AddMinutes(-10));
        SetPinnedAt("ws-order-b", DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal("snap-b", store.GetPinnedSnapshot()!.PinnedSnapshotId);

        SetPinnedAt("ws-order-a", DateTime.UtcNow);
        Assert.Equal("snap-a", store.GetPinnedSnapshot()!.PinnedSnapshotId);
    }

    [Fact]
    public void ClearStalePin_FailsWithTheRealError_WhenTheDeleteIsBlocked()
    {
        using var store = CreateStore();
        InsertSnapshot(store, "snap-a", "ws-stale", DateTime.UtcNow);
        store.SetPinnedSnapshot("snap-a");
        ExecuteSql("""
            UPDATE snapshots SET status = 'failed' WHERE snapshot_id = 'snap-a';
            CREATE TRIGGER block_pin_delete BEFORE DELETE ON snapshot_pins BEGIN SELECT RAISE(ABORT, 'pin delete blocked'); END;
            """);

        var ex = Assert.Throws<SqliteException>(() => store.ClearStalePin());

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
        Assert.Equal("snap-a", PinnedSnapshotIdInDb(_dbPath));
        readOnly.Close();
    }

    [Fact]
    public void Timings_StalePin_WarnsAndUsesLatest()
    {
        var dir = CreateIndexedDirWithTwoSnapshots(out var staleId, out var latestId);
        try
        {
            var result = TestConsole.Capture(() => TimingsHandler.Run([$"--output-dir={dir}", "--json"]));

            Assert.Null(result.Failure);
            Assert.Contains(HandlerBootstrap.StalePinWarning(staleId, latestId), result.Stderr);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(latestId, doc.RootElement.GetProperty("snapshot_id").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadCommand_StalePin_WarnsAndUsesLatest()
    {
        var dir = CreateIndexedDirWithTwoSnapshots(out var staleId, out var latestId);
        try
        {
            var result = TestConsole.Capture(() => FindSymbolHandler.Run([
                "--symbol=T:Missing.Type|assembly",
                $"--output-dir={dir}"
            ]));

            Assert.NotNull(result.Failure);
            Assert.Contains(
                $"WARNING: pinned snapshot '{staleId}' is not complete; this command uses the latest snapshot '{latestId}'.",
                result.Stderr);
            Assert.Contains($"'{latestId}'", result.Failure!.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task McpSession_StalePin_WarnsAndUsesLatest()
    {
        var dir = CreateIndexedDirWithTwoSnapshots(out var staleId, out var latestId);
        try
        {
            McpSessionContext? session = null;
            var result = TestConsole.Capture(() => session = McpSessionContext.Create([$"--output-dir={dir}"]));

            Assert.Contains(HandlerBootstrap.StalePinWarning(staleId, latestId), result.Stderr);
            Assert.Equal(latestId, session!.PinnedSnapshotId);
            await session.DisposeAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Status_StalePin_ShowsIgnoredPin()
    {
        var dir = CreateIndexedDirWithTwoSnapshots(out var staleId, out _);
        try
        {
            var text = await TestConsole.CaptureAsync(() => StatusHandler.Run([$"--output-dir={dir}"]));
            Assert.Null(text.Failure);
            Assert.Contains(
                $"Pin: snapshot '{staleId}' is not complete and is ignored. Clear it with --mode=pin-snapshot --clear.",
                text.Stdout);

            var json = await TestConsole.CaptureAsync(() => StatusHandler.Run([$"--output-dir={dir}", "--json"]));
            Assert.Null(json.Failure);
            using (var doc = JsonDocument.Parse(json.Stdout))
                Assert.Equal(staleId, doc.RootElement.GetProperty("stale_pinned_snapshot_id").GetString());

            // Without a stale pin, no stale id is reported.
            using (var store = new SqliteIndexStore(Path.Combine(dir, "index.db")))
            {
                store.Open();
                store.ClearPinnedSnapshot();
            }

            var noPin = await TestConsole.CaptureAsync(() => StatusHandler.Run([$"--output-dir={dir}", "--json"]));
            Assert.Null(noPin.Failure);
            using (var noPinDoc = JsonDocument.Parse(noPin.Stdout))
            {
                if (noPinDoc.RootElement.TryGetProperty("stale_pinned_snapshot_id", out var field))
                    Assert.Equal(JsonValueKind.Null, field.ValueKind);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Annotate_StalePin_PrintsNoticeAndClearsThePin()
    {
        var dir = CreateIndexedDirWithTwoSnapshots(out var staleId, out var latestId);
        try
        {
            var result = TestConsole.Capture(() => AnnotationHandler.RunAnnotate([
                "--symbol=T:Some.Type|asm",
                "--annotation-kind=note",
                "--value=v1",
                $"--output-dir={dir}"
            ]));

            Assert.Null(result.Failure);
            Assert.Contains(HandlerBootstrap.StalePinRemovedNotice(staleId), result.Stderr);
            Assert.DoesNotContain("NOTICE", result.Stdout);
            using (var doc = JsonDocument.Parse(result.Stdout))
            {
                Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
                Assert.Equal(latestId, doc.RootElement.GetProperty("snapshot_id").GetString());
                Assert.Equal("T:Some.Type|asm", doc.RootElement.GetProperty("symbol_id").GetString());
            }

            Assert.Null(PinnedSnapshotIdIn(dir));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RetractAnnotation_StalePin_PrintsNoticeAndClearsThePin()
    {
        var dir = CreateIndexedDirWithTwoSnapshots(out var staleId, out var latestId);
        long annotationId;
        using (var store = new SqliteIndexStore(Path.Combine(dir, "index.db")))
        {
            store.Open();
            store.SaveAnnotations(latestId, [new AnnotationRecord("T:Some.Type|asm", "note", "v1")]);
            annotationId = store.GetAnnotations(latestId).Single().AnnotationId;
        }

        try
        {
            var result = TestConsole.Capture(() => AnnotationHandler.RunRetractAnnotation([
                $"--annotation-id={annotationId}",
                $"--output-dir={dir}"
            ]));

            Assert.Null(result.Failure);
            Assert.Contains(HandlerBootstrap.StalePinRemovedNotice(staleId), result.Stderr);
            Assert.DoesNotContain("NOTICE", result.Stdout);
            using (var doc = JsonDocument.Parse(result.Stdout))
            {
                Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
                Assert.Equal(latestId, doc.RootElement.GetProperty("snapshot_id").GetString());
                Assert.Equal(annotationId, doc.RootElement.GetProperty("annotation_id").GetInt64());
                Assert.True(doc.RootElement.GetProperty("retracted").GetBoolean());
            }

            Assert.Null(PinnedSnapshotIdIn(dir));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PinSnapshot_ReplacingAStalePin_PrintsNotice()
    {
        var dir = CreateIndexedDirWithTwoSnapshots(out var staleId, out var latestId);
        try
        {
            var result = TestConsole.Capture(() => PinSnapshotHandler.Run([
                $"--snapshot={latestId}",
                $"--output-dir={dir}"
            ]));

            Assert.Null(result.Failure);
            Assert.Contains(HandlerBootstrap.StalePinRemovedNotice(staleId), result.Stderr);
            Assert.Contains($"Pinned snapshot {latestId}", result.Stdout);
            Assert.Equal(latestId, PinnedSnapshotIdIn(dir));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    // R152 (decision 29): `pin-snapshot --snapshot=<id>` reports the pin of that
    // snapshot's workspace, so another workspace's pin is neither the new pin nor the
    // previous one.
    [Fact]
    public void PinSnapshot_WithOnlyAnotherWorkspacePinned_ReportsTheNewPinAndNoPreviousPin()
    {
        var dir = CreateTwoWorkspaceDir(out var completeSpaceId);
        try
        {
            var result = TestConsole.Capture(() => PinSnapshotHandler.Run([
                $"--snapshot={completeSpaceId}",
                "--json",
                $"--output-dir={dir}"
            ]));

            Assert.Null(result.Failure);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(completeSpaceId, doc.RootElement.GetProperty("pinned_snapshot_id").GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("previous_pinned_snapshot_id").ValueKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    // index writes a snapshot and reads no pin, so it leaves a stale pin in place
    // and prints no stale-pin warning. Only annotate, retract-annotation and
    // pin-snapshot remove a stale pin.
    [Fact]
    public async Task Index_StalePin_LeavesThePinAndPrintsNoWarning()
    {
        var dir = CreateIndexedDirWithTwoSnapshots(out var staleId, out var latestId);
        var solutionDir = Path.Combine(Path.GetTempPath(), $"lurp-pin-solution-{Guid.NewGuid():N}");
        try
        {
            IntegrationTestBase.WriteSolution(solutionDir, PartialShapeFixture.ProjectName, PartialShapeFixture.Sources);

            var result = await TestConsole.CaptureAsync(() => IndexHandler.Run([
                $"--solution={Path.Combine(solutionDir, "Test.slnx")}",
                $"--output-dir={dir}",
                "--strategy=full"
            ], CancellationToken.None));

            Assert.Null(result.Failure);
            Assert.DoesNotContain("pinned snapshot", result.Stderr);
            Assert.DoesNotContain("NOTICE", result.Stderr);
            Assert.Equal(staleId, PinnedSnapshotIdIn(dir));

            // Positive control: the same database still has a stale pin, and a read
            // command still warns about it.
            var read = TestConsole.Capture(() => FindSymbolHandler.Run([
                "--symbol=T:Missing.Type|assembly",
                $"--output-dir={dir}"
            ]));
            Assert.Contains($"WARNING: pinned snapshot '{staleId}' is not complete;", read.Stderr);
            Assert.Contains($"'{latestId}'", read.Failure!.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
            TestDatabase.DeleteDirectory(solutionDir);
        }
    }

    private static string? PinnedSnapshotIdIn(string dir)
    {
        return PinnedSnapshotIdInDb(Path.Combine(dir, "index.db"));
    }

    private static string? PinnedSnapshotIdInDb(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pinned_snapshot_id FROM snapshot_pins LIMIT 1;";
        return command.ExecuteScalar() as string;
    }

    private static Dictionary<string, string> PinnedSnapshotIdsByWorkspace(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT workspace_id, pinned_snapshot_id FROM snapshot_pins;";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }

    private static string CreateIndexedDirWithTwoSnapshots(out string staleId, out string latestId)
    {
        return IndexedDirFixture.Create(2, stalePin: true, out staleId, out latestId);
    }

    private static string CreateTwoWorkspaceDir(out string completeSnapshotId)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lurp-pin-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var pinnedEarlier = Guid.NewGuid().ToString();
        completeSnapshotId = Guid.NewGuid().ToString();

        using var store = new SqliteIndexStore(Path.Combine(dir, "index.db"));
        store.Open();
        store.RunMigrations();
        store.SaveWorkspace("ws-pinned", "gitroot-pinned", "pinned.sln");
        store.SaveWorkspace("ws-new", "gitroot-new", "new.sln");
        InsertSnapshot(store, pinnedEarlier, "ws-pinned", DateTime.UtcNow.AddMinutes(-10));
        InsertSnapshot(store, completeSnapshotId, "ws-new", DateTime.UtcNow.AddMinutes(-5));
        store.SetPinnedSnapshot(pinnedEarlier);
        return dir;
    }

    private void SetPinnedAt(string workspaceId, DateTime pinnedAtUtc)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshot_pins SET pinned_at_utc = @pinnedAtUtc WHERE workspace_id = @workspaceId;";
        command.Parameters.AddWithValue("@pinnedAtUtc", pinnedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@workspaceId", workspaceId);
        command.ExecuteNonQuery();
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
