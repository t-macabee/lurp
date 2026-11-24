using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Lurp.Storage;

internal sealed class AnnotationStore
{
    private readonly SqliteConnection _connection;

    public AnnotationStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations, bool requireLiveSymbol = false)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = requireLiveSymbol
                ? """
                    INSERT OR IGNORE INTO annotations (snapshot_id, symbol_id, kind, value, document_path)
                    SELECT @snapshotId, @symbolId, @kind, @value, @documentPath
                    WHERE EXISTS (
                        SELECT 1 FROM snapshot_symbols
                        WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId
                    );
                    """
                : """
                    INSERT OR IGNORE INTO annotations (snapshot_id, symbol_id, kind, value, document_path)
                    VALUES (@snapshotId, @symbolId, @kind, @value, @documentPath);
                    """;

            foreach (var ann in annotations)
            {
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

    public long SaveUserAnnotation(string snapshotId, string symbolId, string kind, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO annotations (snapshot_id, symbol_id, kind, value, document_path)
            VALUES (@snapshotId, @symbolId, @kind, @value, NULL)
            RETURNING annotation_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@value", value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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
        return ReadAnnotations(command);
    }

    public List<AnnotationRecord> GetUserAnnotations(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT annotation_id, symbol_id, kind, value, document_path
            FROM annotations
            WHERE snapshot_id = @snapshotId AND document_path IS NULL
            ORDER BY annotation_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        return ReadAnnotations(command);
    }

    private static List<AnnotationRecord> ReadAnnotations(SqliteCommand command)
    {
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
            throw new ArgumentException("annotationId must be a positive integer.");

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
            throw new ArgumentException("--limit must be a positive integer.");

        var fingerprint = AnnotationCursor.ComputeFingerprint(symbolId, documentPath, kind);
        if (cursor != null)
        {
            cursor.Validate(snapshotId, fingerprint);
        }

        // Total count (without cursor/limit) for the header.
        int totalCount;
        using (var countCmd = _connection.CreateCommand())
        {
            var where = BuildAnnotationsFilterWhereClause(countCmd, snapshotId, symbolId, documentPath, kind, cursor: null);
            countCmd.CommandText = $"SELECT COUNT(*) FROM annotations {where};";
            totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var cmd = _connection.CreateCommand();
        var whereClause = BuildAnnotationsFilterWhereClause(cmd, snapshotId, symbolId, documentPath, kind, cursor);

        cmd.CommandText = $"""
            SELECT annotation_id, symbol_id, kind, value, document_path
            FROM annotations
            {whereClause}
            ORDER BY annotation_id
            LIMIT @limitPlusOne;
            """;
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

    private static string BuildAnnotationsFilterWhereClause(
        SqliteCommand command,
        string snapshotId,
        string? symbolId,
        string? documentPath,
        string? kind,
        AnnotationCursor? cursor)
    {
        var where = "WHERE snapshot_id = @snapshotId";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        if (symbolId != null)
        {
            where += " AND symbol_id = @symbolId";
            command.Parameters.AddWithValue("@symbolId", symbolId);
        }

        if (documentPath != null)
        {
            where += $"""
                  AND (
                      document_path = @documentPath
                      OR symbol_id IN (
                          SELECT d.symbol_id
                          FROM declarations d
                          {SnapshotDeclarationLink.Join("d", "sdecl")}
                          JOIN document_versions dv ON dv.document_version_id = d.document_version_id
                          JOIN documents doc ON doc.document_id = dv.document_id
                          WHERE doc.relative_path = @documentPath
                      )
                  )
                """;
            command.Parameters.AddWithValue("@documentPath", documentPath);
        }

        if (kind != null)
        {
            where += " AND kind = @kind";
            command.Parameters.AddWithValue("@kind", kind);
        }

        if (cursor != null)
        {
            where += " AND annotation_id > @lastAnnotationId";
            command.Parameters.AddWithValue("@lastAnnotationId", cursor.LastAnnotationId);
        }

        return where;
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

    public void PruneOrphanAnnotations(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM annotations
            WHERE snapshot_id = @snapshotId
              AND NOT EXISTS (
                  SELECT 1 FROM snapshot_symbols
                  WHERE snapshot_id = @snapshotId AND symbol_id = annotations.symbol_id
              );
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    internal static string DeleteAnnotationsByDocumentPathsSql() => """
        DELETE FROM annotations
        WHERE snapshot_id = @snapshotId
          AND document_path IN (SELECT value FROM json_each(@documentPaths));
        """;
}
