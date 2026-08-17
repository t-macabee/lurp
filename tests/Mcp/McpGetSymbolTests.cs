using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpGetSymbolTests : IntegrationTestBase
{
    private async Task<string> IndexFixtureAsync()
    {
        CreateProject("SymProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace SymProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create(new[] { $"--solution={SolutionPath}" });

    [Fact]
    public async Task GetSymbol_ThreeForms_ResolveIdentically()
    {
        var snap = await IndexFixtureAsync();
        string barId, barFqn, barDocId;
        using (var store = OpenStore(DbPath))
        {
            barId = store.GetSymbolIdsInSnapshot(snap).First(id => store.GetSymbolInfo(id, snap)?.FullyQualifiedName?.Contains("SymProj.Foo.Bar") == true);
            var info = store.GetSymbolInfo(barId, snap)!;
            barFqn = info.FullyQualifiedName!;
            barDocId = barId.Split('|')[0];
        }
        await using var session = CreateSession();
        var tool = new GetSymbolTool(session);

        var jsonPipe = tool.LurpGetSymbol(symbol: barId, view: "summary");
        var jsonDoc = tool.LurpGetSymbol(symbol: barDocId, view: "summary");
        var jsonFqn = tool.LurpGetSymbol(symbol: barFqn, view: "summary");
        using var dPipe = JsonDocument.Parse(jsonPipe);
        using var dDoc = JsonDocument.Parse(jsonDoc);
        using var dFqn = JsonDocument.Parse(jsonFqn);
        Assert.Equal(dPipe.RootElement.GetProperty("symbol_id").GetString(), dDoc.RootElement.GetProperty("symbol_id").GetString());
        Assert.Equal(dPipe.RootElement.GetProperty("symbol_id").GetString(), dFqn.RootElement.GetProperty("symbol_id").GetString());
        Assert.True(dPipe.RootElement.TryGetProperty("freshness", out _));
        Assert.True(dPipe.RootElement.GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public async Task GetSymbol_View_Variants_ReturnExpectedShape()
    {
        var snap = await IndexFixtureAsync();
        string barId;
        using (var store = OpenStore(DbPath))
            barId = store.GetSymbolIdsInSnapshot(snap).First(id => store.GetSymbolInfo(id, snap)?.FullyQualifiedName?.Contains("SymProj.Foo.Bar") == true);
        await using var session = CreateSession();
        var tool = new GetSymbolTool(session);

        var jsonSummary = tool.LurpGetSymbol(symbol: barId, view: "summary");
        using var dSummary = JsonDocument.Parse(jsonSummary);
        Assert.True(dSummary.RootElement.TryGetProperty("metadata_json", out _));
        Assert.True(dSummary.RootElement.TryGetProperty("locations", out _));
        Assert.True(dSummary.RootElement.GetProperty("source").ValueKind == JsonValueKind.Null);

        var jsonSource = tool.LurpGetSymbol(symbol: barId, view: "source");
        using var dSource = JsonDocument.Parse(jsonSource);
        Assert.True(dSource.RootElement.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(src.GetString()));

        var jsonAll = tool.LurpGetSymbol(symbol: barId, view: "all", context_lines: 2);
        using var dAll = JsonDocument.Parse(jsonAll);
        Assert.True(dAll.RootElement.TryGetProperty("source", out var srcAll) && srcAll.ValueKind == JsonValueKind.String);
        Assert.True(dAll.RootElement.TryGetProperty("metadata_json", out _));

        var jsonCtx = tool.LurpGetSymbol(symbol: barId, view: "source", context_lines: 5);
        using var dCtx = JsonDocument.Parse(jsonCtx);
        Assert.True(dCtx.RootElement.TryGetProperty("source", out _));
    }

    [Fact]
    public async Task GetSymbol_SnapshotMismatch_AndInvalidView()
    {
        var snap = await IndexFixtureAsync();
        string barId;
        using (var store = OpenStore(DbPath))
            barId = store.GetSymbolIdsInSnapshot(snap).First(id => store.GetSymbolInfo(id, snap)?.FullyQualifiedName?.Contains("SymProj.Foo.Bar") == true);
        await using var session = CreateSession();
        var tool = new GetSymbolTool(session);

        var ex1 = Assert.Throws<McpProtocolException>(() => tool.LurpGetSymbol(symbol: barId, snapshot_id: "bad"));
        Assert.Equal(McpErrorCode.InvalidParams, ex1.ErrorCode);
        var ex2 = Assert.Throws<McpProtocolException>(() => tool.LurpGetSymbol(symbol: "NonExistent.Symbol"));
        Assert.Equal(McpErrorCode.InvalidParams, ex2.ErrorCode);
        var ex3 = Assert.Throws<McpProtocolException>(() => tool.LurpGetSymbol(symbol: barId, view: "invalid"));
        Assert.Equal(McpErrorCode.InvalidParams, ex3.ErrorCode);
    }
}
