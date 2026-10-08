using System.Globalization;
using System.Text;
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
///     gets, which was indexed before the product wrote statistics. The SQL text mirrors
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
    public async Task Plans_WithAndWithoutStatistics_DriveFromSymbolIds_AndUseSnapshotTargetIndex()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("QueryPlanProbe", new Dictionary<string, string>
        {
            ["Probe.cs"] = BuildProbeSource()
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var symbolIds = new[]
        {
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe"),
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe.A"),
            ResolveSymbolId(snapshotId, "global::QueryPlanProbe.Probe.B")
        };

        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();

            using (var statCmd = connection.CreateCommand())
            {
                statCmd.CommandText = "SELECT COUNT(*) FROM sqlite_stat1;";
                Assert.True((long)statCmd.ExecuteScalar()! > 0);
            }

            AssertDeadCandidatePlans(connection, snapshotId, symbolIds);

            using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = "DROP TABLE sqlite_stat1;";
            dropCmd.ExecuteNonQuery();
        }

        // A new connection reads the schema again, so the planner loads the dropped statistics as absent.
        using var noStatsConnection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        noStatsConnection.Open();
        AssertDeadCandidatePlans(noStatsConnection, snapshotId, symbolIds);
    }

    private static void AssertDeadCandidatePlans(SqliteConnection connection, string snapshotId, string[] symbolIds)
    {
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

    // With statistics the planner scans tables that have a handful of rows, so the fixture
    // needs enough declarations and edges for the index plans to win; 200 chained methods
    // give the plans of a real database (probe 2026-10-08).
    private static string BuildProbeSource()
    {
        var sb = new StringBuilder();
        sb.AppendLine("namespace QueryPlanProbe;");
        sb.AppendLine();
        sb.AppendLine("internal class Probe");
        sb.AppendLine("{");
        sb.AppendLine("    internal void A() { }");
        sb.AppendLine("    internal void B() { }");
        sb.AppendLine("    internal void C() { }");
        sb.AppendLine("    internal void F0() { }");
        for (var i = 1; i < 200; i++)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    internal void F{i}() {{ F{i - 1}(); }}");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
