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

    /// <summary>Full FTS rebuild for one snapshot; see <see cref="SqliteIndexStore.BuildSearchIndex(string)" />.</summary>
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

            InsertSymbolRows(_connection, snapshotId);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>Seeds the new snapshot's FTS rows from the previous snapshot; see <see cref="SqliteIndexStore.CopySearchIndexToSnapshot" />.</summary>
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
                INSERT INTO symbol_fts (simple_name, name_tokens, container_tokens, fqn, doc_comment_id, kind,
                                        kind_rank, is_test, symbol_id, snapshot_id)
                SELECT simple_name, name_tokens, container_tokens, fqn, doc_comment_id, kind,
                       kind_rank, is_test, symbol_id, @toSnapshotId
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

    /// <summary>Incremental FTS refresh for one snapshot; see <see cref="SqliteIndexStore.BuildSearchIndex(string, HashSet{string}, HashSet{string})" />.</summary>
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
                command.CommandText = """
                    DELETE FROM source_fts
                    WHERE snapshot_id = @snapshotId
                      AND document_path IN (SELECT value FROM json_each(@documentPaths));
                    """;
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                SqlIdList.Bind(command, "@documentPaths", changedDocumentPaths);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }

            if (changedSymbolIds.Count > 0)
            {
                command.CommandText = """
                    DELETE FROM symbol_fts
                    WHERE snapshot_id = @snapshotId
                      AND symbol_id IN (SELECT value FROM json_each(@symbolIds));
                    """;
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                SqlIdList.Bind(command, "@symbolIds", changedSymbolIds);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }

            // Re-insert only for changed documents
            if (changedDocumentPaths.Count > 0)
            {
                command.CommandText = """
                    INSERT INTO source_fts (document_path, content, snapshot_id, document_version_id)
                    SELECT d.relative_path, CAST(dv.content AS TEXT), sd.snapshot_id, dv.document_version_id
                    FROM snapshot_documents sd
                    JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                    JOIN documents d ON d.document_id = dv.document_id
                    WHERE sd.snapshot_id = @snapshotId
                      AND dv.content IS NOT NULL
                      AND d.relative_path IN (SELECT value FROM json_each(@documentPaths));
                    """;
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                SqlIdList.Bind(command, "@documentPaths", changedDocumentPaths);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }

            if (changedSymbolIds.Count > 0)
                InsertSymbolRows(_connection, snapshotId, changedSymbolIds);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal static void InsertSymbolRows(SqliteConnection connection, string snapshotId, HashSet<string>? changedSymbolIds = null)
    {
        var projects = ProjectFacts.Load(connection, snapshotId);
        var isTestByAssembly = new Dictionary<string, bool>(StringComparer.Ordinal);
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT s.symbol_id, ss.fqn, s.doc_comment_id, s.kind, s.assembly_identity, ss.metadata_json
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId AND ss.fqn IS NOT NULL
            """;
        select.Parameters.AddWithValue("@snapshotId", snapshotId);
        if (changedSymbolIds != null)
        {
            select.CommandText += " AND ss.symbol_id IN (SELECT value FROM json_each(@symbolIds))";
            SqlIdList.Bind(select, "@symbolIds", changedSymbolIds);
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO symbol_fts (simple_name, name_tokens, container_tokens, fqn, doc_comment_id, kind,
                                    kind_rank, is_test, symbol_id, snapshot_id)
            VALUES (@name, @nameTokens, @containerTokens, @fqn, @docId, @kind,
                    @kindRank, @isTest, @symbolId, @snapshotId);
            """;
        var simpleName = insert.Parameters.Add("@name", SqliteType.Text);
        var nameTokens = insert.Parameters.Add("@nameTokens", SqliteType.Text);
        var containerTokens = insert.Parameters.Add("@containerTokens", SqliteType.Text);
        var fqn = insert.Parameters.Add("@fqn", SqliteType.Text);
        var docId = insert.Parameters.Add("@docId", SqliteType.Text);
        var kind = insert.Parameters.Add("@kind", SqliteType.Text);
        var kindRank = insert.Parameters.Add("@kindRank", SqliteType.Integer);
        var isTest = insert.Parameters.Add("@isTest", SqliteType.Integer);
        var symbolId = insert.Parameters.Add("@symbolId", SqliteType.Text);
        insert.Parameters.AddWithValue("@snapshotId", snapshotId);
        insert.Prepare();

        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var qualifiedName = reader.GetString(1);
            var commentId = reader.GetString(2);
            var kindName = reader.GetString(3);
            var assembly = reader.GetString(4);
            var metadataJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (!isTestByAssembly.TryGetValue(assembly, out var isTestAssembly))
                isTestByAssembly[assembly] = isTestAssembly = projects.IsTestProject(MetadataReferenceIdentity.SimpleName(assembly));
            var (name, container) = SymbolSearchNames.FromDocCommentId(commentId, qualifiedName);
            simpleName.Value = name;
            nameTokens.Value = SymbolSearchNames.Tokenize(name);
            containerTokens.Value = SymbolSearchNames.Tokenize(container);
            fqn.Value = qualifiedName;
            docId.Value = commentId;
            kind.Value = kindName;
            kindRank.Value = SymbolSearchNames.KindRank(kindName, IsAccessor(metadataJson, id));
            isTest.Value = isTestAssembly ? 1 : 0;
            symbolId.Value = id;
            insert.ExecuteNonQuery();
        }
    }

    private static bool IsAccessor(string? metadataJson, string symbolId) =>
        SymbolMetadata.GetString(SymbolMetadata.Parse(metadataJson, symbolId), SymbolMetadataKeys.AccessorKind) != null;
}
