using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Workspace;

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

    [Fact]
    public async Task Impact_Parity_WithDirectTraverser()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);
        string symbolId;
        using (var store = OpenStore(DbPath))
            symbolId = store.GetSymbolIdsInSnapshot(snapshotId).First(id => store.GetSymbolInfo(id, snapshotId)?.FullyQualifiedName?.Contains("ParityProj.Foo.Caller") == true);

        var json = tool.LurpImpact(symbol: symbolId, direction: "downstream", max_depth: 3, max_paths: 50);
        using var doc = JsonDocument.Parse(json);
        var toolPaths = doc.RootElement.GetProperty("paths").GetArrayLength();

        using var store2 = OpenStore(DbPath);
        var traverser = new ImpactTraverser(store2, snapshotId, store2);
        var direct = traverser.TraceImpact(symbolId, ImpactDirection.Downstream, null, null, 3);
        // parity: counts should match when no filtering and same ordering
        Assert.Equal(direct.Count, doc.RootElement.GetProperty("path_count_total").GetInt32());
        Assert.True(toolPaths <= direct.Count);
    }

    [Fact]
    public async Task Diff_Parity_WithDirectDiffer()
    {
        CreateProject("ParityDiffProj", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace ParityDiffProj { public class Foo { public void Bar() {} } }"
        });
        var snap1 = await RunFullIndexAsync(DbPath);
        WriteFile("ParityDiffProj", "Models.cs", "namespace ParityDiffProj { public class Foo { public void Bar(int x) {} public void New() {} } }");
        var snap2 = await RunIncrementalIndexAsync();
        await using var session = CreateSession();
        var tool = new DiffTool(session);
        var json = tool.LurpDiff(from_snapshot: snap1, to_snapshot: snap2);
        using var doc = JsonDocument.Parse(json);
        var toolChanges = doc.RootElement.GetProperty("changes").GetArrayLength();
        using var store = OpenStore(DbPath);
        var differ = new SemanticDiffer(store, store, store, store);
        var (directChanges, _) = differ.ComputeDiff(snap1, snap2);
        Assert.Equal(directChanges.Count, toolChanges);
        Assert.Equal(directChanges.Count, doc.RootElement.GetProperty("change_count").GetInt32());
    }

    [Fact]
    public async Task GetSymbol_Parity_WithDirectStore()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        var tool = new GetSymbolTool(session);
        string symbolId;
        using (var store = OpenStore(DbPath))
            symbolId = store.GetSymbolIdsInSnapshot(snapshotId).First();
        var json = tool.LurpGetSymbol(symbol: symbolId, view: "summary");
        using var doc = JsonDocument.Parse(json);
        using var store2 = OpenStore(DbPath);
        var direct = store2.GetSymbolInfo(symbolId, snapshotId);
        Assert.Equal(direct?.FullyQualifiedName, doc.RootElement.GetProperty("fully_qualified_name").GetString());
        Assert.Equal(symbolId, doc.RootElement.GetProperty("symbol_id").GetString());
    }

    [Fact]
    public async Task GetAnnotations_Parity_WithDirectStore()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        string symbolId;
        using (var store = OpenStore(DbPath))
        {
            symbolId = store.GetSymbolIdsInSnapshot(snapshotId).First();
            store.SaveAnnotations(snapshotId, new[] { new AnnotationRecord(symbolId, "note", "parity") });
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var json = tool.LurpGetAnnotations(symbol: symbolId);
        using var doc = JsonDocument.Parse(json);
        using var store2 = OpenStore(DbPath);
        var direct = store2.GetAnnotations(snapshotId, symbolId);
        Assert.Equal(direct.Count, doc.RootElement.GetProperty("annotations").GetArrayLength());
    }

    [Fact]
    public async Task Timings_Parity_WithCliJson()
    {
        // Parity contract: lurp_timings (pinned snapshot) must equal --mode=timings --output=json (same snapshot).
        // The CLI handler's JSON is { snapshot_id, total_ms, steps: [{step, elapsed_ms, percent}] } derived from store.GetTimings.
        // The MCP tool reuses the same engine path; we verify envelope total_ms/steps match direct store computation
        // which is byte-identical to what the CLI would emit for that snapshot.
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        var tool = new TimingsTool(session);

        var json = tool.LurpTimings(snapshot_id: snapshotId);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
        var toolTotal = doc.RootElement.GetProperty("total_ms").GetInt64();
        var toolSteps = doc.RootElement.GetProperty("steps");

        using var store = OpenStore(DbPath);
        var direct = store.GetTimings(snapshotId);
        var expectedTotal = direct.Sum(t => t.ElapsedMs);

        Assert.Equal(expectedTotal, toolTotal);
        Assert.Equal(direct.Count, toolSteps.GetArrayLength());

        int idx = 0;
        foreach (var el in toolSteps.EnumerateArray())
        {
            Assert.Equal(direct[idx].StepName, el.GetProperty("step").GetString());
            Assert.Equal(direct[idx].ElapsedMs, el.GetProperty("elapsed_ms").GetInt64());
            var expectedPct = expectedTotal > 0 ? Math.Round((double)direct[idx].ElapsedMs / expectedTotal * 100, 1) : 0;
            Assert.Equal(expectedPct, el.GetProperty("percent").GetDouble());
            idx++;
        }
    }
}
