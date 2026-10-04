using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Adds the evidence-document path to persisted annotations so the incremental
/// pipeline can retire copied-forward annotation rows for the documents it is
/// about to re-extract (the lockstep invariant). Null for user-authored
/// annotations, which are never path-scoped.
/// </summary>
public sealed class Migration_026_AnnotationDocumentPath : IMigration
{
    public int Version => 26;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var existingColumns = GetColumnNames(command, "annotations");

        if (!existingColumns.Contains("document_path"))
        {
            command.CommandText = "ALTER TABLE annotations ADD COLUMN document_path TEXT;";
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
