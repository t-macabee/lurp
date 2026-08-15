using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public interface IBindingIncompletenessStore
{
    void SaveBindingIncompleteness(string snapshotId, IEnumerable<BindingIncompletenessRecord> records);
    List<BindingIncompletenessRecord> GetBindingIncompleteness(string snapshotId, string? projectName = null);
    void CopyBindingIncompleteness(string fromSnapshotId, string toSnapshotId);
    void DeleteBindingIncompletenessByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths);
}

internal sealed class BindingIncompletenessStore(SqliteConnection connection) : IBindingIncompletenessStore
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public void SaveBindingIncompleteness(string snapshotId, IEnumerable<BindingIncompletenessRecord> records)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO binding_incompleteness
                (snapshot_id, project_name, document_path, reason, occurrence_count, extractor_version)
            VALUES (@snapshotId, @projectName, @documentPath, @reason, @count, @extractorVersion)
            ON CONFLICT(snapshot_id, project_name, document_path, reason)
            DO UPDATE SET occurrence_count = excluded.occurrence_count,
                          extractor_version = excluded.extractor_version;
        ";

        foreach (var record in records)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@projectName", record.ProjectName);
            command.Parameters.AddWithValue("@documentPath", record.DocumentPath ?? "");
            command.Parameters.AddWithValue("@reason", record.Reason);
            command.Parameters.AddWithValue("@count", record.Count);
            command.Parameters.AddWithValue("@extractorVersion", record.ExtractorVersion);
            command.ExecuteNonQuery();
        }
    }

    public List<BindingIncompletenessRecord> GetBindingIncompleteness(string snapshotId, string? projectName = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = projectName == null
            ? @"SELECT project_name, NULLIF(document_path, ''), reason, occurrence_count, extractor_version
                FROM binding_incompleteness WHERE snapshot_id = @snapshotId
                ORDER BY project_name, document_path, reason;"
            : @"SELECT project_name, NULLIF(document_path, ''), reason, occurrence_count, extractor_version
                FROM binding_incompleteness WHERE snapshot_id = @snapshotId AND project_name = @projectName
                ORDER BY document_path, reason;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        if (projectName != null)
            command.Parameters.AddWithValue("@projectName", projectName);

        var result = new List<BindingIncompletenessRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new BindingIncompletenessRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4)));
        return result;
    }

    public void CopyBindingIncompleteness(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO binding_incompleteness
                (snapshot_id, project_name, document_path, reason, occurrence_count, extractor_version)
            SELECT @toSnapshotId, project_name, document_path, reason, occurrence_count, extractor_version
            FROM binding_incompleteness WHERE snapshot_id = @fromSnapshotId;
        ";
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    public void DeleteBindingIncompletenessByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM binding_incompleteness WHERE snapshot_id = @snapshotId AND document_path = @path;";
        foreach (var path in documentPaths.Distinct(StringComparer.Ordinal))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@path", path);
            command.ExecuteNonQuery();
        }
    }
}