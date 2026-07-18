using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_006_ExpandEdges : IMigration
    {
        public int Version => 6;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = "ALTER TABLE edges ADD COLUMN extractor_version TEXT;";
            command.ExecuteNonQuery();

            command.CommandText = "ALTER TABLE edges ADD COLUMN source_document_path TEXT;";
            command.ExecuteNonQuery();

            command.CommandText = "ALTER TABLE edges ADD COLUMN source_start_line INTEGER;";
            command.ExecuteNonQuery();

            command.CommandText = "ALTER TABLE edges ADD COLUMN source_start_column INTEGER;";
            command.ExecuteNonQuery();

            command.CommandText = "ALTER TABLE edges ADD COLUMN source_end_line INTEGER;";
            command.ExecuteNonQuery();

            command.CommandText = "ALTER TABLE edges ADD COLUMN source_end_column INTEGER;";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS extractors (extractor_id INTEGER PRIMARY KEY AUTOINCREMENT,name         TEXT NOT NULL,version      TEXT NOT NULL,description  TEXT);
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_edges_document ON edges(source_document_path);";
            command.ExecuteNonQuery();
        }
    }
}
