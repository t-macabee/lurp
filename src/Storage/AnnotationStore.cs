using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class AnnotationStore
{
    private readonly SqliteConnection _connection;

    public AnnotationStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var ann in annotations)
            {
                command.CommandText = """
                    INSERT INTO annotations (snapshot_id, symbol_id, kind, value, document_path)
                    VALUES (@snapshotId, @symbolId, @kind, @value, @documentPath);
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@symbolId", ann.SymbolId);
                command.Parameters.AddWithValue("@kind", ann.Kind);
                command.Parameters.AddWithValue("@value", ann.Value);
                command.Parameters.AddWithValue("@documentPath", (object?)ann.DocumentPath ?? DBNull.Value);
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

    public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
    {
        using var command = _connection.CreateCommand();
        if (symbolId != null)
        {
            command.CommandText = """
                SELECT annotation_id, symbol_id, kind, value, document_path
                FROM annotations
                WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId
                ORDER BY annotation_id;
                """;
            command.Parameters.AddWithValue("@symbolId", symbolId);
        }
        else
        {
            command.CommandText = """
                SELECT annotation_id, symbol_id, kind, value, document_path
                FROM annotations
                WHERE snapshot_id = @snapshotId
                ORDER BY annotation_id;
                """;
        }

        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var results = new List<AnnotationRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new AnnotationRecord(reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(0)));
        return results;
    }

    /// <summary>
    ///     Hard-delete of a single annotation, regardless of provenance. Scoped to exactly one row:
    ///     <c>WHERE snapshot_id = @snapshotId AND annotation_id = @annotationId</c>.
    ///     Returns true when one row was deleted, false when no such row exists in that snapshot.
    ///     Document_path is not filtered — the caller already resolved an annotation_id that may be
    ///     user-authored (NULL path) or document-anchored; retraction is explicit and single-row by design.
    /// </summary>
    public bool TryRetractAnnotation(string snapshotId, long annotationId)
    {
        if (string.IsNullOrEmpty(snapshotId))
            throw new ArgumentException("snapshotId is required.", nameof(snapshotId));
        if (annotationId <= 0)
            throw new ArgumentException("annotationId must be a positive integer.", nameof(annotationId));

        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM annotations
            WHERE snapshot_id = @snapshotId AND annotation_id = @annotationId;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@annotationId", annotationId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    ///     Paged, keyset-based read over annotations. Ordered by <c>annotation_id</c>.
    ///     Supports filtering by symbol, document path, and kind. Uses keyset pagination
    ///     (like SearchCursor/OutlineCursor) because the sequence comes from a SQL
    ///     ORDER BY whose sort key can be pushed into the next query.
    /// </summary>
    public AnnotationPage GetAnnotationsPage(
        string snapshotId,
        string? symbolId,
        string? documentPath,
        string? kind,
        int limit,
        AnnotationCursor? cursor)
    {
        if (string.IsNullOrEmpty(snapshotId))
            throw new ArgumentException("snapshotId is required.", nameof(snapshotId));
        if (limit <= 0)
            throw new ArgumentException("--limit must be a positive integer.", nameof(limit));

        limit = Math.Max(1, limit);
        var fingerprint = AnnotationCursor.ComputeFingerprint(symbolId, documentPath, kind);
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

        // Total count (without cursor/limit) for the header.
        int totalCount;
        using (var countCmd = _connection.CreateCommand())
        {
            var where = "WHERE snapshot_id = @snapshotId";
            if (symbolId != null) where += " AND symbol_id = @symbolId";
            if (documentPath != null) where += " AND document_path = @documentPath";
            if (kind != null) where += " AND kind = @kind";
            countCmd.CommandText = $"SELECT COUNT(*) FROM annotations {where};";
            countCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            if (symbolId != null) countCmd.Parameters.AddWithValue("@symbolId", symbolId);
            if (documentPath != null) countCmd.Parameters.AddWithValue("@documentPath", documentPath);
            if (kind != null) countCmd.Parameters.AddWithValue("@kind", kind);
            totalCount = Convert.ToInt32(countCmd.ExecuteScalar());
        }

        using var cmd = _connection.CreateCommand();
        var whereClause = "WHERE snapshot_id = @snapshotId";
        if (symbolId != null) whereClause += " AND symbol_id = @symbolId";
        if (documentPath != null) whereClause += " AND document_path = @documentPath";
        if (kind != null) whereClause += " AND kind = @kind";
        if (cursor != null) whereClause += " AND annotation_id > @lastAnnotationId";

        cmd.CommandText = $"""
            SELECT annotation_id, symbol_id, kind, value, document_path
            FROM annotations
            {whereClause}
            ORDER BY annotation_id
            LIMIT @limitPlusOne;
            """;
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        if (symbolId != null) cmd.Parameters.AddWithValue("@symbolId", symbolId);
        if (documentPath != null) cmd.Parameters.AddWithValue("@documentPath", documentPath);
        if (kind != null) cmd.Parameters.AddWithValue("@kind", kind);
        if (cursor != null) cmd.Parameters.AddWithValue("@lastAnnotationId", cursor.LastAnnotationId);
        cmd.Parameters.AddWithValue("@limitPlusOne", limit + 1);

        var rows = new List<(long Id, AnnotationRecord Rec)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var rec = new AnnotationRecord(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    id);
                rows.Add((id, rec));
            }
        }

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = new AnnotationCursor(snapshotId, fingerprint, last.Id).Encode();
        }

        var items = rows.Select(r => r.Rec).ToList();
        return new AnnotationPage(items, nextCursor, totalCount);
    }

    public void CopyAnnotationsToSnapshot(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO annotations (snapshot_id, symbol_id, kind, value, document_path)
            SELECT @toSnapshotId, symbol_id, kind, value, document_path
            FROM annotations
            WHERE snapshot_id = @fromSnapshotId;
            """;
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    ///     Deletes annotations produced by walks anchored to the given documents.
    ///     Rows with a NULL <c>document_path</c> (user-authored annotations) are
    ///     never matched and survive untouched.
    /// </summary>
    public void DeleteAnnotationsByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
    {
        var pathList = documentPaths as IReadOnlyCollection<string> ?? [.. documentPaths];
        if (pathList.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM annotations
                WHERE snapshot_id = @snapshotId
                  AND document_path IN (
                """ + string.Join(", ", pathList.Select((_, i) => $"@p{i}")) + """
            );
            """;
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            var i = 0;
            foreach (var path in pathList)
                command.Parameters.AddWithValue($"@p{i++}", path);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}