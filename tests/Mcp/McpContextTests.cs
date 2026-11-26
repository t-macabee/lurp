using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpContextTests : IntegrationTestBase
{
    private async Task<string> IndexFixtureAndGetSnapshot()
    {
        CreateProject("CtxProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace CtxProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                    }
                    public class Tests {
                        public void TestBar() { new Foo().Bar(); }
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private (McpSessionContext session, ContextTool tool) CreateSessionTool()
    {
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        var session = McpSessionContext.Create(args);
        var tool = new ContextTool(session);
        return (session, tool);
    }

    [Fact]
    public async Task Context_CapsuleAndTier_EmitTheSameTopLevelKeys()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            const string symbol = "CtxProj.Foo.Bar";
            using var capsule = JsonDocument.Parse(tool.LurpContext(symbol: symbol));
            using var tier = JsonDocument.Parse(tool.LurpContext(symbol: symbol, tier: "direct_callers"));
            string[] expectedKeys = ["snapshot_id", "freshness", "pinned", "capsule", "tier_page"];
            Assert.Equal(expectedKeys, capsule.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(expectedKeys, tier.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(JsonValueKind.Object, capsule.RootElement.GetProperty("capsule").ValueKind);
            Assert.Equal(JsonValueKind.Null, capsule.RootElement.GetProperty("tier_page").ValueKind);
            Assert.Equal(JsonValueKind.Null, tier.RootElement.GetProperty("capsule").ValueKind);
            Assert.Equal(JsonValueKind.Object, tier.RootElement.GetProperty("tier_page").ValueKind);
        }
    }

    [Fact]
    public async Task ThreeSymbolForms_ResolveIdentically()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();

        // Discover a method symbol dynamically (FQN includes arity/params, so hardcoding is brittle)
        string symbolId;
        string fqn;
        string docCommentId;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(snapshotId);
            string? found = null;
            foreach (var id in ids)
            {
                var info = store.GetSymbolInfo(id, snapshotId);
                if (info != null && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("Foo.Bar"))
                {
                    found = id;
                    fqn = info.FullyQualifiedName;
                    break;
                }
            }
            Assert.False(string.IsNullOrEmpty(found));
            symbolId = found!;
            var infoFound = store.GetSymbolInfo(symbolId, snapshotId)!;
            fqn = infoFound.FullyQualifiedName ?? symbolId;
            docCommentId = symbolId.Split('|')[0];
        }

        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var jsonPipe = tool.LurpContext(symbol: symbolId);
            var jsonDocId = tool.LurpContext(symbol: docCommentId);
            var jsonFqn = tool.LurpContext(symbol: fqn);

            // All three return successfully and contain same snapshot_id / pinned
            using var docPipe = JsonDocument.Parse(jsonPipe);
            using var docDocId = JsonDocument.Parse(jsonDocId);
            using var docFqn = JsonDocument.Parse(jsonFqn);

            Assert.Equal(snapshotId, docPipe.RootElement.GetProperty("snapshot_id").GetString());
            Assert.Equal(snapshotId, docDocId.RootElement.GetProperty("snapshot_id").GetString());
            Assert.Equal(snapshotId, docFqn.RootElement.GetProperty("snapshot_id").GetString());

            // Capsule anchor should be same FQN
            var anchorPipe = docPipe.RootElement.GetProperty("capsule").GetProperty("anchor").GetProperty("fully_qualified_name").GetString();
            var anchorDocId = docDocId.RootElement.GetProperty("capsule").GetProperty("anchor").GetProperty("fully_qualified_name").GetString();
            var anchorFqn = docFqn.RootElement.GetProperty("capsule").GetProperty("anchor").GetProperty("fully_qualified_name").GetString();

            Assert.Equal(anchorPipe, anchorDocId);
            Assert.Equal(anchorPipe, anchorFqn);

            // Budget defaults inside capsule should be consistent (estimated_tokens)
            Assert.True(docPipe.RootElement.GetProperty("capsule").TryGetProperty("budget", out _));
        }
    }

    [Fact]
    public async Task BudgetDefaults_8000_And_16000_ForTypeAnchor()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        string? methodFound = null;
        string? typeFound = null;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(snapshotId);
            foreach (var id in ids)
            {
                var info = store.GetSymbolInfo(id, snapshotId);
                if (info == null) continue;
                if (SymbolId.TryParse(id, out var sid))
                {
                    if (!sid.IsType && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("Foo.Bar"))
                        methodFound = id;
                    if (sid.IsType && info.FullyQualifiedName == "global::CtxProj.Foo")
                        typeFound = id;
                }
            }
        }
        Assert.False(string.IsNullOrEmpty(methodFound));
        Assert.False(string.IsNullOrEmpty(typeFound));
        var methodSymbolId = methodFound!;
        var typeSymbolId = typeFound!;

        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var jsonMethod = tool.LurpContext(symbol: methodSymbolId);
            var jsonType = tool.LurpContext(symbol: typeSymbolId);
            var jsonTypeFqn = tool.LurpContext(symbol: "CtxProj.Foo");
            var jsonTypeDoc = tool.LurpContext(symbol: "T:CtxProj.Foo");
            var jsonTypeLocation = tool.LurpContext(file: "src/CtxProj/Models.cs", line: 2);

            using var docMethod = JsonDocument.Parse(jsonMethod);
            using var docType = JsonDocument.Parse(jsonType);
            using var docTypeFqn = JsonDocument.Parse(jsonTypeFqn);
            using var docTypeDoc = JsonDocument.Parse(jsonTypeDoc);
            using var docTypeLocation = JsonDocument.Parse(jsonTypeLocation);

            Assert.Equal(8000, docMethod.RootElement.GetProperty("capsule").GetProperty("budget").GetInt32());
            Assert.Equal(16000, docType.RootElement.GetProperty("capsule").GetProperty("budget").GetInt32());
            Assert.Equal(16000, docTypeFqn.RootElement.GetProperty("capsule").GetProperty("budget").GetInt32());
            Assert.Equal(16000, docTypeDoc.RootElement.GetProperty("capsule").GetProperty("budget").GetInt32());
            Assert.Equal(16000, docTypeLocation.RootElement.GetProperty("capsule").GetProperty("budget").GetInt32());
        }

        var cliJson = await CliModeRunner.RunAsync("context", [
            "--file=src/CtxProj/Models.cs", "--line=2",
            "--output=json", $"--output-dir={TestDir}"]);
        using var cliDoc = JsonDocument.Parse(cliJson);
        Assert.Equal(16000, cliDoc.RootElement.GetProperty("budget").GetInt32());
    }

    [Fact]
    public async Task GapCapsule_OnUnresolvedFileLine_ReturnsSuccess()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var json = tool.LurpContext(file: "src/CtxProj/Models.cs", line: 9999);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
            Assert.True(doc.RootElement.GetProperty("pinned").GetBoolean());
            var capsule = doc.RootElement.GetProperty("capsule");
            var anchorKind = capsule.GetProperty("anchor").GetProperty("kind").GetString();
            Assert.Equal("gap", anchorKind);
            // All tiers unresolved via omitted_tiers
            Assert.True(capsule.TryGetProperty("omitted_tiers", out var omitted));
            Assert.True(omitted.GetArrayLength() > 0);
        }
    }

    [Fact]
    public async Task MismatchedSnapshotId_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() =>
                tool.LurpContext(symbol: "CtxProj.Foo.Bar", snapshot_id: "mismatch-snapshot-id"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("snapshot 'mismatch-snapshot-id' not found.", ex.Message);
        }
    }

    [Fact]
    public async Task TierAndCursor_Continuation_ReturnsNextPage()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            // Foo.Bar is called by Foo.Caller and Tests.TestBar, so a one-item page always has a successor.
            var jsonFirst = tool.LurpContext(symbol: "CtxProj.Foo.Bar", tier: "direct_callers", tier_limit: 1);
            using var docFirst = JsonDocument.Parse(jsonFirst);
            Assert.Equal(snapshotId, docFirst.RootElement.GetProperty("snapshot_id").GetString());
            var tierPage = docFirst.RootElement.GetProperty("tier_page");
            Assert.Equal("direct_callers", tierPage.GetProperty("tier").GetString());
            Assert.Equal(2, tierPage.GetProperty("total_items").GetInt32());
            var nextCursor = tierPage.GetProperty("next_cursor").GetString();

            Assert.False(string.IsNullOrEmpty(nextCursor));
            var jsonSecond = tool.LurpContext(symbol: "CtxProj.Foo.Bar", tier: "direct_callers", tier_limit: 1, cursor: nextCursor);
            using var docSecond = JsonDocument.Parse(jsonSecond);
            Assert.Equal(1, docSecond.RootElement.GetProperty("tier_page").GetProperty("offset").GetInt32());
        }
    }

    [Fact]
    public async Task Tier_CursorFromPreviousSequenceLayout_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var jsonFirst = tool.LurpContext(symbol: "CtxProj.Foo.Bar", tier: "direct_callers", tier_limit: 1);
            using var docFirst = JsonDocument.Parse(jsonFirst);
            var tierPage = docFirst.RootElement.GetProperty("tier_page");
            var symbolId = tierPage.GetProperty("symbol_id").GetString()!;
            var current = SequenceCursor.TryDecode(tierPage.GetProperty("next_cursor").GetString()!)!;

            // The 4.1.0 fingerprint had no sequence marker.
            var stale = current with { Fingerprint = SequenceCursor.ComputeFingerprint(symbolId, "direct_callers", "3", "False") };
            Assert.NotEqual(current.Fingerprint, stale.Fingerprint);

            var ex = Assert.Throws<McpProtocolException>(() =>
                tool.LurpContext(symbol: "CtxProj.Foo.Bar", tier: "direct_callers", tier_limit: 1, cursor: stale.Encode()));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("request a fresh page", ex.Message);
        }
    }

    [Fact]
    public async Task Tier_Unknown_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() =>
                tool.LurpContext(symbol: "CtxProj.Foo", tier: "unknown_tier"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("unknown tier", ex.Message);
        }
    }

    [Fact]
    public async Task Cursor_Invalid_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() =>
                tool.LurpContext(symbol: "CtxProj.Foo", tier: "direct_callers", cursor: "not-a-valid-cursor"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task Context_HalfAnchorPair_IsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var cases = new[]
            {
                () => tool.LurpContext(symbol: "CtxProj.Foo", file: "src/CtxProj/Models.cs"),
                () => tool.LurpContext(file: "src/CtxProj/Models.cs"),
                () => tool.LurpContext(line: 2)
            };

            foreach (var call in cases)
            {
                var ex = Assert.Throws<McpProtocolException>(call);
                Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
                Assert.Equal("file and line must be given together.", ex.Message);
            }
        }
    }

    // Covers the entry check only: it passes with or without the token forwarded into assembly.
    [Fact]
    public async Task Context_PreCancelledToken_Throws()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                tool.LurpContext(symbol: "CtxProj.Foo", cancellationToken: cts.Token));
        }
    }
}
