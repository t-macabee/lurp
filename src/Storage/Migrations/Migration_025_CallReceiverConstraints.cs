using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_025_CallReceiverConstraints : IMigration
{
    public int Version => 25;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE edges ADD COLUMN receiver_type_constraints_json TEXT;";
        command.ExecuteNonQuery();
    }
}
