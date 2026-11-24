using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests.Mcp;

public sealed class McpParityTests : IntegrationTestBase
{
    private const string Document = "src/ParityProj/Models.cs";
    private const string BarFqn = "global::ParityProj.Foo.Bar";
    private const string BarId = "M:ParityProj.Foo.Bar";
    private const string Source = """
        namespace ParityProj {
            public class Foo {
                public void Bar() {}
                public void Caller() { Bar(); }
            }
        }
        """;

    private Task<string> IndexFixtureAndGetSnapshot() =>
        IndexFullAsync("ParityProj", new Dictionary<string, string> { ["Models.cs"] = Source });

    private McpSessionContext CreateSession() =>
        McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}"]);

    private string RunCli(Action<string[], IOutputSink> run, string[] args)
    {
        var output = new CapturingOutputSink();
        run([.. args, $"--output-dir={Path.GetDirectoryName(DbPath)!}"], output);
        return output.Stdout;
    }

    private JsonDocument RunCliJson(Action<string[], IOutputSink> run, string[] args) =>
        JsonDocument.Parse(RunCli(run, args));

    private static void AssertSameProperty(JsonElement cli, JsonElement mcp, string name) =>
        Assert.True(JsonElement.DeepEquals(cli.GetProperty(name), mcp.GetProperty(name)),
            $"CLI and MCP differ on '{name}'.");

    [Fact]
    public async Task GetSource_Parity()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        using var mcp = JsonDocument.Parse(new GetSourceTool(session).LurpGetSource(document: Document));

        var source = mcp.RootElement.GetProperty("source").GetString();
        Assert.Equal(Source, source);
        Assert.Equal(RunCli(GetSourceHandler.Run, [$"--document={Document}", $"--snapshot={snapshotId}"]), source);
        Assert.Equal(snapshotId, mcp.RootElement.GetProperty("snapshot_id").GetString());
    }

    [Theory]
    [InlineData(2, "T:ParityProj.Foo", 2, 5)]
    [InlineData(3, BarId, 3, 3)]
    public async Task Navigate_Parity(int line, string symbol, int start, int end)
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        using var mcp = JsonDocument.Parse(new NavigateTool(session).LurpNavigate(file: Document, line: line));
        using var cli = RunCliJson(NavigateHandler.Run, [$"--file={Document}", $"--line={line}", $"--snapshot={snapshotId}"]);

        var target = mcp.RootElement.GetProperty("target");
        Assert.Equal(symbol, SymbolId.DocCommentIdOf(target.GetProperty("symbol_id").GetString()!));
        Assert.Equal(start, target.GetProperty("start_line").GetInt32());
        Assert.Equal(end, target.GetProperty("end_line").GetInt32());
        AssertSameProperty(cli.RootElement, mcp.RootElement, "target");
        AssertSameProperty(cli.RootElement, mcp.RootElement, "snapshot_id");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SymbolMetadata_Parity(bool getSymbol)
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        var json = getSymbol
            ? new GetSymbolTool(session).LurpGetSymbol(symbol: BarFqn, view: "summary")
            : new FindSymbolTool(session).LurpFindSymbol(symbol: BarFqn);
        using var mcp = JsonDocument.Parse(json);
        using var cli = getSymbol
            ? RunCliJson(GetSymbolHandler.Run, [$"--symbol={BarFqn}", "--view=metadata", $"--snapshot={snapshotId}"])
            : RunCliJson(FindSymbolHandler.Run, [$"--symbol={BarFqn}", "--output=json", $"--snapshot={snapshotId}"]);

        Assert.Equal(BarFqn, mcp.RootElement.GetProperty("fully_qualified_name").GetString());
        Assert.Equal("Method", mcp.RootElement.GetProperty("kind").GetString());
        Assert.Equal(BarId, mcp.RootElement.GetProperty("doc_comment_id").GetString());
        foreach (var key in new[] { "snapshot_id", "symbol_id", "doc_comment_id", "assembly_identity",
                     "fully_qualified_name", "kind", "metadata", "declaration_count", "is_partial", "locations" })
            AssertSameProperty(cli.RootElement, mcp.RootElement, key);
    }

    [Fact]
    public async Task Search_Parity()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        using var mcp = JsonDocument.Parse(new SearchTool(session).LurpSearch(query: "Foo", type: "symbol"));
        using var cli = RunCliJson(SearchHandler.Run, ["--query=Foo", "--type=symbol", "--output=json", $"--snapshot={snapshotId}"]);

        var names = mcp.RootElement.GetProperty("results").EnumerateArray()
            .Select(row => row.GetProperty("fully_qualified_name").GetString());
        Assert.Equal(["global::ParityProj.Foo", BarFqn, "global::ParityProj.Foo.Caller"], names);
        foreach (var key in new[] { "snapshot_id", "query", "type", "results", "next_cursor" })
            AssertSameProperty(cli.RootElement, mcp.RootElement, key);
    }

    [Fact]
    public async Task Impact_Parity()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        await using var session = CreateSession();
        const string caller = "global::ParityProj.Foo.Caller";
        using var mcp = JsonDocument.Parse(new ImpactTool(session).LurpImpact(symbol: caller, direction: "downstream", max_depth: 1));
        using var cli = RunCliJson(ImpactHandler.Run, [$"--symbol={caller}", "--direction=downstream", "--max-depth=1", "--output=json", $"--snapshot={snapshotId}"]);

        var target = Assert.Single(mcp.RootElement.GetProperty("symbols").EnumerateArray(),
            row => SymbolId.DocCommentIdOf(row.GetProperty("symbol_id").GetString()!) == BarId);
        Assert.Equal(1, target.GetProperty("depth").GetInt32());
        Assert.Equal(1, target.GetProperty("shortest_path_count").GetInt32());
        foreach (var key in new[] { "snapshot_id", "symbol_id", "direction", "symbols", "symbol_count_total", "groups", "frontier_count" })
            AssertSameProperty(cli.RootElement, mcp.RootElement, key);
    }

    [Fact]
    public async Task Diff_Parity()
    {
        const string project = "ParityDiffProj";
        var snap1 = await IndexFullAsync(project, new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace ParityDiffProj { public class Foo { public void Bar() {} } }"
        });
        WriteFile(project, "Models.cs", "namespace ParityDiffProj { public class Foo { public void Bar(int x) {} public int New() => 1; } }");
        var snap2 = await RunIncrementalIndexAsync();
        await using var session = CreateSession();
        using var mcp = JsonDocument.Parse(new DiffTool(session).LurpDiff(from_snapshot: snap1, to_snapshot: snap2));
        using var cli = RunCliJson(DiffHandler.Run, [$"--from-snapshot={snap1}", $"--to-snapshot={snap2}"]);

        foreach (var key in new[] { "from_snapshot", "to_snapshot", "change_count", "skipped_comparisons" })
            AssertSameProperty(cli.RootElement, mcp.RootElement, key);

        static (string? Type, string? Symbol, JsonElement Detail)[] Changes(JsonElement root) =>
            root.GetProperty("changes").EnumerateArray()
                .Select(row => (Type: row.GetProperty("change_type").GetString(),
                    Symbol: row.GetProperty("symbol_id").GetString(), Detail: row.GetProperty("detail")))
                .OrderBy(row => row.Type, StringComparer.Ordinal)
                .ThenBy(row => row.Symbol, StringComparer.Ordinal)
                .ThenBy(row => row.Detail.GetRawText(), StringComparer.Ordinal)
                .ToArray();

        var actual = Changes(mcp.RootElement);
        var expected = Changes(cli.RootElement);
        Assert.Equal(expected.Select(row => (row.Type, row.Symbol)), actual.Select(row => (row.Type, row.Symbol)));
        Assert.All(actual.Zip(expected), pair => Assert.True(JsonElement.DeepEquals(pair.First.Detail, pair.Second.Detail)));
        Assert.Equal(
            [("symbol_removed", "M:ParityDiffProj.Foo.Bar"),
             ("symbol_added", "M:ParityDiffProj.Foo.Bar(System.Int32)"),
             ("symbol_added", "M:ParityDiffProj.Foo.New")],
            actual.Where(row => row.Type is "symbol_added" or "symbol_removed")
                .Select(row => (row.Type, Symbol: SymbolId.DocCommentIdOf(row.Symbol!)))
                .OrderBy(row => row.Symbol, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetAnnotations_Parity()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var annotation = RunCli(AnnotationHandler.RunAnnotate, [$"--symbol={BarFqn}", "--annotation-kind=note", "--value=parity"]);
        using var created = JsonDocument.Parse(annotation);
        var annotationId = created.RootElement.GetProperty("annotation_id").GetInt64();

        await using var session = CreateSession();
        using var mcp = JsonDocument.Parse(new AnnotationsTool(session).LurpGetAnnotations(symbol: BarFqn, kind: "note"));
        using var cli = RunCliJson(AnnotationHandler.RunGetAnnotations, [$"--symbol={BarFqn}", "--kind=note", "--output=json", $"--snapshot={snapshotId}"]);

        var row = Assert.Single(mcp.RootElement.GetProperty("annotations").EnumerateArray());
        Assert.Equal(BarId, SymbolId.DocCommentIdOf(row.GetProperty("symbol_id").GetString()!));
        Assert.Equal("note", row.GetProperty("kind").GetString());
        Assert.Equal("parity", row.GetProperty("value").GetString());
        Assert.Equal(annotationId, row.GetProperty("annotation_id").GetInt64());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("document_path").ValueKind);
        foreach (var key in new[] { "snapshot_id", "symbol_id", "document", "kind", "annotations", "annotation_count", "next_cursor" })
            AssertSameProperty(cli.RootElement, mcp.RootElement, key);
    }

    [Fact]
    public async Task Timings_Parity_WithCliJson()
    {
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

        using var cli = RunCliJson(TimingsHandler.Run, ["--json", $"--snapshot={snapshotId}"]);
        Assert.NotEmpty(doc.RootElement.GetProperty("metrics").EnumerateObject());
        foreach (var key in new[] { "snapshot_id", "total_ms", "steps", "metrics" })
            AssertSameProperty(cli.RootElement, doc.RootElement, key);
    }
}
