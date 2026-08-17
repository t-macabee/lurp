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
