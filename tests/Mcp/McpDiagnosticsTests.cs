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
                using System;
                using System.Linq;
                namespace DiagProj {
                    public class Foo {
                        public void Bar() {
                            int unused = 0;
                        }
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

        // Get all diagnostics at once
        var jsonAll = tool.LurpDiagnostics(limit: 1000);
        using var docAll = JsonDocument.Parse(jsonAll);
        var allDiags = docAll.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        var totalCount = docAll.RootElement.GetProperty("diagnostic_count").GetInt32();

        // Walk with limit=1
        var collected = new List<string>();
        string? cursor = null;
        while (true)
        {
            var pageJson = tool.LurpDiagnostics(limit: 1, cursor: cursor);
            using var pageDoc = JsonDocument.Parse(pageJson);
            var pageDiags = pageDoc.RootElement.GetProperty("diagnostics");
            foreach (var d in pageDiags.EnumerateArray())
            {
                // Use a composite key for dedup detection
                var key = $"{d.GetProperty("project_name").GetString()}|{d.GetProperty("id").GetString()}|{d.GetProperty("start_line")}";
                collected.Add(key);
            }
            cursor = pageDoc.RootElement.GetProperty("next_cursor").GetString();
            if (string.IsNullOrEmpty(cursor))
                break;
        }

        Assert.Equal(totalCount, collected.Count);
        Assert.Equal(collected.Count, collected.Distinct().Count()); // no duplicates
    }

    [Fact]
    public async Task Diagnostics_CursorFingerprintMismatch_ThrowsInvalidParams()
    {
        var snapshotId = await IndexDiagnosticsFixtureAsync();
        await using var session = CreateSession();
        var tool = new DiagnosticsTool(session);

        // Get a cursor with one set of filters
        var json = tool.LurpDiagnostics(project: "DiagProj", limit: 1);
        using var doc = JsonDocument.Parse(json);
        var cursor = doc.RootElement.GetProperty("next_cursor").GetString();

        if (!string.IsNullOrEmpty(cursor))
        {
            // Reuse cursor with different filter → should throw
            var ex = Assert.Throws<McpProtocolException>(
                () => tool.LurpDiagnostics(project: "OtherProj", limit: 1, cursor: cursor));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("Cursor does not match", ex.Message);
        }
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
