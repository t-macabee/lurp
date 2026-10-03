using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Tests.Mcp;

/// <summary>
///     MCP boundary and concurrency contract tests (audit B5 burst tests 1, 1b,
///     2, 3 and A1 <c>McpBoundaryTests</c>): per-call read-only connections must
///     survive parallel calls, the pin must stay coherent under a concurrent
///     refresh, reads must keep working while a writer is running, inputs above
///     the documented caps must be rejected, and <c>lurp_index</c> must refuse to
///     run without a session solution.
/// </summary>
public sealed class McpBoundaryTests : IntegrationTestBase
{
    private const string Source =
        """
        namespace BurstProj {
            public class Foo {
                public void Bar() {}
                public void Caller() { Bar(); }
            }
        }
        """;

    private async Task<(string SnapshotId, string SymbolId, string DocumentPath)> IndexAsync()
    {
        CreateProject("BurstProj", new Dictionary<string, string> { ["Models.cs"] = Source });
        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        var symbolId = store.GetSymbolIdsInSnapshot(snapshotId).First(id =>
            store.GetSymbolInfo(id, snapshotId)?.FullyQualifiedName?.Contains("BurstProj.Foo") == true &&
            store.GetSymbolInfo(id, snapshotId)?.Kind == IndexedSymbolKind.Type);
        var documentPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First();
        store.Close();
        return (snapshotId, symbolId, documentPath);
    }

    private McpSessionContext CreateSession()
    {
        return McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}"]);
    }

    [Fact]
    public void WriteTools_CarryExpectedAnnotations()
    {
        // ReadOnly=false so a client can see the surface is not read-only, and
        // Destructive=true on retract so hosts can gate a hard delete behind
        // confirmation.
        var index = typeof(IndexTool).GetMethod(nameof(IndexTool.LurpIndex))!.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.False(index.ReadOnly);

        var retract = typeof(RetractAnnotationTool).GetMethod(nameof(RetractAnnotationTool.LurpRetractAnnotation))!
            .GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.False(retract.ReadOnly);
        Assert.True(retract.Destructive);
    }

    [Fact]
    public async Task Index_WithoutSessionSolution_IsRejectedWithInvalidParams()
    {
        await IndexAsync();
        // No --solution= on the session: the per-call solution parameter is gone,
        // so there is nothing to index and the tool must say so.
        await using var session = McpSessionContext.Create([$"--output-dir={Path.GetDirectoryName(DbPath)!}"]);
        var state = new McpIndexSessionState();
        var tool = new IndexTool(session, state);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpIndex(strategy: "full"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("no solution configured", ex.Message);
        Assert.Null(state.Current);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("snippet_tokens")]
    public async Task Search_AboveCap_IsRejected(string parameter)
    {
        await IndexAsync();
        await using var session = CreateSession();
        var tool = new SearchTool(session);

        var ex = parameter == "limit"
            ? Assert.Throws<McpProtocolException>(() => tool.LurpSearch(query: "Foo", limit: 501))
            : Assert.Throws<McpProtocolException>(() => tool.LurpSearch(query: "Foo", snippet_tokens: 2001));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("<= ", ex.Message);
    }

    [Fact]
    public async Task Context_AboveCaps_IsRejected()
    {
        var (_, symbolId, _) = await IndexAsync();
        await using var session = CreateSession();
        var tool = new ContextTool(session);

        var budget = Assert.Throws<McpProtocolException>(() => tool.LurpContext(symbol: symbolId, content_budget: 200_001));
        Assert.Equal(McpErrorCode.InvalidParams, budget.ErrorCode);

        var hops = Assert.Throws<McpProtocolException>(() => tool.LurpContext(symbol: symbolId, max_hops: 21));
        Assert.Equal(McpErrorCode.InvalidParams, hops.ErrorCode);
    }

    [Fact]
    public async Task Impact_AboveCaps_IsRejected()
    {
        var (_, symbolId, _) = await IndexAsync();
        await using var session = CreateSession();
        var tool = new ImpactTool(session);

        var depth = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: symbolId, max_depth: 21));
        Assert.Equal(McpErrorCode.InvalidParams, depth.ErrorCode);

        var limit = Assert.Throws<McpProtocolException>(() => tool.LurpImpact(symbol: symbolId, limit: 501));
        Assert.Equal(McpErrorCode.InvalidParams, limit.ErrorCode);
    }

    [Fact]
    public async Task Outline_AboveCap_IsRejected()
    {
        var (_, _, documentPath) = await IndexAsync();
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpOutline(document: documentPath, limit: 501));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task ParallelReadCalls_AllSucceed_AndMatchSequentialResults()
    {
        var (snapshotId, symbolId, documentPath) = await IndexAsync();
        await using var session = CreateSession();

        var search = new SearchTool(session);
        var grep = new GrepTool(session);
        var getSource = new GetSourceTool(session);
        var outline = new OutlineTool(session);
        var impact = new ImpactTool(session);
        var dead = new DeadCandidatesTool(session);

        // Sequential baseline for the search shape (per-call freshness timestamps
        // differ, so compare the stable payload slice).
        var baseline = JsonDocument.Parse(search.LurpSearch(query: "Foo", type: "symbol"))
            .RootElement.GetProperty("results").GetRawText();

        const int callCount = 200;
        var failures = new ConcurrentBag<Exception>();
        var mismatches = new ConcurrentBag<string>();

        var tasks = new List<Task>(callCount);
        for (var i = 0; i < callCount; i++)
        {
            var kind = i % 6;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    string json = kind switch
                    {
                        0 => search.LurpSearch(query: "Foo", type: "symbol"),
                        1 => grep.LurpGrep(query: "Bar"),
                        2 => getSource.LurpGetSource(document: documentPath),
                        3 => outline.LurpOutline(document: documentPath),
                        4 => impact.LurpImpact(symbol: symbolId, direction: "downstream"),
                        _ => dead.LurpDeadCandidates()
                    };

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.GetProperty("snapshot_id").GetString() != snapshotId)
                        mismatches.Add($"snapshot mismatch in burst call {kind}");
                    if (kind == 0)
                    {
                        var results = doc.RootElement.GetProperty("results").GetRawText();
                        if (!string.Equals(results, baseline, StringComparison.Ordinal))
                            mismatches.Add("search results differ from the sequential baseline");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Empty(failures);
        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task ParallelReadCalls_DuringRefreshAck_AllSucceed_AndPinAdvances()
    {
        var (snapshot1, _, _) = await IndexAsync();

        await using var session = CreateSession();
        Assert.Equal(snapshot1, session.PinnedSnapshotId);
        var search = new SearchTool(session);

        // Produce a second snapshot without moving the session pin.
        CreateProject("BurstProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace BurstProj2 { public class B { public void Bar() {} } }"
        });
        var snapshot2 = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.NotEqual(snapshot1, snapshot2);
        Assert.Equal(snapshot1, session.PinnedSnapshotId);

        var failures = new ConcurrentBag<Exception>();
        var refresh = new RefreshTool(session);
        var readings = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            try { search.LurpSearch(query: "Bar", type: "symbol"); }
            catch (Exception ex) { failures.Add(ex); }
        })).ToList();

        // Advance the pin while the reads are in flight: per-call connections make
        // this safe, and every call must still return a pinned snapshot id.
        var refreshJson = refresh.LurpRefresh(ack: snapshot2);
        using var refreshDoc = JsonDocument.Parse(refreshJson);
        Assert.True(refreshDoc.RootElement.GetProperty("pinned").GetBoolean());

        await Task.WhenAll(readings);

        Assert.Empty(failures);
        Assert.Equal(snapshot2, session.PinnedSnapshotId);
    }

    [Fact]
    public async Task ParallelReadCalls_DuringBackgroundIndex_AllSucceed()
    {
        var (snapshot1, _, _) = await IndexAsync();

        CreateProject("BurstProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace BurstProj2 { public class B { public void Bar() {} } }"
        });

        await using var session = CreateSession();
        var indexState = new McpIndexSessionState();
        var indexTool = new IndexTool(session, indexState);
        var search = new SearchTool(session);
        var grep = new GrepTool(session);

        var jsonStart = indexTool.LurpIndex(strategy: "full");
        using var startDoc = JsonDocument.Parse(jsonStart);
        Assert.Equal("running", startDoc.RootElement.GetProperty("status").GetString());

        var failures = new ConcurrentBag<Exception>();
        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            var kind = i % 2;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    _ = kind == 0 ? search.LurpSearch(query: "Foo", type: "symbol") : grep.LurpGrep(query: "Foo");
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }));
        }

        await Task.WhenAll(tasks);

        var background = indexState.Current?.BackgroundTask;
        Assert.NotNull(background);
        await background!;

        var snapshot = indexState.Snapshot();
        Assert.NotNull(snapshot);
        Assert.Equal("completed", snapshot!.Status);
        Assert.Empty(failures);
    }
}
