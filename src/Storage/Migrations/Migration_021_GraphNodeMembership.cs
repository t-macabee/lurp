using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_021_GraphNodeMembership : IMigration
    {
        public int Version => 21;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                CREATE TABLE graph_nodes (
                    node_id TEXT PRIMARY KEY,
                    node_kind TEXT NOT NULL
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE snapshot_graph_nodes (
                    snapshot_id TEXT NOT NULL,
                    node_id TEXT NOT NULL,
                    PRIMARY KEY (snapshot_id, node_id)
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE INDEX idx_snapshot_graph_nodes_node_id
                ON snapshot_graph_nodes (node_id);
            ";
            command.ExecuteNonQuery();
        }
    }
}
