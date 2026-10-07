using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Stores each project's compiled assembly name (<c>Project.AssemblyName</c>) per
/// snapshot. The assembly name is the identity a candidate's <c>assembly_identity</c>
/// carries; a binding-incompleteness record's <c>project_name</c> carries the Roslyn
/// project name, and readers map it to the assembly name through this column. The
/// assembly name differs from the project name whenever the project file sets
/// <c>AssemblyName</c>. A null column means "unknown" (pre-034 snapshot): readers
/// fall back to <c>projects.name</c>, which was the only identity available then.
/// </summary>
public sealed class Migration_034_ProjectAssemblyName : IMigration
{
    public int Version => 34;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        var existingColumns = GetColumnNames(command, "projects");

        if (!existingColumns.Contains("assembly_name"))
        {
            command.CommandText = "ALTER TABLE projects ADD COLUMN assembly_name TEXT;";
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
