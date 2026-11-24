using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpImpactTests : ImpactIndexFixture
{
    private static readonly string[] CallsKind = ["Calls"];
    private static readonly string[] InheritsKind = ["Inherits"];
    private static readonly string[] CompilerProvedProvenance = ["compiler_proved"];
    private static readonly string[] StructuralKinds = ["Contains", "Declares", "TestedBy"];

    private McpSessionContext CreateSession() => McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}"]);

    // Contract/acceptance: pins the v2 lurp_impact response shape.
    [Fact]
    public async Task Impact_Kinds_Provenance_MaxDepth_Limit_Pagination_SemanticCauses()
    {
        await IndexInitialAsync();
        var snap2 = await IndexSecondAsync();
        await using var session = CreateSession();
        Assert.Equal(snap2, session.PinnedSnapshotId);
        var impact = new ImpactTool(session);

        var barId = ResolveSymbolId(snap2, "global::ImpactProj.Foo.Bar");
        var callerId = ResolveSymbolId(snap2, "global::ImpactProj.Foo.Caller");
        var upstreamCallerId = ResolveSymbolId(snap2, "global::ImpactProj.Foo.UpstreamCaller");
        var extraId = ResolveSymbolId(snap2, "global::ImpactProj.Extra.ExtraMethod");
        var anotherId = ResolveSymbolId(snap2, "global::ImpactProj.Another.AnotherMethod");

        // downstream from Caller reaches Bar
        var jsonDown = impact.LurpImpact(symbol: callerId, direction: "downstream", max_depth: 3, limit: 50);
        using var docDown = JsonDocument.Parse(jsonDown);
        Assert.Equal(snap2, docDown.RootElement.GetProperty("snapshot_id").GetString());
        Assert.True(docDown.RootElement.GetProperty("pinned").GetBoolean());
        Assert.Equal(50, docDown.RootElement.GetProperty("limit").GetInt32());
        Assert.True(docDown.RootElement.TryGetProperty("freshness", out _));
        Assert.Equal([barId], McpSymbolIds(docDown));
        Assert.True(docDown.RootElement.TryGetProperty("groups", out _));
        Assert.True(docDown.RootElement.TryGetProperty("semantic_causes", out _));
        Assert.True(docDown.RootElement.TryGetProperty("symbol_count_total", out _));
        Assert.True(docDown.RootElement.TryGetProperty("frontier_count", out _));

        // kinds filtering: Calls keeps Bar, Inherits filters to 0
        var jsonCalls = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: CallsKind);
        using var docCalls = JsonDocument.Parse(jsonCalls);
        Assert.Equal([barId], McpSymbolIds(docCalls));
        var jsonInherits = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: InheritsKind);
        using var docInherits = JsonDocument.Parse(jsonInherits);
        Assert.Empty(McpSymbolIds(docInherits));

        // provenance filtering: compiler_proved keeps Bar; framework_derived yields 0
        var jsonProv = impact.LurpImpact(symbol: callerId, direction: "downstream", provenance: CompilerProvedProvenance);
        using var docProv = JsonDocument.Parse(jsonProv);
        Assert.Equal([barId], McpSymbolIds(docProv));
        var jsonFramework = impact.LurpImpact(symbol: callerId, direction: "downstream", provenance: ["framework_derived"]);
        using var docFramework = JsonDocument.Parse(jsonFramework);
        Assert.Empty(McpSymbolIds(docFramework));

        // max_depth bounds the reached set
        var jsonD1 = impact.LurpImpact(symbol: barId, direction: "upstream", max_depth: 1, limit: 50);
        var jsonD10 = impact.LurpImpact(symbol: barId, direction: "upstream", max_depth: 10, limit: 50);
        using var d1 = JsonDocument.Parse(jsonD1);
        using var d10 = JsonDocument.Parse(jsonD10);
        Assert.Equal([extraId, callerId], McpSymbolIds(d1));
        Assert.Equal([extraId, callerId, anotherId, upstreamCallerId], McpSymbolIds(d10));

        // limit + cursor pagination: concatenating pages equals the full ordered symbol list
        var jsonFull = impact.LurpImpact(symbol: barId, direction: "upstream", limit: 50);
        using var docFull = JsonDocument.Parse(jsonFull);
        var fullSymbols = McpSymbolIds(docFull);
        Assert.Equal([extraId, callerId, anotherId, upstreamCallerId], fullSymbols);

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
        var upBarCauses = docUpBar.RootElement.GetProperty("semantic_causes");
        Assert.True(upBarCauses.GetArrayLength() >= 1);
        Assert.All(upBarCauses.EnumerateArray(),
            cause => Assert.Equal(barId, cause.GetProperty("symbol_id").GetString()));

        // The item shape of lurp_impact (B60): these five keys, nothing else.
        var expectedCauseKeys = new[] { "change_type", "detail", "from_snapshot_id", "symbol_id", "to_snapshot_id" };
        foreach (var cause in upBarCauses.EnumerateArray())
            Assert.Equal(
                expectedCauseKeys,
                cause.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Impact_DefaultTraversal_SkipsDeclaresContainsAndTestedBy()
    {
        var snapshotId = await IndexDirectionFixtureAsync();
        await using var session = CreateSession();
        var impact = new ImpactTool(session);

        var hitId = ResolveSymbolId(snapshotId, "global::DirProj.Target.Hit");
        var targetId = ResolveSymbolId(snapshotId, "global::DirProj.Target");
        var callId = ResolveSymbolId(snapshotId, "global::DirProj.Caller.Call");
        var callHitId = ResolveSymbolId(snapshotId, "global::DirProj.TargetTests.CallHit");

        // (a) Nothing reached only over Declares (Target -> Hit) or TestedBy (Target ->
        // CallHit) appears in the default upstream answer.
        using (var upstream = JsonDocument.Parse(impact.LurpImpact(symbol: hitId, direction: "upstream", limit: 200)))
            Assert.Equal(
                new[] { callId, callHitId }.OrderBy(id => id, StringComparer.Ordinal),
                McpSymbolIds(upstream).OrderBy(id => id, StringComparer.Ordinal));

        using (var upstreamOfTest = JsonDocument.Parse(impact.LurpImpact(symbol: callHitId, direction: "upstream", limit: 200)))
            Assert.DoesNotContain(targetId, McpSymbolIds(upstreamOfTest));

        // (b) An explicit kinds array replaces the default: Declares is followed again.
        using (var declaresOnly = JsonDocument.Parse(impact.LurpImpact(symbol: hitId, direction: "upstream", kinds: ["Declares"], limit: 200)))
            Assert.Equal([targetId], McpSymbolIds(declaresOnly));

        using var downstream = JsonDocument.Parse(impact.LurpImpact(symbol: targetId, direction: "downstream", max_depth: 1, limit: 200));
        Assert.DoesNotContain(downstream.RootElement.GetProperty("groups").EnumerateArray(),
            group => StructuralKinds.Contains(group.GetProperty("edge_kind").GetString()));

        using var structural = JsonDocument.Parse(impact.LurpImpact(symbol: targetId, direction: "downstream",
            kinds: StructuralKinds, max_depth: 1, limit: 200));
        Assert.Equal(StructuralKinds, structural.RootElement.GetProperty("groups").EnumerateArray()
            .Select(group => group.GetProperty("edge_kind").GetString()).Order(StringComparer.Ordinal));
    }

    private static List<string> McpSymbolIds(JsonDocument document) =>
        document.RootElement.GetProperty("symbols").EnumerateArray()
            .Select(symbol => symbol.GetProperty("symbol_id").GetString()!)
            .ToList();

    [Fact]
    public async Task Impact_UnknownSnapshot_ReturnsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var impact = new ImpactTool(session);
        string anyId;
        using (var store = OpenStore(DbPath))
            anyId = store.GetSymbolIdsInSnapshot(session.PinnedSnapshotId).First();
        var ex = Assert.Throws<McpProtocolException>(() => impact.LurpImpact(symbol: anyId, snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Equal("snapshot 'mismatch' not found.", ex.Message);
    }

    [Fact]
    public async Task Impact_MaxDepthBelowOne_NamesMaxDepth()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var impact = new ImpactTool(session);
        string anyId;
        using (var store = OpenStore(DbPath))
            anyId = store.GetSymbolIdsInSnapshot(session.PinnedSnapshotId).First();
        var ex = Assert.Throws<McpProtocolException>(() => impact.LurpImpact(symbol: anyId, max_depth: 0));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Equal("max_depth must be a positive integer.", ex.Message);
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

        var mcpJson = impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: CallsKind, max_depth: 3, limit: 50);
        using var mcpDoc = JsonDocument.Parse(mcpJson);

        // Contract/acceptance: the CLI and MCP documents come from the same
        // ImpactPaging builder; the MCP envelope adds only pinned and limit.
        var cliResult = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={callerId}",
            "--direction=downstream",
            "--kinds=Calls",
            "--max-depth=3",
            "--limit=50",
            $"--output-dir={TestDir}",
            "--output=json"
        ], sink));
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

    [Fact]
    public async Task Mcp_EmptyDirection_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", direction: ""));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task Mcp_UnknownKind_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", kinds: ["Bogus"]));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    // R136: an empty filter is a rejection, not "no filter".
    [Fact]
    public async Task Mcp_EmptyKinds_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", kinds: []));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.StartsWith("kinds must name at least one edge kind. Use one of: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_BlankKindsItem_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", kinds: [""]));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.StartsWith("kinds must name at least one edge kind. Use one of: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_KindsWithEmptyItem_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", kinds: ["Calls", ""]));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Equal("kinds contains an empty item.", ex.Message);
    }

    [Fact]
    public async Task Mcp_EmptyProvenance_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", provenance: []));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.StartsWith("provenance must name at least one provenance. Use one of: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_BlankProvenanceItem_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", provenance: [""]));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.StartsWith("provenance must name at least one provenance. Use one of: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_ProvenanceWithEmptyItem_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", provenance: ["compiler_proved", ""]));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Equal("provenance contains an empty item.", ex.Message);
    }

    [Fact]
    public async Task Mcp_UnknownProvenance_IsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", provenance: ["bogus"]));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task Impact_GlobalImplementationRelationProvenance_ThrowsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: "T:ImpactProj.Foo|ImpactProj", provenance: ["global_implementation_relation"]));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("Unknown provenance 'global_implementation_relation' in provenance.", ex.Message);
        Assert.Contains("compiler_proved, framework_derived, possible, convention, name_candidate, runtime_unknown", ex.Message);
    }

    [Fact]
    public async Task Mcp_KindsAreCaseInsensitive()
    {
        var snap = await IndexInitialAsync();
        await using var session = CreateSession();
        var impact = new ImpactTool(session);

        string callerId;
        using (var store = OpenStore(DbPath))
            callerId = store.GetSymbolIdsInSnapshot(snap).First(id => store.GetSymbolInfo(id, snap)?.FullyQualifiedName?.Contains("ImpactProj.Foo.Caller") == true);

        using var lower = JsonDocument.Parse(impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: ["calls"]));
        using var upper = JsonDocument.Parse(impact.LurpImpact(symbol: callerId, direction: "downstream", kinds: ["Calls"]));
        var lowerTotal = lower.RootElement.GetProperty("symbol_count_total").GetInt32();
        Assert.Equal(lowerTotal, upper.RootElement.GetProperty("symbol_count_total").GetInt32());
        Assert.True(lowerTotal > 0);

        using var provLower = JsonDocument.Parse(impact.LurpImpact(symbol: callerId, direction: "downstream", provenance: ["compiler_proved"]));
        using var provUpper = JsonDocument.Parse(impact.LurpImpact(symbol: callerId, direction: "downstream", provenance: ["COMPILER_PROVED"]));
        var provLowerTotal = provLower.RootElement.GetProperty("symbol_count_total").GetInt32();
        Assert.Equal(provLowerTotal, provUpper.RootElement.GetProperty("symbol_count_total").GetInt32());
        Assert.True(provLowerTotal > 0);
    }
}
