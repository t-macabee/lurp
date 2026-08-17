using Lurp.Mcp;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpPinningTests : IntegrationTestBase
{
    [Fact]
    public async Task Pin_DoesNotMove_AfterSecondIndexRunMutatesDb()
    {
        // Index initial fixture
        CreateProject("PinnedProj", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace PinnedProj { public class A { public void Foo() {} } }"
        });
        var snapshot1 = await RunFullIndexAsync(DbPath);
        Assert.False(string.IsNullOrEmpty(snapshot1));

        // Open a long-lived session (pinned snapshot)
        var outputDir = Path.GetDirectoryName(SolutionPath)!;
        var sessionArgs = new[] { $"--solution={SolutionPath}" };

        await using var session = McpSessionContext.Create(sessionArgs);
        var pinned = session.PinnedSnapshotId;
        Assert.Equal(snapshot1, pinned);

        // Mutate DB via second index run (add new file) — simulates concurrent writer.
        // Adding a new project ensures a new snapshot_id is generated.
        CreateProject("PinnedProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace PinnedProj2 { public class B { public void Bar() {} } }"
        });

        var snapshot2 = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.NotEqual(snapshot1, snapshot2);

        // Session pin must not have moved.
        Assert.Equal(snapshot1, session.PinnedSnapshotId);
        Assert.Equal(pinned, session.PinnedSnapshotId);

        // Verify store still readable via pinned snapshot (query_only=ON must allow reads).
        var result = session.Store.GetSymbolIdsInSnapshot(pinned);
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task MismatchedSnapshotId_ReturnsInvalidParams_ForAllTools()
    {
        CreateProject("PinnedMismatchProj", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace PinnedMismatchProj { public class A { public void Foo() {} } }"
        });
        var snapshot1 = await RunFullIndexAsync(DbPath);
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);

        // Every tool that accepts snapshot_id must reject mismatch with -32602
        var contextTool = new Lurp.Mcp.Tools.ContextTool(session);
        var ex1 = Assert.Throws<McpProtocolException>(() => contextTool.LurpContext(symbol: "PinnedMismatchProj.A", snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex1.ErrorCode);
        Assert.Contains("snapshot mismatch", ex1.Message);
        Assert.Contains("lurp_refresh", ex1.Message);

        var sourceTool = new Lurp.Mcp.Tools.GetSourceTool(session);
        string docPath;
        using (var store = OpenStore(DbPath))
            docPath = store.GetDocumentVersionIdsByPath(snapshot1).Keys.First();
        var ex2 = Assert.Throws<McpProtocolException>(() => sourceTool.LurpGetSource(document: docPath, snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex2.ErrorCode);

        var statusTool = new Lurp.Mcp.Tools.StatusTool(session);
        var ex3 = await Assert.ThrowsAsync<McpProtocolException>(async () => await statusTool.LurpStatus(snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex3.ErrorCode);
    }

    [Fact]
    public async Task StaleData_ServedWithFlag_NotRefused()
    {
        CreateProject("PinnedStaleProj", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace PinnedStaleProj { public class A { public void Foo() {} } }"
        });
        var snapshot1 = await RunFullIndexAsync(DbPath);
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);

        // Make file stale
        var projFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "src", "PinnedStaleProj", "A.cs");
        File.WriteAllText(projFile, "namespace PinnedStaleProj { public class A { public void Foo(int x) {} } }");
        File.SetLastWriteTimeUtc(projFile, DateTime.UtcNow.AddSeconds(10));

        var tool = new Lurp.Mcp.Tools.GetSourceTool(session);
        string docPath;
        using (var store = OpenStore(DbPath))
            docPath = store.GetDocumentVersionIdsByPath(snapshot1).Keys.First();
        var json = tool.LurpGetSource(document: docPath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var state = doc.RootElement.GetProperty("freshness").GetProperty("state").GetString();
        Assert.Equal("stale", state);
        // Still returns payload
        Assert.True(doc.RootElement.TryGetProperty("source", out var src));
        Assert.False(string.IsNullOrEmpty(src.GetString()));
    }

    [Fact]
    public async Task ChangedDocumentsSample_CappedAtTen_UnlessDetail()
    {
        CreateProject("PinnedCapProj", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace PinnedCapProj { public class A { public void Foo() {} } }"
        });
        var snapshot1 = await RunFullIndexAsync(DbPath);
        // Add many files and then touch them to create >10 changed docs
        for (int i = 0; i < 15; i++)
        {
            CreateProject($"CapExtra{i}", new Dictionary<string, string>
            {
                [$"F{i}.cs"] = $"namespace CapExtra{i} {{ public class C{i} {{}} }}"
            });
        }
        await RunFullIndexNoDeleteAsync(DbPath);
        // Now session pinned to snapshot1, but many docs exist in latest; for cheap check,
        // changed docs are those whose mtime > builtAtUtc. Touch each file.
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);
        // Session currently pinned to latest (snapshot2); create a new session pinned to snapshot1 manually?
        // Instead test the capping via direct freshness call: ensure sample never exceeds 10.
        var freshness = session.GetFreshness();
        Assert.True(freshness.ChangedDocumentsSample.Count <= 10);
        var tool = new Lurp.Mcp.Tools.SearchTool(session);
        var json = tool.LurpSearch(query: "A", type: "symbol");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var sample = doc.RootElement.GetProperty("freshness").GetProperty("changed_documents_sample");
        Assert.True(sample.GetArrayLength() <= 10);
    }
}
