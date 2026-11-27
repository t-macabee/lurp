using Lurp.Storage;
using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;

namespace Lurp.Tests;

public sealed class CrlfParityTests
{
    private const string Document = "src/Lines/Example.cs";
    private const string Source = "namespace Lines;\npublic class Example\n{\n    public void Target() { }\n    public void Caller() { Target(); }\n}\n";
    private const string EditedSource = "namespace Lines;\npublic class Example\n{\n    public void Target() { }\n    public void Caller() { Target(); Target(); }\n}\n";

    private sealed class Fixture : IntegrationTestBase;

    [Fact]
    public async Task LineEndings_PreserveQueriesAndIncrementalParity()
    {
        using var lf = new Fixture();
        using var crlf = new Fixture();
        var lfResult = await ExerciseAsync(lf, "\n");
        var crlfResult = await ExerciseAsync(crlf, "\r\n");

        Assert.NotEqual(lfResult.Hash, crlfResult.Hash);
        Assert.Equal(lfResult.Symbols, crlfResult.Symbols);
        Assert.Equal(lfResult.Edges, crlfResult.Edges);
        Assert.Equal(lfResult.Outline, crlfResult.Outline);
        Assert.Equal(lfResult.Navigate, crlfResult.Navigate);
        Assert.Equal(lfResult.Symbol, crlfResult.Symbol);
        Assert.Equal(lfResult.Search, crlfResult.Search);
        Assert.Equal(lfResult.Grep, crlfResult.Grep);
    }

    private static async Task<(string Hash, string[] Symbols, string[] Edges, string Outline, string Navigate, string Symbol, string Search, string Grep)>
        ExerciseAsync(Fixture fixture, string newline)
    {
        string ConvertLines(string text) => text.Replace("\n", newline, StringComparison.Ordinal);
        fixture.CreateProject("Lines", new Dictionary<string, string> { ["Example.cs"] = ConvertLines(Source) });
        var snapshot = await fixture.RunFullIndexAsync(fixture.DbPath);
        var document = Assert.Single(SnapshotAssertions.ReadDocuments(fixture.DbPath, snapshot), d => d.RelativePath == Document);
        var symbols = SnapshotAssertions.ReadSymbols(fixture.DbPath, snapshot).Select(s => s.ToString()!).ToArray();
        var edges = SnapshotAssertions.ReadEdgeSites(fixture.DbPath, snapshot).Select(e => e.ToString()!).ToArray();
        Assert.NotEmpty(symbols);
        Assert.NotEmpty(edges);

        await using var session = McpSessionContext.Create([$"--solution={fixture.SolutionPath}", $"--output-dir={fixture.TestDir}"]);
        using var outline = JsonDocument.Parse(new OutlineTool(session).LurpOutline(document: Document));
        using var navigate = JsonDocument.Parse(new NavigateTool(session).LurpNavigate(file: Document, line: 4));
        using var source = JsonDocument.Parse(new GetSourceTool(session).LurpGetSource(document: Document, start_line: 4, end_line: 4));
        using var symbol = JsonDocument.Parse(new GetSymbolTool(session).LurpGetSymbol(symbol: "global::Lines.Example.Target", view: "summary"));
        using var symbolSource = JsonDocument.Parse(new GetSymbolTool(session).LurpGetSymbol(symbol: "global::Lines.Example.Target", view: "source"));
        using var search = JsonDocument.Parse(new SearchTool(session).LurpSearch(query: "Example", type: "symbol"));
        using var grep = JsonDocument.Parse(new GrepTool(session).LurpGrep(query: "Target"));
        using var status = JsonDocument.Parse(await new StatusTool(session).LurpStatus(full: true));
        Assert.True(status.RootElement.GetProperty("freshness").GetProperty("is_fresh").GetBoolean());
        // Grep strips line terminators; get-source preserves the stored line ending.
        Assert.Equal("    public void Target() { }" + newline, source.RootElement.GetProperty("source").GetString());
        var grepResults = grep.RootElement.GetProperty("results");
        var declaration = Assert.Single(grepResults.EnumerateArray(), match => match.GetProperty("start_line").GetInt32() == 4);
        Assert.Equal("    public void Target() { }", declaration.GetProperty("line_text").GetString());
        Assert.Equal("public void Target() { }", symbolSource.RootElement.GetProperty("source").GetString()?.TrimStart());
        var target = navigate.RootElement.GetProperty("target");
        Assert.Equal(4, target.GetProperty("start_line").GetInt32());
        Assert.Equal(4, target.GetProperty("end_line").GetInt32());
        Assert.Equal("M:Lines.Example.Target", SymbolId.DocCommentIdOf(target.GetProperty("symbol_id").GetString()!));

        fixture.WriteFile("Lines", "Example.cs", ConvertLines(EditedSource));
        var incremental = await fixture.RunIncrementalIndexAsync();
        var cleanDb = Path.Combine(fixture.TestDir, "clean.db");
        var full = await fixture.RunFullIndexAsync(cleanDb);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(fixture.DbPath, incremental, cleanDb, full);

        return (document.ContentHash, symbols, edges,
            outline.RootElement.GetProperty("declarations").GetRawText(),
            string.Join("|", target.GetProperty("symbol_id").GetString(), target.GetProperty("start_line").GetInt32(), target.GetProperty("end_line").GetInt32()),
            symbol.RootElement.GetProperty("locations").GetRawText(), search.RootElement.GetProperty("results").GetRawText(),
            grepResults.GetRawText());
    }
}
