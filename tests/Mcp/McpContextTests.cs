using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Workspace;
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
        var outputDir = Path.GetDirectoryName(SolutionPath)!;
        var args = new[] { $"--solution={SolutionPath}" };
        var session = McpSessionContext.Create(args);
        var tool = new ContextTool(session);
        return (session, tool);
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

            // Budget defaults inside capsule should be consistent (estimatedTokens)
            Assert.True(docPipe.RootElement.GetProperty("capsule").TryGetProperty("budget", out _));
        }
    }

    [Fact]
    public async Task BudgetDefaults_8000_And_16000_ForTypeAnchor()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        // Discover method and type symbolIds to test budget defaults (type should be 16000 via pipe/T: form)
        string methodSymbolId;
        string typeSymbolId;
        string typeDocCommentId;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(snapshotId);
            string? methodFound = null;
            string? typeFound = null;
            foreach (var id in ids)
            {
                var info = store.GetSymbolInfo(id, snapshotId);
                if (info == null) continue;
                if (SymbolId.TryParse(id, out var sid))
                {
                    if (!sid.IsType && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("Foo.Bar"))
                        methodFound = id;
                    if (sid.IsType && info.FullyQualifiedName == "CtxProj.Foo")
                        typeFound = id;
                }
            }
            // Fallback if exact match not found — pick any method and any type
            if (methodFound == null || typeFound == null)
            {
                foreach (var id in ids)
                {
                    var info = store.GetSymbolInfo(id, snapshotId);
                    if (info == null) continue;
                    if (methodFound == null && SymbolId.TryParse(id, out var sid) && !sid.IsType)
                        methodFound = id;
                    if (typeFound == null && SymbolId.TryParse(id, out var sid2) && sid2.IsType)
                        typeFound = id;
                }
            }
            Assert.False(string.IsNullOrEmpty(methodFound));
            Assert.False(string.IsNullOrEmpty(typeFound));
            methodSymbolId = methodFound!;
            typeSymbolId = typeFound!;
            typeDocCommentId = typeSymbolId.Split('|')[0];
        }

        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var jsonMethod = tool.LurpContext(symbol: methodSymbolId);
            // Type via full symbolId (pipe form T:...|assembly) triggers 16000 budget; bare FQN/docComment alone would be 8000 per Handler logic
            var jsonType = tool.LurpContext(symbol: typeSymbolId);

            using var docMethod = JsonDocument.Parse(jsonMethod);
            using var docType = JsonDocument.Parse(jsonType);

            var budgetMethod = docMethod.RootElement.GetProperty("capsule").GetProperty("budget").GetInt32();
            var budgetType = docType.RootElement.GetProperty("capsule").GetProperty("budget").GetInt32();

            Assert.Equal(8000, budgetMethod);
            Assert.Equal(16000, budgetType);
        }
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
            // All tiers unresolved via omittedTiers
            Assert.True(capsule.TryGetProperty("omitted_tiers", out var omitted));
            Assert.True(omitted.GetArrayLength() > 0);
        }
    }

    [Fact]
    public async Task MismatchedSnapshotId_ReturnsInvalidParams()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() =>
                tool.LurpContext(symbol: "CtxProj.Foo.Bar", snapshot_id: "mismatch-snapshot-id"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("snapshot mismatch", ex.Message);
        }
    }

    [Fact]
    public async Task TierAndCursor_Continuation_ReturnsNextPage()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            // Use a type anchor whose tiers have items
            var jsonFirst = tool.LurpContext(symbol: "CtxProj.Foo", tier: "direct_callers", tier_limit: 1);
            using var docFirst = JsonDocument.Parse(jsonFirst);
            Assert.Equal(snapshotId, docFirst.RootElement.GetProperty("snapshot_id").GetString());
            var tierPage = docFirst.RootElement.GetProperty("tier_page");
            Assert.Equal("direct_callers", tierPage.GetProperty("tier").GetString());
            var hasMore = tierPage.GetProperty("total_items").GetInt32() > 1;
            var nextCursor = tierPage.GetProperty("next_cursor").GetString();

            if (hasMore)
            {
                Assert.False(string.IsNullOrEmpty(nextCursor));
                var jsonSecond = tool.LurpContext(symbol: "CtxProj.Foo", tier: "direct_callers", tier_limit: 1, cursor: nextCursor);
                using var docSecond = JsonDocument.Parse(jsonSecond);
                Assert.Equal(1, docSecond.RootElement.GetProperty("tier_page").GetProperty("offset").GetInt32());
            }
            else
            {
                Assert.True(string.IsNullOrEmpty(nextCursor) || nextCursor == null);
            }
        }
    }

    [Fact]
    public async Task Tier_Unknown_ReturnsInvalidParams()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
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
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, tool) = CreateSessionTool();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() =>
                tool.LurpContext(symbol: "CtxProj.Foo", tier: "direct_callers", cursor: "not-a-valid-cursor"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }
    }
}
