using System.Text.Json.Nodes;
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
        return await IndexFullAsync("DiffProj", new Dictionary<string, string>
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
    }

    private async Task<string> IndexSecondAsync()
    {
        WriteFile("DiffProj", "Models.cs", """
            namespace DiffProj {
                public class Foo {
                    public void Bar(int x) {}
                    public void Caller() { Bar(1); }
                    // A different return type keeps the differ from reading Bar -> NewMethod as a rename.
                    public int NewMethod() => 1;
                }
            }
            """);
        return await RunIncrementalIndexAsync();
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}"]);

    [Fact]
    public async Task Diff_RepeatedComputations_HaveStableChangeIds()
    {
        var from = await IndexInitialAsync();
        var to = await IndexSecondAsync();
        using var store = OpenStore(DbPath);
        var differ = new SemanticDiffer(store, store, store);
        var first = differ.ComputeDiff(from, to).Changes;
        var second = differ.ComputeDiff(from, to).Changes;
        Assert.NotEmpty(first);
        Assert.Equal(first.Select(c => c.ChangeId), second.Select(c => c.ChangeId));
    }

    [Fact]
    public async Task Diff_PagesAreStableAndBoundToBothSnapshots()
    {
        var from = await IndexInitialAsync();
        var to = await IndexSecondAsync();
        await using var session = CreateSession();
        var tool = new DiffTool(session);
        List<string> ReadPages()
        {
            var ids = new List<string>();
            string? cursor = null;
            do
            {
                using var page = JsonDocument.Parse(tool.LurpDiff(from, to, limit: 2, cursor: cursor));
                var root = page.RootElement;
                ids.AddRange(root.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("change_id").GetString()!));
                Assert.True(root.GetProperty("changes").GetArrayLength() <= 2);
                Assert.True(root.GetProperty("change_count").GetInt32() > 2);
                cursor = root.GetProperty("next_cursor").GetString();
            } while (cursor != null);
            return ids;
        }
        var first = ReadPages();
        Assert.Equal(first, ReadPages());
        Assert.Equal(first.Count, first.Distinct().Count());
        using var firstPage = JsonDocument.Parse(tool.LurpDiff(from, to, limit: 2));
        var token = firstPage.RootElement.GetProperty("next_cursor").GetString();
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpDiff(from, from, limit: 2, cursor: token));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

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
        var differ = new SemanticDiffer(store, store, store);
        var (directChanges, _) = differ.ComputeDiff(snap1, snap2);
        Assert.Equal(directChanges.Count, changes.GetArrayLength());
        // Multiple edges can change for one symbol. Match each occurrence by key and detail.
        var remaining = directChanges.Select(change =>
            (Key: (change.ChangeType, change.SymbolId),
             Detail: change.DetailJson is { } detail ? JsonNode.Parse(detail) : null)).ToList();
        foreach (var row in changes.EnumerateArray())
        {
            var key = (row.GetProperty("change_type").GetString()!, row.GetProperty("symbol_id").GetString()!);
            var actualDetail = JsonNode.Parse(row.GetProperty("detail").GetRawText());
            var index = remaining.FindIndex(change => change.Key == key && JsonNode.DeepEquals(change.Detail, actualDetail));
            Assert.True(index >= 0, $"Unexpected change or detail for {key}: {actualDetail}");
            remaining.RemoveAt(index);
            Assert.True(row.TryGetProperty("change_id", out _));
            Assert.True(row.TryGetProperty("created_at_utc", out _));
        }
        Assert.Empty(remaining);
        Assert.Contains(directChanges, change => change.DetailJson is not null);

        var symbolChanges = changes.EnumerateArray().Select(row => (
            Type: row.GetProperty("change_type").GetString(),
            Symbol: SymbolId.DocCommentIdOf(row.GetProperty("symbol_id").GetString()!)))
            .Where(change => change.Type is "symbol_added" or "symbol_removed")
            .OrderBy(change => change.Symbol, StringComparer.Ordinal);
        Assert.Equal(
            [("symbol_removed", "M:DiffProj.Foo.Bar"),
             ("symbol_added", "M:DiffProj.Foo.Bar(System.Int32)"),
             ("symbol_added", "M:DiffProj.Foo.NewMethod")],
            symbolChanges);
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
        Assert.Equal("snapshot 'nonexistent' not found.", ex1.Message);
        var ex2 = Assert.Throws<McpProtocolException>(() => diff.LurpDiff(from_snapshot: snap1, to_snapshot: "nonexistent"));
        Assert.Equal(McpErrorCode.InvalidParams, ex2.ErrorCode);
        Assert.Equal("snapshot 'nonexistent' not found.", ex2.Message);
        var ex3 = Assert.Throws<McpProtocolException>(() => diff.LurpDiff(from_snapshot: null, to_snapshot: snap2));
        Assert.Equal(McpErrorCode.InvalidParams, ex3.ErrorCode);
        Assert.Equal("from_snapshot is required.", ex3.Message);
        var ex4 = Assert.Throws<McpProtocolException>(() => diff.LurpDiff(from_snapshot: snap1, to_snapshot: null));
        Assert.Equal(McpErrorCode.InvalidParams, ex4.ErrorCode);
        Assert.Equal("to_snapshot is required.", ex4.Message);
    }
}
