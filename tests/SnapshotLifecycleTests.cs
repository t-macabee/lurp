using System.Text.Json;
using Lurp.Handlers;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class SnapshotLifecycleTests : IntegrationTestBase
{
    private const string StateA = """
        namespace P;
        public class Svc
        {
            public int Value() => 42;
        }
        """;

    private const string StateB = """
        namespace P;
        public class Svc
        {
            public int Value() => 42;
        }
        public class RBase { }
        """;

    private const string StateC = """
        namespace P;
        public class Svc
        {
            public int Value() => 42;
            public int Extra() => 1;
        }
        """;

    [Fact]
    public async Task RevertToAnIndexedState_MakesTheReusedSnapshotTheLatest()
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = StateA });
        var (idA, _) = await RunIndexAsync();

        WriteFile("P", "Svc.cs", StateB);
        var (idB, _) = await RunIndexAsync();
        Assert.NotEqual(idA, idB);

        WriteFile("P", "Svc.cs", StateA);
        var (reusedId, output) = await RunIndexAsync();
        Assert.Contains("reusing it", output, StringComparison.Ordinal);
        Assert.Equal(idA, reusedId);

        var ws = WorkspaceId.Create(TestDir, Path.GetFullPath(SolutionPath)).Value;
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(idA, store.GetSelectedLatestSnapshotId(ws));
            Assert.Equal(idA, store.GetLatestSnapshotId());
            Assert.Equal(idA, store.LoadLatestSnapshot(ws)!.SnapshotId);

            var latest = store.GetLatestSnapshotId()!;
            Assert.DoesNotContain(store.GetSymbolIdsInSnapshot(latest), id => id.StartsWith("T:P.RBase", StringComparison.Ordinal));
        }

        var statusSink = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([$"--output-dir={TestDir}", "--json"], sink));
        Assert.Null(statusSink.Failure);
        using (var doc = JsonDocument.Parse(statusSink.Stdout))
        {
            var root = doc.RootElement;
            Assert.Equal(idA, root.GetProperty("latest_snapshot_id").GetString());
            Assert.Equal(idA, root.GetProperty("selected_latest_snapshot_id").GetString());
            Assert.False(root.TryGetProperty("built_at_latest_snapshot_id", out _));
        }

        WriteFile("P", "Svc.cs", StateC);
        var (idC, _) = await RunIndexAsync();
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(idA, store.LoadSnapshot(idC)!.PreviousSnapshotId);
        }
    }

    private const string RecoveryStateA = """
        namespace P;
        public class Svc
        {
            public int Value() => Helper();
            private int Helper() => 1;
        }
        """;

    private const string RecoveryStateB = """
        namespace P;
        public class Svc
        {
            public int Value() => Helper();
            private int Helper() => 1;
            public int Twice() => Helper() + Helper();
        }
        """;

    [Fact]
    public async Task FullStrategy_RebuildsAWrongIncrementalSnapshot()
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        await RunIndexAsync("full");

        WriteFile("P", "Svc.cs", RecoveryStateB);
        var (idB, _) = await RunIndexAsync("incremental");

        string twiceId;
        string helperId;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(idB);
            twiceId = Assert.Single(ids, static id => id.StartsWith("M:P.Svc.Twice|", StringComparison.Ordinal));
            helperId = Assert.Single(ids, static id => id.StartsWith("M:P.Svc.Helper|", StringComparison.Ordinal));
        }
        using (var connection = OpenDbConnection())
        {
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = """
                DELETE FROM edges
                WHERE snapshot_id = @snapshotId
                  AND source_symbol_id = @sourceId
                  AND target_symbol_id = @targetId
                  AND kind = 'Calls';
                """;
            deleteCmd.Parameters.AddWithValue("@snapshotId", idB);
            deleteCmd.Parameters.AddWithValue("@sourceId", twiceId);
            deleteCmd.Parameters.AddWithValue("@targetId", helperId);
            Assert.Equal(1, deleteCmd.ExecuteNonQuery());
        }

        var (rebuiltId, rebuildOutput) = await RunIndexAsync("full");
        Assert.Contains("an incremental run built it; --strategy=full deletes it and rebuilds it.", rebuildOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("reusing it", rebuildOutput, StringComparison.Ordinal);
        Assert.Equal(idB, rebuiltId);

        var expectedEdges = await IndexFreshEdgeSetAsync(new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateB });
        DateTime builtAtAfterRebuild;
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(expectedEdges, ReadEdgeSet(store, idB));
            builtAtAfterRebuild = store.LoadSnapshot(idB)!.CreatedAtUtc;
        }

        var (_, reuseOutput) = await RunIndexAsync("full");
        Assert.Contains("reusing it", reuseOutput, StringComparison.Ordinal);
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(builtAtAfterRebuild, store.LoadSnapshot(idB)!.CreatedAtUtc);
        }
    }

    [Fact]
    public async Task IncrementalForce_RebuildsAnExistingSnapshotInPlace()
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var (idA, _) = await RunIndexAsync("full");

        DateTime builtAtBefore;
        using (var store = OpenStore(DbPath))
        {
            builtAtBefore = store.LoadSnapshot(idA)!.CreatedAtUtc;
        }

        WriteFile("P", "Svc.cs", RecoveryStateB);
        var (idB, _) = await RunIndexAsync("incremental");

        WriteFile("P", "Svc.cs", RecoveryStateA);
        var (rebuiltId, _) = await RunIndexAsync("incremental", force: true);
        Assert.Equal(idA, rebuiltId);

        var expectedEdges = await IndexFreshEdgeSetAsync(new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var ws = WorkspaceId.Create(TestDir, Path.GetFullPath(SolutionPath)).Value;
        using (var store = OpenStore(DbPath))
        {
            Assert.Single(store.GetSnapshotIds(ws), id => id == idA);
            Assert.Equal(Lurp.Storage.SnapshotStatusValues.Complete, store.GetSnapshotStatus(idA, ws));
            var rebuiltRow = store.LoadSnapshot(idA)!;
            Assert.True(rebuiltRow.CreatedAtUtc > builtAtBefore);
            Assert.Equal(idB, rebuiltRow.PreviousSnapshotId);
            Assert.Equal(expectedEdges, ReadEdgeSet(store, idA));
        }
    }

    [Fact]
    public async Task FullForce_OnAPinnedSnapshot_StopsBeforeAnyDelete()
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var (idA, _) = await RunIndexAsync("full");

        DateTime builtAtBefore;
        using (var store = OpenStore(DbPath))
        {
            store.SetPinnedSnapshot(idA);
            builtAtBefore = store.LoadSnapshot(idA)!.CreatedAtUtc;
        }

        var sink = new CapturingOutputSink();
        InvalidOperationException ex;
        using (var store = OpenStore(DbPath))
        {
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                IndexRunner.RunAsync(store, SolutionPath, [], null, "full", false, sink, false, true, CancellationToken.None));
        }

        Assert.Equal(
            $"snapshot '{idA}' is pinned; --force cannot rebuild it. Clear the pin with --mode=pin-snapshot --clear, or run without --force.",
            ex.Message);
        Assert.DoesNotContain("deleting", sink.Output.ToString(), StringComparison.OrdinalIgnoreCase);

        var ws = WorkspaceId.Create(TestDir, Path.GetFullPath(SolutionPath)).Value;
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(Lurp.Storage.SnapshotStatusValues.Complete, store.GetSnapshotStatus(idA, ws));
            Assert.Equal(idA, store.GetPinnedSnapshot(ws)?.PinnedSnapshotId);
            Assert.Equal(builtAtBefore, store.LoadSnapshot(idA)!.CreatedAtUtc);
        }
    }

    private static HashSet<(string Source, string Target, string Kind)> ReadEdgeSet(Lurp.Storage.SqliteIndexStore store, string snapshotId)
    {
        return store.GetEdges(snapshotId)
            .Select(static e => (e.SourceSymbolId, e.TargetSymbolId, e.Kind))
            .ToHashSet();
    }

    private static async Task<HashSet<(string Source, string Target, string Kind)>> IndexFreshEdgeSetAsync(IReadOnlyDictionary<string, string> sourceFiles)
    {
        var freshDir = Path.Combine(Path.GetTempPath(), $"lurp-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(freshDir);
        var freshSolution = Path.Combine(freshDir, "Test.slnx");
        var freshDb = Path.Combine(freshDir, "index.db");
        try
        {
            WriteSolution(freshDir, "P", sourceFiles);
            using var store = OpenStore(freshDb);
            await IndexRunner.RunAsync(store, freshSolution, [], null, "full", false, new CapturingOutputSink(), false, false, CancellationToken.None);
            var id = store.GetSelectedLatestSnapshotId()!;
            return ReadEdgeSet(store, id);
        }
        finally
        {
            TestDatabase.DeleteSolutionCache(freshSolution);
            TestDatabase.DeleteDirectory(freshDir);
        }
    }
}
