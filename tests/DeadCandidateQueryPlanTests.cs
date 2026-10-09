using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Pins the query plans of the two hot reads in
///     <see cref="Lurp.Storage.DeadCandidateStore" />. The 4x profile regression was
///     caused by the planner's cost model (not C# code): with no sqlite_stat1 rows it
///     drove the declaration query from snapshot_documents and satisfied the incoming
///     edge ORDER BY via idx_edges_snapshot_id, scanning the full snapshot edge set per
///     chunk. The product writes statistics after each index run, so the test pins the
///     plans with them. It then drops sqlite_stat1 and pins the plans an older database
///     gets, which was indexed before the product wrote statistics. The SQL texts are the
///     store's own <see cref="Lurp.Storage.DeadCandidateStore.DeclarationSql" /> and
///     <see cref="Lurp.Storage.DeadCandidateStore.IncomingEdgesSql" />, so a change to a
///     store query fails this test instead of pinning the old text.
/// </summary>
public sealed class DeadCandidateQueryPlanTests : IntegrationTestBase
{
    private const int ProbeIdCount = 3;

    private static readonly string DeclarationQuerySql = Lurp.Storage.DeadCandidateStore.DeclarationSql(ProbeIdCount);

    private static readonly string IncomingEdgesQuerySql = Lurp.Storage.DeadCandidateStore.IncomingEdgesSql(Lurp.Storage.DeadCandidateLiveness.LiveEdgeKinds, ProbeIdCount);

    // B25: the type-use query is a second batched query with the same shape, one kind list
    // narrower. It is the same store text with TypeUseEdgeKinds.
    private static readonly string TypeUseEdgesQuerySql = Lurp.Storage.DeadCandidateStore.IncomingEdgesSql(Lurp.Storage.DeadCandidateLiveness.TypeUseEdgeKinds, ProbeIdCount);

    [SkippableFact]
    public async Task Plans_WithAndWithoutStatistics_DriveFromSymbolIds_AndUseSnapshotTargetIndex()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("QueryPlanProbe", new Dictionary<string, string>
        {
            ["Probe.cs"] = QueryPlanFixture.ChainedMethodsSource("QueryPlanProbe")
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var symbolIds = new[]
        {
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe"),
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe.A"),
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe.B")
        };

        AssertPlansWithAndWithoutStatistics(connection => AssertDeadCandidatePlans(connection, snapshotId, symbolIds));
    }

    private static void AssertDeadCandidatePlans(SqliteConnection connection, string snapshotId, string[] symbolIds)
    {
        var parameters = DeadCandidateParameters(snapshotId, symbolIds);

        // The declaration query must be driven by declarations (its own row set), never by
        // snapshot_documents probing the IN list per document row.
        var declarationPlan = QueryPlanFixture.Explain(connection, DeclarationQuerySql, parameters);
        Assert.NotEmpty(declarationPlan);
        Assert.DoesNotContain("snapshot_documents", declarationPlan[0]);
        Assert.Matches(@"^(SEARCH|SCAN) d ", declarationPlan[0]);

        // The incoming-edge query must probe idx_edges_snapshot_target, not scan the whole
        // snapshot edge set.
        var edgePlan = QueryPlanFixture.Explain(connection, IncomingEdgesQuerySql, parameters);
        Assert.Contains(edgePlan, detail => detail.Contains("idx_edges_snapshot_target", StringComparison.Ordinal));

        // The type-use query (B25) must use the same probe index, not a scan.
        var typeUsePlan = QueryPlanFixture.Explain(connection, TypeUseEdgesQuerySql, parameters);
        Assert.Contains(typeUsePlan, detail => detail.Contains("idx_edges_snapshot_target", StringComparison.Ordinal));
    }

    private static Dictionary<string, object> DeadCandidateParameters(string snapshotId, string[] symbolIds)
    {
        var parameters = new Dictionary<string, object> { ["@snapshotId"] = snapshotId };
        for (var i = 0; i < symbolIds.Length; i++)
            parameters[$"@p{i}"] = symbolIds[i];
        return parameters;
    }
}
