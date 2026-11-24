using System.Text;
using Lurp.Storage;

namespace Lurp.Tests;

public sealed class DeclarationSpanTests : IntegrationTestBase
{
    [Fact]
    public void MapDeclaration_ExclusiveEndAtNextLine_UsesPreviousLine()
    {
        var location = SourceLineMap.MapDeclaration("Test.cs", 0, 4, [0, 4], Encoding.UTF8.GetBytes("one\ntwo"),
            false, "doc", "sym");

        Assert.NotNull(location);
        Assert.Equal(1, location.EndLine);
        Assert.Equal(3, location.EndColumn);
    }

    [Fact]
    public async Task OutlineNavigateAndGetSymbol_UseAttachedCommentsAndExactEnds()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["C.cs"] = """
                class C
                {
                    void First() { }

                    // detached

                    // attached
                    /// <summary>Documentation.</summary>
                    void Second()
                    {
                    }

                    void Third() { }
                }
                """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        const string path = "src/App/C.cs";
        var outline = store.GetDeclarationsOutline(path, snapshot, false, 100, null)!;
        var methods = outline.Items.Where(item => item.Kind == "Method").ToArray();
        Assert.Equal([(3, 3), (7, 11), (13, 13)], methods.Select(item => (item.StartLine, item.EndLine)).ToArray());

        Assert.Equal("T:C", SymbolId.DocCommentIdOf(store.NavigateToLocation(path, 4, snapshot)!.SymbolId));
        var second = store.NavigateToLocation(path, 7, snapshot)!;
        Assert.Equal("M:C.Second", SymbolId.DocCommentIdOf(second.SymbolId));
        Assert.Equal((7, 11), (second.StartLine, second.EndLine));

        var source = await CliModeRunner.RunAsync("get-symbol",
            [$"--output-dir={TestDir}", $"--symbol={second.SymbolId}", "--view=declaration"]);
        Assert.Contains("// attached", source);
        Assert.Contains("/// <summary>Documentation.</summary>", source);
        Assert.DoesNotContain("// detached", source);
        Assert.DoesNotContain("Third", source);
    }

    [Fact]
    public async Task MultiTfm_KeepsDistinctSpansAndDeduplicatesIdenticalDeclarations()
    {
        const string path = "src/App/C.cs";
        CreateProject("App", new Dictionary<string, string>
        {
            ["C.cs"] = """
                class C
                {
                #if NET9_0
                    int Version() => 9;
                #elif NET10_0
                    int Version() => 10;
                #endif

                    void Shared() { }

                    void Sized()
                    {
                #if NET9_0
                    }
                #elif NET10_0
                        Shared();
                    }
                #endif
                }
                """
        }, targetFramework: "net9.0;net10.0");
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var outline = store.GetDeclarationsOutline(path, snapshot, false, 100, null)!;
        var versions = outline.Items.Where(item => SymbolId.DocCommentIdOf(item.SymbolId) == "M:C.Version").ToArray();
        Assert.Equal([(4, 4), (6, 6)], versions.Select(item => (item.StartLine, item.EndLine)).ToArray());
        var id = versions[0].SymbolId;
        Assert.Equal(id, versions[1].SymbolId);
        Assert.Equal("int Version() => 9;\n\nint Version() => 10;", store.GetSymbolSource(id, snapshot, ViewKind.Declaration));
        Assert.Equal(2, store.GetSymbolInfo(id, snapshot)!.DeclarationCount);
        var shared = Assert.Single(outline.Items, item => SymbolId.DocCommentIdOf(item.SymbolId) == "M:C.Shared");
        Assert.Equal(1, store.GetSymbolInfo(shared.SymbolId, snapshot)!.DeclarationCount);
        Assert.Equal(["net10.0", "net9.0"], SnapshotAssertions.ReadTargetFrameworksForSymbol(DbPath, snapshot, "M:C.Version"));

        // Two TFM variants can start at the same token and end on different lines.
        var paged = new List<(int StartLine, int EndLine)>();
        OutlineCursor? cursor = null;
        do
        {
            var page = store.GetDeclarationsOutline(path, snapshot, false, 1, cursor)!;
            var item = Assert.Single(page.Items);
            if (SymbolId.DocCommentIdOf(item.SymbolId) == "M:C.Sized")
                paged.Add((item.StartLine, item.EndLine));
            cursor = page.NextCursor is { } token ? Assert.IsType<OutlineCursor>(OutlineCursor.TryDecode(token)) : null;
        } while (cursor != null);
        Assert.Equal([(11, 14), (11, 17)], paged);
    }
}
