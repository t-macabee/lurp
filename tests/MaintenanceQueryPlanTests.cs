using Lurp.Storage;
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
    [Fact]
    public async Task Plans_WithAndWithoutStatistics_AreDrivenByTheJsonList()
    {
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

        var siteDeletePlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.EdgeOperationsStore.DeleteSitesByDocumentPathsSql,
            new Dictionary<string, object> { ["@snapshotId"] = snapshotId, ["@documentPaths"] = ListJson });
        Assert.Contains(siteDeletePlan, detail => detail.Contains("idx_edge_sites_document", StringComparison.Ordinal));
        Assert.DoesNotContain(siteDeletePlan, detail => detail.StartsWith("SCAN edge_sites", StringComparison.Ordinal));

        var edgeDeletePlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.EdgeOperationsStore.DeleteAffectedEdgesWithoutSitesSql,
            new Dictionary<string, object> { ["@edgeIds"] = "[1]" });
        Assert.Contains(edgeDeletePlan, detail => detail.StartsWith("SEARCH edges USING INTEGER PRIMARY KEY", StringComparison.Ordinal));

        _ = new EdgeOperationsStore(connection);
        var projectionPlan = QueryPlanFixture.Explain(connection, EdgeOperationsStore.ProjectEvidenceSql,
            new Dictionary<string, object> { ["@edgeIds"] = "[1]", ["@keepUnlocated"] = false, ["@locatedOnly"] = true });
        Assert.Contains(projectionPlan, detail =>
            detail.StartsWith("SEARCH edge_sites USING", StringComparison.Ordinal) &&
            detail.Contains("(edge_id=?)", StringComparison.Ordinal));
        Assert.DoesNotContain(projectionPlan, detail => detail.StartsWith("SCAN edge_sites", StringComparison.Ordinal));

        var diagnosticsPlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.DiagnosticStore.DeleteDiagnosticsByProjectNamesSql(),
            new Dictionary<string, object> { ["@snapshotId"] = snapshotId, ["@projectNames"] = ListJson });
        Assert.DoesNotContain(diagnosticsPlan, detail => detail.Contains("idx_diagnostics_snapshot_id", StringComparison.Ordinal));

        var bindingPlan = QueryPlanFixture.Explain(
            connection,
            Lurp.Storage.BindingIncompletenessStore.DeleteByProjectAndDocumentPathsSql(),
            new Dictionary<string, object> { ["@snapshotId"] = snapshotId, ["@projectName"] = "MaintenancePlanProbe", ["@documentPaths"] = ListJson });
        Assert.DoesNotContain(bindingPlan, detail => detail.Contains("idx_binding_incompleteness_snapshot_reason", StringComparison.Ordinal));
    }
}
