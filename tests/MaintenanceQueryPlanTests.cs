using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Lurp.Tests;

/// <summary>
///     Pins the query plans of the maintenance statements that take a JSON list (B16 U1b,
///     B50). With a list the planner has no row estimate, and without the pins it reads the
///     whole snapshot through the snapshot_id index. The SQL texts are the stores' own, so a
///     change to a store query fails this test instead of pinning the old text.
/// </summary>
public sealed class MaintenanceQueryPlanTests : IntegrationTestBase
{
    [SkippableFact]
    public async Task Plans_WithAndWithoutStatistics_AreDrivenByTheJsonList()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("MaintenancePlanProbe", new Dictionary<string, string>
        {
            ["Probe.cs"] = QueryPlanFixture.ChainedMethodsSource("MaintenancePlanProbe")
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        AssertPlansWithAndWithoutStatistics(connection => AssertMaintenancePlans(connection, snapshotId));
    }

    // A one-item list is enough: the pinned plans must hold for every list size.
    private static readonly string ListJson = JsonSerializer.Serialize(new[] { "x" });

    private static void AssertMaintenancePlans(SqliteConnection connection, string snapshotId)
    {
        var symbolIdPlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.DeclarationMaintenanceStore.SymbolIdsByDocumentVersionIdsSql(),
            new Dictionary<string, object> { ["@snapshotId"] = snapshotId, ["@documentVersionIds"] = ListJson });
        // On this small fixture a scan of the outer table can be cheapest; the pins guarantee only that the list drives the query and the snapshot index is not read. U1c measured the probe plans at 16x.
        Assert.Matches(@"^(SEARCH|SCAN) d ", symbolIdPlan[0]);
        Assert.DoesNotContain(symbolIdPlan, detail => detail.Contains("snapshot_symbols USING COVERING INDEX sqlite_autoindex_snapshot_symbols_1 (snapshot_id=?)", StringComparison.Ordinal));

        var edgePlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.EdgeOperationsStore.DeleteEdgesByDocumentPathsSql(),
            new Dictionary<string, object> { ["@snapshotId"] = snapshotId, ["@documentPaths"] = ListJson });
        Assert.DoesNotContain(edgePlan, detail => detail.Contains("idx_edges_snapshot_id", StringComparison.Ordinal));

        var diagnosticsPlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.DiagnosticStore.DeleteDiagnosticsByProjectNamesSql(),
            new Dictionary<string, object> { ["@snapshotId"] = snapshotId, ["@projectNames"] = ListJson });
        Assert.DoesNotContain(diagnosticsPlan, detail => detail.Contains("idx_diagnostics_snapshot_id", StringComparison.Ordinal));
    }
}
