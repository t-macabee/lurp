using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Lurp.Tests;

/// <summary>
///     Pins the query plans of the scoped semantic-diff reads (B47). Both take a JSON list;
///     CROSS JOIN keeps the list outer, so a change to the text that loses the pin fails
///     here (the B16 lesson).
/// </summary>
public sealed class SemanticDiffQueryPlanTests : IntegrationTestBase
{
    [SkippableFact]
    public async Task Plans_WithAndWithoutStatistics_AreDrivenByTheJsonList()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("SemanticDiffPlanProbe", new Dictionary<string, string>
        {
            ["Probe.cs"] = QueryPlanFixture.ChainedMethodsSource("SemanticDiffPlanProbe")
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        AssertPlansWithAndWithoutStatistics(connection => AssertSemanticDiffPlans(connection, snapshotId));
    }

    // A one-item list is enough: the pinned plans must hold for every list size.
    private static readonly string ListJson = JsonSerializer.Serialize(new[] { "x" });

    private static void AssertSemanticDiffPlans(SqliteConnection connection, string snapshotId)
    {
        var edgePlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.EdgeOperationsStore.EdgesTouchingSymbolsSql(),
            new Dictionary<string, object> { ["@snapshotId"] = snapshotId, ["@symbolIds"] = ListJson });
        Assert.DoesNotContain(edgePlan, detail => detail.Contains("idx_edges_snapshot_id", StringComparison.Ordinal));
        Assert.Contains(edgePlan, detail => detail.StartsWith("SCAN j ", StringComparison.Ordinal));

        var documentVersionPlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.DeclarationReadStore.DocumentVersionIdsBySymbolsSql(),
            new Dictionary<string, object> { ["@snapshotId"] = snapshotId, ["@symbolIds"] = ListJson });
        Assert.StartsWith("SCAN j ", documentVersionPlan[0], StringComparison.Ordinal);
        Assert.DoesNotContain(documentVersionPlan, detail => detail.Contains("ux_snapshot_documents (snapshot_id=?)", StringComparison.Ordinal));
    }
}
