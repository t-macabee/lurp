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
                command.CommandText = """
                    INSERT INTO diagnostics (snapshot_id, project_name, document_path, severity, id, message,start_line, start_column, end_line, end_column)
                    VALUES (@snapshotId, @projectName, @documentPath, @severity, @id, @message,@startLine, @startColumn, @endLine, @endColumn);
                    """;
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
            command.CommandText = """
                SELECT project_name, document_path, severity, id, message,
                       start_line, start_column, end_line, end_column
                FROM diagnostics
                WHERE snapshot_id = @snapshotId AND project_name = @projectName
                ORDER BY diagnostic_id;
                """;
            command.Parameters.AddWithValue("@projectName", projectName);
        }
        else
        {
            command.CommandText = """
                SELECT project_name, document_path, severity, id, message,
                       start_line, start_column, end_line, end_column
                FROM diagnostics
                WHERE snapshot_id = @snapshotId
                ORDER BY diagnostic_id;
                """;
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

    /// <summary>
    ///     Paged, keyset-based read over diagnostics. Ordered by <c>diagnostic_id</c>.
    ///     Supports pushdown filtering by project, severity, and id in SQL.
    ///     Document-path filtering and <c>in_snapshot</c> computation happen in memory
    ///     after normalization — the stored <c>document_path</c> column holds the raw
    ///     Roslyn absolute path today, so the document filter cannot push into SQL
    ///     until the column itself is normalized (option (a), deferred).
    /// </summary>
    public DiagnosticsPage GetDiagnosticsPage(
        string snapshotId,
        string? projectName,
        string? documentPath,
        string? severity,
        bool excludeHidden,
        string? id,
        int limit,
        DiagnosticsCursor? cursor,
        string? gitRoot,
        HashSet<string> snapshotDocumentPaths)
    {
        if (string.IsNullOrEmpty(snapshotId))
            throw new ArgumentException("snapshotId is required.", nameof(snapshotId));
        if (limit <= 0)
            throw new ArgumentException("--limit must be a positive integer.", nameof(limit));

        limit = Math.Max(1, limit);
        var fingerprint = DiagnosticsCursor.ComputeFingerprint(projectName, documentPath, severity, excludeHidden, id);
        if (cursor != null)
        {
            try
            {
                cursor.Validate(snapshotId, fingerprint);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(ex.Message, ex);
            }
        }

        // SQL query — push down project, severity (or excludeHidden), and id.
        // Document and in_snapshot are computed post-read.
        using var cmd = _connection.CreateCommand();
        var where = "WHERE snapshot_id = @snapshotId";
        if (projectName != null)
            where += " AND project_name = @projectName";

        if (severity != null)
        {
            where += " AND severity = @severity COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@severity", severity);
        }
        else if (excludeHidden)
        {
            where += " AND severity != 'Hidden' COLLATE NOCASE";
        }

        if (id != null)
            where += " AND id = @id";

        cmd.CommandText = $"""
            SELECT diagnostic_id, project_name, document_path, severity, id, message,
                   start_line, start_column, end_line, end_column
            FROM diagnostics
            {where}
            ORDER BY diagnostic_id;
            """;
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        if (projectName != null) cmd.Parameters.AddWithValue("@projectName", projectName);
        if (id != null) cmd.Parameters.AddWithValue("@id", id);

        // Read all SQL-filtered rows, normalize paths, compute in_snapshot.
        var all = new List<(long Id, DiagnosticEntry Entry)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var diagId = reader.GetInt64(0);
                var raw = new DiagnosticRecord
                {
                    ProjectName = reader.GetString(1),
                    DocumentPath = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Severity = reader.GetString(3),
                    Id = reader.GetString(4),
                    Message = reader.GetString(5),
                    StartLine = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    StartColumn = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    EndLine = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    EndColumn = reader.IsDBNull(9) ? null : reader.GetInt32(9)
                };

                string? normalized = raw.DocumentPath;
                bool? inSnapshot;

                if (raw.DocumentPath == null)
                {
                    // No source location (compilation-wide errors) — no document to be in/out of snapshot.
                    inSnapshot = null;
                }
                else if (gitRoot != null)
                {
                    try
                    {
                        // Inline PathNormalizer.ToGitRelative (Lurp.Storage can't reference Lurp.Shared).
                        var root = Path.GetFullPath(gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        normalized = Path.GetRelativePath(root + Path.DirectorySeparatorChar, raw.DocumentPath).Replace('\\', '/');
                        inSnapshot = snapshotDocumentPaths.Contains(normalized);
                    }
                    catch
                    {
                        // Path outside git root (e.g. linked file) — leave as-is, mark out-of-snapshot.
                        normalized = raw.DocumentPath;
                        inSnapshot = false;
                    }
                }
                else
                {
                    inSnapshot = false;
                }

                all.Add((diagId, new DiagnosticEntry(raw, normalized, inSnapshot)));
            }
        }

        // In-memory document filter (can't push into SQL until path column is normalized).
        IEnumerable<(long Id, DiagnosticEntry Entry)> filtered = all;
        if (documentPath != null)
            filtered = all.Where(x => string.Equals(x.Entry.NormalizedDocumentPath, documentPath, StringComparison.Ordinal));

        // Keyset pagination: skip past cursor, take limit + 1.
        var windowed = filtered;
        if (cursor != null)
            windowed = windowed.Where(x => x.Id > cursor.LastDiagnosticId);

        var rows = windowed.Take(limit + 1).ToList();

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = new DiagnosticsCursor(snapshotId, fingerprint, last.Id).Encode();
        }

        // Total count is the post-filter count (excluding pagination).
        var totalCount = filtered.Count();
        var items = rows.Select(r => r.Entry).ToList();
        return new DiagnosticsPage(items, nextCursor, totalCount);
    }

    public void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO diagnostics (snapshot_id, project_name, document_path, severity, id, message,start_line, start_column, end_line, end_column)
            SELECT @toSnapshotId, project_name, document_path, severity, id, message,
                   start_line, start_column, end_line, end_column
            FROM diagnostics
            WHERE snapshot_id = @fromSnapshotId;
            """;
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    public void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames)
    {
        var nameList = projectNames as IReadOnlyCollection<string> ?? [.. projectNames];
        if (nameList.Count == 0)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM diagnostics
            WHERE snapshot_id = @snapshotId
              AND project_name IN (
            """ + string.Join(", ", nameList.Select((_, i) => $"@p{i}")) + """
        );
        """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var i = 0;
        foreach (var name in nameList)
            command.Parameters.AddWithValue($"@p{i++}", name);
        command.ExecuteNonQuery();
    }
}