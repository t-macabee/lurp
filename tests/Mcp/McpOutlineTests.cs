using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpOutlineTests : IntegrationTestBase
{
    private async Task<(string snapshotId, string docPath)> IndexOutlineFixtureAsync()
    {
        CreateProject("OutlineProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace OutlineProj {
                    /// <summary>Class A</summary>
                    public class ClassA {
                        /// <summary>Method one</summary>
                        public void MethodOne() {}
                        public void MethodTwo(int x) {}
                    }
                    public class ClassB {
                        public void BMethod() {}
                    }
                    // extra declarations to test pagination
                    public class ClassC {
                        public void CMethod() {}
                    }
                }
                """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        string docPath;
        using (var store = OpenStore(DbPath))
        {
            docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First(k => k.Contains("Models.cs"));
        }
        return (snapshotId, docPath);
    }

    private async Task<(string snapshotId, string docPath)> IndexPartialFixtureAsync()
    {
        CreateProject("PartialProj", new Dictionary<string, string>
        {
            ["Partial.cs"] = """
                namespace PartialProj {
                    public partial class PartialClass {
                        public void PartOne() {}
                    }
                    public class RegularClass {
                        public void Regular() {}
                    }
                }
                """,
            ["Partial2.cs"] = """
                namespace PartialProj {
                    public partial class PartialClass {
                        public void PartTwo() {}
                    }
                }
                """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        string docPath;
        using (var store = OpenStore(DbPath))
        {
            docPath = store.GetDocumentVersionIdsByPath(snapshotId).Keys.First(k => k.Contains("Partial.cs"));
        }
        return (snapshotId, docPath);
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create(new[] { $"--solution={SolutionPath}" });

    [Fact]
    public async Task Outline_Basic_ReturnsOrderedDeclarationsWithCorrectLines()
    {
        var (snapshotId, docPath) = await IndexOutlineFixtureAsync();
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var json = tool.LurpOutline(document: docPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
        Assert.Equal(docPath, doc.RootElement.GetProperty("document").GetString());
        var decls = doc.RootElement.GetProperty("declarations");
        Assert.True(decls.GetArrayLength() >= 4, "should have at least 4 declarations");
        // Ordered by start_line then symbol_id
        int prevStart = 0;
        foreach (var el in decls.EnumerateArray())
        {
            var start = el.GetProperty("start_line").GetInt32();
            var end = el.GetProperty("end_line").GetInt32();
            Assert.True(start >= 1);
            Assert.True(end >= start);
            Assert.True(start >= prevStart, "declarations should be ordered by start_line");
            prevStart = start;
        }
        // Check total count
        var total = doc.RootElement.GetProperty("declaration_count").GetInt32();
        Assert.Equal(decls.GetArrayLength(), total);
    }

    [Fact]
    public async Task Outline_IncludeGenerated_False_ExcludesGenerated()
    {
        var (_, docPath) = await IndexOutlineFixtureAsync();
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var json = tool.LurpOutline(document: docPath, include_generated: false);
        using var doc = JsonDocument.Parse(json);
        var decls = doc.RootElement.GetProperty("declarations");
        foreach (var el in decls.EnumerateArray())
        {
            Assert.False(el.GetProperty("is_generated").GetBoolean(), "include_generated=false should not return generated");
        }
        var json2 = tool.LurpOutline(document: docPath);
        using var doc2 = JsonDocument.Parse(json2);
        // omitted should behave same as false
        Assert.Equal(decls.GetArrayLength(), doc2.RootElement.GetProperty("declarations").GetArrayLength());
    }

    [Fact]
    public async Task Outline_IncludeGenerated_True_IncludesGenerated()
    {
        var (_, docPath) = await IndexOutlineFixtureAsync();
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var jsonFalse = tool.LurpOutline(document: docPath, include_generated: false);
        using var docFalse = JsonDocument.Parse(jsonFalse);
        var countFalse = docFalse.RootElement.GetProperty("declaration_count").GetInt32();

        var jsonTrue = tool.LurpOutline(document: docPath, include_generated: true);
        using var docTrue = JsonDocument.Parse(jsonTrue);
        var countTrue = docTrue.RootElement.GetProperty("declaration_count").GetInt32();
        // True should be >= false (if no generated, equal; if generated, greater)
        Assert.True(countTrue >= countFalse);
    }

    [Fact]
    public async Task Outline_Pagination_WalkEqualsWholeSet()
    {
        var (_, docPath) = await IndexOutlineFixtureAsync();
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var fullJson = tool.LurpOutline(document: docPath, limit: 100);
        using var fullDoc = JsonDocument.Parse(fullJson);
        var fullDecls = fullDoc.RootElement.GetProperty("declarations").EnumerateArray().Select(e => e.GetProperty("symbol_id").GetString()!).ToList();
        var total = fullDoc.RootElement.GetProperty("declaration_count").GetInt32();

        // Walk with limit=1
        var collected = new List<string>();
        string? cursor = null;
        int pages = 0;
        do
        {
            var json = tool.LurpOutline(document: docPath, limit: 1, cursor: cursor);
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.GetProperty("declarations").EnumerateArray())
                collected.Add(el.GetProperty("symbol_id").GetString()!);
            cursor = doc.RootElement.GetProperty("next_cursor").GetString();
            // declaration_count should stay constant across pages
            Assert.Equal(total, doc.RootElement.GetProperty("declaration_count").GetInt32());
            pages++;
            if (pages > 20) break; // safety
        } while (!string.IsNullOrEmpty(cursor));

        Assert.Equal(fullDecls.Count, collected.Count);
        Assert.Equal(fullDecls.OrderBy(x => x).ToList(), collected.OrderBy(x => x).ToList()); // no duplicates/gaps check via set equality
        // Also ensure no duplicates in collected itself
        Assert.Equal(collected.Count, collected.Distinct().Count());
        // And concatenated equals full in order
        Assert.Equal(fullDecls, collected);
    }

    [Fact]
    public async Task Outline_CursorFingerprintMismatch_ThrowsInvalidParams()
    {
        var (_, docPath) = await IndexOutlineFixtureAsync();
        // Need a second document for mismatch
        WriteFile("OutlineProj", "Other.cs", "namespace OutlineProj { public class Other { public void M() {} } }");
        await RunFullIndexNoDeleteAsync(DbPath);
        string otherPath;
        using (var store = OpenStore(DbPath))
        {
            var keys = store.GetDocumentVersionIdsByPath(await Task.FromResult(store.GetLatestSnapshotId()!)).Keys;
            otherPath = keys.First(k => k.Contains("Other.cs"));
        }
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var json = tool.LurpOutline(document: docPath, limit: 1);
        using var doc = JsonDocument.Parse(json);
        var cursor = doc.RootElement.GetProperty("next_cursor").GetString();
        if (!string.IsNullOrEmpty(cursor))
        {
            var ex = Assert.Throws<McpProtocolException>(() => tool.LurpOutline(document: otherPath, limit: 1, cursor: cursor));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            Assert.Contains("Cursor does not match", ex.Message);
        }
        else
        {
            // If only one declaration, test the include_generated mismatch instead
            var json2 = tool.LurpOutline(document: docPath, include_generated: false, limit: 1);
            using var doc2 = JsonDocument.Parse(json2);
            var cursor2 = doc2.RootElement.GetProperty("next_cursor").GetString();
            if (!string.IsNullOrEmpty(cursor2))
            {
                var ex = Assert.Throws<McpProtocolException>(() => tool.LurpOutline(document: docPath, include_generated: true, limit: 1, cursor: cursor2));
                Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            }
        }
    }

    [Fact]
    public async Task Outline_DocumentNotInSnapshot_ThrowsInvalidParams()
    {
        await IndexOutlineFixtureAsync();
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpOutline(document: "NotExist/Fake.cs"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Outline_SignatureAndNameLines_PopulatedAndGEStart()
    {
        await IndexOutlineFixtureAsync();
        // Use a fixture with doc comment above method
        CreateProject("OutlineSigProj", new Dictionary<string, string>
        {
            ["Sig.cs"] = """
                namespace OutlineSigProj {
                    public class SigClass {
                        /// <summary>docs for method</summary>
                        public void DocMethod(int x) {}
                    }
                }
                """
        });
        await RunFullIndexNoDeleteAsync(DbPath);
        string sigPath;
        using (var store = OpenStore(DbPath))
        {
            var snap = store.GetLatestSnapshotId()!;
            sigPath = store.GetDocumentVersionIdsByPath(snap).Keys.First(k => k.Contains("Sig.cs"));
        }
        await using var session = CreateSession();
        var tool = new OutlineTool(session);
        var json = tool.LurpOutline(document: sigPath);
        using var doc = JsonDocument.Parse(json);
        var decls = doc.RootElement.GetProperty("declarations").EnumerateArray().ToList();
        // Find DocMethod
        var method = decls.FirstOrDefault(e => e.GetProperty("fully_qualified_name").GetString()!.Contains("DocMethod"));
        Assert.False(method.ValueKind == JsonValueKind.Undefined, "DocMethod should be found");
        var start = method.GetProperty("start_line").GetInt32();
        var sig = method.GetProperty("signature_start_line");
        var name = method.GetProperty("name_start_line");
        Assert.True(sig.ValueKind != JsonValueKind.Null);
        Assert.True(name.ValueKind != JsonValueKind.Null);
        Assert.True(sig.GetInt32() >= start);
        Assert.True(name.GetInt32() >= sig.GetInt32());
    }

    [Fact]
    public async Task Outline_IsPartial_TrueForPartialFalseOtherwise()
    {
        var (_, partialDocPath) = await IndexPartialFixtureAsync();
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var json = tool.LurpOutline(document: partialDocPath);
        using var doc = JsonDocument.Parse(json);
        var decls = doc.RootElement.GetProperty("declarations").EnumerateArray().ToList();
        // PartialClass should be is_partial true
        var partial = decls.Where(e => e.GetProperty("fully_qualified_name").GetString()!.Contains("PartialClass")).ToList();
        Assert.NotEmpty(partial);
        Assert.Contains(partial, e => e.GetProperty("is_partial").GetBoolean() == true);
        // RegularClass should be false
        var regular = decls.FirstOrDefault(e => e.GetProperty("fully_qualified_name").GetString()!.Contains("RegularClass"));
        if (regular.ValueKind != JsonValueKind.Undefined)
            Assert.False(regular.GetProperty("is_partial").GetBoolean());
    }

    [Fact]
    public async Task Outline_DeclarationCount_ConstantAcrossPages()
    {
        var (_, docPath) = await IndexOutlineFixtureAsync();
        await using var session = CreateSession();
        var tool = new OutlineTool(session);

        var json1 = tool.LurpOutline(document: docPath, limit: 1);
        using var doc1 = JsonDocument.Parse(json1);
        var total1 = doc1.RootElement.GetProperty("declaration_count").GetInt32();
        var cursor = doc1.RootElement.GetProperty("next_cursor").GetString();
        if (!string.IsNullOrEmpty(cursor))
        {
            var json2 = tool.LurpOutline(document: docPath, limit: 1, cursor: cursor);
            using var doc2 = JsonDocument.Parse(json2);
            var total2 = doc2.RootElement.GetProperty("declaration_count").GetInt32();
            Assert.Equal(total1, total2);
        }
        // Also check that total equals full count
        var fullJson = tool.LurpOutline(document: docPath);
        using var fullDoc = JsonDocument.Parse(fullJson);
        Assert.Equal(total1, fullDoc.RootElement.GetProperty("declaration_count").GetInt32());
        Assert.Equal(total1, fullDoc.RootElement.GetProperty("declarations").GetArrayLength());
    }
}
