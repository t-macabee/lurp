// Purpose: index-maintenance half of the search store.
// Owns: FTS5 index build (full + incremental) and cross-snapshot copy.
// Must not contain: the other halves, or any Roslyn dependency.

using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SearchIndexMaintenance
{
    private readonly SqliteConnection _connection;

    public SearchIndexMaintenance(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc cref="ISearchStore.BuildSearchIndex(string)" />
    public void BuildSearchIndex(string snapshotId)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = "DELETE FROM source_fts WHERE snapshot_id = @snapshotId;";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.ExecuteNonQuery();

            command.CommandText = "DELETE FROM symbol_fts WHERE snapshot_id = @snapshotId;";
            command.ExecuteNonQuery();

            command.CommandText = """
                INSERT INTO source_fts (document_path, content, snapshot_id, document_version_id)
                SELECT d.relative_path, CAST(dv.content AS TEXT), sd.snapshot_id, dv.document_version_id
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN documents d ON d.document_id = dv.document_id
                WHERE sd.snapshot_id = @snapshotId
                  AND dv.content IS NOT NULL;
                """;
            command.ExecuteNonQuery();

            command.CommandText = """
                INSERT INTO symbol_fts (symbol_id, fqn, doc_comment_id, kind, snapshot_id)
                SELECT s.symbol_id, ss.fqn, s.doc_comment_id, s.kind, ss.snapshot_id
                FROM snapshot_symbols ss
                JOIN symbols s ON s.symbol_id = ss.symbol_id
                WHERE ss.snapshot_id = @snapshotId
                  AND ss.fqn IS NOT NULL;
                """;
            command.ExecuteNonQuery();

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc cref="ISearchStore.CopySearchIndexToSnapshot" />
    public void CopySearchIndexToSnapshot(string fromSnapshotId, string toSnapshotId)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO source_fts (document_path, content, snapshot_id, document_version_id)
                SELECT document_path, content, @toSnapshotId, document_version_id
                FROM source_fts
                WHERE snapshot_id = @fromSnapshotId;
                """;
            command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
            command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
            command.ExecuteNonQuery();

            command.CommandText = """
                INSERT INTO symbol_fts (symbol_id, fqn, doc_comment_id, kind, snapshot_id)
                SELECT symbol_id, fqn, doc_comment_id, kind, @toSnapshotId
                FROM symbol_fts
                WHERE snapshot_id = @fromSnapshotId;
                """;
            command.ExecuteNonQuery();

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc cref="ISearchStore.BuildSearchIndex(string, HashSet{string}, HashSet{string})" />
    public void BuildSearchIndex(string snapshotId, HashSet<string> changedDocumentPaths, HashSet<string> changedSymbolIds)
    {
        if (changedDocumentPaths.Count == 0 && changedSymbolIds.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            // Delete FTS rows for changed documents/symbols (after copy-forward, only the stale ones)
            if (changedDocumentPaths.Count > 0)
            {
                var pathPlaceholders = BuildPlaceholderList(changedDocumentPaths, command, "path");
                command.CommandText = $"""
                    DELETE FROM source_fts
                    WHERE snapshot_id = @snapshotId
                      AND document_path IN ({pathPlaceholders});
                    """;
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }

            if (changedSymbolIds.Count > 0)
            {
                var symPlaceholders = BuildPlaceholderList(changedSymbolIds, command, "sym");
                command.CommandText = $"""
                    DELETE FROM symbol_fts
                    WHERE snapshot_id = @snapshotId
                      AND symbol_id IN ({symPlaceholders});
                    """;
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }

            // Re-insert only for changed documents
            if (changedDocumentPaths.Count > 0)
            {
                var pathPlaceholders = BuildPlaceholderList(changedDocumentPaths, command, "path2");
                command.CommandText = $"""
                    INSERT INTO source_fts (document_path, content, snapshot_id, document_version_id)
                    SELECT d.relative_path, CAST(dv.content AS TEXT), sd.snapshot_id, dv.document_version_id
                    FROM snapshot_documents sd
                    JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                    JOIN documents d ON d.document_id = dv.document_id
                    WHERE sd.snapshot_id = @snapshotId
                      AND dv.content IS NOT NULL
                      AND d.relative_path IN ({pathPlaceholders});
                    """;
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }

            // Re-insert only for changed symbols
            if (changedSymbolIds.Count > 0)
            {
                var symPlaceholders = BuildPlaceholderList(changedSymbolIds, command, "sym2");
                command.CommandText = $"""
                    INSERT INTO symbol_fts (symbol_id, fqn, doc_comment_id, kind, snapshot_id)
                    SELECT s.symbol_id, ss.fqn, s.doc_comment_id, s.kind, ss.snapshot_id
                    FROM snapshot_symbols ss
                    JOIN symbols s ON s.symbol_id = ss.symbol_id
                    WHERE ss.snapshot_id = @snapshotId
                      AND ss.fqn IS NOT NULL
                      AND ss.symbol_id IN ({symPlaceholders});
                    """;
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
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

    private static string BuildPlaceholderList(IEnumerable<string> items, SqliteCommand command, string prefix)
    {
        var placeholders = new List<string>();
        var i = 0;
        foreach (var item in items)
        {
            var paramName = $"@{prefix}{i}";
            placeholders.Add(paramName);
            command.Parameters.AddWithValue(paramName, item);
            i++;
        }

        return string.Join(", ", placeholders);
    }
}