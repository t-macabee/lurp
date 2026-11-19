using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Adds <c>projects.internals_visible_to</c> (D48, D171): JSON array of sorted,
///     distinct <c>InternalsVisibleTo</c> target simple names (or <c>*</c> when a target
///     cannot be named from syntax), NULL when the project grants none. No backfill:
///     existing rows read as NULL (no target).
/// </summary>
public sealed class Migration_045_ProjectInternalsVisibleTo : IMigration
{
    public int Version => 45;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var existingColumns = GetColumnNames(command, "projects");
        if (!existingColumns.Contains("internals_visible_to"))
        {
            command.CommandText = "ALTER TABLE projects ADD COLUMN internals_visible_to TEXT;";
            command.ExecuteNonQuery();
        }
    }

    private static HashSet<string> GetColumnNames(SqliteCommand command, string tableName)
    {
        command.CommandText = $"PRAGMA table_info({tableName});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
