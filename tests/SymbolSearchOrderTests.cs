using System.Text.Json;

namespace Lurp.Tests;

public sealed class SymbolSearchOrderTests : IntegrationTestBase
{
    private async Task IndexOrderFixtureAsync()
    {
        CreateProject("OrderProj", new Dictionary<string, string>
        {
            ["Zeta.cs"] = """
                namespace OrderProj;
                public static class AZeta { }
                public static class Zeta { }
                public static class Calc { public static int Add(int a) => a; }
                """
        });
        await RunFullIndexAsync(DbPath);
    }

    private async Task<JsonDocument> RunSearchAsync(params string[] extraArgs)
    {
        string[] args =
        [
            $"--output-dir={Path.GetDirectoryName(DbPath)!}",
            .. extraArgs
        ];
        var stdout = await CliModeRunner.RunAsync("search", args);
        return JsonDocument.Parse(stdout);
    }

    private static string[] SymbolFqns(JsonDocument doc) =>
        doc.RootElement.GetProperty("results")
            .EnumerateArray()
            .Where(r => r.GetProperty("type").GetString() == "symbol")
            .Select(r => r.GetProperty("fully_qualified_name").GetString()!)
            .ToArray();

    [Fact]
    public async Task Search_TokenMatchComesFirst_ForEveryLimit()
    {
        await IndexOrderFixtureAsync();

        foreach (var limit in new[] { 2, 3 })
        {
            using var doc = await RunSearchAsync("--type=symbol", "--query=Zeta", "--kind=Type", $"--limit={limit}");
            Assert.Equal(["global::OrderProj.Zeta", "global::OrderProj.AZeta"], SymbolFqns(doc));
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("next_cursor").ValueKind);
        }

        using var page1 = await RunSearchAsync("--type=symbol", "--query=Zeta", "--kind=Type", "--limit=1");
        Assert.Equal(["global::OrderProj.Zeta"], SymbolFqns(page1));
        var nextCursor = page1.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(nextCursor));

        using var page2 = await RunSearchAsync("--type=symbol", "--query=Zeta", "--kind=Type", "--limit=1", $"--cursor={nextCursor}");
        Assert.Equal(["global::OrderProj.AZeta"], SymbolFqns(page2));
        Assert.Equal(JsonValueKind.Null, page2.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Theory]
    [InlineData("Method")]
    [InlineData("Int32")]
    [InlineData("neutral")]
    public async Task Search_MatchesTheNameOnly(string query)
    {
        await IndexOrderFixtureAsync();

        using var doc = await RunSearchAsync("--type=symbol", $"--query={query}");
        Assert.Empty(SymbolFqns(doc));

        using var limitedDoc = await RunSearchAsync("--type=symbol", $"--query={query}", "--limit=1");
        Assert.Empty(SymbolFqns(limitedDoc));
    }

    [Fact]
    public async Task Search_TypeAll_HasTheSymbolPageOfTypeSymbol()
    {
        await IndexOrderFixtureAsync();

        using var doc = await RunSearchAsync("--type=all", "--query=Zeta", "--kind=Type", "--limit=2");
        Assert.Equal(["global::OrderProj.Zeta", "global::OrderProj.AZeta"], SymbolFqns(doc));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("next_cursor").ValueKind);
    }
}
