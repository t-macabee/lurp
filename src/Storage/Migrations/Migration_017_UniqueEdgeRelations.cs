using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_017_UniqueEdgeRelations : IMigration
    {
        public int Version => 17;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE UNIQUE INDEX IF NOT EXISTS ux_edges_relation
                ON edges(snapshot_id, source_symbol_id, target_symbol_id, kind);
            ";
            command.ExecuteNonQuery();
        }
    }
}
