using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Makes declarations snapshot facts (D44, D154) by adding <c>snapshot_declarations</c>
///     so each snapshot links only to the declaration rows produced for its compilation.
/// </summary>
public sealed class Migration_038_SnapshotDeclarations : IMigration
{
    public int Version => 38;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE snapshot_declarations (
                snapshot_id    TEXT    NOT NULL REFERENCES snapshots(snapshot_id),
                declaration_id INTEGER NOT NULL REFERENCES declarations(declaration_id),
                PRIMARY KEY (snapshot_id, declaration_id)
            );

            CREATE INDEX idx_snapshot_declarations_declaration_id
                ON snapshot_declarations(declaration_id);

            INSERT INTO snapshot_declarations (snapshot_id, declaration_id)
            SELECT sd.snapshot_id, d.declaration_id
            FROM snapshot_documents sd
            JOIN declarations d ON d.document_version_id = sd.document_version_id
            JOIN snapshot_symbols ss ON ss.snapshot_id = sd.snapshot_id AND ss.symbol_id = d.symbol_id;
            """;
        command.ExecuteNonQuery();
    }
}
