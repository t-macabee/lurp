using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DiagnosticStore
{
    private readonly SqliteConnection _connection;

    public DiagnosticStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var diag in diagnostics)
            {
                command.CommandText = @"
                    INSERT INTO diagnostics (snapshot_id, project_name, document_path, severity, id, message,start_line, start_column, end_line, end_column)
                    VALUES (@snapshotId, @projectName, @documentPath, @severity, @id, @message,@startLine, @startColumn, @endLine, @endColumn);
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@projectName", diag.ProjectName);
                command.Parameters.AddWithValue("@documentPath", (object?)diag.DocumentPath ?? DBNull.Value);
                command.Parameters.AddWithValue("@severity", diag.Severity);
                command.Parameters.AddWithValue("@id", diag.Id);
                command.Parameters.AddWithValue("@message", diag.Message);
                command.Parameters.AddWithValue("@startLine", (object?)diag.StartLine ?? DBNull.Value);
                command.Parameters.AddWithValue("@startColumn", (object?)diag.StartColumn ?? DBNull.Value);
                command.Parameters.AddWithValue("@endLine", (object?)diag.EndLine ?? DBNull.Value);
                command.Parameters.AddWithValue("@endColumn", (object?)diag.EndColumn ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null)
    {
        using var command = _connection.CreateCommand();
        if (projectName != null)
        {
            command.CommandText = @"
                SELECT project_name, document_path, severity, id, message,
                       start_line, start_column, end_line, end_column
                FROM diagnostics
                WHERE snapshot_id = @snapshotId AND project_name = @projectName
                ORDER BY diagnostic_id;
            ";
            command.Parameters.AddWithValue("@projectName", projectName);
        }
        else
        {
            command.CommandText = @"
                SELECT project_name, document_path, severity, id, message,
                       start_line, start_column, end_line, end_column
                FROM diagnostics
                WHERE snapshot_id = @snapshotId
                ORDER BY diagnostic_id;
            ";
        }

        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var results = new List<DiagnosticRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new DiagnosticRecord
            {
                ProjectName = reader.GetString(0),
                DocumentPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                Severity = reader.GetString(2),
                Id = reader.GetString(3),
                Message = reader.GetString(4),
                StartLine = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                StartColumn = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                EndLine = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                EndColumn = reader.IsDBNull(8) ? null : reader.GetInt32(8)
            });
        return results;
    }

    public int CountDiagnostics(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM diagnostics WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO diagnostics (snapshot_id, project_name, document_path, severity, id, message,start_line, start_column, end_line, end_column)
            SELECT @toSnapshotId, project_name, document_path, severity, id, message,
                   start_line, start_column, end_line, end_column
            FROM diagnostics
            WHERE snapshot_id = @fromSnapshotId;
        ";
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    public void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames)
    {
        var nameList = projectNames as IReadOnlyCollection<string> ?? projectNames.ToList();
        if (nameList.Count == 0)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM diagnostics
            WHERE snapshot_id = @snapshotId
              AND project_name IN (" + string.Join(", ", nameList.Select((_, i) => $"@p{i}")) + @");
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var i = 0;
        foreach (var name in nameList)
            command.Parameters.AddWithValue($"@p{i++}", name);
        command.ExecuteNonQuery();
    }
}