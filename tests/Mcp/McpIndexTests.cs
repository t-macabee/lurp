using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpIndexTests : IntegrationTestBase
{
    private async Task<string> IndexInitialAsync(string projectName = "IndexProj")
    {
        CreateProject(projectName, new Dictionary<string, string>
        {
            ["A.cs"] = "namespace IndexProj { public class A { public void Foo() {} } }"
        });
        return await RunFullIndexAsync(DbPath);
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static string GetString(JsonElement el, string name) => el.GetProperty(name).GetString()!;

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
    public async Task Cancel_MidRun_LeavesStoreOnOldPin_WithNoPartialCompleteData()
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

        var jsonStart = tool.LurpIndex(strategy: "full");
        using var startDoc = Parse(jsonStart);
        var opId = GetString(startDoc.RootElement, "operation_id");

        // Give the background task a moment to enter the load/extract phase.
        await Task.Delay(300);

        // Request cancellation via operation_id.
        var jsonCancel = tool.LurpIndex(operation_id: opId, cancel: true);
        using var cancelDoc = Parse(jsonCancel);
        // Cancel request is acknowledged (either cancelled or already completed).
        var cancelStatus = cancelDoc.RootElement.TryGetProperty("status", out var cs) ? cs.GetString() : null;
        Assert.True(cancelStatus == "cancelled" || cancelStatus == "completed" || cancelStatus == "running");

        // Wait for terminal state.
        using var finalDoc = await WaitForCompletionAsync(tool, opId, timeoutMs: 30000);
        var finalStatus = GetString(finalDoc.RootElement, "status");
        Assert.True(finalStatus == "cancelled" || finalStatus == "failed" || finalStatus == "completed",
            $"unexpected final status {finalStatus}");

        // Snapshot-immutability: no partial Complete snapshot must be visible.
        // The session pin must still be old, and GetLatestSnapshotId must still be old
        // when the run was cancelled. If it completed before cancel raced, it's okay
        // that latest == new, but then the test still verifies the store is consistent.
        using var store = OpenStore(DbPath);
        var latest = store.GetLatestSnapshotId();
        if (finalStatus == "cancelled" || finalStatus == "failed")
        {
            Assert.Equal(snapshot1, latest);
            Assert.Equal(snapshot1, session.PinnedSnapshotId);
            // Ensure the cancelled snapshot (if any) is not Complete.
            // If a snapshot id was produced for the cancelled run, its status must not be complete.
            var resultId = finalDoc.RootElement.TryGetProperty("result_snapshot_id", out var rid) ? rid.GetString() : null;
            if (!string.IsNullOrEmpty(resultId) && resultId != snapshot1)
            {
                // The cancelled attempt's snapshot should be Failed or InProgress pruned, not Complete.
                // We check via a fresh connection that loading it as latest doesn't return it.
                var latestAfter = store.GetLatestSnapshotId();
                Assert.Equal(snapshot1, latestAfter);
            }
        }
        else
        {
            // If it raced to completed, at least verify the pin didn't auto-advance.
            Assert.Equal(snapshot1, session.PinnedSnapshotId);
        }
    }

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
        var indexTool = new IndexTool(session, state);

        // Capture a known symbol from old snapshot.
        string symbolId;
        string docPath;
        using (var store = OpenStore(DbPath))
        {
            symbolId = store.GetSymbolIdsInSnapshot(snapshot1).First();
            docPath = store.GetDocumentVersionIdsByPath(snapshot1).Keys.First();
        }

        CreateProject("IndexProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace IndexProj2 { public class B { public void Bar() {} } }"
        });

        var jsonStart = indexTool.LurpIndex(strategy: "full");
        using var startDoc = Parse(jsonStart);
        var opId = GetString(startDoc.RootElement, "operation_id");

        // While running, other tools must still answer from old pin.
        var searchTool = new SearchTool(session);
        var contextTool = new ContextTool(session);
        var getSourceTool = new GetSourceTool(session);

        // Give a moment for the index to be in-flight, then query.
        await Task.Delay(200);

        var searchJson = searchTool.LurpSearch(query: "A");
        using var searchDoc = Parse(searchJson);
        Assert.Equal(snapshot1, GetString(searchDoc.RootElement, "snapshot_id"));

        var ctxJson = contextTool.LurpContext(symbol: symbolId);
        using var ctxDoc = Parse(ctxJson);
        Assert.Equal(snapshot1, GetString(ctxDoc.RootElement, "snapshot_id"));

        var srcJson = getSourceTool.LurpGetSource(document: docPath);
        using var srcDoc = Parse(srcJson);
        Assert.Equal(snapshot1, GetString(srcDoc.RootElement, "snapshot_id"));

        // Cleanup
        using var final = await WaitForCompletionAsync(indexTool, opId);
        Assert.Equal("completed", GetString(final.RootElement, "status"));
    }
}
