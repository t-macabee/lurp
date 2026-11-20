using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;

namespace Lurp.Tests;

public sealed class AnnotationDocumentFilterTests : IntegrationTestBase
{
    private const string DocA = "src/P/A.cs";
    private const string DocB = "src/P/B.cs";

    private async Task<string> SeedAndIndexAsync()
    {
        CreateProject("P", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace P; public class InA { } public partial class Both { }",
            ["B.cs"] = "namespace P; public class InB { } public partial class Both { }"
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        var inAId = HandlerBootstrap.ResolveSymbolArg(store, "P.InA", snapshotId);
        var inBId = HandlerBootstrap.ResolveSymbolArg(store, "P.InB", snapshotId);
        var bothId = HandlerBootstrap.ResolveSymbolArg(store, "P.Both", snapshotId);

        store.SaveUserAnnotation(snapshotId, inAId, "note", "a");
        store.SaveUserAnnotation(snapshotId, inBId, "note", "b");
        store.SaveUserAnnotation(snapshotId, bothId, "note", "both");
        store.SaveAnnotations(snapshotId, [new AnnotationRecord(inBId, "note", "path-a", DocA)]);

        return snapshotId;
    }

    [Fact]
    public async Task GetAnnotations_Document_IncludesUserAnnotationsOfDeclaredSymbols()
    {
        await SeedAndIndexAsync();

        var result = CapturingOutputSink.Capture(sink => AnnotationHandler.RunGetAnnotations([
            $"--document={DocA}",
            "--output=json",
            $"--output-dir={TestDir}"
        ], sink));

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal(3, doc.RootElement.GetProperty("annotation_count").GetInt32());
        var values = doc.RootElement.GetProperty("annotations").EnumerateArray()
            .Select(e => e.GetProperty("value").GetString())
            .ToList();
        Assert.Equal(["a", "both", "path-a"], values);
    }

    [Fact]
    public async Task GetAnnotations_Document_PartialTypeInBothFiles()
    {
        await SeedAndIndexAsync();

        var result = CapturingOutputSink.Capture(sink => AnnotationHandler.RunGetAnnotations([
            $"--document={DocB}",
            "--output=json",
            $"--output-dir={TestDir}"
        ], sink));

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        // D68: B.cs declares InB and Both, so it includes InB's user annotation (b),
        // Both's user annotation (both), and InB's extractor annotation (path-a),
        // while excluding InA's annotation (a).
        Assert.Equal(3, doc.RootElement.GetProperty("annotation_count").GetInt32());
        var values = doc.RootElement.GetProperty("annotations").EnumerateArray()
            .Select(e => e.GetProperty("value").GetString())
            .ToList();
        Assert.Equal(["b", "both", "path-a"], values);
    }

    [Fact]
    public async Task GetAnnotations_Document_PagesTheUnion()
    {
        await SeedAndIndexAsync();

        var collected = new List<string>();
        string? cursor = null;

        for (var page = 0; page < 3; page++)
        {
            var args = new List<string>
            {
                $"--document={DocA}",
                "--limit=1",
                "--output=json",
                $"--output-dir={TestDir}"
            };
            if (cursor != null)
                args.Add($"--cursor={cursor}");

            var result = CapturingOutputSink.Capture(sink => AnnotationHandler.RunGetAnnotations([.. args], sink));
            Assert.Null(result.Failure);

            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(3, doc.RootElement.GetProperty("annotation_count").GetInt32());
            var item = Assert.Single(doc.RootElement.GetProperty("annotations").EnumerateArray());
            collected.Add(item.GetProperty("value").GetString()!);

            cursor = doc.RootElement.GetProperty("next_cursor").ValueKind == JsonValueKind.Null
                ? null
                : doc.RootElement.GetProperty("next_cursor").GetString();

            if (page < 2)
                Assert.NotNull(cursor);
            else
                Assert.Null(cursor);
        }

        Assert.Equal(["a", "both", "path-a"], collected);
    }

    [Fact]
    public async Task McpGetAnnotations_Document_IncludesUserAnnotations()
    {
        await SeedAndIndexAsync();

        await using var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={TestDir}"]);
        var tool = new AnnotationsTool(session);
        var json = tool.LurpGetAnnotations(document: DocA);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("annotation_count").GetInt32());
        var values = doc.RootElement.GetProperty("annotations").EnumerateArray()
            .Select(e => e.GetProperty("value").GetString())
            .ToList();
        Assert.Equal(["a", "both", "path-a"], values);
    }
}
