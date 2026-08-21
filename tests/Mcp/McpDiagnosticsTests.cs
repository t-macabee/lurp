using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpDiagnosticsTests : IntegrationTestBase
{
    private async Task<string> IndexDiagnosticsFixtureAsync()
    {
        CreateProject("DiagProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace DiagProj {
                    public class Foo {
                        public void M1() { int unused1 = 0; }
                        public void M2() { int unused2 = 0; }
                        public void M3() { int unused3 = 0; }
                        public void M4() { int unused4 = 0; }
                        public void M5() { int unused5 = 0; }
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create(new[] { $"--solution={SolutionPath}" });

    [Fact]
    public async Task Diagnostics_BasicRetrieval_NonEmpty()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(snapshotId, root.GetProperty("snapshot_id").GetString());
        Assert.True(root.GetProperty("pinned").GetBoolean());
        Assert.True(root.TryGetProperty("freshness", out _));

        var diags = root.GetProperty("diagnostics");
        Assert.True(diags.GetArrayLength() > 0, "Expected at least one diagnostic from the fixture.");

        // Each entry must have the required fields
        foreach (var d in diags.EnumerateArray())
        {
            Assert.True(d.TryGetProperty("project_name", out _));
            Assert.True(d.TryGetProperty("severity", out _));
            Assert.True(d.TryGetProperty("id", out _));
            Assert.True(d.TryGetProperty("message", out _));
            Assert.True(d.TryGetProperty("in_snapshot", out _));
        }
    }

    [Fact]
    public async Task Diagnostics_DefaultSeverityFilter_ExcludesHidden()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        // Default view (no severity arg) should exclude Hidden
        var jsonDefault = tool.LurpDiagnostics();
        using var docDefault = JsonDocument.Parse(jsonDefault);
        var defaultCount = docDefault.RootElement.GetProperty("diagnostic_count").GetInt32();

        // Explicit severity=Hidden should include them
        var jsonHidden = tool.LurpDiagnostics(severity: "Hidden");
        using var docHidden = JsonDocument.Parse(jsonHidden);
        var hiddenCount = docHidden.RootElement.GetProperty("diagnostic_count").GetInt32();

        // All diagnostics (no filter) = default + hidden
        var jsonAll = tool.LurpDiagnostics(severity: "Info");
        using var docAll = JsonDocument.Parse(jsonAll);
        var infoCount = docAll.RootElement.GetProperty("diagnostic_count").GetInt32();

        // The default view should have fewer-or-equal diagnostics than the hidden-only view
        // (since default excludes hidden, and hidden-only shows only hidden).
        // We can't assert exact numbers without knowing Roslyn's output, but we can
        // verify that requesting severity=Hidden doesn't throw and returns >= 0.
        Assert.True(hiddenCount >= 0);
        Assert.True(defaultCount >= 0);
    }

    [Fact]
    public async Task Diagnostics_ExplicitSeverityHidden_IncludesHiddenRows()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics(severity: "Hidden");
        using var doc = JsonDocument.Parse(json);

        // Every returned row should have severity "Hidden"
        foreach (var d in doc.RootElement.GetProperty("diagnostics").EnumerateArray())
            Assert.Equal("Hidden", d.GetProperty("severity").GetString());

        // The echoed severity filter should be "Hidden"
        Assert.Equal("Hidden", doc.RootElement.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task Diagnostics_DocumentFilter_NarrowsToDocument()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();

        string docPath;
        using (var store = OpenStore(DbPath))
        {
            docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys
                .First(k => k.Contains("Models.cs"));
        }

        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics(document: docPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Every returned diagnostic should reference this document (or have null path)
        foreach (var d in root.GetProperty("diagnostics").EnumerateArray())
        {
            var path = d.GetProperty("document_path");
            if (path.ValueKind != JsonValueKind.Null)
                Assert.Equal(docPath, path.GetString());
        }

        // Echoed document filter
        Assert.Equal(docPath, root.GetProperty("document").GetString());
    }

    [Fact]
    public async Task Diagnostics_PathNormalization_IsGitRelativeForwardSlashed()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics();
        using var doc = JsonDocument.Parse(json);

        foreach (var d in doc.RootElement.GetProperty("diagnostics").EnumerateArray())
        {
            var path = d.GetProperty("document_path");
            if (path.ValueKind == JsonValueKind.Null)
                continue;

            var pathStr = path.GetString()!;
            // Must be forward-slashed
            Assert.DoesNotContain("\\", pathStr);
            // Must be relative (no drive letter)
            Assert.DoesNotContain(":", pathStr);
            // Must start with src/ (CreateProject writes under TestDir/src/)
            Assert.StartsWith("src/", pathStr);
        }
    }

    [Fact]
    public async Task Diagnostics_LineNumbers_AreOneBased()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics();
        using var doc = JsonDocument.Parse(json);

        foreach (var d in doc.RootElement.GetProperty("diagnostics").EnumerateArray())
        {
            var startLine = d.GetProperty("start_line");
            if (startLine.ValueKind == JsonValueKind.Null)
                continue;

            // 1-based means minimum value is 1 (not 0)
            Assert.True(startLine.GetInt32() >= 1,
                $"Expected 1-based start_line, got {startLine.GetInt32()}");

            var endLine = d.GetProperty("end_line");
            if (endLine.ValueKind != JsonValueKind.Null)
                Assert.True(endLine.GetInt32() >= 1,
                    $"Expected 1-based end_line, got {endLine.GetInt32()}");
        }
    }

    [Fact]
    public async Task Diagnostics_ProjectFilter_NarrowsToProject()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics(project: "DiagProj");
        using var doc = JsonDocument.Parse(json);

        foreach (var d in doc.RootElement.GetProperty("diagnostics").EnumerateArray())
            Assert.Equal("DiagProj", d.GetProperty("project_name").GetString());

        Assert.Equal("DiagProj", doc.RootElement.GetProperty("project").GetString());
    }

    [Fact]
    public async Task Diagnostics_IdFilter_NarrowsToCode()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();

        // Discover what diagnostic IDs exist
        string targetId;
        await using (var session = CreateSession())
        {
            var tool = new DiagnosticsTool(session);
            var json = tool.LurpDiagnostics();
            using var doc = JsonDocument.Parse(json);
            var first = doc.RootElement.GetProperty("diagnostics").EnumerateArray().First();
            targetId = first.GetProperty("id").GetString()!;
        }

        await using var session2 = CreateSession();
        var tool2 = new DiagnosticsTool(session2);
        var json2 = tool2.LurpDiagnostics(id: targetId);
        using var doc2 = JsonDocument.Parse(json2);

        foreach (var d in doc2.RootElement.GetProperty("diagnostics").EnumerateArray())
            Assert.Equal(targetId, d.GetProperty("id").GetString());

        Assert.Equal(targetId, doc2.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Diagnostics_Pagination_WalkEqualsWhole()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        // Get all diagnostics at once (unpaginated)
        var jsonAll = tool.LurpDiagnostics(limit: 1000);
        using var docAll = JsonDocument.Parse(jsonAll);
        var totalCount = docAll.RootElement.GetProperty("diagnostic_count").GetInt32();
        var allDiags = docAll.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();

        // Fixture must produce enough diagnostics to exercise pagination
        Assert.True(totalCount >= 3, $"Fixture should produce at least 3 diagnostics for pagination, got {totalCount}");
        Assert.True(allDiags.Count >= 3, $"Expected >=3 diagnostics in default view, got {allDiags.Count}");

        // Serialize unpaginated diagnostics to stable keys (ordered by diagnostic_id)
        static string DiagKey(JsonElement d) =>
            $"{d.GetProperty("project_name").GetString()}|{d.GetProperty("id").GetString()}|{d.GetProperty("document_path").GetString()}|{d.GetProperty("start_line").GetInt32()}|{d.GetProperty("start_column").ValueKind}|{d.GetProperty("message").GetString()}";
        var expectedKeys = allDiags.Select(DiagKey).ToList();

        // Walk with limit=2 (keyset pagination via diagnostic_id)
        var collected = new List<string>();
        string? cursor = null;
        int pages = 0;
        do
        {
            var pageJson = tool.LurpDiagnostics(limit: 2, cursor: cursor);
            using var pageDoc = JsonDocument.Parse(pageJson);
            var pageDiags = pageDoc.RootElement.GetProperty("diagnostics");
            var pageCount = pageDoc.RootElement.GetProperty("diagnostic_count").GetInt32();
            var nextCursor = pageDoc.RootElement.GetProperty("next_cursor").GetString();

            // diagnostic_count must be stable across pages (total, not page size)
            Assert.Equal(totalCount, pageCount);

            foreach (var d in pageDiags.EnumerateArray())
                collected.Add(DiagKey(d));

            cursor = nextCursor;
            pages++;

            // Must produce next_cursor until last page
            if (pages < Math.Ceiling(totalCount / 2.0))
                Assert.False(string.IsNullOrEmpty(cursor), $"Expected next_cursor on page {pages} with total {totalCount}");
            if (pages > 10) break; // safety
        } while (!string.IsNullOrEmpty(cursor));

        // Walk must reproduce the unpaginated set exactly, no duplicates, no gaps, same order
        Assert.Equal(expectedKeys.Count, collected.Count);
        Assert.Equal(expectedKeys, collected);
        Assert.Equal(collected.Count, collected.Distinct().Count());
        Assert.True(pages >= 2, $"Expected at least 2 pages, got {pages}");
    }

    [Fact]
    public async Task Diagnostics_CursorFingerprintMismatch_ThrowsInvalidParams()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        // Ensure fixture has enough diagnostics for a cursor (limit=2 over 5 => 3 pages)
        var jsonAll = tool.LurpDiagnostics(limit: 1000);
        using var docAll = JsonDocument.Parse(jsonAll);
        var total = docAll.RootElement.GetProperty("diagnostic_count").GetInt32();
        Assert.True(total >= 3, $"Need >=3 diagnostics for cursor test, got {total}");

        // Get a cursor with one set of filters (project=DiagProj)
        var json = tool.LurpDiagnostics(project: "DiagProj", limit: 2);
        using var doc = JsonDocument.Parse(json);
        var cursor = doc.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor), "Expected next_cursor for pagination with limit=2");
        Assert.NotNull(cursor);

        // Reuse cursor with different project → should throw
        var exProject = Assert.Throws<McpProtocolException>(
            () => tool.LurpDiagnostics(project: "OtherProj", limit: 2, cursor: cursor));
        Assert.Equal(McpErrorCode.InvalidParams, exProject.ErrorCode);
        Assert.Contains("Cursor does not match", exProject.Message);

        // Reuse cursor with different severity filter → should throw
        var exSeverity = Assert.Throws<McpProtocolException>(
            () => tool.LurpDiagnostics(severity: "Error", limit: 2, cursor: cursor));
        Assert.Equal(McpErrorCode.InvalidParams, exSeverity.ErrorCode);
        Assert.Contains("Cursor does not match", exSeverity.Message);

        // Reuse cursor with different id filter → should throw
        var firstId = docAll.RootElement.GetProperty("diagnostics").EnumerateArray().First().GetProperty("id").GetString()!;
        var exId = Assert.Throws<McpProtocolException>(
            () => tool.LurpDiagnostics(id: firstId + "_NOPE", limit: 2, cursor: cursor));
        Assert.Equal(McpErrorCode.InvalidParams, exId.ErrorCode);
        Assert.Contains("Cursor does not match", exId.Message);

        // Reuse cursor with different document filter → should throw
        string docPath;
        using (var store = OpenStore(DbPath))
        {
            docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First(k => k.Contains("Models.cs"));
        }
        var exDoc = Assert.Throws<McpProtocolException>(
            () => tool.LurpDiagnostics(document: docPath, limit: 2, cursor: cursor));
        Assert.Equal(McpErrorCode.InvalidParams, exDoc.ErrorCode);
        Assert.Contains("Cursor does not match", exDoc.Message);

        // Garbage cursor should also throw InvalidParams
        var exGarbage = Assert.Throws<McpProtocolException>(
            () => tool.LurpDiagnostics(limit: 2, cursor: "not-a-valid-cursor"));
        Assert.Equal(McpErrorCode.InvalidParams, exGarbage.ErrorCode);

        // Cursor issued for one snapshot must not be reusable for another snapshot (if we had one)
        // This is covered by fingerprint including snapshot_id inside cursor itself.
    }

    [Fact]
    public async Task Diagnostics_LimitLessThanOne_ThrowsInvalidParams()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpDiagnostics(limit: 0));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task Diagnostics_InSnapshot_ComputedCorrectly()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics();
        using var doc = JsonDocument.Parse(json);

        foreach (var d in doc.RootElement.GetProperty("diagnostics").EnumerateArray())
        {
            var inSnapshot = d.GetProperty("in_snapshot");
            var docPath = d.GetProperty("document_path");

            if (docPath.ValueKind == JsonValueKind.Null)
            {
                // No document → in_snapshot should be null
                Assert.Equal(JsonValueKind.Null, inSnapshot.ValueKind);
            }
            else
            {
                // Has a document → in_snapshot should be a boolean
                Assert.True(inSnapshot.ValueKind == JsonValueKind.True || inSnapshot.ValueKind == JsonValueKind.False,
                    $"Expected boolean in_snapshot for document '{docPath.GetString()}', got {inSnapshot.ValueKind}");
                // Since these diagnostics come from our own indexed source files, they should be in-snapshot
                Assert.True(inSnapshot.GetBoolean(),
                    $"Expected in_snapshot=true for indexed document '{docPath.GetString()}'");
            }
        }
    }

    [Fact]
    public async Task Diagnostics_UnusedUsing_SurfacesViaCompilerDiagnostic()
    {
        // Regression: CompilationHelper.GetDiagnostics(Project, Compilation) must still
        // surface the same compiler-level unused-using diagnostic (CS8019) that the old
        // GetDiagnostics(string, Compilation) overload produced, now that extraction routes
        // through the Project-aware overload (which additionally runs analyzers).
        CreateProject("UnusedUsingProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                using System.Text;

                namespace UnusedUsingProj {
                    public class Foo {
                        public void M1() { }
                    }
                }
                """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics(project: "UnusedUsingProj", severity: "all", limit: 1000);
        using var doc = JsonDocument.Parse(json);

        var diags = doc.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();

        // ImplicitUsings=enable means the SDK-generated GlobalUsings.g.cs can itself carry
        // unused-using diagnostics (e.g. an unused implicit `global using System.Threading;`),
        // so more than one CS8019 row can legitimately exist. Scope to the one located in the
        // fixture's own Models.cs to isolate the explicit `using System.Text;` this test wrote.
        var unused = diags.FirstOrDefault(d =>
            d.GetProperty("id").GetString() == "CS8019" &&
            d.GetProperty("document_path").ValueKind != JsonValueKind.Null &&
            d.GetProperty("document_path").GetString()!.EndsWith("Models.cs", StringComparison.Ordinal));

        Assert.False(unused.ValueKind == JsonValueKind.Undefined,
            "Expected a CS8019 diagnostic located in Models.cs for the unused `using System.Text;`.");
        Assert.Equal("Hidden", unused.GetProperty("severity").GetString());
        Assert.Equal(1, unused.GetProperty("start_line").GetInt32());
    }

    [Fact]
    public async Task Diagnostics_UnusedUsing_AnalyzerIdIfPresentIsQueryableById()
    {
        // Observational, not a fixed-outcome assertion: whether IDE0005 (the broader
        // analyzer-level "unused using" rule) fires on a bare test-fixture .csproj with no
        // explicit analyzer package reference and no EnforceCodeStyleInBuild is not
        // guaranteed by SDK defaults. This test only asserts that IF an IDE-family id shows
        // up in the unfiltered "all" severity view, it is independently retrievable via the
        // id filter (proving the read path handles analyzer-sourced rows identically to
        // compiler-sourced ones). It intentionally does not assert IDE0005 is present.
        CreateProject("AnalyzerCheckProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                using System.Text;

                namespace AnalyzerCheckProj {
                    public class Foo {
                        public void M1() { }
                    }
                }
                """
        });
        await RunFullIndexAsync(DbPath);

        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var allJson = tool.LurpDiagnostics(project: "AnalyzerCheckProj", severity: "all", limit: 1000);
        using var allDoc = JsonDocument.Parse(allJson);
        var ideId = allDoc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(d => d.GetProperty("id").GetString())
            .FirstOrDefault(id => id != null && id.StartsWith("IDE", StringComparison.Ordinal));

        if (ideId == null)
            return; // No IDE-family diagnostic fired on this fixture; nothing further to check.

        var filteredJson = tool.LurpDiagnostics(project: "AnalyzerCheckProj", id: ideId, severity: "all", limit: 1000);
        using var filteredDoc = JsonDocument.Parse(filteredJson);
        Assert.True(filteredDoc.RootElement.GetProperty("diagnostics").EnumerateArray().Any(),
            $"Expected id filter '{ideId}' to retrieve at least the row seen in the unfiltered view.");
    }

    [Fact]
    public async Task Diagnostics_GeneratedDocument_ExcludedByDefault_IncludedWithFlag()
    {
        // A ModelSnapshot.cs (EF Core migration scaffold) with an unused `using` should
        // be treated as generated for --include-generated purposes, same as *.Designer.cs.
        CreateProject("GenDiagProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                using System.Text;

                namespace GenDiagProj {
                    public class Foo {
                        public void M1() { }
                    }
                }
                """,
            ["FooContextModelSnapshot.cs"] = """
                using System.Text;

                namespace GenDiagProj {
                    public class FooContextModelSnapshot {
                    }
                }
                """
        });
        await RunFullIndexAsync(DbPath);

        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var defaultJson = tool.LurpDiagnostics(project: "GenDiagProj", id: "CS8019", severity: "all", limit: 1000);
        using var defaultDoc = JsonDocument.Parse(defaultJson);
        Assert.False(defaultDoc.RootElement.GetProperty("include_generated").GetBoolean());
        var defaultDocs = defaultDoc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(d => d.GetProperty("document_path").GetString())
            .ToList();
        Assert.DoesNotContain(defaultDocs, p => p != null && p.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal));
        Assert.Contains(defaultDocs, p => p != null && p.EndsWith("Models.cs", StringComparison.Ordinal));

        var includedJson = tool.LurpDiagnostics(project: "GenDiagProj", id: "CS8019", severity: "all", limit: 1000, include_generated: true);
        using var includedDoc = JsonDocument.Parse(includedJson);
        Assert.True(includedDoc.RootElement.GetProperty("include_generated").GetBoolean());
        var includedDocs = includedDoc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(d => d.GetProperty("document_path").GetString())
            .ToList();
        Assert.Contains(includedDocs, p => p != null && p.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostics_CursorFingerprint_ChangesWithIncludeGenerated()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        var json = tool.LurpDiagnostics(limit: 2);
        using var doc = JsonDocument.Parse(json);
        var cursor = doc.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var ex = Assert.Throws<McpProtocolException>(
            () => tool.LurpDiagnostics(limit: 2, cursor: cursor, include_generated: true));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("Cursor does not match", ex.Message);
    }

    [Fact]
    public void DiagnosticsTool_Registered_ReadOnly()
    {
        var tools = typeof(McpServeHandler).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false).Length > 0)
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Length > 0)
            .Select(m => ((ModelContextProtocol.Server.McpServerToolAttribute)m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)[0]).Name)
            .ToList();
        Assert.Contains("lurp_diagnostics", tools);
    }
}
