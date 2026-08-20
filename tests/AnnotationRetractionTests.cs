using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using ModelContextProtocol;

namespace Lurp.Tests;

// Contract test: retraction is scoped to exactly one (snapshot_id, annotation_id) row
// and does not bleed into other snapshots' copies. This covers the CLI handler's
// single-row hard-DELETE contract and the MCP tool's same WHERE clause.
public sealed class AnnotationRetractionTests : IntegrationTestBase
{
    private async Task<string> IndexInitialAsync()
    {
        CreateProject("AnnoProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace AnnoProj {
                    public class Foo {
                        public void Bar() {}
                    }
                    public class Bar {
                        public void Baz() {}
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create(new[] { $"--solution={SolutionPath}" });

    [Fact]
    public async Task Retract_Store_ScopedToOneRow_DoesNotTouchOtherSnapshot()
    {
        var snap1 = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap1).First(id =>
            {
                var info = store.GetSymbolInfo(id, snap1);
                return info != null && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("AnnoProj.Foo") && info.Kind == IndexedSymbolKind.Type;
            });

            // Three annotations in snap1
            store.SaveAnnotations(snap1, new[]
            {
                new AnnotationRecord(stableId, "note", "v1"),
                new AnnotationRecord(stableId, "note", "v2"),
                new AnnotationRecord(stableId, "todo", "t1")
            });
        }

        // Copy-forward to snap2: simulates IncrementalIndexer.CopyAnnotationsToSnapshot
        string snap2 = "snap-2-retract-contract";
        using (var store = OpenStore(DbPath))
        {
            // create a second snapshot row manually enough for annotation tests: reuse same manifest but new id
            // Easier: use real second index via file change, but we need deterministic copy. Direct copy.
            var manifest = store.LoadSnapshot(snap1)!;
            manifest = new SnapshotRow
            {
                SnapshotId = snap2,
                WorkspaceId = manifest.WorkspaceId,
                GitRoot = manifest.GitRoot,
                SolutionPath = manifest.SolutionPath,
                SdkVersion = manifest.SdkVersion,
                CompilerVersion = manifest.CompilerVersion,
                CreatedAtUtc = DateTime.UtcNow,
                DatabaseSchemaVersion = manifest.DatabaseSchemaVersion,
                OutputSchemaVersion = manifest.OutputSchemaVersion,
                ExtractorVersion = manifest.ExtractorVersion,
                ToolVersion = manifest.ToolVersion,
                PreviousSnapshotId = snap1
            };
            store.SaveSnapshot(manifest);
            store.CopyAnnotationsToSnapshot(snap1, snap2);
        }

        long snap1V1Id, snap1V2Id, snap2V1CopyId;
        using (var store = OpenStore(DbPath))
        {
            var snap1Anns = store.GetAnnotations(snap1);
            Assert.Equal(3, snap1Anns.Count);
            snap1V1Id = snap1Anns.Single(a => a.Value == "v1").AnnotationId;
            snap1V2Id = snap1Anns.Single(a => a.Value == "v2").AnnotationId;
            Assert.True(snap1V1Id > 0 && snap1V2Id > 0 && snap1V1Id != snap1V2Id);

            var snap2Anns = store.GetAnnotations(snap2);
            Assert.Equal(3, snap2Anns.Count);
            snap2V1CopyId = snap2Anns.Single(a => a.Value == "v1").AnnotationId;
            // ids are distinct across snapshots
            Assert.NotEqual(snap1V1Id, snap2V1CopyId);
            // page also exposes ids
            var page = store.GetAnnotationsPage(snap1, null, null, null, 100, null);
            Assert.Equal(3, page.TotalCount);
            Assert.All(page.Items, a => Assert.True(a.AnnotationId > 0));
        }

        // Retract exactly one row in snap1
        using (var store = OpenStore(DbPath))
        {
            Assert.True(store.TryRetractAnnotation(snap1, snap1V1Id));
            Assert.False(store.TryRetractAnnotation(snap1, snap1V1Id), "second retract of same id must report not-found");
            Assert.False(store.TryRetractAnnotation("no-such-snapshot", snap1V2Id));
            Assert.False(store.TryRetractAnnotation(snap2, snap1V1Id), "foreign id must not delete in other snapshot");
        }

        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(2, store.GetAnnotations(snap1).Count);
            Assert.DoesNotContain(store.GetAnnotations(snap1), a => a.AnnotationId == snap1V1Id);
            Assert.Contains(store.GetAnnotations(snap1), a => a.AnnotationId == snap1V2Id);

            // other snapshot untouched
            Assert.Equal(3, store.GetAnnotations(snap2).Count);
            Assert.Contains(store.GetAnnotations(snap2), a => a.AnnotationId == snap2V1CopyId);

            // page totalCount reflects retraction
            var pageAfter = store.GetAnnotationsPage(snap1, null, null, null, 100, null);
            Assert.Equal(2, pageAfter.TotalCount);
            Assert.Equal(2, pageAfter.Items.Count);
        }
    }

    [Fact]
    public async Task Retract_Mcp_ScopedToPinnedSnapshot()
    {
        var snap = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap).First(id =>
            {
                var info = store.GetSymbolInfo(id, snap);
                return info != null && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("AnnoProj.Foo");
            });
            store.SaveAnnotations(snap, new[] { new AnnotationRecord(stableId, "note", "mcp-v1"), new AnnotationRecord(stableId, "note", "mcp-v2") });
        }

        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);

        // id via get
        var json = tool.LurpGetAnnotations(symbol: stableId);
        using var doc = JsonDocument.Parse(json);
        var anns = doc.RootElement.GetProperty("annotations");
        Assert.Equal(2, anns.GetArrayLength());
        Assert.True(anns[0].TryGetProperty("annotation_id", out var idProp0) && idProp0.GetInt64() > 0);
        var idToRetract = anns[0].GetProperty("annotation_id").GetInt64();
        var otherId = anns[1].GetProperty("annotation_id").GetInt64();

        var retractJson = tool.LurpRetractAnnotation(annotation_id: idToRetract);
        using var rdoc = JsonDocument.Parse(retractJson);
        Assert.Equal("ok", rdoc.RootElement.GetProperty("status").GetString());

        var jsonAfter = tool.LurpGetAnnotations(symbol: stableId);
        using var docAfter = JsonDocument.Parse(jsonAfter);
        Assert.Equal(1, docAfter.RootElement.GetProperty("annotations").GetArrayLength());
        Assert.Equal(1, docAfter.RootElement.GetProperty("annotation_count").GetInt32());
        Assert.DoesNotContain(docAfter.RootElement.GetProperty("annotations").EnumerateArray(), e => e.GetProperty("annotation_id").GetInt64() == idToRetract);
        Assert.Contains(docAfter.RootElement.GetProperty("annotations").EnumerateArray(), e => e.GetProperty("annotation_id").GetInt64() == otherId);

        // second retract of same id must be InvalidParams
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpRetractAnnotation(annotation_id: idToRetract));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);

        // cross-snapshot mismatch
        var ex2 = Assert.Throws<McpProtocolException>(() => tool.LurpRetractAnnotation(annotation_id: otherId, snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex2.ErrorCode);
    }

    [Fact]
    public void Retract_InvalidId_Throws()
    {
        using var store = OpenStore(DbPath);
        // need at least one snapshot row to hit the table — create a dummy snapshot
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = "snap-invalid-id",
            WorkspaceId = "ws",
            GitRoot = TestDir,
            SolutionPath = SolutionPath,
            SdkVersion = "8",
            CompilerVersion = "8",
            CreatedAtUtc = DateTime.UtcNow,
            DatabaseSchemaVersion = 28,
            OutputSchemaVersion = 1,
            ExtractorVersion = "1",
            ToolVersion = "1"
        });
        Assert.Throws<ArgumentException>(() => store.TryRetractAnnotation("snap-invalid-id", 0));
        Assert.Throws<ArgumentException>(() => store.TryRetractAnnotation("snap-invalid-id", -5));
    }
}
