using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Pins the no-statistics query plans of the two hot reads in
///     <see cref="Lurp.Storage.DeadCandidateStore" />. The 4x profile regression was
///     caused by the planner's cost model (not C# code): with no sqlite_stat1 rows it
///     drove the declaration query from snapshot_documents and satisfied the incoming
///     edge ORDER BY via idx_edges_snapshot_id, scanning the full snapshot edge set per
///     chunk. These tests run on a freshly indexed test database, which never has
///     sqlite_stat1 because the product never runs ANALYZE. The SQL text mirrors
///     DeadCandidateStore.FetchDeclarationInfo / FetchIncomingLiveEdgesBatched — keep it
///     in sync when those change.
/// </summary>
public sealed class DeadCandidateQueryPlanTests : IntegrationTestBase
{
    private const string DeclarationQuerySql = """
        SELECT d.symbol_id, d.document_version_id, d.full_start, d.full_end, COALESCE(d.is_generated,0), d.is_partial
        FROM declarations d
        CROSS JOIN snapshot_documents sd
        WHERE sd.snapshot_id = @snapshotId
          AND sd.document_version_id = d.document_version_id
          AND d.symbol_id IN (@p0, @p1, @p2);
        """;

    private static readonly string IncomingEdgesQuerySql =
        "SELECT edge_id, source_symbol_id, target_symbol_id, kind, provenance, snapshot_id, extractor_version, source_document_path, source_start_line, source_start_column, source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json\n" +
        "FROM edges\n" +
        "WHERE snapshot_id = @snapshotId\n" +
        "  AND target_symbol_id IN (@p0, @p1, @p2)\n" +
        "  AND kind IN (" + string.Join(",", Lurp.Storage.DeadCandidateLiveness.LiveEdgeKinds.Select(k => $"'{k}'")) + ");\n";

    // B25: the type-use query is a second batched query with the same shape, one kind list
    // narrower. Mirrors DeadCandidateStore.FetchIncomingEdgesBatched with TypeUseEdgeKinds.
    private static readonly string TypeUseEdgesQuerySql =
        "SELECT edge_id, source_symbol_id, target_symbol_id, kind, provenance, snapshot_id, extractor_version, source_document_path, source_start_line, source_start_column, source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json\n" +
        "FROM edges\n" +
        "WHERE snapshot_id = @snapshotId\n" +
        "  AND target_symbol_id IN (@p0, @p1, @p2)\n" +
        "  AND kind IN (" + string.Join(",", Lurp.Storage.DeadCandidateLiveness.TypeUseEdgeKinds.Select(k => $"'{k}'")) + ");\n";

    [SkippableFact]
    public async Task Plans_WithoutStatistics_DriveFromSymbolIds_AndUseSnapshotTargetIndex()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("QueryPlanProbe", new Dictionary<string, string>
        {
            ["Probe.cs"] = """
                           namespace QueryPlanProbe;

                           internal class Probe
                           {
                               internal void A() { }
                               internal void B() { }
                               internal void C() { }
                           }
                           """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var symbolIds = new[]
        {
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe"),
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe.A"),
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe.B")
        };

        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();

        using (var statCmd = connection.CreateCommand())
        {
            statCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sqlite_stat1';";
            Assert.Equal(0L, (long)statCmd.ExecuteScalar()!);
        }

        // The declaration query must be driven by declarations (its own row set), never by
        // snapshot_documents probing the IN list per document row.
        var declarationPlan = Explain(connection, DeclarationQuerySql, snapshotId, symbolIds);
        Assert.NotEmpty(declarationPlan);
        Assert.DoesNotContain("snapshot_documents", declarationPlan[0]);
        Assert.Matches(@"^(SEARCH|SCAN) d ", declarationPlan[0]);

        // The incoming-edge query must probe idx_edges_snapshot_target, not scan the whole
        // snapshot edge set.
        var edgePlan = Explain(connection, IncomingEdgesQuerySql, snapshotId, symbolIds);
        Assert.Contains(edgePlan, detail => detail.Contains("idx_edges_snapshot_target", StringComparison.Ordinal));

        // The type-use query (B25) must use the same probe index, not a scan.
        var typeUsePlan = Explain(connection, TypeUseEdgesQuerySql, snapshotId, symbolIds);
        Assert.Contains(typeUsePlan, detail => detail.Contains("idx_edges_snapshot_target", StringComparison.Ordinal));
    }

    private static List<string> Explain(SqliteConnection connection, string sql, string snapshotId, string[] symbolIds)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        for (var i = 0; i < symbolIds.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", symbolIds[i]);
        var details = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return details;
    }
}
