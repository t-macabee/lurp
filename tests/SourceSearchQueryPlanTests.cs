using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Pins the plan of the generated-document filter that source search and grep append (B38).
///     The filter must use the covering index idx_declarations_doc_version_generated with and
///     without planner statistics, so the plan does not depend on the statistics. The SQL texts
///     are the stores' own <see cref="Lurp.Storage.SearchSourceStore.FtsMatchSql" />,
///     <see cref="Lurp.Storage.SearchSourceStore.FallbackSql" /> and
///     <see cref="Lurp.Storage.TextSearchStore.CandidateSql" />, so a change to a store query
///     fails this test instead of pinning the old text.
/// </summary>
public sealed class SourceSearchQueryPlanTests : IntegrationTestBase
{
    // The matches CTE body of SearchSourceStore.SearchSource.
    private static readonly string FtsMatchQuerySql = Lurp.Storage.SearchSourceStore.FtsMatchSql(includeGenerated: false);

    // The fallback query of SearchSourceStore.SearchSource.
    private static readonly string FallbackQuerySql = Lurp.Storage.SearchSourceStore.FallbackSql(includeGenerated: false);

    // The candidate query of TextSearchStore.SearchTextPage, case-sensitive.
    private static readonly string GrepCandidateQuerySql = Lurp.Storage.TextSearchStore.CandidateSql(includeGenerated: false, ignoreCase: false);

    [SkippableFact]
    public async Task Plans_WithAndWithoutStatistics_UseCoveringGeneratedFilterIndex()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("SearchPlanProbe", new Dictionary<string, string>
        {
            ["Probe.cs"] = QueryPlanFixture.ChainedMethodsSource("SearchPlanProbe")
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        AssertPlansWithAndWithoutStatistics(connection => AssertGeneratedFilterPlans(connection, snapshotId));
    }

    private static void AssertGeneratedFilterPlans(SqliteConnection connection, string snapshotId)
    {
        const string covering = "COVERING INDEX idx_declarations_doc_version_generated (document_version_id=? AND is_generated=?)";

        var ftsPlan = QueryPlanFixture.Explain(connection, FtsMatchQuerySql, new Dictionary<string, object>
        {
            ["@query"] = "Probe",
            ["@snapshotId"] = snapshotId
        });
        Assert.Contains(ftsPlan, detail => detail.Contains(covering, StringComparison.Ordinal));

        var fallbackPlan = QueryPlanFixture.Explain(connection, FallbackQuerySql, new Dictionary<string, object>
        {
            ["@snapshotId"] = snapshotId,
            ["@likePattern"] = "%Probe%",
            ["@remaining"] = 20
        });
        Assert.Contains(fallbackPlan, detail => detail.Contains(covering, StringComparison.Ordinal));

        var grepPlan = QueryPlanFixture.Explain(connection, GrepCandidateQuerySql, new Dictionary<string, object>
        {
            ["@snapshotId"] = snapshotId,
            ["@query"] = "Probe"
        });
        Assert.Contains(grepPlan, detail => detail.Contains(covering, StringComparison.Ordinal));
    }
}
