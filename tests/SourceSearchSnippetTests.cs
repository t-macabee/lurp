using System.Text.Json;

namespace Lurp.Tests;

public sealed class SourceSearchSnippetTests : IntegrationTestBase
{
    private async Task IndexSnippetFixtureAsync()
    {
        CreateProject("SnipProj", new Dictionary<string, string>
        {
            ["Text.cs"] = "// alpha beta gamma delta epsilon zeta eta theta iota kappa needle lambda mu\nnamespace SnipProj { static class T { } }\n",
            ["Twice.cs"] = "// needle one two three four five six seven needle needle eight\n",
            ["Shapes.cs"] = "namespace SnipProj { static class R { } }\n"
        });
        await RunFullIndexAsync(DbPath);
    }

    private async Task<Dictionary<string, string>> RunSourceSearchAsync(string query, int snippetTokens = 64)
    {
        var stdout = await CliModeRunner.RunAsync("search", [
            $"--output-dir={Path.GetDirectoryName(DbPath)!}",
            "--type=source",
            $"--query={query}",
            $"--snippet-tokens={snippetTokens}"
        ]);
        using var doc = JsonDocument.Parse(stdout);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            Assert.Equal("source", item.GetProperty("type").GetString());
            var path = item.GetProperty("document_path").GetString()!;
            var snippet = item.GetProperty("snippet").GetString()!;
            map[path] = snippet;
        }
        return map;
    }

    private static string SnippetForFile(Dictionary<string, string> snippets, string fileName) =>
        snippets.Single(kvp => kvp.Key.EndsWith("/" + fileName, StringComparison.Ordinal)).Value;

    [Fact]
    public async Task Snippet_ShowsTheMatchInAWindow()
    {
        await IndexSnippetFixtureAsync();
        var snippets = await RunSourceSearchAsync("needle", snippetTokens: 5);
        Assert.Equal("…iota kappa <mark>needle</mark> lambda mu…", SnippetForFile(snippets, "Text.cs"));
    }

    [Fact]
    public async Task Snippet_WindowHoldsTheFirstMatch()
    {
        await IndexSnippetFixtureAsync();
        var snippets = await RunSourceSearchAsync("needle", snippetTokens: 3);
        Assert.Equal("<mark>needle</mark> one two…", SnippetForFile(snippets, "Twice.cs"));
        Assert.Equal("…kappa <mark>needle</mark> lambda…", SnippetForFile(snippets, "Text.cs"));
    }

    [Fact]
    public async Task Snippet_PhraseIsOneMarkedSpan()
    {
        await IndexSnippetFixtureAsync();
        var snippets = await RunSourceSearchAsync("kappa needle", snippetTokens: 5);
        Assert.Equal("…iota <mark>kappa needle</mark> lambda mu…", SnippetForFile(snippets, "Text.cs"));
    }

    [Fact]
    public async Task Snippet_SourceControlCharacters_DoNotMoveTheMark()
    {
        await IndexFullAsync("App", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace App;\n// \u0002decoy\u0003 needle\npublic class A { }\n"
        });

        var snippets = await RunSourceSearchAsync("needle", snippetTokens: 1);
        Assert.Equal("…<mark>needle</mark>…", SnippetForFile(snippets, "A.cs"));
    }

    [Fact]
    public async Task Snippet_UnpairedSourceMarker_DoesNotWidenTheMark()
    {
        await IndexFullAsync("App", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace App;\n// \u0002 lead needle tail\npublic class B { }\n"
        });

        var snippets = await RunSourceSearchAsync("needle", snippetTokens: 1);
        Assert.Equal("…<mark>needle</mark>…", SnippetForFile(snippets, "B.cs"));
    }

    [Fact]
    public async Task Snippet_SourceMarkersNextToMatch_StayOutsideTheMark()
    {
        await IndexFullAsync("App", new Dictionary<string, string>
        {
            ["Adjacent.cs"] = "namespace App;\n// lead \u0002needle\u0003\u0003 tail\npublic class A { }\n"
        });

        var snippets = await RunSourceSearchAsync("needle", snippetTokens: 3);
        Assert.Equal("…lead \u0002<mark>needle</mark>\u0003\u0003 tail…", SnippetForFile(snippets, "Adjacent.cs"));
    }

    [Fact]
    public async Task Snippet_MatchLongerThanTheLimit_KeepsTheWholeMatch()
    {
        await IndexFullAsync("App", new Dictionary<string, string>
        {
            ["C.cs"] = "namespace App;\n// gamma alpha beta delta\npublic class C { }\n"
        });

        var snippets = await RunSourceSearchAsync("alpha beta", snippetTokens: 1);
        Assert.Equal("…<mark>alpha beta</mark>…", SnippetForFile(snippets, "C.cs"));
    }

    [Fact]
    public async Task Snippet_FragmentFallbackHasTheSameFormat()
    {
        await IndexSnippetFixtureAsync();
        var snippets = await RunSourceSearchAsync("eedl", snippetTokens: 5);
        Assert.Equal("…iota kappa n<mark>eedl</mark>e lambda mu…", SnippetForFile(snippets, "Text.cs"));
    }

    [Fact]
    public async Task SourceSearch_DoesNotMatchThePath()
    {
        await IndexSnippetFixtureAsync();
        var snippets = await RunSourceSearchAsync("Shapes");
        Assert.Empty(snippets);
    }
}
