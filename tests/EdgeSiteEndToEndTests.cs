using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class EdgeSiteEndToEndTests : IntegrationTestBase
{
    [Fact]
    public async Task TwoSitesReachTheIndex()
    {
        // Line 0: namespace N;
        // Line 1: public class C
        // Line 2: {
        // Line 3:     public void Target() { }
        // Line 4:     public void Twice()
        // Line 5:     {
        // Line 6:         Target();
        // Line 7:         Target();
        // Line 8:     }
        // Line 9: }
        // Both Target() expressions start at column 8 (8 spaces) and span 8 chars (to column 16).
        var source = """
            namespace N;
            public class C
            {
                public void Target() { }
                public void Twice()
                {
                    Target();
                    Target();
                }
            }
            """;

        var snapshotId = await IndexFullAsync("App", new Dictionary<string, string>
        {
            ["C.cs"] = source
        });

        var twiceId = ResolveSymbolId(snapshotId, "global::N.C.Twice");
        var targetId = ResolveSymbolId(snapshotId, "global::N.C.Target");

        using var conn = OpenDbConnection();
        using var edgeCmd = conn.CreateCommand();
        edgeCmd.CommandText = """
            SELECT edge_id, source_start_line
            FROM edges
            WHERE snapshot_id = @s
              AND source_symbol_id = @src
              AND target_symbol_id = @tgt
              AND kind = 'Calls';
            """;
        edgeCmd.Parameters.AddWithValue("@s", snapshotId);
        edgeCmd.Parameters.AddWithValue("@src", twiceId);
        edgeCmd.Parameters.AddWithValue("@tgt", targetId);

        long edgeId;
        int? primaryLine;
        using (var reader = edgeCmd.ExecuteReader())
        {
            Assert.True(reader.Read());
            edgeId = reader.GetInt64(0);
            primaryLine = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            Assert.False(reader.Read());
        }

        Assert.Equal(6, primaryLine);

        using var siteCmd = conn.CreateCommand();
        siteCmd.CommandText = """
            SELECT start_line, start_column, end_line, end_column
            FROM edge_sites
            WHERE edge_id = @edgeId
            ORDER BY document_path, start_line, start_column, end_line, end_column;
            """;
        siteCmd.Parameters.AddWithValue("@edgeId", edgeId);

        var sites = new List<(int Sl, int Sc, int El, int Ec)>();
        using (var reader = siteCmd.ExecuteReader())
        {
            while (reader.Read())
                sites.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        Assert.Equal(
            [
                (6, 8, 6, 16),
                (7, 8, 7, 16)
            ],
            sites);
    }
}
