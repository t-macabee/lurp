using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpRefreshTests : IntegrationTestBase
{
    private async Task<string> IndexInitialAsync()
    {
        CreateProject("RefreshProj", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace RefreshProj { public class A { public void Foo() {} } }"
        });
        return await RunFullIndexAsync(DbPath);
    }

    [Fact]
    public async Task Refresh_NoAck_DoesNotMovePin()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        CreateProject("RefreshProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace RefreshProj2 { public class B { public void Bar() {} } }"
        });
        var snapshot2 = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.NotEqual(snapshot1, snapshot2);

        // Pin must not have moved yet
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        var refreshTool = new RefreshTool(session);
        var json = refreshTool.LurpRefresh();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshot1, doc.RootElement.GetProperty("old_snapshot_id").GetString());
        Assert.Equal(snapshot2, doc.RootElement.GetProperty("new_snapshot_id").GetString());
        Assert.True(doc.RootElement.GetProperty("changed").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("requires_ack").GetBoolean());
        // Pin must still be old
        Assert.Equal(snapshot1, session.PinnedSnapshotId);
    }

    [Fact]
    public async Task Refresh_WithAck_AdvancesPin()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);

        CreateProject("RefreshProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace RefreshProj2 { public class B { public void Bar() {} } }"
        });
        var snapshot2 = await RunFullIndexNoDeleteAsync(DbPath);

        var refreshTool = new RefreshTool(session);
        // First check without ack to get latest
        var jsonNoAck = refreshTool.LurpRefresh();
        using var docNoAck = JsonDocument.Parse(jsonNoAck);
        var latest = docNoAck.RootElement.GetProperty("new_snapshot_id").GetString()!;

        var jsonAck = refreshTool.LurpRefresh(ack: latest);
        using var docAck = JsonDocument.Parse(jsonAck);
        Assert.Equal(snapshot1, docAck.RootElement.GetProperty("old_snapshot_id").GetString());
        Assert.Equal(latest, docAck.RootElement.GetProperty("new_snapshot_id").GetString());
        Assert.False(docAck.RootElement.GetProperty("changed").GetBoolean());
        Assert.True(docAck.RootElement.GetProperty("pinned").GetBoolean());
        Assert.Equal(latest, session.PinnedSnapshotId);
        // New snapshot should be readable
        var ids = session.Store.GetSymbolIdsInSnapshot(latest);
        Assert.NotEmpty(ids);
    }

    [Fact]
    public async Task Refresh_WithMismatchedAck_ReturnsInvalidParams()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);

        CreateProject("RefreshProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace RefreshProj2 { public class B { public void Bar() {} } }"
        });
        await RunFullIndexNoDeleteAsync(DbPath);
        var tool = new RefreshTool(session);
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpRefresh(ack: "bogus-snapshot-id"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("snapshot mismatch", ex.Message);
    }

    [Fact]
    public async Task Refresh_WhenNoNewSnapshot_ReturnsNotChanged()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new RefreshTool(session);
        var json = tool.LurpRefresh();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshot1, doc.RootElement.GetProperty("old_snapshot_id").GetString());
        Assert.Equal(snapshot1, doc.RootElement.GetProperty("new_snapshot_id").GetString());
        Assert.False(doc.RootElement.GetProperty("changed").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("requires_ack").GetBoolean());
        // Ack with same should also return not changed
        var json2 = tool.LurpRefresh(ack: snapshot1);
        using var doc2 = JsonDocument.Parse(json2);
        Assert.False(doc2.RootElement.GetProperty("changed").GetBoolean());
        Assert.False(doc2.RootElement.GetProperty("requires_ack").GetBoolean());
        Assert.Equal(snapshot1, session.PinnedSnapshotId);
    }

    [Fact]
    public async Task ToolCall_OnStaleData_ReturnsStaleFlag_AndPayload()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);
        var contextTool = new ContextTool(session);

        // Touch a file to make pinned snapshot stale
        var projFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "src", "RefreshProj", "A.cs");
        File.WriteAllText(projFile, "namespace RefreshProj { public class A { public void Foo(int x) {} public void New() {} } }");
        File.SetLastWriteTimeUtc(projFile, DateTime.UtcNow.AddSeconds(5));

        // Add a new snapshot without moving pin
        CreateProject("RefreshProjStaleExtra", new Dictionary<string, string>
        {
            ["C.cs"] = "namespace RefreshProjStaleExtra { public class C {} }"
        });
        await RunFullIndexNoDeleteAsync(DbPath);

        // Session still pinned to old
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        // Any tool must still serve stale data with stale flag, not refuse
        string symbolId;
        using (var store = OpenStore(DbPath))
        {
            symbolId = store.GetSymbolIdsInSnapshot(snapshot1).First();
        }
        var json = contextTool.LurpContext(symbol: symbolId);
        using var doc = JsonDocument.Parse(json);
        var freshnessState = doc.RootElement.GetProperty("freshness").GetProperty("state").GetString();
        Assert.Equal("stale", freshnessState);
        // Payload must still be present
        Assert.True(doc.RootElement.TryGetProperty("capsule", out var cap));
        Assert.True(cap.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Pin_DoesNotMove_WhileServingOldData_AfterReindex()
    {
        var snapshot1 = await IndexInitialAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);
        var pinned = session.PinnedSnapshotId;
        Assert.Equal(snapshot1, pinned);

        CreateProject("RefreshProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace RefreshProj2 { public class B { public void Bar() {} } }"
        });
        var snapshot2 = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.NotEqual(snapshot1, snapshot2);
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        // Session must still serve old pin via any tool
        var tool = new GetSourceTool(session);
        string docPath;
        using (var store = OpenStore(DbPath))
            docPath = store.GetDocumentVersionIdsByPath(snapshot1).Keys.First();
        var json = tool.LurpGetSource(document: docPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshot1, doc.RootElement.GetProperty("snapshot_id").GetString());
        // Freshness may be fresh or stale depending on file mtimes, but pin is old
        Assert.Equal(pinned, doc.RootElement.GetProperty("snapshot_id").GetString());
    }
}
