using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Adds <c>projects.project_path</c> (D178): the project file path relative to the
///     git root with forward slashes, NULL when unavailable or for rows from before
///     migration 046. No backfill.
/// </summary>
public sealed class Migration_046_ProjectFilePath : IMigration
{
    public int Version => 46;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var existingColumns = GetColumnNames(command, "projects");
        if (!existingColumns.Contains("project_path"))
        {
            command.CommandText = "ALTER TABLE projects ADD COLUMN project_path TEXT;";
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
