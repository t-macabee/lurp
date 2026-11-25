using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Parity.Shared;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpIndexTests : IntegrationTestBase
{
    private async Task<string> IndexInitialAsync()
    {
        return await IndexFullAsync("IndexProj", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace IndexProj { public class A { public void Foo() {} } }"
        });
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static string GetString(JsonElement el, string name) => el.GetProperty(name).GetString()!;

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("", false)]
    public async Task MissingOrEmptyOperationId_IsRejectedWithoutStarting(string? operationId, bool cancel)
    {
        await IndexInitialAsync();
        await using var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={TestDir}"]);
        var state = new McpIndexSessionState();
        var tool = new IndexTool(session, state);
        string? startedId = null;
        try
        {
            var error = Assert.Throws<McpProtocolException>(() =>
            {
                using var result = Parse(tool.LurpIndex(operation_id: operationId, cancel: cancel));
                startedId = GetString(result.RootElement, "operation_id");
            });
            Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
            Assert.Null(state.RunningOperationId);
        }
        finally
        {
            if (startedId != null)
                using (await WaitForCompletionAsync(tool, startedId)) { }
        }
    }

    private static async Task<JsonDocument> WaitForCompletionAsync(IndexTool tool, string operationId, int timeoutMs = 60000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var json = tool.LurpIndex(operation_id: operationId);
            var doc = Parse(json);
            var status = GetString(doc.RootElement, "status");
            if (status != "running")
                return doc;
            doc.Dispose();
            await Task.Delay(250);
        }
        throw new TimeoutException($"operation {operationId} did not finish within {timeoutMs}ms");
    }

    [Fact]
    public async Task Run_Completes_And_NewSnapshotAppears()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var state = new McpIndexSessionState();
        var tool = new IndexTool(session, state);

        // Modify source so incremental will see a change — add a new project.
        CreateProject("IndexProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace IndexProj2 { public class B { public void Bar() {} } }"
        });

        var jsonStart = tool.LurpIndex(strategy: "incremental");
        using var startDoc = Parse(jsonStart);
        Assert.Equal("running", GetString(startDoc.RootElement, "status"));
        var opId = GetString(startDoc.RootElement, "operation_id");
        Assert.False(string.IsNullOrEmpty(opId));

        using var finalDoc = await WaitForCompletionAsync(tool, opId);
        var finalStatus = GetString(finalDoc.RootElement, "status");
        Assert.Equal("completed", finalStatus);
        var newSnapshotId = finalDoc.RootElement.TryGetProperty("result_snapshot_id", out var rs) ? rs.GetString() : null;
        Assert.False(string.IsNullOrEmpty(newSnapshotId));
        Assert.NotEqual(snapshot1, newSnapshotId);

        // Verify new snapshot is visible via a fresh store, but session pin hasn't moved (D1).
        Assert.Equal(snapshot1, session.PinnedSnapshotId);
        using var checkStore = OpenStore(DbPath);
        var latest = checkStore.GetLatestSnapshotId();
        Assert.Equal(newSnapshotId, latest);
        Assert.Equal(newSnapshotId, checkStore.GetSelectedLatestSnapshotId());
        // Ensure new snapshot has symbols
        var ids = checkStore.GetSymbolIdsInSnapshot(newSnapshotId!);
        Assert.NotEmpty(ids);
    }

    [Fact]
    public async Task SecondCall_WhileRunning_IsRejected_WithInvalidParams()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var state = new McpIndexSessionState();
        var tool = new IndexTool(session, state);

        CreateProject("IndexProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace IndexProj2 { public class B { public void Bar() {} } }"
        });

        var json1 = tool.LurpIndex(strategy: "incremental");
        using var doc1 = Parse(json1);
        var opId1 = GetString(doc1.RootElement, "operation_id");

        // Second call while first is running must be rejected with -32602 (InvalidParams).
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpIndex(strategy: "full"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("already running", ex.Message);

        // Cleanup: wait for first to finish so test temp dir can be deleted.
        using var final = await WaitForCompletionAsync(tool, opId1);
        Assert.Equal("completed", GetString(final.RootElement, "status"));
        Assert.Equal(snapshot1, session.PinnedSnapshotId);
    }

    [Fact]
    public async Task Cancel_MidRun_ReturnsRunningWithCancelRequested_ThenTransitionsToCancelled()
    {
        CreateProject("IndexProj", new Dictionary<string, string> { ["A.cs"] = "namespace IndexProj; public class A {}" });
        IndexedDirFixture.Create(1, stalePin: false, out _, out var snapshot1, outputDir: TestDir);
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var state = new McpIndexSessionState();
        using var gatedStore = new GatedStoreFactory();
        var tool = new IndexTool(session, state, gatedStore.Create);

        var jsonStart = tool.LurpIndex(strategy: "full");
        using var startDoc = Parse(jsonStart);
        AssertOperationShape(startDoc.RootElement);
        var opId = GetString(startDoc.RootElement, "operation_id");

        // The run is held inside the background task before IndexRunner starts, so it is
        // still exactly 'running' when the cancel request arrives: no race, no sleep.
        Assert.True(gatedStore.WaitForEntered(TimeSpan.FromSeconds(30)),
            "the background index never reached the store factory, so the run was not in flight");

        // Request cancellation via operation_id.
        using (var cancelDoc = Parse(tool.LurpIndex(operation_id: opId, cancel: true)))
        {
            AssertOperationShape(cancelDoc.RootElement);
            Assert.Equal("running", GetString(cancelDoc.RootElement, "status"));
            Assert.True(cancelDoc.RootElement.GetProperty("cancel_requested").GetBoolean());
            Assert.Equal(JsonValueKind.Null, cancelDoc.RootElement.GetProperty("finished_at_utc").ValueKind);
        }

        gatedStore.Release();

        // Wait for terminal state.
        using var finalDoc = await WaitForCompletionAsync(tool, opId, timeoutMs: 30000);
        Assert.Equal("cancelled", GetString(finalDoc.RootElement, "status"));
        Assert.True(finalDoc.RootElement.GetProperty("cancel_requested").GetBoolean());
        Assert.Equal(JsonValueKind.String, finalDoc.RootElement.GetProperty("finished_at_utc").ValueKind);
        using var terminalCancel = Parse(tool.LurpIndex(operation_id: opId, cancel: true));
        Assert.Equal(finalDoc.RootElement.GetRawText(), terminalCancel.RootElement.GetRawText());

        // Snapshot-immutability: the cancelled run wrote no new snapshot and advanced no pin.
        using var store = OpenStore(DbPath);
        Assert.Equal(snapshot1, store.GetLatestSnapshotId());
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        var workspaceId = store.LoadSnapshot(snapshot1)!.WorkspaceId;
        var completeSnapshots = store.GetSnapshotIds(workspaceId);
        Assert.True(completeSnapshots.Count == 1 && completeSnapshots[0] == snapshot1,
            $"a cancelled run must not leave a second complete snapshot; complete snapshots: [{string.Join(", ", completeSnapshots)}]");
    }

    [Fact]
    public async Task IndexTool_WithDatabasePin_ReportsBuiltSnapshotId()
    {
        var snapshotA = await IndexInitialAsync();
        CreateProject("IndexProj2", new Dictionary<string, string> { ["B.cs"] = "namespace IndexProj2; public class B {}" });
        var snapshotB = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.NotEqual(snapshotA, snapshotB);
        using (var store = OpenStore(DbPath))
            store.SetPinnedSnapshot(snapshotA);

        await using var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={TestDir}"]);
        CreateProject("IndexProj3", new Dictionary<string, string> { ["C.cs"] = "namespace IndexProj3; public class C {}" });
        var tool = new IndexTool(session, new McpIndexSessionState());
        using var start = Parse(tool.LurpIndex(strategy: "full"));
        Assert.Equal(snapshotA, GetString(start.RootElement, "previous_snapshot_id"));
        Assert.Empty(start.RootElement.GetProperty("progress").EnumerateArray());
        using var final = await WaitForCompletionAsync(tool, GetString(start.RootElement, "operation_id"));
        Assert.Equal("completed", GetString(final.RootElement, "status"));

        using var check = OpenStore(DbPath);
        var snapshotC = check.GetSelectedLatestSnapshotId();
        Assert.NotNull(snapshotC);
        Assert.NotEqual(snapshotA, snapshotC);
        Assert.NotEqual(snapshotB, snapshotC);
        Assert.Equal(snapshotC, GetString(final.RootElement, "result_snapshot_id"));
        Assert.Equal(snapshotA, GetString(final.RootElement, "previous_snapshot_id"));
        Assert.Contains(final.RootElement.GetProperty("progress").EnumerateArray(),
            line => line.GetString()!.EndsWith($"snapshot: {snapshotC}", StringComparison.Ordinal));
        Assert.Equal(snapshotA, check.GetPinnedSnapshot(check.LoadSnapshot(snapshotA)!.WorkspaceId)?.PinnedSnapshotId);
        Assert.Equal(snapshotA, session.PinnedSnapshotId);
    }

    [Fact]
    public async Task Serve_Index_CancelOverStdio_KeepsOneShape()
    {
        var initial = await IndexInitialAsync();
        // Use a new identity so the forced run cannot replace the original snapshot in place.
        WriteFile("IndexProj", "A.cs", "namespace IndexProj; public class A { public void Bar() {} }");
        using var server = McpServeProcess.Start(LurpTool.Argv("--mode=serve", "--enable-write-tools",
            $"--solution={SolutionPath}", $"--output-dir={TestDir}"));
        var timeout = TimeSpan.FromSeconds(60);
        await server.Client.InitializeAsync("test", timeout);

        async Task<JsonElement> CallAsync(object arguments, CancellationToken deadline)
        {
            var reply = await server.Client.SendAsync("tools/call", new { name = "lurp_index", arguments }, timeout)
                .WaitAsync(deadline);
            var content = Assert.Single(reply.GetProperty("result").GetProperty("content").EnumerateArray());
            using var json = Parse(GetString(content, "text"));
            AssertOperationShape(json.RootElement);
            return json.RootElement.Clone();
        }

        using var deadline = new CancellationTokenSource(timeout);
        var start = await CallAsync(new { strategy = "full", force = true }, deadline.Token);
        Assert.Equal("running", GetString(start, "status"));
        Assert.False(start.GetProperty("cancel_requested").GetBoolean());
        var operation_id = GetString(start, "operation_id");
        var cancel = await CallAsync(new { operation_id, cancel = true }, deadline.Token);

        JsonElement final;
        do
        {
            final = await CallAsync(new { operation_id }, deadline.Token);
            if (GetString(final, "status") == "running")
                await Task.Delay(100, deadline.Token);
        }
        while (GetString(final, "status") == "running");

        var status = GetString(final, "status");
        Assert.True(status is "cancelled" or "completed", $"Unexpected operation state: {final}");
        var wasRunning = GetString(cancel, "status") == "running";
        Assert.Equal(wasRunning, cancel.GetProperty("cancel_requested").GetBoolean());
        Assert.Equal(wasRunning, final.GetProperty("cancel_requested").GetBoolean());
        Assert.Equal(JsonValueKind.String, final.GetProperty("finished_at_utc").ValueKind);
        if (status == "cancelled")
        {
            Assert.Equal(JsonValueKind.Null, final.GetProperty("result_snapshot_id").ValueKind);
            using var store = OpenStore(DbPath);
            Assert.Equal(initial, store.GetSelectedLatestSnapshotId());
        }
    }

    [Fact]
    public async Task Index_UnreadableWorkspace_ErrorDataIsTheReasonPayload()
    {
        await IndexInitialAsync();
        var solutionDir = Path.Combine(TestDir, "unreadable");
        WriteUnreadableSolution(solutionDir);
        var solution = Path.Combine(solutionDir, "Test.slnx");
        try
        {
            // Serve needs a complete snapshot at startup. Seed the store with the readable fixture above.
            await using var session = McpSessionContext.Create([$"--solution={solution}", $"--output-dir={TestDir}"]);
            var tool = new IndexTool(session, new McpIndexSessionState());
            using var start = Parse(tool.LurpIndex(strategy: "full"));
            using var final = await WaitForCompletionAsync(tool, GetString(start.RootElement, "operation_id"));
            Assert.Equal("failed", GetString(final.RootElement, "status"));
            Assert.Equal("workspace_unreadable", GetString(final.RootElement, "error_code"));
            var data = final.RootElement.GetProperty("error_data");
            string[] reasonKeys = ["reason_code", "message", "remediation"];
            Assert.Equal(reasonKeys, data.EnumerateObject().Select(p => p.Name));
            Assert.Equal("workspace_unreadable", GetString(data, "reason_code"));
            Assert.Equal(GetString(final.RootElement, "error"), GetString(data, "message"));
            Assert.Contains("dotnet restore", GetString(data, "remediation"));
        }
        finally
        {
            TestDatabase.DeleteSolutionCache(solution);
        }
    }

    [Fact]
    public async Task Index_MalformedSolution_RemediationNamesSolution()
    {
        await IndexInitialAsync();
        File.WriteAllText(SolutionPath, "<NotASolution />");
        await using var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={TestDir}"]);
        var tool = new IndexTool(session, new McpIndexSessionState());

        using var start = Parse(tool.LurpIndex(strategy: "full"));
        using var final = await WaitForCompletionAsync(tool, GetString(start.RootElement, "operation_id"));

        Assert.Equal("failed", GetString(final.RootElement, "status"));
        Assert.Equal("workspace_unreadable", GetString(final.RootElement, "error_code"));
        var remediation = GetString(final.RootElement.GetProperty("error_data"), "remediation");
        Assert.Contains(SolutionPath, remediation);
    }

    private static readonly string[] OperationKeys =
    [
        "operation_id", "status", "started_at_utc", "finished_at_utc", "progress", "error",
        "error_code", "error_data", "result_snapshot_id", "previous_snapshot_id", "cancel_requested"
    ];

    private static void AssertOperationShape(JsonElement operation) =>
        Assert.Equal(OperationKeys, operation.EnumerateObject().Select(p => p.Name));

    [Fact]
    public async Task Refresh_AfterCompletion_AdvancesPin()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var state = new McpIndexSessionState();
        var indexTool = new IndexTool(session, state);

        CreateProject("IndexProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace IndexProj2 { public class B { public void Bar() {} } }"
        });

        var jsonStart = indexTool.LurpIndex(strategy: "incremental");
        using var startDoc = Parse(jsonStart);
        var opId = GetString(startDoc.RootElement, "operation_id");

        using var finalDoc = await WaitForCompletionAsync(indexTool, opId);
        Assert.Equal("completed", GetString(finalDoc.RootElement, "status"));
        var newId = GetString(finalDoc.RootElement, "result_snapshot_id");
        Assert.NotEqual(snapshot1, newId);
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        // lurp_refresh without ack reports changed.
        var refreshTool = new RefreshTool(session);
        var jsonRefreshNoAck = refreshTool.LurpRefresh();
        using var noAckDoc = Parse(jsonRefreshNoAck);
        Assert.True(noAckDoc.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(newId, GetString(noAckDoc.RootElement, "new_snapshot_id"));
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        // Ack advances.
        var jsonRefreshAck = refreshTool.LurpRefresh(ack: newId);
        using var ackDoc = Parse(jsonRefreshAck);
        Assert.Equal(newId, GetString(ackDoc.RootElement, "new_snapshot_id"));
        Assert.Equal(newId, session.PinnedSnapshotId);
        Assert.False(ackDoc.RootElement.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task OtherTools_KeepAnswering_FromOldPin_WhileRunIsActive()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var state = new McpIndexSessionState();

        // Move the store's latest past the session pin without moving the pin. Reads must
        // answer from the pin (snapshot1), never from the newer complete snapshot, so this
        // assertion cannot be satisfied by a tool that simply reads 'latest'.
        WriteFile("IndexProj", "A.cs", "namespace IndexProj { public class A { public void Foo() {} public void Foo2() {} } }");
        var latestAfterSecondRun = await RunIncrementalIndexAsync();
        Assert.NotEqual(snapshot1, latestAfterSecondRun);
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        // Capture a known symbol from old snapshot.
        string symbolId;
        string docPath;
        using (var store = OpenStore(DbPath))
        {
            symbolId = store.GetSymbolIdsInSnapshot(snapshot1).First();
            docPath = store.GetDocumentVersionIdsByPath(snapshot1).Keys.First();
        }

        // A third run has a different deterministic identity, so it really writes while the
        // queries below run.
        CreateProject("IndexProj3", new Dictionary<string, string>
        {
            ["C.cs"] = "namespace IndexProj3 { public class C { public void Baz() {} } }"
        });

        using var gatedStore = new GatedStoreFactory();
        var indexTool = new IndexTool(session, state, gatedStore.Create);
        var jsonStart = indexTool.LurpIndex(strategy: "full");
        using var startDoc = Parse(jsonStart);
        var opId = GetString(startDoc.RootElement, "operation_id");

        // Hold the background run inside the store factory: the operation stays 'running'
        // for as long as the assertions below take, so the in-flight case is the tested one.
        Assert.True(gatedStore.WaitForEntered(TimeSpan.FromSeconds(30)),
            "the background index never reached the store factory, so the run was not in flight");

        using (var pollDoc = Parse(indexTool.LurpIndex(operation_id: opId)))
            Assert.Equal("running", GetString(pollDoc.RootElement, "status"));

        // While running, other tools must still answer from old pin.
        var searchTool = new SearchTool(session);
        var contextTool = new ContextTool(session);
        var getSourceTool = new GetSourceTool(session);

        var searchJson = searchTool.LurpSearch(query: "A");
        using var searchDoc = Parse(searchJson);
        Assert.Equal(snapshot1, GetString(searchDoc.RootElement, "snapshot_id"));

        var ctxJson = contextTool.LurpContext(symbol: symbolId);
        using var ctxDoc = Parse(ctxJson);
        Assert.Equal(snapshot1, GetString(ctxDoc.RootElement, "snapshot_id"));

        var srcJson = getSourceTool.LurpGetSource(document: docPath);
        using var srcDoc = Parse(srcJson);
        Assert.Equal(snapshot1, GetString(srcDoc.RootElement, "snapshot_id"));

        gatedStore.Release();

        using var final = await WaitForCompletionAsync(indexTool, opId);
        Assert.Equal("completed", GetString(final.RootElement, "status"));
    }

    [Fact]
    public async Task Cancel_DuringWrite_MarksSnapshotFailedAndKeepsPreviousCompleteSnapshot()
    {
        var snapshot1 = await IndexInitialAsync();

        // A different workspace yields a different deterministic snapshot identity, so this
        // run writes a new snapshot row instead of reusing the identical complete one.
        CreateProject("IndexProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace IndexProj2 { public class B { public void Bar() {} } }"
        });

        using var cts = new CancellationTokenSource();
        var sink = new CancelOnSnapshotRowSink(cts);

        using var store = OpenStore(DbPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IndexRunner.RunAsync(store, SolutionPath, [], "full", false, sink, false, false, cts.Token));

        Assert.True(sink.CancelledAfterSnapshotRow,
            $"the run never reached the point after the snapshot row was written; lines: {sink.DescribeLines()}");

        // The interrupted run left the previous complete snapshot as the latest one.
        Assert.Equal(snapshot1, store.GetLatestSnapshotId());

        var workspaceId = store.LoadSnapshot(snapshot1)!.WorkspaceId;
        var attempted = ReadSnapshotIdsIncludingIncomplete(workspaceId)
            .Where(id => id != snapshot1)
            .ToList();
        Assert.True(attempted.Count == 1,
            $"the cancelled run must leave exactly one non-complete snapshot row; found [{string.Join(", ", attempted)}]");
        Assert.Equal(SnapshotStatusValues.Failed, store.GetSnapshotStatus(attempted[0], workspaceId));
    }

    [Fact]
    public async Task Run_PublishesFinalStatus_OnlyAfterItReleasesStoreAndLocks()
    {
        await IndexInitialAsync();
        await using var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={TestDir}"]);
        using var holdingStore = new HoldOnDisposeStoreFactory();
        var tool = new IndexTool(session, new McpIndexSessionState(), holdingStore.Create);
        CreateProject("IndexProj2", new Dictionary<string, string> { ["B.cs"] = "namespace IndexProj2; public class B {}" });

        using var start = Parse(tool.LurpIndex(strategy: "full"));
        var opId = GetString(start.RootElement, "operation_id");

        // A client that sees a final status can delete the database or start the next run at once,
        // so the run must not report one while it still holds its writer store.
        Assert.True(holdingStore.WaitForDisposeEntered(TimeSpan.FromSeconds(60)),
            "the background run never disposed its writer store");
        using (var held = Parse(tool.LurpIndex(operation_id: opId)))
            Assert.Equal("running", GetString(held.RootElement, "status"));

        holdingStore.Release();
        using var final = await WaitForCompletionAsync(tool, opId);
        Assert.Equal("completed", GetString(final.RootElement, "status"));
        foreach (var path in new[] { SolutionPath, DbPath })
        {
            using var runLock = IndexRunLock.TryAcquire(IndexRunLock.NormalizeKey(Path.GetFullPath(path)));
            Assert.True(runLock != null, $"the completed run still holds the index run lock of {path}");
        }
    }

    /// <summary>All snapshot rows of the workspace, whatever their status.</summary>
    private List<string> ReadSnapshotIdsIncludingIncomplete(string workspaceId)
    {
        using var connection = OpenDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_id FROM snapshots WHERE workspace_id = @workspaceId ORDER BY built_at_utc;";
        command.Parameters.AddWithValue("@workspaceId", workspaceId);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    ///     Store factory for <see cref="IndexTool" /> that blocks the background run before
    ///     <see cref="IndexRunner.RunAsync" /> starts. Used to hold an operation at a known
    ///     point ('running', nothing written) instead of sleeping and hoping.
    /// </summary>
    private sealed class GatedStoreFactory : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        /// <summary>Waits until the background run has entered the factory.</summary>
        public bool WaitForEntered(TimeSpan timeout) => _entered.Wait(timeout);

        /// <summary>Lets the background run proceed past the gate.</summary>
        public void Release() => _release.Set();

        public SqliteIndexStore Create(string path)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(60)))
                throw new InvalidOperationException("the gated store factory was never released; the test did not reach its release step");
            return new SqliteIndexStore(path);
        }

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    /// <summary>
    ///     Store factory for <see cref="IndexTool" /> whose stores stop inside <c>Dispose</c> until
    ///     the test releases them. Holds the background run at the point where it frees its store.
    /// </summary>
    private sealed class HoldOnDisposeStoreFactory : IDisposable
    {
        private readonly ManualResetEventSlim _disposeEntered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public bool WaitForDisposeEntered(TimeSpan timeout) => _disposeEntered.Wait(timeout);

        public void Release() => _release.Set();

        public SqliteIndexStore Create(string path) => new HoldOnDisposeStore(path, this);

        public void Dispose()
        {
            _release.Set();
            _disposeEntered.Dispose();
            _release.Dispose();
        }

        private sealed class HoldOnDisposeStore(string path, HoldOnDisposeStoreFactory owner) : SqliteIndexStore(path)
        {
            protected override void Dispose(bool disposing)
            {
                owner._disposeEntered.Set();
                if (!owner._release.Wait(TimeSpan.FromSeconds(60)))
                    throw new InvalidOperationException("the held store was never released; the test did not reach its release step");
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    ///     Output sink that cancels the run once the snapshot row exists: it fires on the
    ///     line printed immediately after <c>manifest.Save</c> wrote the snapshot rows.
    /// </summary>
    private sealed class CancelOnSnapshotRowSink : IOutputSink
    {
        private const string SnapshotSaveStart = "Saving snapshot to database";
        private readonly CancellationTokenSource _cts;
        private readonly List<string> _lines = [];
        private bool _snapshotRowWriteStarted;

        public CancelOnSnapshotRowSink(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public bool CancelledAfterSnapshotRow { get; private set; }

        public void Write(string message)
        {
            _lines.Add(message);
            if (message.Contains(SnapshotSaveStart, StringComparison.Ordinal))
                _snapshotRowWriteStarted = true;
        }

        public void WriteLine(string message = "")
        {
            _lines.Add(message);
            if (!_snapshotRowWriteStarted || CancelledAfterSnapshotRow) return;
            CancelledAfterSnapshotRow = true;
            _cts.Cancel();
        }

        public void WriteErrorLine(string message = "")
        {
            _lines.Add("[stderr] " + message);
        }

        public string DescribeLines() => string.Join(" | ", _lines);
    }
}
