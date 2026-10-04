using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Stores each project's document membership per snapshot as a JSON array of
/// git-relative paths. Which project compiles which document is otherwise
/// recorded nowhere: a &lt;Compile Remove&gt; or a linked file changes only this
/// set. A null column means "unknown" (pre-032 snapshot) and is skipped by the
/// membership comparator.
/// </summary>
public sealed class Migration_032_ProjectDocumentPaths : IMigration
{
    public int Version => 32;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        var existingColumns = GetColumnNames(command, "projects");

        if (!existingColumns.Contains("document_paths_json"))
        {
            command.CommandText = "ALTER TABLE projects ADD COLUMN document_paths_json TEXT;";
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
