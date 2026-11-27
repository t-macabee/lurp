using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_055_SnapshotExcludedSymbols : IMigration
{
    public int Version => 55;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE projects ADD COLUMN excluded_documents_fingerprint TEXT;
            CREATE TABLE IF NOT EXISTS snapshot_excluded_symbols (
                snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id),
                project_name TEXT NOT NULL,
                symbol_id TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, project_name, symbol_id)
            ) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
    }
}
