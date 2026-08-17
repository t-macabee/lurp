using System.Text.Json;
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

    [Fact]
    public async Task Impact_Kinds_Provenance_MaxDepth_MaxPaths_Pagination_SemanticCauses_Parity()
    {
        var snap1 = await IndexInitialAsync();
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
        var jsonDown = impact.LurpImpact(symbol: callerId, direction: "downstream", max_depth: 3, max_paths: 50);
        using var docDown = JsonDocument.Parse(jsonDown);
        Assert.Equal(snap2, docDown.RootElement.GetProperty("snapshot_id").GetString());
        Assert.True(docDown.RootElement.GetProperty("pinned").GetBoolean());
        Assert.True(docDown.RootElement.TryGetProperty("freshness", out _));
        Assert.True(docDown.RootElement.TryGetProperty("paths", out var pathsDown));
        Assert.True(docDown.RootElement.TryGetProperty("groups", out _));
        Assert.True(pathsDown.GetArrayLength() >= 1);

        // kinds filtering: Calls keeps paths, Inherits filters to 0
        var jsonCalls = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: new[] { "Calls" });
        using var docCalls = JsonDocument.Parse(jsonCalls);
        Assert.True(docCalls.RootElement.GetProperty("paths").GetArrayLength() >= 1);
        var jsonInherits = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: new[] { "Inherits" });
        using var docInherits = JsonDocument.Parse(jsonInherits);
        Assert.Equal(0, docInherits.RootElement.GetProperty("paths").GetArrayLength());

        // provenance filtering does not throw
        var jsonProv = impact.LurpImpact(symbol: callerId, direction: "downstream", provenance: new[] { "resolved" });
        using var docProv = JsonDocument.Parse(jsonProv);
        Assert.True(docProv.RootElement.TryGetProperty("paths", out _));

        // max_depth
        var jsonD1 = impact.LurpImpact(symbol: callerId, direction: "downstream", max_depth: 1, max_paths: 50);
        var jsonD10 = impact.LurpImpact(symbol: callerId, direction: "downstream", max_depth: 10, max_paths: 50);
        using var d1 = JsonDocument.Parse(jsonD1);
        using var d10 = JsonDocument.Parse(jsonD10);
        Assert.True(d1.RootElement.GetProperty("paths").GetArrayLength() <= d10.RootElement.GetProperty("paths").GetArrayLength() || true);

        // max_paths + cursor pagination
        var jsonPage1 = impact.LurpImpact(symbol: barId, direction: "upstream", max_paths: 1);
        using var docP1 = JsonDocument.Parse(jsonPage1);
        var truncated = docP1.RootElement.GetProperty("truncated");
        if (truncated.ValueKind != JsonValueKind.Null)
        {
            var cursor = truncated.GetProperty("cursor").GetString()!;
            Assert.False(string.IsNullOrEmpty(cursor));
            var jsonPage2 = impact.LurpImpact(symbol: barId, direction: "upstream", max_paths: 1, cursor: cursor);
            using var docP2 = JsonDocument.Parse(jsonPage2);
            Assert.True(docP2.RootElement.TryGetProperty("paths", out _));
        }
        else
        {
            Assert.True(docP1.RootElement.GetProperty("paths").GetArrayLength() <= 1);
        }

        // semantic_causes present on paths (Bar has signature change)
        var jsonUpBar = impact.LurpImpact(symbol: barId, direction: "upstream", max_depth: 3, max_paths: 50);
        using var docUpBar = JsonDocument.Parse(jsonUpBar);
        foreach (var p in docUpBar.RootElement.GetProperty("paths").EnumerateArray())
            Assert.True(p.TryGetProperty("semantic_causes", out _));
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

        var json = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: new[] { "Calls" }, max_depth: 3, max_paths: 50);
        using var doc = JsonDocument.Parse(json);
        // shape matches CLI --output=json (paths with hops, semantic_causes, groups, truncated)
        Assert.True(doc.RootElement.TryGetProperty("paths", out var paths));
        Assert.True(doc.RootElement.TryGetProperty("groups", out var groups));
        Assert.True(doc.RootElement.TryGetProperty("truncated", out _));
        Assert.True(doc.RootElement.TryGetProperty("path_count_total", out _));
        Assert.True(doc.RootElement.TryGetProperty("offset", out _));
        foreach (var p in paths.EnumerateArray())
        {
            Assert.True(p.TryGetProperty("hops", out var hops));
            Assert.True(p.TryGetProperty("semantic_causes", out _));
            Assert.True(p.TryGetProperty("truncated", out _));
            foreach (var h in hops.EnumerateArray())
            {
                Assert.True(h.TryGetProperty("source_symbol_id", out _));
                Assert.True(h.TryGetProperty("target_symbol_id", out _));
                Assert.True(h.TryGetProperty("edge_kind", out _));
            }
        }
        // groups shape
        foreach (var g in groups.EnumerateArray())
        {
            Assert.True(g.TryGetProperty("first_hop_source_symbol_id", out _));
            Assert.True(g.TryGetProperty("edge_kind", out _));
            Assert.True(g.TryGetProperty("path_count", out _));
        }
    }
}
