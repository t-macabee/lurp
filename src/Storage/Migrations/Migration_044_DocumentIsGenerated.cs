using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Adds <c>document_versions.is_generated</c> (D4, D50, D165.2, D167.7),
///     backfills existing rows from generated declarations and the D4 file-name
///     rule, scopes <c>trg_document_versions_immutable</c> to the immutable
///     content/identity columns so rule updates can refresh <c>is_generated</c>,
///     drops <c>idx_declarations_doc_version_generated</c>, and replaces it with
///     the covering index <c>idx_document_versions_generated</c>.
/// </summary>
public sealed class Migration_044_DocumentIsGenerated : IMigration
{
    public int Version => 44;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        var existingColumns = MigrationSchema.ColumnNames(command, "document_versions");
        if (!existingColumns.Contains("is_generated"))
        {
            command.CommandText = "ALTER TABLE document_versions ADD COLUMN is_generated INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
        }

        command.CommandText = """
            DROP TRIGGER IF EXISTS trg_document_versions_immutable;

            UPDATE document_versions
            SET is_generated = 1
            WHERE EXISTS (
                SELECT 1
                FROM declarations dec
                WHERE dec.document_version_id = document_versions.document_version_id
                  AND dec.is_generated = 1
            )
            OR EXISTS (
                SELECT 1
                FROM documents d
                WHERE d.document_id = document_versions.document_id
                  AND (
                      d.relative_path LIKE 'TemporaryGeneratedFile\_%' ESCAPE '\'
                      OR d.relative_path LIKE '%/TemporaryGeneratedFile\_%' ESCAPE '\'
                      OR d.relative_path LIKE '%.designer.cs'
                      OR d.relative_path LIKE '%.generated.cs'
                      OR d.relative_path LIKE '%.g.cs'
                      OR d.relative_path LIKE '%.g.i.cs'
                      OR d.relative_path LIKE '%ModelSnapshot.cs'
                  )
            );

            CREATE TRIGGER trg_document_versions_immutable
            BEFORE UPDATE OF document_version_id, document_id, content_hash, content, encoding, byte_count, line_starts ON document_versions
            BEGIN
                SELECT RAISE(ABORT, 'document_versions is immutable');
            END;

            DROP INDEX IF EXISTS idx_declarations_doc_version_generated;

            CREATE UNIQUE INDEX IF NOT EXISTS idx_document_versions_generated
            ON document_versions(document_version_id, is_generated);
            """;
        command.ExecuteNonQuery();
    }
}
