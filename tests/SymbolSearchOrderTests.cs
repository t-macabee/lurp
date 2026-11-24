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

    [Fact]
    public async Task Search_PrefersProductionTypesAndPaginatesAcrossAllFourTiers()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Ranking.cs"] = """
                namespace App
                {
                    public class Impact {}
                    public class ImpactReachability {}
                    public class SearchIndex {}
                    public class ContextAssembler {}
                    public class DeadCandidate {}
                    public class RentalService {}
                    public class XMLParser {}
                    public class Ranking
                    {
                        private int _impact;
                        public void Impact() {}
                        public void Search() {}
                        private int context_holder;
                        public void Dead() {}
                        private int _rental;
                    }
                    public class Preimpact {}
                }
                namespace Containers.Impact { public class Nested {} }
                """
        });
        CreateProject("Checks", new Dictionary<string, string>
        {
            ["Tests.cs"] = """
                namespace AChecks;
                public class Impact {}
                public class ImpactTests { [Xunit.Fact] public void Impact() {} }
                """
        }, packageReferences: ["xunit@2.9.3"]);
        RestoreSolution();
        await RunFullIndexAsync(DbPath);

        string[] expected =
        [
            "global::App.Impact", "global::App.Ranking.Impact",
            "global::AChecks.Impact", "global::AChecks.ImpactTests.Impact",
            "global::App.ImpactReachability", "global::App.Ranking._impact",
            "global::AChecks.ImpactTests",
            "global::App.Preimpact", "global::Containers.Impact.Nested"
        ];
        using var all = await RunSearchAsync("--type=symbol", "--query=Impact", "--limit=100");
        Assert.Equal(expected, SymbolFqns(all));

        foreach (var limit in new[] { 1, 3, 4 })
        {
            var collected = new List<string>();
            string? cursor = null;
            do
            {
                string[] args = ["--type=symbol", "--query=Impact", $"--limit={limit}",
                    .. (cursor == null ? Array.Empty<string>() : new[] { $"--cursor={cursor}" })];
                using var page = await RunSearchAsync(args);
                collected.AddRange(SymbolFqns(page));
                Assert.True(collected.Count <= expected.Length);
                cursor = page.RootElement.GetProperty("next_cursor").GetString();
            } while (cursor != null);
            Assert.Equal(expected, collected);
        }

        foreach (var (query, first) in new[]
                 {
                     ("Search", "global::App.Ranking.Search"),
                     ("Context", "global::App.ContextAssembler"),
                     ("Dead", "global::App.Ranking.Dead"),
                     ("Rental", "global::App.RentalService"),
                     ("XML Par", "global::App.XMLParser")
                 })
        {
            using var page = await RunSearchAsync("--type=symbol", $"--query={query}", "--limit=1");
            Assert.Equal(first, Assert.Single(SymbolFqns(page)));
        }
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
    public async Task Search_RanksPropertyAboveItsAccessors()
    {
        CreateProject("Ranked", new Dictionary<string, string>
        {
            ["Holder.cs"] = """
                namespace Ranked;
                public class Holder
                {
                    public int SearchRankTarget { get; set; }
                }
                """
        });
        await RunFullIndexAsync(DbPath);

        using var doc = await RunSearchAsync("--type=symbol", "--query=SearchRank", "--limit=100");
        Assert.Equal(
            [
                "global::Ranked.Holder.SearchRankTarget",
                "global::Ranked.Holder.SearchRankTarget.get",
                "global::Ranked.Holder.SearchRankTarget.set"
            ],
            SymbolFqns(doc));
    }

    [Fact]
    public async Task Search_ExactCamelCaseNames_AreTierOne_AbovePrefixMatches()
    {
        CreateProject("Exact", new Dictionary<string, string>
        {
            ["Names.cs"] = """
                namespace Exact;
                public class AlphaBeta { }
                public class alphabetaHelper { }
                public class SearchSymbolStore { }
                public class SearchsymbolstoreFixture { }
                """
        });
        await RunFullIndexAsync(DbPath);

        using var alphaBeta = await RunSearchAsync("--type=symbol", "--query=AlphaBeta", "--limit=100");
        Assert.Equal(
            ["global::Exact.AlphaBeta", "global::Exact.alphabetaHelper"],
            SymbolFqns(alphaBeta));

        using var searchSymbolStore = await RunSearchAsync("--type=symbol", "--query=searchsymbolstore", "--limit=100");
        Assert.Equal(
            ["global::Exact.SearchSymbolStore", "global::Exact.SearchsymbolstoreFixture"],
            SymbolFqns(searchSymbolStore));
    }

    [Fact]
    public async Task Search_TierThreeOrdersAlphabetically_NotByContainerMatch()
    {
        CreateProject("Tiered", new Dictionary<string, string>
        {
            ["Names.cs"] = """
                namespace Anthology { public class Prealpha { } }
                namespace Containers.Alpha { public class Prealpha { } }
                """
        });
        await RunFullIndexAsync(DbPath);

        using var doc = await RunSearchAsync("--type=symbol", "--query=alpha", "--kind=Type", "--limit=100");
        Assert.Equal(
            ["global::Anthology.Prealpha", "global::Containers.Alpha.Prealpha"],
            SymbolFqns(doc));
    }

    [Fact]
    public async Task Search_PagesLosslesslyAcrossLimits_ForIdentifierAndNonIdentifierQueries()
    {
        CreateProject("Paged", new Dictionary<string, string>
        {
            ["Paged.cs"] = """
                namespace Paged;
                public class Alpha { public int AlphaValue { get; set; } }
                public class AlphaBeta { }
                public class AlphaBetaGamma { }
                public class Prealpha { }
                """
        });
        await RunFullIndexAsync(DbPath);

        foreach (var query in new[] { "Alpha", "Alpha Beta" })
        {
            using var all = await RunSearchAsync("--type=symbol", $"--query={query}", "--limit=100");
            var expected = SymbolFqns(all);
            Assert.NotEmpty(expected);

            foreach (var limit in new[] { 1, 2, 3, 4 })
            {
                var collected = new List<string>();
                string? cursor = null;
                do
                {
                    string[] args = ["--type=symbol", $"--query={query}", $"--limit={limit}",
                        .. (cursor == null ? Array.Empty<string>() : new[] { $"--cursor={cursor}" })];
                    using var page = await RunSearchAsync(args);
                    collected.AddRange(SymbolFqns(page));
                    Assert.True(collected.Count <= expected.Length);
                    cursor = page.RootElement.GetProperty("next_cursor").GetString();
                } while (cursor != null);

                Assert.Equal(expected, collected);
            }
        }
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
