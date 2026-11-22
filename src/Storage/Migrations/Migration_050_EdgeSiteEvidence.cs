using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>Stores evidence for each provenance of an edge site (D209).</summary>
public sealed class Migration_050_EdgeSiteEvidence : IMigration
{
    public int Version => 50;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE edge_sites_new (
                edge_id            INTEGER NOT NULL REFERENCES edges(edge_id) ON DELETE CASCADE,
                provenance         TEXT    NOT NULL,
                document_path      TEXT    NOT NULL,
                start_line         INTEGER NOT NULL,
                start_column       INTEGER NOT NULL,
                end_line           INTEGER NOT NULL,
                end_column         INTEGER NOT NULL,
                extractor_version  TEXT    NOT NULL,
                is_cross_generated INTEGER NOT NULL,
                PRIMARY KEY (edge_id, provenance, document_path, start_line, start_column, end_line, end_column)
            );

            INSERT INTO edge_sites_new (edge_id, provenance, document_path, start_line, start_column, end_line, end_column,
                                        extractor_version, is_cross_generated)
            SELECT s.edge_id, COALESCE(e.provenance, ''), s.document_path, s.start_line, s.start_column, s.end_line, s.end_column,
                   COALESCE(e.extractor_version, ''), COALESCE(e.is_cross_generated, 0)
            FROM edge_sites s LEFT JOIN edges e ON e.edge_id = s.edge_id;

            DROP TABLE edge_sites;
            ALTER TABLE edge_sites_new RENAME TO edge_sites;
            CREATE INDEX idx_edge_sites_document ON edge_sites(document_path);
            """;
        command.ExecuteNonQuery();
    }
}
