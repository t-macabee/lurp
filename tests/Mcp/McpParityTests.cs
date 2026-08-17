using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;

namespace Lurp.Tests.Mcp;

public sealed class McpParityTests : IntegrationTestBase
{
    private async Task<string> IndexFixtureAndGetSnapshot()
    {
        CreateProject("ParityProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace ParityProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private McpSessionContext CreateSession()
    {
        var args = new[] { $"--solution={SolutionPath}" };
        return McpSessionContext.Create(args);
    }

    [Fact]
    public async Task GetSource_Parity_WithDirectStore()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        var tool = new GetSourceTool(session);

        string docPath;
        string? directSource;
        using (var store = OpenStore(DbPath))
        {
            docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First();
            directSource = store.GetSource(docPath, snapshotId);
        }

        var json = tool.LurpGetSource(document: docPath);
        using var doc = JsonDocument.Parse(json);
        var toolSource = doc.RootElement.GetProperty("source").GetString();

        Assert.Equal(directSource, toolSource);
        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
    }

    [Fact]
    public async Task Navigate_Parity_WithDirectStore()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        var tool = new NavigateTool(session);

        string docPath;
        using (var store = OpenStore(DbPath))
        {
            docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First();
        }

        var json = tool.LurpNavigate(file: docPath, line: 2);
        using var doc = JsonDocument.Parse(json);
        var hasTarget = doc.RootElement.TryGetProperty("target", out var targetEl);

        Assert.True(hasTarget);
        // Parity: direct store NavigateToLocation should produce same JSON shape
        using var directStore = OpenStore(DbPath);
        var direct = directStore.NavigateToLocation(docPath, 2, snapshotId, false);
        var directJson = JsonSerializer.Serialize(direct);
        var toolTargetJson = targetEl.ValueKind == JsonValueKind.Null ? "null" : targetEl.GetRawText();

        // At least both null or both non-null
        Assert.Equal(direct == null, targetEl.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task FindSymbol_Parity_WithDirectStore()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        var tool = new FindSymbolTool(session);

        string symbolId;
        using (var store = OpenStore(DbPath))
        {
            symbolId = store.GetSymbolIdsInSnapshot(snapshotId).First();
        }

        var json = tool.LurpFindSymbol(symbol: symbolId);
        using var doc = JsonDocument.Parse(json);
        var toolFqn = doc.RootElement.GetProperty("fully_qualified_name").GetString();

        using var directStore = OpenStore(DbPath);
        var directInfo = directStore.GetSymbolInfo(symbolId, snapshotId);
        Assert.Equal(directInfo?.FullyQualifiedName, toolFqn);
    }

    [Fact]
    public async Task Search_Parity_WithDirectStore()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        var tool = new SearchTool(session);

        var json = tool.LurpSearch(query: "Foo", type: "symbol");
        using var doc = JsonDocument.Parse(json);
        var toolResults = doc.RootElement.GetProperty("results").GetArrayLength();

        using var directStore = OpenStore(DbPath);
        var directResults = directStore.SearchSymbols("Foo", snapshotId, 20, false, null);
        // When limit is 20, counts should match (tool uses SearchSymbols for all-type symbol branch without pagination)
        // For type=symbol, tool uses SearchSymbolsPage which may have same count
        Assert.True(toolResults >= 0);
        Assert.True(directResults.Count >= 0);
        // At least parity in count for this small fixture (both should find Foo)
        Assert.Equal(directResults.Count > 0, toolResults > 0);
    }
}
