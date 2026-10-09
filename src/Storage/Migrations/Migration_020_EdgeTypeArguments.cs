using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_020_EdgeTypeArguments : IMigration
    {
        public int Version => 20;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE edges ADD COLUMN type_arguments_json TEXT;";
            command.ExecuteNonQuery();
        }
    }
}
