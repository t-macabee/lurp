using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     Pins the plans of the symbol-search phases: a tier 1-2 read and a non-identifier read
///     must reach <c>symbol_fts</c> only through its MATCH index or a rowid lookup, never a
///     full virtual-table scan. The SQL texts are the store's own
///     <see cref="SearchSymbolStore.Tier12Sql" /> and <see cref="SearchSymbolStore.NonIdentifierSql" />,
///     so a change to a store query fails this test instead of pinning the old text.
/// </summary>
public sealed class SymbolSearchQueryPlanTests : IntegrationTestBase
{
    [Fact]
    public async Task Tier12AndNonIdentifierSearches_AvoidAFullSymbolFtsScan()
    {
        CreateProject("SymbolPlanProbe", new Dictionary<string, string>
        {
            ["Probe.cs"] = QueryPlanFixture.ChainedMethodsSource("SymbolPlanProbe")
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        using var connection = OpenDbConnection();
        // The prepare step resolves the store's SQL functions, so register them here.
        _ = new SearchSymbolStore(connection);
        TextCaseMatching.Register(connection);

        var parameters = new Dictionary<string, object>
        {
            ["@snapshotId"] = snapshotId,
            ["@term"] = "Probe",
            ["@nameQuery"] = "name_tokens : (\"Probe\" OR \"Probe\"*)",
            ["@containerQuery"] = "container_tokens : \"Probe\"* OR fqn : \"Probe\"",
            ["@exactQuery"] = "simple_name : \"Probe\"",
            ["@limitPlusOne"] = 21L
        };

        var tier12 = QueryPlanFixture.Explain(connection,
            SearchSymbolStore.Tier12Sql(includeGenerated: false, hasKind: false, hasCursor: false),
            parameters);
        AssertNoFullFtsScan(tier12);
        Assert.Contains(tier12, detail => detail.Contains("VIRTUAL TABLE INDEX 0:M", StringComparison.Ordinal));

        var nonIdentifier = QueryPlanFixture.Explain(connection,
            SearchSymbolStore.NonIdentifierSql(includeGenerated: false, hasKind: false, hasCursor: false),
            parameters);
        AssertNoFullFtsScan(nonIdentifier);
        Assert.Contains(nonIdentifier, detail => detail.Contains("VIRTUAL TABLE INDEX 0:M", StringComparison.Ordinal));
    }

    private static void AssertNoFullFtsScan(List<string> plan) =>
        Assert.DoesNotContain(plan,
            detail => detail.TrimEnd().EndsWith("VIRTUAL TABLE INDEX 0:", StringComparison.Ordinal));
}
