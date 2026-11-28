using System.Text.Json;
using System.Text.Json.Nodes;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

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
    public void ExcludedSymbolMethods_OnAnUnopenedStore_ThrowInvalidOperation()
    {
        using var store = new SqliteIndexStore(DbPath);

        Assert.Throws<InvalidOperationException>(() =>
            store.SaveExcludedSymbols("snapshot", new Dictionary<string, HashSet<string>>()));
        Assert.Throws<InvalidOperationException>(() => store.LoadExcludedSymbolsByProject("snapshot"));
    }

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

    [Theory]
    [InlineData("full")]
    [InlineData("incremental")]
    public async Task ForcedRebuild_StoresIdsForTheTargetSnapshot(string strategy)
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var (idA, _) = await RunIndexAsync("full");
        WriteFile("P", "Svc.cs", RecoveryStateB);
        var (idB, _) = await RunIndexAsync("incremental");
        WriteFile("P", "Svc.cs", RecoveryStateA);
        await RunIndexAsync("full");
        WriteFile("P", "Svc.cs", RecoveryStateB);
        var (rebuilt, _) = await RunIndexAsync(strategy, force: true);
        Assert.Equal(idB, rebuilt);
        using var store = OpenStore(DbPath);
        var stored = store.GetSemanticChanges(idA, idB);
        var (live, _) = new SemanticDiffer(store, store, store).ComputeDiff(idA, idB);
        Assert.NotEmpty(live);
        Assert.Equal(live.Select(c => c.ChangeId).Order(), stored.Select(c => c.ChangeId).Order());
        Assert.All(stored, c => Assert.Equal(idB, c.ToSnapshotId));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("incremental")]
    public async Task ForcedRebuild_FailureAfterDiff_PreservesLiveChanges(string strategy)
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var (idA, _) = await RunIndexAsync("full");
        WriteFile("P", "Svc.cs", RecoveryStateB);
        var (idB, _) = await RunIndexAsync("incremental");
        using var store = OpenStore(DbPath);
        var before = store.GetSemanticChanges(idA, idB)
            .Select(c => (c.ChangeId, c.ToSnapshotId)).OrderBy(c => c.ChangeId).ToArray();
        Assert.NotEmpty(before);
        WriteFile("P", "Svc.cs", RecoveryStateA);
        await RunIndexAsync("full");
        WriteFile("P", "Svc.cs", RecoveryStateB);

        (string ChangeId, string ToSnapshotId)[]? during = null;
        long? stagingRows = null;
        var sink = new AfterSemanticDiffSink(() =>
        {
            using var reader = OpenStore(DbPath);
            during = reader.GetSemanticChanges(idA, idB)
                .Select(c => (c.ChangeId, c.ToSnapshotId)).OrderBy(c => c.ChangeId).ToArray();
            using var connection = OpenDbConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM semantic_changes WHERE from_snapshot_id = @from AND to_snapshot_id NOT IN (@a, @b)";
            command.Parameters.AddWithValue("@from", idA);
            command.Parameters.AddWithValue("@a", idA);
            command.Parameters.AddWithValue("@b", idB);
            stagingRows = (long)command.ExecuteScalar()!;
            throw new InvalidOperationException("injected failure after semantic diff");
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndexRunner.RunAsync(store, SolutionPath, [], strategy, output: sink, force: true));

        Assert.Equal("injected failure after semantic diff", error.Message);
        Assert.Equal(before, during);
        Assert.Equal(0, stagingRows);
        Assert.Equal(before, store.GetSemanticChanges(idA, idB)
            .Select(c => (c.ChangeId, c.ToSnapshotId)).OrderBy(c => c.ChangeId).ToArray());
        Assert.All(before, c => Assert.Equal(idB, c.ToSnapshotId));
    }

    [Fact]
    public async Task FullStrategy_RebuildsAWrongIncrementalSnapshot()
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var (idA, _) = await RunIndexAsync("full");

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
        Assert.Contains("an incremental run built it; --strategy=full rebuilds it while the existing snapshot remains readable.", rebuildOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("reusing it", rebuildOutput, StringComparison.Ordinal);
        Assert.Equal(idB, rebuiltId);

        await AssertMatchesFreshIndexAsync(idB);
        DateTime builtAtAfterRebuild;
        using (var store = OpenStore(DbPath))
        {
            var rebuilt = store.LoadSnapshot(idB)!;
            Assert.Equal(SnapshotBuildStrategies.Full, rebuilt.BuildStrategy);
            Assert.NotEmpty(store.GetSemanticChanges(idA, idB));
            builtAtAfterRebuild = rebuilt.CreatedAtUtc;
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
        using (var store = OpenStore(DbPath))
        {
            store.SetPinnedSnapshot(idA);
            store.SetPinnedSnapshot(idB);
        }

        WriteFile("P", "Svc.cs", RecoveryStateA);
        var (rebuiltId, _) = await RunIndexAsync("incremental", force: true);
        Assert.Equal(idA, rebuiltId);

        await AssertMatchesFreshIndexAsync(idA);
        var ws = WorkspaceId.Create(TestDir, Path.GetFullPath(SolutionPath)).Value;
        using (var store = OpenStore(DbPath))
        {
            Assert.Single(store.GetSnapshotIds(ws), id => id == idA);
            Assert.Equal(Lurp.Storage.SnapshotStatusValues.Complete, store.GetSnapshotStatus(idA, ws));
            var rebuiltRow = store.LoadSnapshot(idA)!;
            Assert.True(rebuiltRow.CreatedAtUtc > builtAtBefore);
            Assert.Equal(idB, rebuiltRow.PreviousSnapshotId);
            Assert.Equal(idA, store.GetPinnedSnapshot(ws)?.PreviousPinnedSnapshotId);
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
                IndexRunner.RunAsync(store, SolutionPath, [], "full", false, sink, false, true, CancellationToken.None));
        }

        Assert.Equal(
            $"snapshot '{idA}' is pinned; --force cannot rebuild it. Clear the pin with --mode=pin-snapshot --clear, or run without --force.",
            ex.Message);
        Assert.DoesNotContain("rebuilding it", sink.Output.ToString(), StringComparison.OrdinalIgnoreCase);

        var ws = WorkspaceId.Create(TestDir, Path.GetFullPath(SolutionPath)).Value;
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(Lurp.Storage.SnapshotStatusValues.Complete, store.GetSnapshotStatus(idA, ws));
            Assert.Equal(idA, store.GetPinnedSnapshot(ws)?.PinnedSnapshotId);
            Assert.Equal(builtAtBefore, store.LoadSnapshot(idA)!.CreatedAtUtc);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledRebuild_KeepsTheCompleteSnapshotReadable(bool force)
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        await RunIndexAsync("full");
        WriteFile("P", "Svc.cs", RecoveryStateB);
        var (target, _) = await RunIndexAsync("incremental");
        await using var session = McpSessionContext.Create(
            [$"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)}"]);
        var symbol = new GetSymbolTool(session);
        var impact = new ImpactTool(session);
        string[] ReadSnapshotQueries() =>
        [
            WithoutFreshnessTimestamp(symbol.LurpGetSymbol("P.Svc")),
            WithoutFreshnessTimestamp(impact.LurpImpact("P.Svc"))
        ];
        var before = ReadSnapshotQueries();
        string[]? during = null;
        Exception? readFailure = null;
        using var cancellation = new CancellationTokenSource();
        using var store = OpenStore(DbPath);
        var sink = new DuringExtractionSink(() =>
        {
            readFailure = Record.Exception(() => during = ReadSnapshotQueries());
            cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IndexRunner.RunAsync(
            store, SolutionPath, [], "full", output: sink, force: force, cancellationToken: cancellation.Token));

        Assert.Null(readFailure);
        Assert.Equal(target, store.GetSelectedLatestSnapshotId());
        Assert.Equal(before, during);
        Assert.Equal(before, ReadSnapshotQueries());
        Assert.Equal("cancelled", store.GetLatestSnapshotFailure()?.ReasonCode);
    }

    [Fact]
    public async Task Rebuild_PinnedDuringExtraction_KeepsBaselineAndRecordsFailureUntilSuccess()
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var (target, _) = await RunIndexAsync("full");
        using var store = OpenStore(DbPath);
        var baseline = store.LoadSnapshot(target)!;
        string? selectedDuring = null;
        var sink = new DuringExtractionSink(() =>
        {
            using var reader = OpenStore(DbPath);
            selectedDuring = reader.GetSelectedLatestSnapshotId();
            if (selectedDuring == target)
                reader.SetPinnedSnapshot(target);
        });

        var error = await Record.ExceptionAsync(() =>
            IndexRunner.RunAsync(store, SolutionPath, [], "full", output: sink, force: true));

        Assert.Equal(target, selectedDuring);
        Assert.Contains("pinned", Assert.IsType<InvalidOperationException>(error).Message);
        Assert.Equal(target, store.GetSelectedLatestSnapshotId());
        Assert.Equal(baseline.CreatedAtUtc, store.LoadSnapshot(target)!.CreatedAtUtc);
        var failure = Assert.IsType<SnapshotFailureRow>(store.GetLatestSnapshotFailure());
        Assert.NotEqual(target, failure.SnapshotId);
        Assert.Equal("full_index_failure", failure.ReasonCode);

        store.ClearPinnedSnapshot(baseline.WorkspaceId);
        await RunIndexAsync("full", force: true);
        Assert.Null(store.LoadSnapshotMetadata(failure.SnapshotId));
        Assert.Null(store.GetLatestSnapshotFailure());
    }

    [Fact]
    public async Task Rebuild_PromotionWriteFailure_RollsBackTheReplacement()
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var (target, _) = await RunIndexAsync("full");
        using var store = OpenStore(DbPath);
        var before = store.LoadSnapshot(target)!;
        using var connection = OpenDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_snapshot_promotion BEFORE UPDATE OF snapshot_id ON snapshot_symbols
            BEGIN
                SELECT RAISE(ABORT, 'injected promotion write failure');
            END;
            """;
        command.ExecuteNonQuery();

        var error = await Assert.ThrowsAsync<SqliteException>(() => RunIndexAsync("full", force: true));

        Assert.Contains("injected promotion write failure", error.Message);
        Assert.Equal(target, store.GetSelectedLatestSnapshotId());
        Assert.Equal(before.CreatedAtUtc, store.LoadSnapshot(target)!.CreatedAtUtc);
        await AssertMatchesFreshIndexAsync(target);
        var failure = Assert.IsType<SnapshotFailureRow>(store.GetLatestSnapshotFailure());
        Assert.NotEqual(target, failure.SnapshotId);
        Assert.Equal("full_index_failure", failure.ReasonCode);
    }

    [Theory]
    [InlineData("full", false, true)]
    [InlineData("full", true, true)]
    [InlineData("incremental", false, true)]
    [InlineData("full", true, false)]
    public async Task OutputJson_UsesCompletedSnapshot(string strategy, bool force, bool writable)
    {
        CreateProject("P", new Dictionary<string, string> { ["Svc.cs"] = RecoveryStateA });
        var (target, _) = await RunIndexAsync("full");
        using var store = OpenStore(DbPath);
        var before = store.LoadSnapshot(target)!.CreatedAtUtc;
        var destination = writable ? Path.Combine(TestDir, "manifest.json") : TestDir;

        List<string> args = ["--mode=index", $"--solution={SolutionPath}", $"--strategy={strategy}", $"--output-json={destination}"];
        if (force)
            args.Add("--force");
        var result = RunCli([.. args]);

        Assert.Equal(writable ? 0 : 1, result.ExitCode);
        Assert.Equal(target, store.GetSelectedLatestSnapshotId());
        Assert.Null(store.GetLatestSnapshotFailure());
        var selected = store.LoadSnapshot(target)!;
        Assert.Equal(force, selected.CreatedAtUtc > before);
        if (writable)
        {
            Assert.True(File.Exists(destination));
            using var json = JsonDocument.Parse(File.ReadAllText(destination));
            Assert.Equal(target, json.RootElement.GetProperty("snapshot_id").GetString());
        }
        else
        {
            var error = Assert.Single(result.Stderr.Split('\n'), line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            Assert.StartsWith("ERROR: Could not export snapshot manifest.", error);
        }
    }

    private static string WithoutFreshnessTimestamp(string response)
    {
        var json = JsonNode.Parse(response)!.AsObject();
        json["freshness"]!.AsObject().Remove("checked_at_utc");
        return json.ToJsonString();
    }

    private (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var argv = LurpTool.Argv([.. args, $"--output-dir={Path.GetDirectoryName(DbPath)}"]);
        return TestProcess.Run(argv[0], argv[1..], TimeSpan.FromMinutes(2));
    }

    private sealed class AfterSemanticDiffSink(Action onDiff) : IOutputSink
    {
        private bool _waitingForDiff;

        public void Write(string message)
        {
            if (message == "Computing semantic diff from previous snapshot... ")
                _waitingForDiff = true;
        }

        public void WriteLine(string message = "")
        {
            if (_waitingForDiff && message.StartsWith("done (", StringComparison.Ordinal))
                onDiff();
        }

        public void WriteErrorLine(string message = "") { }
    }

    private sealed class DuringExtractionSink(Action onExtraction) : IOutputSink
    {
        public void Write(string message)
        {
            if (message == "  [P] ")
                onExtraction();
        }

        public void WriteLine(string message = "") { }
        public void WriteErrorLine(string message = "") { }
    }

    private async Task AssertMatchesFreshIndexAsync(string promotedSnapshotId)
    {
        var freshDb = Path.Combine(TestDir, "fresh.db");
        var freshId = await RunFullIndexAsync(freshDb);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, promotedSnapshotId, freshDb, freshId);
    }
}
