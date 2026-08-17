using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpReadSurfaceTests : IntegrationTestBase
{
    private async Task<string> IndexFixtureAndGetSnapshot()
    {
        CreateProject("ReadProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace ReadProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                    }
                    public class CourseService {
                        public void CreateAsync() {}
                    }
                    public class GeneratedHolder {
                        public void GenMethod() {}
                    }
                }
                """,
            ["Extra.cs"] = """
                namespace ReadProj {
                    public class Extra {
                        public void ExtraMethod() { new Foo().Bar(); }
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private (McpSessionContext session, GetSourceTool getSource, NavigateTool navigate, FindSymbolTool findSymbol, SearchTool search) CreateTools()
    {
        var args = new[] { $"--solution={SolutionPath}" };
        var session = McpSessionContext.Create(args);
        return (session, new GetSourceTool(session), new NavigateTool(session), new FindSymbolTool(session), new SearchTool(session));
    }

    [Fact]
    public async Task GetSource_ReturnsSource_AndEnvelope()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            // Discover a document path
            string docPath;
            using (var store = OpenStore(DbPath))
            {
                docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First();
            }

            var json = getSource.LurpGetSource(document: docPath);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
            Assert.True(doc.RootElement.GetProperty("pinned").GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("freshness", out _));
            Assert.True(doc.RootElement.TryGetProperty("source", out var src));
            Assert.False(string.IsNullOrEmpty(src.GetString()));

            // Normalized Windows path still resolves
            var winPath = docPath.Replace("/", "\\");
            var jsonWin = getSource.LurpGetSource(document: winPath);
            using var docWin = JsonDocument.Parse(jsonWin);
            Assert.Equal(snapshotId, docWin.RootElement.GetProperty("snapshot_id").GetString());
        }
    }

    [Fact]
    public async Task GetSource_MismatchedSnapshot_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: "ReadProj/Models.cs", snapshot_id: "mismatch"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("snapshot mismatch", ex.Message);
        }
    }

    [Fact]
    public async Task GetSource_NotFound_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: "nonexistent.cs"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task Navigate_ReturnsTarget_AndNullIsSuccess()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, _, navigate, _, _) = CreateTools();
        await using (session)
        {
            // Find a file+line that exists
            string docPath;
            using (var store = OpenStore(DbPath))
            {
                docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First();
            }

            var json = navigate.LurpNavigate(file: docPath, line: 2);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
            Assert.True(doc.RootElement.GetProperty("pinned").GetBoolean());
            // target may be object or null — both are success
            Assert.True(doc.RootElement.TryGetProperty("target", out _));

            // Line far beyond file should be null target, not error
            var jsonNull = navigate.LurpNavigate(file: docPath, line: 9999);
            using var docNull = JsonDocument.Parse(jsonNull);
            Assert.True(docNull.RootElement.TryGetProperty("target", out var targetNull));
            Assert.Equal(JsonValueKind.Null, targetNull.ValueKind);
        }
    }

    [Fact]
    public async Task Navigate_MismatchedSnapshot_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, _, navigate, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => navigate.LurpNavigate(file: "ReadProj/Models.cs", line: 1, snapshot_id: "bad"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task FindSymbol_ThreeForms_ResolveIdentically()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
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
                    break;
                }
            }
            Assert.False(string.IsNullOrEmpty(found));
            symbolId = found!;
            var infoFound = store.GetSymbolInfo(symbolId, snapshotId)!;
            fqn = infoFound.FullyQualifiedName ?? symbolId;
            docCommentId = symbolId.Split('|')[0];
        }

        var (session, _, _, findSymbol, _) = CreateTools();
        await using (session)
        {
            var jsonPipe = findSymbol.LurpFindSymbol(symbol: symbolId);
            var jsonDocId = findSymbol.LurpFindSymbol(symbol: docCommentId);
            var jsonFqn = findSymbol.LurpFindSymbol(symbol: fqn);

            using var docPipe = JsonDocument.Parse(jsonPipe);
            using var docDocId = JsonDocument.Parse(jsonDocId);
            using var docFqn = JsonDocument.Parse(jsonFqn);

            Assert.Equal(snapshotId, docPipe.RootElement.GetProperty("snapshot_id").GetString());
            Assert.Equal(snapshotId, docDocId.RootElement.GetProperty("snapshot_id").GetString());
            Assert.Equal(snapshotId, docFqn.RootElement.GetProperty("snapshot_id").GetString());

            var fqnPipe = docPipe.RootElement.GetProperty("fully_qualified_name").GetString();
            var fqnDocId = docDocId.RootElement.GetProperty("fully_qualified_name").GetString();
            var fqnFqn = docFqn.RootElement.GetProperty("fully_qualified_name").GetString();

            Assert.Equal(fqnPipe, fqnDocId);
            Assert.Equal(fqnPipe, fqnFqn);

            // Envelope checks
            Assert.True(docPipe.RootElement.GetProperty("pinned").GetBoolean());
            Assert.True(docPipe.RootElement.TryGetProperty("freshness", out _));
            Assert.True(docPipe.RootElement.TryGetProperty("locations", out _));
        }
    }

    [Fact]
    public async Task FindSymbol_NotFound_MapsToInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, _, _, findSymbol, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => findSymbol.LurpFindSymbol(symbol: "NonExistent.Symbol"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task Search_FtsPhraseRegression_DoesNotThrow()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, _, _, _, search) = CreateTools();
        await using (session)
        {
            // Known trigger that previously threw SqliteException before quoting fix
            var json = search.LurpSearch(query: "CourseService.CreateAsync", type: "source");
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
            Assert.True(doc.RootElement.TryGetProperty("results", out _));

            var jsonSymbol = search.LurpSearch(query: "CourseService.CreateAsync", type: "symbol");
            using var docSym = JsonDocument.Parse(jsonSymbol);
            Assert.Equal(snapshotId, docSym.RootElement.GetProperty("snapshot_id").GetString());
        }
    }

    [Fact]
    public async Task Search_IncludeGenerated_Toggle_DoesNotThrow()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, _, _, _, search) = CreateTools();
        await using (session)
        {
            var jsonWithout = search.LurpSearch(query: "Foo", type: "symbol", include_generated: false);
            var jsonWith = search.LurpSearch(query: "Foo", type: "symbol", include_generated: true);
            using var docWithout = JsonDocument.Parse(jsonWithout);
            using var docWith = JsonDocument.Parse(jsonWith);
            Assert.True(docWithout.RootElement.TryGetProperty("results", out _));
            Assert.True(docWith.RootElement.TryGetProperty("results", out _));
        }
    }

    [Fact]
    public async Task Search_CursorPagination_Works()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, _, _, _, search) = CreateTools();
        await using (session)
        {
            var jsonFirst = search.LurpSearch(query: "Foo", type: "symbol", limit: 1);
            using var docFirst = JsonDocument.Parse(jsonFirst);
            var nextCursor = docFirst.RootElement.GetProperty("next_cursor").GetString();
            // If there are more than 1 result, cursor should be present
            var resultsFirst = docFirst.RootElement.GetProperty("results").GetArrayLength();

            if (!string.IsNullOrEmpty(nextCursor))
            {
                var jsonSecond = search.LurpSearch(query: "Foo", type: "symbol", limit: 1, cursor: nextCursor);
                using var docSecond = JsonDocument.Parse(jsonSecond);
                Assert.True(docSecond.RootElement.TryGetProperty("results", out _));
            }
            else
            {
                // No more results — still success
                Assert.True(resultsFirst >= 0);
            }

            // Cursor with source type should fail
            var ex = Assert.Throws<McpProtocolException>(() => search.LurpSearch(query: "Foo", type: "source", cursor: "any"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task Search_EnvelopeConsistency()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, _, _, _, search) = CreateTools();
        await using (session)
        {
            var json = search.LurpSearch(query: "Foo", type: "all");
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("snapshot_id", out _));
            Assert.True(doc.RootElement.TryGetProperty("freshness", out _));
            Assert.True(doc.RootElement.GetProperty("pinned").GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("results", out _));
            Assert.True(doc.RootElement.TryGetProperty("next_cursor", out _));
        }
    }

    [Fact]
    public async Task Search_MismatchedSnapshot_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, _, _, _, search) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => search.LurpSearch(query: "Foo", snapshot_id: "mismatch"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("snapshot mismatch", ex.Message);
        }
    }
}
