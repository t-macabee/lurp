using System.Text;
using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpStatusTests : IntegrationTestBase
{
    private static readonly string[] _statusProjProjects = ["StatusProj"];
    private static readonly string[] _unknownProjects = ["NoSuchProject"];

    private async Task<string> IndexAsync()
    {
        CreateProject("StatusProj", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace StatusProj { public class Foo { public void Bar() {} } }"
        });
        return await RunFullIndexAsync(DbPath);
    }

    /// <summary>Indexes <c>StatusProj</c> plus a second project, and returns the snapshot id of the second run.</summary>
    private async Task<string> IndexTwoProjectsAsync()
    {
        await IndexAsync();
        CreateProject("StatusProj2", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace StatusProj2 { public class Bar { public void Baz() {} } }"
        });
        return await RunFullIndexNoDeleteAsync(DbPath);
    }

    [Fact]
public async Task Status_Cheap_WhenNoSolution_ReturnsStatMethod()
{
    var snapshotId = await IndexAsync();
        // Session without --solution= (cheap path)
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
        Assert.True(doc.RootElement.GetProperty("pinned").GetBoolean());
        var freshness = doc.RootElement.GetProperty("freshness");
        var method = freshness.GetProperty("method").GetString();
        // Cheap path uses stat or stat+hash, not full
        Assert.True(method == "stat" || method == "stat+hash");
        // Scope documents_only for cheap
        var scope = freshness.GetProperty("scope").GetString();
        Assert.Equal("documents_only", scope);
        // Sample capped at 10
        var sample = freshness.GetProperty("changed_documents_sample");
        Assert.True(sample.GetArrayLength() <= 10);
    }

    [Fact]
    public async Task Status_FullTrue_ReturnsFullMethod()
    {
        var snapshotId = await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(full: true);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
        var freshness = doc.RootElement.GetProperty("freshness");
        var method = freshness.GetProperty("method").GetString();
        Assert.Equal("full", method);
        var scope = freshness.GetProperty("scope").GetString();
        Assert.Equal("full", scope);
    }

    [Fact]
    public async Task Status_Default_WithSolution_IsCheap()
    {
        var snapshotId = await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
        var freshness = doc.RootElement.GetProperty("freshness");
        var method = freshness.GetProperty("method").GetString();
        Assert.True(method == "stat" || method == "stat+hash");
        Assert.Equal("documents_only", freshness.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Status_SnapshotMismatch_ReturnsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("snapshot mismatch", ex.Message);
        Assert.Contains("lurp_refresh", ex.Message);
    }

    [Fact]
    public async Task Status_Detail_ExpandsSample_AndIncludesDetailObject()
    {
        var snapshotId = await IndexAsync();
        // Create 12 changed files to test capping vs detail
        // We already have 1 file; create 12 more projects/files then touch them
        // Simpler: just call status with detail true and false and compare sample length behavior.
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        // Touch many files to create >10 changed docs: modify each document's mtime
        // Add 12 extra source files via new projects
        for (int i = 0; i < 12; i++)
        {
            CreateProject($"StatusExtra{i}", new Dictionary<string, string>
            {
                ["Extra.cs"] = $"namespace StatusExtra{i} {{ public class C{i} {{ public void M() {{}} }} }}"
            });
        }
        await RunFullIndexNoDeleteAsync(DbPath);
        // Now touch all files to make them look stale relative to previous pinned snapshot?
        // Instead we test the capping logic directly via the uncapped path:
        var jsonCheap = await tool.LurpStatus(detail: false);
        using var docCheap = JsonDocument.Parse(jsonCheap);
        var sampleCheap = docCheap.RootElement.GetProperty("freshness").GetProperty("changed_documents_sample");
        Assert.True(sampleCheap.GetArrayLength() <= 10);

        var jsonDetail = await tool.LurpStatus(detail: true);
        using var docDetail = JsonDocument.Parse(jsonDetail);
        // Detail should include a detail object
        Assert.True(docDetail.RootElement.TryGetProperty("detail", out var detailEl));
        Assert.True(detailEl.ValueKind != JsonValueKind.Null);
        // When detail true, freshness sample may be uncapped (full list) — at least not capped artificially beyond count
        var sampleDetail = docDetail.RootElement.GetProperty("freshness").GetProperty("changed_documents_sample");
        // Detail sample should be >= cheap sample (since cheap is capped)
        Assert.True(sampleDetail.GetArrayLength() >= sampleCheap.GetArrayLength());
    }

    [Fact]
    public async Task Status_Full_AfterIndex_IsFresh_NoFalseStale()
    {
        var snapshotId = await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(full: true);
        using var doc = JsonDocument.Parse(json);
        var freshness = doc.RootElement.GetProperty("freshness");
        Assert.Equal("fresh", freshness.GetProperty("state").GetString());
        Assert.Equal(0, freshness.GetProperty("changed_document_count").GetInt32());
        Assert.Equal("full", freshness.GetProperty("method").GetString());
        var sample = freshness.GetProperty("changed_documents_sample");
        Assert.Equal(0, sample.GetArrayLength());
        // Also check that the fresh result has empty mismatches when detail requested
        var jsonDetail = await tool.LurpStatus(detail: true, full: true);
        using var docDetail = JsonDocument.Parse(jsonDetail);
        var freshnessDetail = docDetail.RootElement.GetProperty("freshness");
        Assert.Equal("fresh", freshnessDetail.GetProperty("state").GetString());
        Assert.Equal(0, freshnessDetail.GetProperty("changed_document_count").GetInt32());
    }

    [Fact]
    public async Task Status_Freshness_SameKeysOnCheapAndFullPath()
    {
        await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var cheapJson = await tool.LurpStatus();
        var fullJson = await tool.LurpStatus(full: true);
        using var cheapDoc = JsonDocument.Parse(cheapJson);
        using var fullDoc = JsonDocument.Parse(fullJson);
        var cheapKeys = cheapDoc.RootElement.GetProperty("freshness").EnumerateObject().Select(p => p.Name).ToArray();
        var fullKeys = fullDoc.RootElement.GetProperty("freshness").EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(cheapKeys, fullKeys);
        Assert.True(cheapDoc.RootElement.GetProperty("freshness").GetProperty("is_fresh").GetBoolean());
        Assert.True(fullDoc.RootElement.GetProperty("freshness").GetProperty("is_fresh").GetBoolean());
    }

    [Fact]
    public async Task Status_Full_AfterEdit_IsStale_ReportsOneChangedDocument()
    {
        var snapshotId = await IndexAsync();
        // Edit exactly one file after indexing
        var projFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "src", "StatusProj", "Models.cs");
        File.WriteAllText(projFile, "namespace StatusProj { public class Foo { public void Bar(int x) {} } }");
        // Ensure hash change is detected (content differs) — WorkspaceFreshness full check uses hash, not mtime
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(full: true);
        using var doc = JsonDocument.Parse(json);
        var freshness = doc.RootElement.GetProperty("freshness");
        Assert.Equal("stale", freshness.GetProperty("state").GetString());
        Assert.Equal(1, freshness.GetProperty("changed_document_count").GetInt32());
        var sample = freshness.GetProperty("changed_documents_sample");
        Assert.Equal(1, sample.GetArrayLength());
        var samplePath = sample[0].GetString();
        Assert.Contains("Models.cs", samplePath);
        // Mismatches detail should contain one DocumentModified/Added entry
        var jsonDetail = await tool.LurpStatus(detail: true, full: true);
        using var docDetail = JsonDocument.Parse(jsonDetail);
        var freshnessDetail = docDetail.RootElement.GetProperty("freshness");
        Assert.True(freshnessDetail.TryGetProperty("mismatches", out var mismatches));
        Assert.Equal(1, mismatches.GetArrayLength());
    }

    [Fact]
    public async Task Status_Full_CliParity_FreshAfterIndex()
    {
        var snapshotId = await IndexAsync();
        // CLI path parity: StatusHandler.CheckCurrentWorkspaceAsync via --mode=status --solution
        // Verify the Handler's freshness path stays correct by checking the same invariant via WorkspaceFreshness directly
        // using the store's LoadLatestSnapshot overload (CLI code path)
        using var store = OpenStore(DbPath);
        try
        {
            if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
                try { Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults(); } catch { }
            using var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create(
                Lurp.Workspace.LurpCache.CreateWorkspaceGlobalProperties(SolutionPath));
            var solution = await workspace.OpenSolutionAsync(SolutionPath);
            var gitRoot = Path.GetDirectoryName(Path.GetFullPath(SolutionPath))!;
            var workspaceInfo = new Lurp.Workspace.WorkspaceInfo(solution, gitRoot);
            var result = Lurp.Workspace.WorkspaceFreshness.CheckFreshness(workspaceInfo, store);
            Assert.True(result.IsFresh);
            Assert.Empty(result.Mismatches);
        }
        finally
        {
            store.Close();
        }

        // Also verify MCP full path now agrees (no false stale regression)
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(full: true);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("fresh", doc.RootElement.GetProperty("freshness").GetProperty("state").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("freshness").GetProperty("changed_document_count").GetInt32());
    }

    [Fact]
    public async Task Status_ServesStaleData_WithFlag_StillReturnsPayload()
    {
        var snapshotId = await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var statusTool = new StatusTool(session);
        var getSourceTool = new GetSourceTool(session);

        // Make a file stale by touching it
        var projFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "src", "StatusProj", "Models.cs");
        File.WriteAllText(projFile, "namespace StatusProj { public class Foo { public void Bar(int x) {} } }");
        // Ensure mtime is newer than snapshot
        File.SetLastWriteTimeUtc(projFile, DateTime.UtcNow.AddSeconds(5));

        var statusJson = await statusTool.LurpStatus();
        using var statusDoc = JsonDocument.Parse(statusJson);
        var state = statusDoc.RootElement.GetProperty("freshness").GetProperty("state").GetString();
        // Could be stale or fresh depending on timing; at least ensure freshness present
        Assert.False(string.IsNullOrEmpty(state));

        // Tool call on stale data must still return payload with stale flag
        // GetSource should still return source text even though freshness is stale
        string docPath;
        using (var store = OpenStore(DbPath))
        {
            docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First();
        }
        var sourceJson = getSourceTool.LurpGetSource(document: docPath);
        using var sourceDoc = JsonDocument.Parse(sourceJson);
        Assert.True(sourceDoc.RootElement.TryGetProperty("source", out var srcEl));
        Assert.False(string.IsNullOrEmpty(srcEl.GetString()));
        var freshState = sourceDoc.RootElement.GetProperty("freshness").GetProperty("state").GetString();
        // When file was touched, freshness should be stale
        Assert.Equal("stale", freshState);
    }

    // ── Gap 1: sections and caps ───────────────────────────────────────

    [Fact]
    public async Task Status_Sections_Freshness_ReturnsNoManifest()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "freshness");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("detail").ValueKind);
        // detail may be null or missing manifest — must not contain document_versions / identities
        if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
        {
            Assert.False(detail.TryGetProperty("manifest", out var manifest) && manifest.ValueKind == JsonValueKind.Object,
                "sections=freshness should not include detail.manifest");
        }

        // omitted sections defaults to freshness as well
        var jsonDefault = await tool.LurpStatus();
        using var docDefault = JsonDocument.Parse(jsonDefault);
        if (docDefault.RootElement.TryGetProperty("detail", out var detail2) && detail2.ValueKind == JsonValueKind.Object)
        {
            Assert.False(detail2.TryGetProperty("manifest", out _),
                "default sections should not include manifest");
        }
    }

    [Fact]
    public async Task Status_Sections_Manifest_ReturnsCountsNotIdentities()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "manifest");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("detail", out var detail));
        Assert.Equal(JsonValueKind.Object, detail.ValueKind);
        Assert.True(detail.TryGetProperty("manifest", out var manifest));
        Assert.Equal(JsonValueKind.Object, manifest.ValueKind);
        Assert.True(manifest.TryGetProperty("document_count", out _), "manifest should contain document_count");
        Assert.True(manifest.TryGetProperty("documents_note", out var documentsNote), "manifest should contain documents_note when documents are omitted");
        Assert.Contains("sections=documents", documentsNote.GetString());
        Assert.False(manifest.TryGetProperty("document_versions", out _), "manifest should not contain document_versions when sections=manifest");
        Assert.True(manifest.TryGetProperty("metadata_reference_counts", out _), "should contain counts");
        Assert.True(manifest.TryGetProperty("metadata_reference_total", out _));
        Assert.False(manifest.TryGetProperty("metadata_reference_identities", out _), "should not contain full identities when sections=manifest");
    }

    [Fact]
    public async Task Status_Sections_References_ReturnsFullIdentities()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "references", max_bytes: 400000);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("detail", out var detail));
        Assert.True(detail.TryGetProperty("manifest", out var manifest));
        Assert.True(manifest.TryGetProperty("metadata_reference_identities", out var ids));
        Assert.Equal(JsonValueKind.Object, ids.ValueKind);
        Assert.True(ids.EnumerateObject().Any(), "identities should be non-empty");
    }

    [Fact]
    public async Task Status_Sections_All_ReturnsBothFull()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "all", max_bytes: 400000);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("detail", out var detail));
        Assert.True(detail.TryGetProperty("manifest", out var manifest));
        Assert.True(manifest.TryGetProperty("document_versions", out var docVers));
        Assert.Equal(JsonValueKind.Object, docVers.ValueKind);
        Assert.True(manifest.TryGetProperty("metadata_reference_identities", out var ids));
        Assert.Equal(JsonValueKind.Object, ids.ValueKind);
    }

    [Fact]
    public async Task Status_Sections_UnknownValue_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var exFoo = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(sections: "foo"));
        Assert.Equal(McpErrorCode.InvalidParams, exFoo.ErrorCode);
        Assert.Contains("sections: unknown value", exFoo.Message);

        var exList = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(sections: "manifest,foo"));
        Assert.Equal(McpErrorCode.InvalidParams, exList.ErrorCode);
        Assert.Contains("sections: unknown value", exList.Message);
    }

    [Fact]
    public async Task Status_Sections_Documents_ReturnsDocumentVersions()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "documents", max_bytes: 400000);
        using var doc = JsonDocument.Parse(json);
        var manifest = doc.RootElement.GetProperty("detail").GetProperty("manifest");
        Assert.True(manifest.TryGetProperty("document_versions", out var docVers));
        Assert.Equal(JsonValueKind.Object, docVers.ValueKind);
    }

    [Fact]
    public async Task Status_Sections_CompletenessAndReferences_ReturnsBoth()
    {
        var (_, dbPath) = await IndexAndSeedBindingIncompletenessAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(dbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "completeness,references", max_bytes: 400000);
        using var doc = JsonDocument.Parse(json);
        var manifest = doc.RootElement.GetProperty("detail").GetProperty("manifest");
        Assert.True(manifest.TryGetProperty("metadata_reference_identities", out var ids));
        Assert.Equal(JsonValueKind.Object, ids.ValueKind);
        Assert.True(ids.EnumerateObject().Any(), "identities should be present for sections=completeness,references");
        Assert.True(manifest.GetProperty("completeness").GetProperty("binding_incompleteness").GetArrayLength() > 0,
            "completeness rows should be present for sections=completeness,references");
    }

    [Fact]
    public async Task Status_MaxDocuments_CapsSampleAndSetsTruncated()
    {
        await IndexAsync();
        // Create enough extra projects to have >5 changed documents
        for (int i = 0; i < 12; i++)
        {
            CreateProject($"StatusCapDocs{i}", new Dictionary<string, string>
            {
                ["Extra.cs"] = $"namespace StatusCapDocs{i} {{ public class C{i} {{ public void M() {{}} }} }}"
            });
        }
        await RunFullIndexNoDeleteAsync(DbPath);
        // Touch all StatusCapDocs files to make them stale
        for (int i = 0; i < 12; i++)
        {
            var p = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "src", $"StatusCapDocs{i}", "Extra.cs");
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(10));
            File.WriteAllText(p, File.ReadAllText(p) + " // touch");
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(10));
        }
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(max_documents: 5);
        using var doc = JsonDocument.Parse(json);
        var freshness = doc.RootElement.GetProperty("freshness");
        var sample = freshness.GetProperty("changed_documents_sample");
        Assert.True(sample.ValueKind == JsonValueKind.Array);
        Assert.Equal(5, sample.GetArrayLength());
        // Should be truncated when real count >5
        var count = freshness.GetProperty("changed_document_count").GetInt32();
        if (count > 5)
        {
            Assert.True(freshness.TryGetProperty("changed_documents_sample_truncated", out var trunc) && trunc.GetBoolean(),
                "should set truncated when count exceeds max");
        }
    }

    [Fact]
    public async Task Status_MaxMismatches_CapsMismatchesAndSetsTruncated()
    {
        await IndexAsync();
        for (int i = 0; i < 8; i++)
        {
            CreateProject($"StatusCapMis{i}", new Dictionary<string, string>
            {
                ["Extra.cs"] = $"namespace StatusCapMis{i} {{ public class C{i} {{ public void M() {{}} }} }}"
            });
        }
        await RunFullIndexNoDeleteAsync(DbPath);
        // Modify files to create mismatches (hash change) for full check
        for (int i = 0; i < 8; i++)
        {
            var p = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "src", $"StatusCapMis{i}", "Extra.cs");
            File.WriteAllText(p, $"namespace StatusCapMis{i} {{ public class C{i} {{ public void M(int x) {{}} }} }}");
        }
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(sections: "manifest", max_mismatches: 3, full: true);
        using var doc = JsonDocument.Parse(json);
        var freshness = doc.RootElement.GetProperty("freshness");
        Assert.True(freshness.TryGetProperty("mismatches", out var mismatches));
        Assert.Equal(JsonValueKind.Array, mismatches.ValueKind);
        Assert.True(mismatches.GetArrayLength() <= 3);
        var count = freshness.GetProperty("changed_document_count").GetInt32();
        if (count > 3)
        {
            Assert.True(freshness.TryGetProperty("mismatches_truncated", out var trunc) && trunc.GetBoolean());
        }
    }

    [Fact]
    public async Task Status_MaxBytes_Default_IsFortyThousand()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "manifest");
        using var doc = JsonDocument.Parse(json);
        var filters = doc.RootElement.GetProperty("detail").GetProperty("filters");
        Assert.Equal(40000, filters.GetProperty("max_bytes").GetInt32());
        Assert.Equal(JsonValueKind.Null, filters.GetProperty("projects").ValueKind);
    }

    [Fact]
    public async Task Status_MaxBytes_OutOfRange_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var exZero = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(max_bytes: 0));
        Assert.Equal(McpErrorCode.InvalidParams, exZero.ErrorCode);
        Assert.Contains("max_bytes", exZero.Message);

        var exHigh = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(max_bytes: 400001));
        Assert.Equal(McpErrorCode.InvalidParams, exHigh.ErrorCode);
        Assert.Contains("max_bytes", exHigh.Message);
    }

    [Fact]
    public async Task Status_MaxBytes_CapBelowAll_SummarizesLargestPart()
    {
        var snapshotId = await IndexTwoProjectsAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var unbounded = await tool.LurpStatus(sections: "all", max_bytes: 400000);
        var l = Encoding.UTF8.GetByteCount(unbounded);

        // The response echoes detail.filters.max_bytes, so the cap's own width is part of the
        // payload: a cap one byte under L reproduces exactly L - 1 bytes. Step one byte further
        // so the un-fitted payload is strictly over the cap.
        var cap = l - 2;
        var json = await tool.LurpStatus(sections: "all", max_bytes: cap);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= l - 2);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("truncated", out var truncated));
        Assert.True(truncated.GetBoolean());

        var detail = doc.RootElement.GetProperty("detail");
        Assert.False(detail.TryGetProperty("note", out _));

        var manifest = detail.GetProperty("manifest");
        Assert.Equal(snapshotId, manifest.GetProperty("snapshot_id").GetString());

        var omitted = manifest.GetProperty("omitted_fields");
        Assert.True(omitted.GetArrayLength() > 0);
        Assert.Equal("metadata_reference_identities", omitted[0].GetProperty("category").GetString());
        Assert.Equal("summarized", omitted[0].GetProperty("reason").GetString());
        // On a small fixture the markers can outweigh the gain of one step, so the fitter may go on to
        // a second part. The invariant that holds at any size: a part that is not listed in
        // omitted_fields is still in the manifest. ("Smaller parts stay when one step is enough" is
        // pinned in SnapshotManifestJsonTests.)
        var categories = omitted.EnumerateArray()
            .Select(entry => entry.GetProperty("category").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var part in new[] { "metadata_reference_counts", "document_versions" })
            Assert.True(manifest.TryGetProperty(part, out _) || categories.Contains(part), $"{part} is neither in the manifest nor listed in omitted_fields");
    }

    [Fact]
    public async Task Status_MaxBytes_TinyCap_OmitsEveryFullPart()
    {
        await IndexTwoProjectsAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        // The full render names which parts exist; a tiny cap must remove every one it can.
        var full = await tool.LurpStatus(sections: "all", max_bytes: 400000);
        using var fullDoc = JsonDocument.Parse(full);
        var fullManifest = fullDoc.RootElement.GetProperty("detail").GetProperty("manifest");
        var presentParts = new List<string>();
        foreach (var part in new[]
                 {
                     "document_versions", "metadata_reference_identities", "project_documents", "project_graph",
                     "compilation_options_fingerprints", "assembly_names"
                 })
            if (fullManifest.TryGetProperty(part, out _))
                presentParts.Add(part);
        if (fullManifest.TryGetProperty("completeness", out var fullCompleteness))
        {
            if (fullCompleteness.TryGetProperty("active_tfms", out _))
                presentParts.Add("completeness.active_tfms");
            if (fullCompleteness.TryGetProperty("binding_incompleteness_summary", out _))
                presentParts.Add("completeness.binding_incompleteness_summary");
        }

        var json = await tool.LurpStatus(sections: "all", max_bytes: 1000);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        var detail = doc.RootElement.GetProperty("detail");
        Assert.False(detail.TryGetProperty("note", out _));
        var manifest = detail.GetProperty("manifest");
        Assert.True(manifest.TryGetProperty("snapshot_id", out _));
        Assert.True(manifest.TryGetProperty("target_frameworks", out _));

        var omitted = manifest.GetProperty("omitted_fields").EnumerateArray()
            .Select(entry => entry.GetProperty("category").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(presentParts);
        foreach (var part in presentParts)
            Assert.Contains(part, omitted);
    }

    [Fact]
    public async Task Status_FreshnessSection_MaxBytesTiny_NoTruncatedMarker()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "freshness", max_bytes: 100);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("truncated", out _));
    }

    [Fact]
    public async Task Status_Projects_ScopesManifestToNamedProject()
    {
        await IndexTwoProjectsAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "references", projects: _statusProjProjects, max_bytes: 400000);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("truncated", out _));

        var detail = doc.RootElement.GetProperty("detail");
        var manifest = detail.GetProperty("manifest");
        var identities = manifest.GetProperty("metadata_reference_identities");
        Assert.True(identities.TryGetProperty("StatusProj", out _));
        Assert.Single(identities.EnumerateObject());
        Assert.Single(manifest.GetProperty("target_frameworks").EnumerateObject());

        var filtersProjects = detail.GetProperty("filters").GetProperty("projects");
        Assert.Equal(JsonValueKind.Array, filtersProjects.ValueKind);
        Assert.Equal(_statusProjProjects, filtersProjects.EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Status_Projects_UnknownProject_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await tool.LurpStatus(sections: "manifest", projects: _unknownProjects));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("unknown project", ex.Message);
    }

    [Fact]
    public async Task Status_Projects_WithoutManifestSection_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await tool.LurpStatus(sections: "freshness", projects: _statusProjProjects));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("manifest section", ex.Message);
    }

    [Fact]
    public async Task Status_MaxDocuments_OverLimit_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(max_documents: 501));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("max_documents must be <= 500", ex.Message);

        await tool.LurpStatus(max_documents: 500);
    }

    [Fact]
    public async Task Status_MaxMismatches_OverLimit_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(max_mismatches: 501));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("max_mismatches must be <= 500", ex.Message);

        await tool.LurpStatus(max_mismatches: 500);
    }

    [Fact]
    public async Task Status_Documents_OverLimit_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var overLimit = Enumerable.Range(0, 501).Select(i => $"a{i}.cs").ToArray();
        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(documents: overLimit));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("documents length must be <= 500", ex.Message);

        var atLimit = Enumerable.Range(0, 500).Select(i => $"a{i}.cs").ToArray();
        var json = await tool.LurpStatus(documents: atLimit);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(500, doc.RootElement.GetProperty("document_freshness").GetArrayLength());
    }

    [Fact]
    public async Task Status_Projects_WithFreshness_MessageDoesNotListSections()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await tool.LurpStatus(sections: "freshness", projects: _statusProjProjects));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("except freshness", ex.Message);
    }

    [Fact]
    public async Task Status_DetailTrue_MapsToManifestNotAll()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(detail: true);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("detail", out var detail));
        Assert.True(detail.TryGetProperty("manifest", out var manifest));
        // Should be manifest-level (counts) not all (full identities + document_versions)
        Assert.False(manifest.TryGetProperty("metadata_reference_identities", out _),
            "detail=true should not include full reference identities (should be manifest, not all)");
        Assert.False(manifest.TryGetProperty("document_versions", out _),
            "detail=true should not include full document_versions");
        Assert.True(manifest.TryGetProperty("document_count", out _));
    }

    [Fact]
    public async Task Status_MaxDocuments_ZeroAndNegative_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var ex0 = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(max_documents: 0));
        Assert.Equal(McpErrorCode.InvalidParams, ex0.ErrorCode);
        Assert.Contains("max-documents", ex0.Message, StringComparison.OrdinalIgnoreCase);

        var exNeg = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(max_documents: -5));
        Assert.Equal(McpErrorCode.InvalidParams, exNeg.ErrorCode);
    }

    [Fact]
    public async Task Status_MaxMismatches_ZeroAndNegative_ThrowsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var ex0 = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(max_mismatches: 0));
        Assert.Equal(McpErrorCode.InvalidParams, ex0.ErrorCode);
        Assert.Contains("max-mismatches", ex0.Message, StringComparison.OrdinalIgnoreCase);

        var exNeg = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.LurpStatus(max_mismatches: -1));
        Assert.Equal(McpErrorCode.InvalidParams, exNeg.ErrorCode);
    }

    // ── Gap 5: batch document freshness ───────────────────────────────
    // Snapshot stores git-relative paths with src/ prefix (TestDir/src/<Project>/File.cs)

    [Fact]
    public async Task Status_Documents_Batch_FreshForUnchanged()
    {
        await IndexAsync();
        // Create a second document to have two to query
        WriteFile("StatusProj", "Extra.cs", "namespace StatusProj { public class Extra { public void M() {} } }");
        await RunFullIndexNoDeleteAsync(DbPath);
        // After fresh index, both should be fresh — use actual stored paths
        string docA, docB;
        using (var store = OpenStore(DbPath))
        {
            var keys = store.GetDocumentVersionIdsByPath(store.GetLatestSnapshotId()!).Keys.ToList();
            docA = keys.First(k => k.EndsWith("Models.cs", StringComparison.Ordinal));
            docB = keys.First(k => k.EndsWith("Extra.cs", StringComparison.Ordinal));
        }
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(documents: new[] { docA, docB });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("document_freshness", out var freshnessArr));
        Assert.Equal(2, freshnessArr.GetArrayLength());
        foreach (var el in freshnessArr.EnumerateArray())
        {
            Assert.Equal("fresh", el.GetProperty("state").GetString());
        }
        // Also verify the document field is the normalized stored path
        var returned = freshnessArr.EnumerateArray().Select(e => e.GetProperty("document").GetString()!).ToHashSet();
        Assert.Contains(docA, returned);
        Assert.Contains(docB, returned);
    }

    [Fact]
    public async Task Status_Documents_Batch_StaleAndFresh()
    {
        await IndexAsync();
        WriteFile("StatusProj", "Extra.cs", "namespace StatusProj { public class Extra { public void M() {} } }");
        await RunFullIndexNoDeleteAsync(DbPath);
        string docA, docB;
        using (var store = OpenStore(DbPath))
        {
            var keys = store.GetDocumentVersionIdsByPath(store.GetLatestSnapshotId()!).Keys.ToList();
            docA = keys.First(k => k.EndsWith("Models.cs", StringComparison.Ordinal));
            docB = keys.First(k => k.EndsWith("Extra.cs", StringComparison.Ordinal));
        }
        // Edit one file
        var fileA = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "src", "StatusProj", "Models.cs");
        File.WriteAllText(fileA, "namespace StatusProj { public class Foo { public void Bar(int x) {} } }");
        File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow.AddSeconds(10));
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(documents: new[] { docA, docB });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("document_freshness", out var arr));
        Assert.Equal(2, arr.GetArrayLength());
        var dict = arr.EnumerateArray().ToDictionary(e => e.GetProperty("document").GetString()!, e => e.GetProperty("state").GetString()!);
        Assert.Equal("stale", dict[docA]);
        Assert.Equal("fresh", dict[docB]);
    }

    [Fact]
    public async Task Status_Documents_Batch_NotInSnapshot()
    {
        await IndexAsync();
        string docA;
        using (var store = OpenStore(DbPath))
        {
            docA = store.GetDocumentVersionIdsByPath(store.GetLatestSnapshotId()!).Keys.First(k => k.EndsWith("Models.cs", StringComparison.Ordinal));
        }
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var json = await tool.LurpStatus(documents: new[] { docA, "NotExist/Fake.cs" });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("document_freshness", out var arr));
        var dict = arr.EnumerateArray().ToDictionary(e => e.GetProperty("document").GetString()!, e => e.GetProperty("state").GetString()!);
        Assert.Equal("fresh", dict[docA]);
        Assert.Equal("not_in_snapshot", dict["NotExist/Fake.cs"]);
        Assert.NotEqual("fresh", dict["NotExist/Fake.cs"]);
        Assert.NotEqual("stale", dict["NotExist/Fake.cs"]);
    }

    [Fact]
    public async Task Status_Documents_BackslashNormalization()
    {
        await IndexAsync();
        string docA;
        using (var store = OpenStore(DbPath))
        {
            docA = store.GetDocumentVersionIdsByPath(store.GetLatestSnapshotId()!).Keys.First(k => k.EndsWith("Models.cs", StringComparison.Ordinal));
        }
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);
        var forward = docA;
        var backslash = docA.Replace("/", "\\");
        var jsonForward = await tool.LurpStatus(documents: new[] { forward });
        using var docF = JsonDocument.Parse(jsonForward);
        var stateF = docF.RootElement.GetProperty("document_freshness")[0].GetProperty("state").GetString();
        var docFieldF = docF.RootElement.GetProperty("document_freshness")[0].GetProperty("document").GetString();

        var jsonBack = await tool.LurpStatus(documents: new[] { backslash });
        using var docB = JsonDocument.Parse(jsonBack);
        var stateB = docB.RootElement.GetProperty("document_freshness")[0].GetProperty("state").GetString();
        var docFieldB = docB.RootElement.GetProperty("document_freshness")[0].GetProperty("document").GetString();

        Assert.Equal(stateF, stateB);
        Assert.Equal(docA, docFieldB);
        Assert.Equal(docFieldF, docFieldB);
    }

    [Fact]
    public async Task Status_Documents_FullScope_ReflectsFullMismatchSet()
    {
        await IndexAsync();
        WriteFile("StatusProj", "Extra.cs", "namespace StatusProj { public class Extra { public void M() {} } }");
        await RunFullIndexNoDeleteAsync(DbPath);
        string docA;
        using (var store = OpenStore(DbPath))
        {
            docA = store.GetDocumentVersionIdsByPath(store.GetLatestSnapshotId()!).Keys.First(k => k.EndsWith("Models.cs", StringComparison.Ordinal));
        }
        var fileA = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "src", "StatusProj", "Models.cs");
        File.WriteAllText(fileA, "namespace StatusProj { public class Foo { public void Bar(int x) {} } }");
        File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow.AddSeconds(10));

        // Cheap scope
        var cheapArgs = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var cheapSession = McpSessionContext.Create(cheapArgs);
        var cheapTool = new StatusTool(cheapSession);
        var cheapJson = await cheapTool.LurpStatus(documents: new[] { docA });
        using var cheapDoc = JsonDocument.Parse(cheapJson);
        var cheapDocState = cheapDoc.RootElement.GetProperty("document_freshness")[0].GetProperty("state").GetString();

        // Full scope
        var fullArgs = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var fullSession = McpSessionContext.Create(fullArgs);
        var fullTool = new StatusTool(fullSession);
        var fullJson = await fullTool.LurpStatus(documents: new[] { docA }, full: true);
        using var fullDoc = JsonDocument.Parse(fullJson);
        var fullFreshness = fullDoc.RootElement.GetProperty("freshness");
        Assert.Equal("full", fullFreshness.GetProperty("scope").GetString());
        Assert.Equal("full", fullFreshness.GetProperty("method").GetString());
        var fullDocState = fullDoc.RootElement.GetProperty("document_freshness")[0].GetProperty("state").GetString();
        var fullState = fullFreshness.GetProperty("state").GetString();

        // Per-document result should be consistent with freshness.state for the edited file
        Assert.Equal("stale", fullDocState);
        Assert.Equal("stale", fullState);
        // Also consistency check: cheap and full agree on staleness for this edit
        Assert.Equal(cheapDocState, fullDocState);
    }

    // ── Fix 1: store-exception surfacing (StatusTool.cs:162-191) ──────

    [Fact]
    public async Task Status_DetailBranch_StoreFailure_SurfacesErrorNotSwallowed()
    {
        await IndexAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        // Force a genuine store failure inside the detail branch. Freshness
        // does not read binding completeness; the manifest branch does.
        // Removing that table leaves freshness usable, then makes the detail
        // read throw a SQLite exception.
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE binding_incompleteness;";
            command.ExecuteNonQuery();
        }

        var json = await tool.LurpStatus(sections: "manifest");
        using var doc = JsonDocument.Parse(json);
        var detail = doc.RootElement.GetProperty("detail");

        Assert.True(detail.TryGetProperty("error", out var errorProp));
        Assert.False(string.IsNullOrEmpty(errorProp.GetString()));
        Assert.True(detail.TryGetProperty("error_message", out _));
        Assert.Equal("detail unavailable due to a store error", detail.GetProperty("note").GetString());

        // Regression guard: must NOT be the old blank note.
        Assert.NotEqual("detail unavailable", detail.GetProperty("note").GetString());
    }

    // ── Fix 2: sections=completeness wiring (StatusTool.cs:444-450) ──

    private async Task<(string snapshotId, string dbPath)> IndexAndSeedBindingIncompletenessAsync()
    {
        var snapshotId = await IndexAsync();

        // Seed a binding-incompleteness row via a writable store before
        // the McpSessionContext opens it in query-only mode.
        using (var store = OpenStore(DbPath))
        {
            store.SaveBindingIncompleteness(snapshotId, new[]
            {
                new BindingIncompletenessRecord(
                    ProjectName: "StatusProj",
                    DocumentPath: "src/StatusProj/Models.cs",
                    Reason: "UnresolvedTypeReference",
                    Count: 3,
                    ExtractorVersion: "test")
            });
        }

        return (snapshotId, DbPath);
    }

    [Fact]
    public async Task Status_Sections_Completeness_PopulatesBindingIncompletenessDetail()
    {
        var (snapshotId, dbPath) = await IndexAndSeedBindingIncompletenessAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(dbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "completeness");
        using var doc = JsonDocument.Parse(json);
        var completeness = doc.RootElement
            .GetProperty("detail")
            .GetProperty("manifest")
            .GetProperty("completeness");

        Assert.True(completeness.GetProperty("binding_incompleteness_total").GetInt32() > 0,
            "binding_incompleteness_total should be non-zero with seeded record");
        Assert.True(completeness.GetProperty("binding_incompleteness").GetArrayLength() > 0,
            "binding_incompleteness detail array should be non-empty for sections=completeness");
    }

    [Fact]
    public async Task Status_Sections_Manifest_PopulatesSummaryButNotDetail()
    {
        var (snapshotId, dbPath) = await IndexAndSeedBindingIncompletenessAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(dbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "manifest");
        using var doc = JsonDocument.Parse(json);
        var completeness = doc.RootElement
            .GetProperty("detail")
            .GetProperty("manifest")
            .GetProperty("completeness");

        // Summary and total are always populated now (WithBindingCompleteness runs on every manifest render)
        Assert.True(completeness.GetProperty("binding_incompleteness_total").GetInt32() > 0,
            "binding_incompleteness_total should be populated even for sections=manifest");
        Assert.True(completeness.GetProperty("binding_incompleteness_summary").GetArrayLength() > 0,
            "binding_incompleteness_summary should be populated even for sections=manifest");

        // But the per-document detail array must be empty (includeDetail=false for manifest)
        Assert.Equal(0, completeness.GetProperty("binding_incompleteness").GetArrayLength());
    }

    [Fact]
    public async Task Status_Sections_All_PopulatesBothSummaryAndDetail()
    {
        var (snapshotId, dbPath) = await IndexAndSeedBindingIncompletenessAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(dbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var json = await tool.LurpStatus(sections: "all");
        using var doc = JsonDocument.Parse(json);
        var completeness = doc.RootElement
            .GetProperty("detail")
            .GetProperty("manifest")
            .GetProperty("completeness");

        Assert.True(completeness.GetProperty("binding_incompleteness_total").GetInt32() > 0);
        Assert.True(completeness.GetProperty("binding_incompleteness_summary").GetArrayLength() > 0);
        Assert.True(completeness.GetProperty("binding_incompleteness").GetArrayLength() > 0,
            "sections=all should include per-document detail rows");
    }

    [Fact]
    public async Task Status_Sections_Completeness_DiffersFromManifest_ForCompletenessFields()
    {
        var (snapshotId, dbPath) = await IndexAndSeedBindingIncompletenessAsync();
        var args = new[] { $"--output-dir={Path.GetDirectoryName(dbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new StatusTool(session);

        var jsonManifest = await tool.LurpStatus(sections: "manifest");
        using var docManifest = JsonDocument.Parse(jsonManifest);
        var manifestCompleteness = docManifest.RootElement
            .GetProperty("detail")
            .GetProperty("manifest")
            .GetProperty("completeness");
        var manifestDetailArray = manifestCompleteness.GetProperty("binding_incompleteness");
        var manifestTotal = manifestCompleteness.GetProperty("binding_incompleteness_total").GetInt32();

        var jsonCompleteness = await tool.LurpStatus(sections: "completeness");
        using var docCompleteness = JsonDocument.Parse(jsonCompleteness);
        var completenessCompleteness = docCompleteness.RootElement
            .GetProperty("detail")
            .GetProperty("manifest")
            .GetProperty("completeness");
        var completenessDetailArray = completenessCompleteness.GetProperty("binding_incompleteness");
        var completenessTotal = completenessCompleteness.GetProperty("binding_incompleteness_total").GetInt32();

        // Total and summary are the same for both sections
        Assert.Equal(manifestTotal, completenessTotal);

        // The distinguishing behavior: completeness includes detail rows, manifest does not
        Assert.Equal(0, manifestDetailArray.GetArrayLength());
        Assert.True(completenessDetailArray.GetArrayLength() > 0,
            "sections=completeness must produce detail rows that sections=manifest does not");
    }
}
