using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpImpactTests : IntegrationTestBase
{
    private async Task<string> IndexInitialAsync()
    {
        CreateProject("ImpactProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace ImpactProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                        public void UpstreamCaller() { Caller(); }
                    }
                    public class Extra {
                        public void ExtraMethod() { new Foo().Bar(); }
                    }
                }
                """,
            ["Extra2.cs"] = """
                namespace ImpactProj {
                    public class Another {
                        public void AnotherMethod() { new Foo().Caller(); }
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private async Task<string> IndexSecondAsync()
    {
        WriteFile("ImpactProj", "Models.cs", """
            namespace ImpactProj {
                public class Foo {
                    public void Bar(int x) {}
                    public void Caller() { Bar(1); }
                    public void UpstreamCaller() { Caller(); }
                    public void NewMethod() {}
                }
                public class Extra {
                    public void ExtraMethod() { new Foo().Bar(1); }
                }
            }
            """);
        return await RunIncrementalIndexAsync();
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create(new[] { $"--solution={SolutionPath}" });

    // Contract/acceptance: pins the v2 lurp_impact response shape.
    [Fact]
    public async Task Impact_Kinds_Provenance_MaxDepth_Limit_Pagination_SemanticCauses()
    {
        await IndexInitialAsync();
        var snap2 = await IndexSecondAsync();
        await using var session = CreateSession();
        Assert.Equal(snap2, session.PinnedSnapshotId);
        var impact = new ImpactTool(session);

        string barId, callerId;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(snap2);
            barId = ids.First(id => store.GetSymbolInfo(id, snap2)?.FullyQualifiedName?.Contains("ImpactProj.Foo.Bar") == true);
            callerId = ids.First(id => store.GetSymbolInfo(id, snap2)?.FullyQualifiedName?.Contains("ImpactProj.Foo.Caller") == true);
        }

        // downstream from Caller reaches Bar
        var jsonDown = impact.LurpImpact(symbol: callerId, direction: "downstream", max_depth: 3, limit: 50);
        using var docDown = JsonDocument.Parse(jsonDown);
        Assert.Equal(snap2, docDown.RootElement.GetProperty("snapshot_id").GetString());
        Assert.True(docDown.RootElement.GetProperty("pinned").GetBoolean());
        Assert.Equal(50, docDown.RootElement.GetProperty("limit").GetInt32());
        Assert.True(docDown.RootElement.TryGetProperty("freshness", out _));
        Assert.True(docDown.RootElement.GetProperty("symbols").GetArrayLength() >= 1);
        Assert.True(docDown.RootElement.TryGetProperty("groups", out _));
        Assert.True(docDown.RootElement.TryGetProperty("semantic_causes", out _));
        Assert.True(docDown.RootElement.TryGetProperty("symbol_count_total", out _));
        Assert.True(docDown.RootElement.TryGetProperty("frontier_count", out _));

        // kinds filtering: Calls keeps symbols, Inherits filters to 0
        var jsonCalls = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: new[] { "Calls" });
        using var docCalls = JsonDocument.Parse(jsonCalls);
        Assert.True(docCalls.RootElement.GetProperty("symbols").GetArrayLength() >= 1);
        var jsonInherits = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: new[] { "Inherits" });
        using var docInherits = JsonDocument.Parse(jsonInherits);
        Assert.Equal(0, docInherits.RootElement.GetProperty("symbols").GetArrayLength());

        // provenance filtering does not throw
        var jsonProv = impact.LurpImpact(symbol: callerId, direction: "downstream", provenance: new[] { "resolved" });
        using var docProv = JsonDocument.Parse(jsonProv);
        Assert.True(docProv.RootElement.TryGetProperty("symbols", out _));

        // max_depth bounds the reached set
        var jsonD1 = impact.LurpImpact(symbol: callerId, direction: "downstream", max_depth: 1, limit: 50);
        var jsonD10 = impact.LurpImpact(symbol: callerId, direction: "downstream", max_depth: 10, limit: 50);
        using var d1 = JsonDocument.Parse(jsonD1);
        using var d10 = JsonDocument.Parse(jsonD10);
        Assert.True(d1.RootElement.GetProperty("symbols").GetArrayLength() <= d10.RootElement.GetProperty("symbols").GetArrayLength());

        // limit + cursor pagination: concatenating pages equals the full ordered symbol list
        var jsonFull = impact.LurpImpact(symbol: barId, direction: "upstream", limit: 50);
        using var docFull = JsonDocument.Parse(jsonFull);
        var fullSymbols = docFull.RootElement.GetProperty("symbols").EnumerateArray()
            .Select(s => s.GetProperty("symbol_id").GetString()!)
            .ToList();
        Assert.True(fullSymbols.Count >= 2);

        var pagedSymbols = new List<string>();
        string? cursor = null;
        while (true)
        {
            var jsonPage = impact.LurpImpact(symbol: barId, direction: "upstream", limit: 1, cursor: cursor);
            using var docPage = JsonDocument.Parse(jsonPage);
            var pageRoot = docPage.RootElement;
            pagedSymbols.AddRange(pageRoot.GetProperty("symbols").EnumerateArray()
                .Select(s => s.GetProperty("symbol_id").GetString()!));
            if (pageRoot.TryGetProperty("truncated", out var trunc) && trunc.ValueKind != JsonValueKind.Null)
            {
                Assert.Equal("limit", trunc.GetProperty("reason").GetString());
                cursor = trunc.GetProperty("cursor").GetString();
                Assert.False(string.IsNullOrEmpty(cursor));
            }
            else
            {
                break;
            }
        }
        Assert.Equal(fullSymbols, pagedSymbols);

        // witness_path shape on each page symbol
        foreach (var symbol in docFull.RootElement.GetProperty("symbols").EnumerateArray())
        {
            Assert.True(symbol.GetProperty("depth").GetInt32() >= 1);
            Assert.True(symbol.TryGetProperty("shortest_path_count", out _));
            Assert.True(symbol.TryGetProperty("frontier", out _));
            var witness = symbol.GetProperty("witness_path");
            Assert.True(witness.GetProperty("total_steps").GetInt32() >= 1);
            foreach (var hop in witness.GetProperty("hops").EnumerateArray())
            {
                Assert.True(hop.TryGetProperty("source_symbol_id", out _));
                Assert.True(hop.TryGetProperty("target_symbol_id", out _));
                Assert.True(hop.TryGetProperty("edge_kind", out _));
            }
        }

        // semantic_causes is top level (Bar has a signature change against the previous snapshot)
        var jsonUpBar = impact.LurpImpact(symbol: barId, direction: "upstream", max_depth: 3, limit: 50);
        using var docUpBar = JsonDocument.Parse(jsonUpBar);
        Assert.True(docUpBar.RootElement.GetProperty("semantic_causes").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Impact_SnapshotMismatch_ReturnsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var impact = new ImpactTool(session);
        string anyId;
        using (var store = OpenStore(DbPath))
            anyId = store.GetSymbolIdsInSnapshot(session.PinnedSnapshotId).First();
        var ex = Assert.Throws<McpProtocolException>(() => impact.LurpImpact(symbol: anyId, snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("snapshot mismatch", ex.Message);
    }

    [Fact]
    public async Task Impact_Parity_WithHandlerShape()
    {
        var snap = await IndexInitialAsync();
        await using var session = CreateSession();
        var impact = new ImpactTool(session);
        string callerId;
        using (var store = OpenStore(DbPath))
            callerId = store.GetSymbolIdsInSnapshot(snap).First(id => store.GetSymbolInfo(id, snap)?.FullyQualifiedName?.Contains("ImpactProj.Foo.Caller") == true);

        var mcpJson = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: new[] { "Calls" }, max_depth: 3, limit: 50);
        using var mcpDoc = JsonDocument.Parse(mcpJson);

        // Contract/acceptance: the CLI and MCP documents come from the same
        // ImpactPaging builder; the MCP envelope adds only pinned and limit.
        var cliResult = RunCaptured(() => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={callerId}",
            "--direction=downstream",
            "--kinds=Calls",
            "--max-depth=3",
            "--limit=50",
            $"--output-dir={TestDir}",
            "--output=json"
        ]));
        Assert.Null(cliResult.Failure);
        using var cliDoc = JsonDocument.Parse(cliResult.Stdout);

        var cliFields = cliDoc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var mcpFields = mcpDoc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.True(cliFields.SetEquals(mcpFields.Except(["pinned", "limit"])));

        Assert.True(cliDoc.RootElement.TryGetProperty("symbols", out var cliSymbols));
        Assert.True(mcpDoc.RootElement.TryGetProperty("symbols", out var mcpSymbols));
        Assert.Equal(cliSymbols.GetArrayLength(), mcpSymbols.GetArrayLength());
        foreach (var symbol in mcpSymbols.EnumerateArray())
        {
            Assert.True(symbol.TryGetProperty("symbol_id", out _));
            Assert.True(symbol.GetProperty("depth").GetInt32() >= 1);
            Assert.True(symbol.TryGetProperty("shortest_path_count", out _));
            Assert.True(symbol.TryGetProperty("frontier", out _));
            Assert.True(symbol.TryGetProperty("witness_path", out var witness));
            Assert.True(witness.TryGetProperty("total_steps", out _));
            Assert.True(witness.TryGetProperty("hops", out _));
        }

        foreach (var group in mcpDoc.RootElement.GetProperty("groups").EnumerateArray())
        {
            Assert.True(group.TryGetProperty("first_hop_source_symbol_id", out _));
            Assert.True(group.TryGetProperty("first_hop_target_symbol_id", out _));
            Assert.True(group.TryGetProperty("edge_kind", out _));
            Assert.True(group.TryGetProperty("provenance", out _));
            Assert.True(group.TryGetProperty("symbol_count", out _));
            Assert.True(group.TryGetProperty("max_depth", out _));
        }
    }

    private static RunResult RunCaptured(Action action)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            action();
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return new RunResult(stdout.ToString(), failure);
    }

    private sealed record RunResult(string Stdout, CliExitException? Failure);
}
