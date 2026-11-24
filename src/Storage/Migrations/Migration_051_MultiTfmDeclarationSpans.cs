using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_051_MultiTfmDeclarationSpans : IMigration
{
    public int Version => 51;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // Negative offsets are invalid. Use a sentinel so spanless declarations also deduplicate.
        command.CommandText = """
            DROP INDEX idx_declarations_unique;
            CREATE UNIQUE INDEX idx_declarations_unique
            ON declarations(symbol_id, document_version_id, COALESCE(full_start, -1), COALESCE(full_end, -1));
            """;
        command.ExecuteNonQuery();
    }
}
