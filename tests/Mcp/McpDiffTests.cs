using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpDiffTests : IntegrationTestBase
{
    private async Task<string> IndexInitialAsync()
    {
        CreateProject("DiffProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace DiffProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private async Task<string> IndexSecondAsync()
    {
        WriteFile("DiffProj", "Models.cs", """
            namespace DiffProj {
                public class Foo {
                    public void Bar(int x) {}
                    public void Caller() { Bar(1); }
                    public void NewMethod() {}
                }
            }
            """);
        return await RunIncrementalIndexAsync();
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create(new[] { $"--solution={SolutionPath}" });

    [Fact]
    public async Task Diff_FromTo_Parity_WithDirectDiffer()
    {
        var snap1 = await IndexInitialAsync();
        var snap2 = await IndexSecondAsync();
        await using var session = CreateSession();
        var diff = new DiffTool(session);

        var json = diff.LurpDiff(from_snapshot: snap1, to_snapshot: snap2);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snap1, doc.RootElement.GetProperty("from_snapshot").GetString());
        Assert.Equal(snap2, doc.RootElement.GetProperty("to_snapshot").GetString());
        Assert.True(doc.RootElement.TryGetProperty("changes", out var changes));
        Assert.True(changes.GetArrayLength() > 0);
        Assert.True(doc.RootElement.TryGetProperty("change_count", out _));
        Assert.True(doc.RootElement.TryGetProperty("skipped_comparisons", out _));
        Assert.True(doc.RootElement.TryGetProperty("snapshot_id", out var sid) && sid.GetString() == snap2);
        Assert.True(doc.RootElement.TryGetProperty("freshness", out _));
        Assert.True(doc.RootElement.TryGetProperty("pinned", out _));

        using var store = OpenStore(DbPath);
        var differ = new SemanticDiffer(store, store, store, store);
        var (directChanges, _) = differ.ComputeDiff(snap1, snap2);
        Assert.Equal(directChanges.Count, changes.GetArrayLength());
        // each change has required fields matching DiffHandler shape
        foreach (var c in changes.EnumerateArray())
        {
            Assert.True(c.TryGetProperty("change_id", out _));
            Assert.True(c.TryGetProperty("change_type", out _));
            Assert.True(c.TryGetProperty("symbol_id", out _));
            Assert.True(c.TryGetProperty("created_at_utc", out _));
        }
    }

    [Fact]
    public async Task Diff_MissingSnapshot_ReturnsInvalidParams()
    {
        var snap1 = await IndexInitialAsync();
        var snap2 = await IndexSecondAsync();
        await using var session = CreateSession();
        var diff = new DiffTool(session);

        var ex1 = Assert.Throws<McpProtocolException>(() => diff.LurpDiff(from_snapshot: "nonexistent", to_snapshot: snap2));
        Assert.Equal(McpErrorCode.InvalidParams, ex1.ErrorCode);
        var ex2 = Assert.Throws<McpProtocolException>(() => diff.LurpDiff(from_snapshot: snap1, to_snapshot: "nonexistent"));
        Assert.Equal(McpErrorCode.InvalidParams, ex2.ErrorCode);
        var ex3 = Assert.Throws<McpProtocolException>(() => diff.LurpDiff(from_snapshot: null, to_snapshot: snap2));
        Assert.Equal(McpErrorCode.InvalidParams, ex3.ErrorCode);
        var ex4 = Assert.Throws<McpProtocolException>(() => diff.LurpDiff(from_snapshot: snap1, to_snapshot: null));
        Assert.Equal(McpErrorCode.InvalidParams, ex4.ErrorCode);
    }
}
