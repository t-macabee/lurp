using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Adds <c>edge_sites</c> (D19, D155.1, D155.9) so each edge keeps every
///     source site in source order, and backfills one site for each located edge.
/// </summary>
public sealed class Migration_040_EdgeSites : IMigration
{
    public int Version => 40;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE edge_sites (
                edge_id       INTEGER NOT NULL REFERENCES edges(edge_id) ON DELETE CASCADE,
                document_path TEXT    NOT NULL,
                start_line    INTEGER NOT NULL,
                start_column  INTEGER NOT NULL,
                end_line      INTEGER NOT NULL,
                end_column    INTEGER NOT NULL,
                PRIMARY KEY (edge_id, document_path, start_line, start_column, end_line, end_column)
            );

            INSERT INTO edge_sites (edge_id, document_path, start_line, start_column, end_line, end_column)
            SELECT edge_id, source_document_path, source_start_line, source_start_column, source_end_line, source_end_column
            FROM edges
            WHERE source_document_path IS NOT NULL
              AND source_start_line IS NOT NULL
              AND source_start_column IS NOT NULL
              AND source_end_line IS NOT NULL
              AND source_end_column IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }
}
