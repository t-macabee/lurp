using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
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
                    public class FooHelper {}
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
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
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
    public async Task GetSource_UnknownSnapshot_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: "ReadProj/Models.cs", snapshot_id: "mismatch"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("snapshot 'mismatch' not found.", ex.Message);
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
    public async Task Navigate_UnknownSnapshot_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, _, navigate, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => navigate.LurpNavigate(file: "ReadProj/Models.cs", line: 1, snapshot_id: "bad"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("snapshot 'bad' not found.", ex.Message);
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
    public async Task FindSymbol_And_GetSymbol_NotFound_ReturnSameMessageWithoutErrorPrefix()
    {
        var snapshotId = await IndexFixtureAndGetSnapshot();
        var (session, _, _, findSymbol, _) = CreateTools();
        await using (session)
        {
            const string symbol = "NonExistent.Symbol";
            var findError = Assert.Throws<McpProtocolException>(() => findSymbol.LurpFindSymbol(symbol: symbol));
            var getError = Assert.Throws<McpProtocolException>(() => new GetSymbolTool(session).LurpGetSymbol(symbol: symbol));
            Assert.Equal(McpErrorCode.InvalidParams, findError.ErrorCode);
            Assert.Equal(McpErrorCode.InvalidParams, getError.ErrorCode);
            Assert.Equal($"Symbol '{symbol}' not found in snapshot '{snapshotId}'. {HandlerBootstrap.AcceptedSymbolFormsText}",
                findError.Message);
            Assert.Equal(findError.Message, getError.Message);
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
            // The dotted text is not literally in the fixture, so zero hits is correct: the regression is the throw.
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("results").ValueKind);

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
            // Foo is hand-written, so the generated-code toggle must not hide it either way.
            Assert.True(docWithout.RootElement.GetProperty("results").GetArrayLength() > 0);
            Assert.True(docWith.RootElement.GetProperty("results").GetArrayLength() > 0);
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
            // The fixture declares both Foo and FooHelper, so a one-item page must hand back a cursor.
            var firstResults = docFirst.RootElement.GetProperty("results");
            Assert.Equal(1, firstResults.GetArrayLength());
            Assert.False(string.IsNullOrEmpty(nextCursor), "a limit-1 page of a 2+ match query must carry a cursor");

            var jsonSecond = search.LurpSearch(query: "Foo", type: "symbol", limit: 1, cursor: nextCursor);
            using var docSecond = JsonDocument.Parse(jsonSecond);
            var secondResults = docSecond.RootElement.GetProperty("results");
            Assert.Equal(1, secondResults.GetArrayLength());
            Assert.NotEqual(firstResults[0].GetRawText(), secondResults[0].GetRawText());

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
            var results = doc.RootElement.GetProperty("results");
            Assert.True(results.GetArrayLength() > 0);
            Assert.Contains(results.EnumerateArray(),
                r => r.TryGetProperty("fully_qualified_name", out var fqn)
                    && (fqn.GetString() ?? "").Contains("Foo", StringComparison.Ordinal));
            Assert.True(doc.RootElement.TryGetProperty("next_cursor", out _));
        }
    }

    [Fact]
    public async Task Search_UnknownSnapshot_ReturnsInvalidParams()
    {
        await IndexFixtureAndGetSnapshot();
        var (session, _, _, _, search) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => search.LurpSearch(query: "Foo", snapshot_id: "mismatch"));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("snapshot 'mismatch' not found.", ex.Message);
        }
    }

    // ── Gap 6: line-windowed reads ───────────────────────────────────

    private async Task<(string snapshotId, string docPath, string fullContent)> IndexLineFixtureAsync()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"// line {i:D2} content for line {i}").ToList();
        // Add a class declaration spanning lines 21-23 to give outline/declarations something to find
        lines.Add("namespace LineProj {");
        lines.Add("    public class LineClass {");
        lines.Add("        public void MethodA() {}");
        lines.Add("        public void MethodB() {}");
        lines.Add("    }");
        lines.Add("}");
        var content = string.Join("\n", lines);
        CreateProject("LineProj", new Dictionary<string, string>
        {
            ["Lines.cs"] = content
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        // Resolve normalized path
        string docPath;
        using (var store = OpenStore(DbPath))
        {
            docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First(k => k.Contains("Lines.cs"));
        }
        return (snapshotId, docPath, content);
    }

    [Fact]
    public async Task GetSource_LineWindow_ReturnsExactLinesAndEchoesRange()
    {
        var (snapshotId, docPath, fullContent) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var json = getSource.LurpGetSource(document: docPath, start_line: 5, end_line: 8);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
            Assert.Equal(5, doc.RootElement.GetProperty("start_line").GetInt32());
            Assert.Equal(8, doc.RootElement.GetProperty("end_line").GetInt32());
            var source = doc.RootElement.GetProperty("source").GetString()!;
            // The store slices at byte offsets including the trailing newline of line 8.
            var expected = string.Join("\n", fullContent.Split('\n').Skip(4).Take(4)) + "\n";
            Assert.Equal(expected, source);
            Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean(), "windowed read should be truncated");
        }
    }

    [Fact]
    public async Task GetSource_ContextLines_WithOnlyStartLine_ExpandsAndClampsAtOne()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            // start_line=2, context=5 => expandedStart should clamp to 1
            var json = getSource.LurpGetSource(document: docPath, start_line: 2, context_lines: 5);
            using var doc = JsonDocument.Parse(json);
            var start = doc.RootElement.GetProperty("start_line").GetInt32();
            Assert.Equal(1, start);
            // end should be clamped appropriately (totalLines or start+ctx expanded)
            var end = doc.RootElement.GetProperty("end_line").GetInt32();
            Assert.True(end >= 2);
        }
    }

    [Fact]
    public async Task GetSource_ContextLines_WithOnlyEndLine_ExpandsAndClampsAtTotal()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            // Get total lines first
            var wholeJson = getSource.LurpGetSource(document: docPath);
            using var wholeDoc = JsonDocument.Parse(wholeJson);
            var total = wholeDoc.RootElement.GetProperty("total_lines").GetInt32();

            var json = getSource.LurpGetSource(document: docPath, end_line: total - 1, context_lines: 5);
            using var doc = JsonDocument.Parse(json);
            var end = doc.RootElement.GetProperty("end_line").GetInt32();
            Assert.Equal(total, end);
            var start = doc.RootElement.GetProperty("start_line").GetInt32();
            Assert.True(start <= total - 1);
        }
    }

    [Fact]
    public async Task GetSource_ContextLines_WithoutBounds_ThrowsInvalidParams()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: docPath, context_lines: 2));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("context_lines requires start_line or end_line.", ex.Message);
        }
    }

    [Fact]
    public async Task GetSource_NegativeContextLines_ThrowsInvalidParams()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: docPath, start_line: 1, context_lines: -1));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("context_lines must be a non-negative integer.", ex.Message);
        }
    }

    [Fact]
    public async Task GetSource_StartLineBelowOne_ThrowsInvalidParams()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: docPath, start_line: 0));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("start_line must be a positive integer (>=1).", ex.Message);
        }
    }

    [Fact]
    public async Task GetSource_EndLineBelowOne_ThrowsInvalidParams()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: docPath, start_line: 1, end_line: 0));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("end_line must be a positive integer (>=1).", ex.Message);
        }
    }

    [Fact]
    public async Task GetSource_StartGreaterThanEnd_ThrowsInvalidParams()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: docPath, start_line: 8, end_line: 5));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Equal("start_line must be <= end_line.", ex.Message);
        }
    }

    [Fact]
    public async Task GetSource_StartBeyondTotal_ThrowsOutOfRange()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            // Get total lines
            var wholeJson = getSource.LurpGetSource(document: docPath);
            using var wholeDoc = JsonDocument.Parse(wholeJson);
            var total = wholeDoc.RootElement.GetProperty("total_lines").GetInt32();

            var ex = Assert.Throws<McpProtocolException>(() => getSource.LurpGetSource(document: docPath, start_line: total + 100));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("beyond", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GetSource_EndBeyondTotal_ClampsToTotal()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var wholeJson = getSource.LurpGetSource(document: docPath);
            using var wholeDoc = JsonDocument.Parse(wholeJson);
            var total = wholeDoc.RootElement.GetProperty("total_lines").GetInt32();
            var wholeSource = wholeDoc.RootElement.GetProperty("source").GetString();

            var json = getSource.LurpGetSource(document: docPath, start_line: 1, end_line: total + 100);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(total, doc.RootElement.GetProperty("end_line").GetInt32());
            var source = doc.RootElement.GetProperty("source").GetString();
            Assert.Equal(wholeSource, source);
        }
    }

    [Fact]
    public async Task GetSource_WholeFile_WhenNoWindow_ReturnsAllWithTruncatedFalse()
    {
        var (_, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var json = getSource.LurpGetSource(document: docPath);
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("source", out var src));
            Assert.False(string.IsNullOrEmpty(src.GetString()));
            Assert.True(doc.RootElement.TryGetProperty("total_lines", out var totalEl));
            Assert.True(totalEl.GetInt32() > 0);

            // Compare with explicit whole-file call — should be same
            var json2 = getSource.LurpGetSource(document: docPath, start_line: null, end_line: null, context_lines: null);
            using var doc2 = JsonDocument.Parse(json2);
            Assert.Equal(src.GetString(), doc2.RootElement.GetProperty("source").GetString());
            Assert.Equal(doc.RootElement.GetProperty("total_lines").GetInt32(), doc2.RootElement.GetProperty("total_lines").GetInt32());
        }
    }

    [Fact]
    public async Task GetSource_OutlineTrue_ReturnsOutlineAlongsideSource()
    {
        var (snapshotId, docPath, _) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var json = getSource.LurpGetSource(document: docPath, outline: true);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("outline", out var outline));
            Assert.Equal(JsonValueKind.Array, outline.ValueKind);
            Assert.True(outline.GetArrayLength() > 0);
            Assert.True(doc.RootElement.TryGetProperty("outline_declaration_count", out var countEl));
            var count = countEl.GetInt32();
            Assert.Equal(outline.GetArrayLength(), count);

            // Cross-check via direct store
            using var store = OpenStore(DbPath);
            var page = store.GetDeclarationsOutline(docPath, snapshotId, false, 100, null);
            Assert.NotNull(page);
            Assert.Equal(page!.TotalCount, count);
        }
    }

    [Fact]
    public async Task GetSource_TotalLines_EqualsActualLineCount()
    {
        var (_, docPath, fullContent) = await IndexLineFixtureAsync();
        var (session, getSource, _, _, _) = CreateTools();
        await using (session)
        {
            var json = getSource.LurpGetSource(document: docPath);
            using var doc = JsonDocument.Parse(json);
            var total = doc.RootElement.GetProperty("total_lines").GetInt32();
            var expected = fullContent.Split('\n').Length;
            // Note: Join with \n gives N lines; ensure file ends without extra empty line
            Assert.Equal(expected, total);
        }
    }
}
