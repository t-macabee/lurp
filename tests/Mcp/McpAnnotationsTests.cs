using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpAnnotationsTests : IntegrationTestBase
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
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private async Task<string> IndexSecondAsync()
    {
        WriteFile("AnnoProj", "Models.cs", """
            namespace AnnoProj {
                public class Foo {
                    public void Bar() {}
                    public void Baz() {}
                }
            }
            """);
        return await RunIncrementalIndexAsync();
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create(new[] { $"--solution={SolutionPath}" });

    [Fact]
    public async Task GetAnnotations_ReadIsolation_PerSnapshot()
    {
        var snap1 = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap1).First(id => {
                var info = store.GetSymbolInfo(id, snap1);
                return info != null && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("AnnoProj.Foo") && info.Kind == IndexedSymbolKind.Type;
            });
            store.SaveAnnotations(snap1, new[] { new AnnotationRecord(stableId, "note", "value1") });
        }
        var snap2 = await IndexSecondAsync();
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);

        using (var store = OpenStore(DbPath))
        {
            Assert.Single(store.GetAnnotations(snap1, stableId));
            Assert.Single(store.GetAnnotations(snap2, stableId));
            Assert.Equal("value1", store.GetAnnotations(snap2, stableId)[0].Value);
        }

        var json = tool.LurpGetAnnotations(symbol: stableId);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snap2, doc.RootElement.GetProperty("snapshot_id").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("annotations").GetArrayLength());

        using (var store = OpenStore(DbPath))
            store.SaveAnnotations(snap2, new[] { new AnnotationRecord(stableId, "note", "value2") });
        using (var store = OpenStore(DbPath))
        {
            Assert.Single(store.GetAnnotations(snap1, stableId));
            Assert.Equal(2, store.GetAnnotations(snap2, stableId).Count);
        }
        await using var session2 = CreateSession();
        var tool2 = new AnnotationsTool(session2);
        var json2 = tool2.LurpGetAnnotations(symbol: stableId);
        using var doc2 = JsonDocument.Parse(json2);
        Assert.Equal(2, doc2.RootElement.GetProperty("annotations").GetArrayLength());
    }

    [Fact]
    public async Task GetAnnotations_ThreeForms_AndSnapshotMismatch()
    {
        var snap = await IndexInitialAsync();
        string stableId, stableFqn, stableDocId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap).First(id => {
                var info = store.GetSymbolInfo(id, snap);
                return info != null && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("AnnoProj.Foo") && info.Kind == IndexedSymbolKind.Type;
            });
            var info = store.GetSymbolInfo(stableId, snap)!;
            stableFqn = info.FullyQualifiedName!;
            stableDocId = stableId.Split('|')[0];
            store.SaveAnnotations(snap, new[] { new AnnotationRecord(stableId, "note", "v") });
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var jsonPipe = tool.LurpGetAnnotations(symbol: stableId);
        var jsonDoc = tool.LurpGetAnnotations(symbol: stableDocId);
        var jsonFqn = tool.LurpGetAnnotations(symbol: stableFqn);
        using var dPipe = JsonDocument.Parse(jsonPipe);
        using var dDoc = JsonDocument.Parse(jsonDoc);
        using var dFqn = JsonDocument.Parse(jsonFqn);
        Assert.Equal(dPipe.RootElement.GetProperty("annotations").GetArrayLength(), dDoc.RootElement.GetProperty("annotations").GetArrayLength());
        Assert.Equal(dPipe.RootElement.GetProperty("annotations").GetArrayLength(), dFqn.RootElement.GetProperty("annotations").GetArrayLength());

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpGetAnnotations(symbol: stableId, snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    // ── Gap 2: document / kind / pagination ───────────────────────────

    [Fact]
    public async Task Annotations_DocumentFilter_ReturnsOnlyMatchingPath()
    {
        var snap = await IndexInitialAsync();
        string stableId;
        string docPath = "src/AnnoProj/Models.cs";
        // Create a second document to have another path
        WriteFile("AnnoProj", "Other.cs", "namespace AnnoProj { public class Other { public void M() {} } }");
        var snap2 = await RunFullIndexNoDeleteAsync(DbPath);
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap2).First(id => {
                var info = store.GetSymbolInfo(id, snap2);
                return info != null && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("AnnoProj.Foo");
            });
            // Save annotations with different document_paths
            store.SaveAnnotations(snap2, new[] {
                new AnnotationRecord(stableId, "note", "v1", "src/AnnoProj/Models.cs"),
                new AnnotationRecord(stableId, "note", "v2", "src/AnnoProj/Other.cs"),
                new AnnotationRecord(stableId, "note", "v3", null)
            });
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var json = tool.LurpGetAnnotations(document: docPath);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("annotations");
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("src/AnnoProj/Models.cs", arr[0].GetProperty("document_path").GetString());
        Assert.Equal("v1", arr[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Annotations_DocumentFilter_EmptyButValid_ReturnsEmptyNotError()
    {
        var snap = await IndexInitialAsync();
        WriteFile("AnnoProj", "Empty.cs", "namespace AnnoProj { public class Empty { } }");
        var snap2 = await RunFullIndexNoDeleteAsync(DbPath);
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var json = tool.LurpGetAnnotations(document: "src/AnnoProj/Empty.cs");
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("annotations");
        Assert.Equal(0, arr.GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("annotation_count").GetInt32());
    }

    [Fact]
    public async Task Annotations_DocumentNotInSnapshot_ThrowsInvalidParams()
    {
        await IndexInitialAsync();
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpGetAnnotations(document: "NotExist/Fake.cs"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Annotations_SymbolAndDocument_MutuallyExclusive_ThrowsInvalidParams()
    {
        var snap = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap).First();
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpGetAnnotations(symbol: stableId, document: "AnnoProj/Models.cs"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("mutually exclusive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Annotations_KindFilter_NarrowsToKindOnly()
    {
        var snap = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap).First();
            store.SaveAnnotations(snap, new[] {
                new AnnotationRecord(stableId, "note", "n1"),
                new AnnotationRecord(stableId, "todo", "t1"),
                new AnnotationRecord(stableId, "note", "n2")
            });
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var json = tool.LurpGetAnnotations(kind: "note");
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("annotations");
        Assert.True(arr.GetArrayLength() >= 2);
        foreach (var el in arr.EnumerateArray())
            Assert.Equal("note", el.GetProperty("kind").GetString());
        // Whole snapshot without kind should be larger
        var jsonAll = tool.LurpGetAnnotations();
        using var docAll = JsonDocument.Parse(jsonAll);
        Assert.True(docAll.RootElement.GetProperty("annotation_count").GetInt32() > arr.GetArrayLength() || docAll.RootElement.GetProperty("annotations").GetArrayLength() > arr.GetArrayLength());
    }

    [Fact]
    public async Task Annotations_WholeSnapshot_ReturnsAllRespectingLimit()
    {
        var snap = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap).First();
            var recs = Enumerable.Range(0, 5).Select(i => new AnnotationRecord(stableId, "note", $"v{i}")).ToArray();
            store.SaveAnnotations(snap, recs);
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var json = tool.LurpGetAnnotations(limit: 10);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(5, doc.RootElement.GetProperty("annotations").GetArrayLength());
        Assert.Equal(5, doc.RootElement.GetProperty("annotation_count").GetInt32());
    }

    [Fact]
    public async Task Annotations_Pagination_WalkEqualsWhole()
    {
        var snap = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap).First();
            var recs = Enumerable.Range(0, 5).Select(i => new AnnotationRecord(stableId, "note", $"v{i}")).ToArray();
            store.SaveAnnotations(snap, recs);
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);

        var fullJson = tool.LurpGetAnnotations(limit: 10);
        using var fullDoc = JsonDocument.Parse(fullJson);
        var fullValues = fullDoc.RootElement.GetProperty("annotations").EnumerateArray().Select(e => e.GetProperty("value").GetString()!).ToList();
        var total = fullDoc.RootElement.GetProperty("annotation_count").GetInt32();

        var collected = new List<string>();
        string? cursor = null;
        int pages = 0;
        do
        {
            var json = tool.LurpGetAnnotations(limit: 2, cursor: cursor);
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.GetProperty("annotations").EnumerateArray())
                collected.Add(el.GetProperty("value").GetString()!);
            cursor = doc.RootElement.GetProperty("next_cursor").GetString();
            Assert.Equal(total, doc.RootElement.GetProperty("annotation_count").GetInt32());
            pages++;
            if (pages > 10) break;
        } while (!string.IsNullOrEmpty(cursor));

        Assert.Equal(fullValues, collected);
        Assert.Equal(collected.Count, collected.Distinct().Count());
    }

    [Fact]
    public async Task Annotations_CursorFingerprintMismatch_ThrowsInvalidParams()
    {
        var snap = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap).First();
            var recs = Enumerable.Range(0, 4).Select(i => new AnnotationRecord(stableId, "note", $"v{i}")).ToArray();
            store.SaveAnnotations(snap, recs);
            // Also add a todo kind to have alternative fingerprint
            store.SaveAnnotations(snap, new[] { new AnnotationRecord(stableId, "todo", "t1") });
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);
        var json = tool.LurpGetAnnotations(limit: 1, kind: "note");
        using var doc = JsonDocument.Parse(json);
        var cursor = doc.RootElement.GetProperty("next_cursor").GetString();
        if (!string.IsNullOrEmpty(cursor))
        {
            var ex = Assert.Throws<McpProtocolException>(() => tool.LurpGetAnnotations(limit: 1, kind: "todo", cursor: cursor));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("Cursor does not match", ex.Message);
        }
        else
        {
            // If no cursor (not enough data), test symbol mismatch instead
            var json2 = tool.LurpGetAnnotations(symbol: stableId, limit: 1);
            using var doc2 = JsonDocument.Parse(json2);
            var cursor2 = doc2.RootElement.GetProperty("next_cursor").GetString();
            if (!string.IsNullOrEmpty(cursor2))
            {
                var ex = Assert.Throws<McpProtocolException>(() => tool.LurpGetAnnotations(limit: 1, cursor: cursor2));
                Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            }
        }
    }

    [Fact]
    public async Task Annotations_NullDocumentPath_UnreachableByDocFilterButVisibleBySnapshot()
    {
        var snap = await IndexInitialAsync();
        string stableId;
        using (var store = OpenStore(DbPath))
        {
            stableId = store.GetSymbolIdsInSnapshot(snap).First();
            store.SaveAnnotations(snap, new[] { new AnnotationRecord(stableId, "note", "nullDoc", null) });
        }
        await using var session = CreateSession();
        var tool = new AnnotationsTool(session);

        // Document filter should not see it
        var jsonDoc = tool.LurpGetAnnotations(document: "src/AnnoProj/Models.cs");
        using var docDoc = JsonDocument.Parse(jsonDoc);
        Assert.Equal(0, docDoc.RootElement.GetProperty("annotations").GetArrayLength());

        // Whole snapshot should see it
        var jsonAll = tool.LurpGetAnnotations();
        using var docAll = JsonDocument.Parse(jsonAll);
        Assert.True(docAll.RootElement.GetProperty("annotations").GetArrayLength() >= 1);
        Assert.Contains(docAll.RootElement.GetProperty("annotations").EnumerateArray(), e => e.GetProperty("value").GetString() == "nullDoc");

        // Symbol filter should see it
        var jsonSym = tool.LurpGetAnnotations(symbol: stableId);
        using var docSym = JsonDocument.Parse(jsonSym);
        Assert.Contains(docSym.RootElement.GetProperty("annotations").EnumerateArray(), e => e.GetProperty("value").GetString() == "nullDoc");
    }

    [Fact]
    public void Annotate_Gated_ReadOnly()
    {
        var tools = typeof(McpServeHandler).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false).Length > 0)
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Length > 0)
            .Select(m => ((ModelContextProtocol.Server.McpServerToolAttribute)m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)[0]).Name)
            .ToList();
        Assert.Contains("lurp_get_annotations", tools);
        Assert.DoesNotContain("lurp_annotate", tools);
    }
}
