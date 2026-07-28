using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class EdgeOperationsStore
{
    private readonly SqliteConnection _connection;

    public EdgeOperationsStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var edge in edges)
            {
                command.CommandText = @"
                    INSERT OR IGNORE INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column, is_cross_generated, type_arguments_json) VALUES (@snapshotId, @sourceSymbolId, @targetSymbolId, @kind, @provenance,@extractorVersion, @sourceDocumentPath,@sourceStartLine, @sourceStartColumn,@sourceEndLine, @sourceEndColumn, @isCrossGenerated, @typeArgumentsJson);
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@sourceSymbolId", edge.SourceSymbolId);
                command.Parameters.AddWithValue("@targetSymbolId", edge.TargetSymbolId);
                command.Parameters.AddWithValue("@kind", edge.Kind);
                command.Parameters.AddWithValue("@provenance", (object?)edge.Provenance ?? DBNull.Value);
                command.Parameters.AddWithValue("@extractorVersion", (object?)edge.ExtractorVersion ?? DBNull.Value);
                command.Parameters.AddWithValue("@sourceDocumentPath", (object?)edge.SourceDocumentPath ?? DBNull.Value);
                command.Parameters.AddWithValue("@sourceStartLine", (object?)edge.SourceStartLine ?? DBNull.Value);
                command.Parameters.AddWithValue("@sourceStartColumn", (object?)edge.SourceStartColumn ?? DBNull.Value);
                command.Parameters.AddWithValue("@sourceEndLine", (object?)edge.SourceEndLine ?? DBNull.Value);
                command.Parameters.AddWithValue("@sourceEndColumn", (object?)edge.SourceEndColumn ?? DBNull.Value);
                command.Parameters.AddWithValue("@isCrossGenerated", edge.IsCrossGenerated);
                command.Parameters.AddWithValue("@typeArgumentsJson", (object?)edge.TypeArgumentsJson ?? DBNull.Value);
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

    public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
    {
        using var command = _connection.CreateCommand();
        if (symbolId != null)
        {
            command.CommandText = @"
                SELECT source_symbol_id, target_symbol_id, kind, provenance,
                       snapshot_id, extractor_version,
                       source_document_path, source_start_line, source_start_column,
                       source_end_line, source_end_column,
                       is_cross_generated, type_arguments_json
                FROM edges
                WHERE snapshot_id = @snapshotId
                  AND (source_symbol_id = @symbolId OR target_symbol_id = @symbolId)
                ORDER BY edge_id;
            ";
            command.Parameters.AddWithValue("@symbolId", symbolId);
        }
        else
        {
            command.CommandText = @"
                SELECT source_symbol_id, target_symbol_id, kind, provenance,
                       snapshot_id, extractor_version,
                       source_document_path, source_start_line, source_start_column,
                       source_end_line, source_end_column,
                       is_cross_generated, type_arguments_json
                FROM edges
                WHERE snapshot_id = @snapshotId
                ORDER BY edge_id;
            ";
        }
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        return ReadEdgeRecords(command);
    }

    public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT source_symbol_id, target_symbol_id, kind, provenance,
                   snapshot_id, extractor_version,
                   source_document_path, source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json
            FROM edges
            WHERE snapshot_id = @snapshotId AND kind = @kind
            ORDER BY edge_id;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@kind", kind);

        return ReadEdgeRecords(command);
    }

    public List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT source_symbol_id, target_symbol_id, kind, provenance,
                   snapshot_id, extractor_version,
                   source_document_path, source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json
            FROM edges
            WHERE snapshot_id = @snapshotId AND target_symbol_id = @symbolId
            ORDER BY edge_id;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);

        return ReadEdgeRecords(command);
    }

    public List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT source_symbol_id, target_symbol_id, kind, provenance,
                   snapshot_id, extractor_version,
                   source_document_path, source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json
            FROM edges
            WHERE snapshot_id = @snapshotId AND source_symbol_id = @symbolId
            ORDER BY edge_id;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);

        return ReadEdgeRecords(command);
    }

    public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
    {
        var pathList = documentPaths as IReadOnlyCollection<string> ?? documentPaths.ToList();
        if (pathList.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                DELETE FROM edges
                WHERE snapshot_id = @snapshotId
                  AND source_document_path IN (" + string.Join(", ", pathList.Select((_, i) => $"@p{i}")) + @");
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            int i = 0;
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

    public void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities)
    {
        var identityList = assemblyIdentities as IReadOnlyCollection<string> ?? assemblyIdentities.ToList();
        if (identityList.Count == 0)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM edges
            WHERE snapshot_id = @snapshotId
              AND source_document_path IS NULL
              AND (" + string.Join(" OR ", identityList.Select((_, i) => $"source_symbol_id LIKE @p{i} ESCAPE '\\'")) + @");
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        int i = 0;
        foreach (var identity in identityList)
        {
            var escaped = identity.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            command.Parameters.AddWithValue($"@p{i++}", "%|" + escaped);
        }
        command.ExecuteNonQuery();
    }

    public void DeleteEdgesWithNullDocumentPathForSymbols(string snapshotId, IEnumerable<string> symbolIds)
    {
        var idList = symbolIds as IReadOnlyCollection<string> ?? symbolIds.ToList();
        if (idList.Count == 0)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM edges
            WHERE snapshot_id = @snapshotId
              AND source_document_path IS NULL
              AND source_symbol_id IN (" + string.Join(", ", idList.Select((_, i) => $"@p{i}")) + @");
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        int i = 0;
        foreach (var id in idList)
            command.Parameters.AddWithValue($"@p{i++}", id);
        command.ExecuteNonQuery();
    }

    public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column, is_cross_generated, type_arguments_json)
            SELECT @toSnapshotId, source_symbol_id, target_symbol_id, kind, provenance,
                   extractor_version, source_document_path,
                   source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json
            FROM edges
            WHERE snapshot_id = @fromSnapshotId;
        ";
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    public void DeleteOrphanEdges(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM edges
            WHERE snapshot_id = @snapshotId
              AND (
                  source_symbol_id NOT IN (
                      SELECT symbol_id FROM snapshot_symbols WHERE snapshot_id = @snapshotId
                  )
                  OR target_symbol_id NOT IN (
                      SELECT symbol_id FROM snapshot_symbols WHERE snapshot_id = @snapshotId
                  )
              );
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    public void UpsertExtractors(IEnumerable<(string Name, string Version, string Description)> extractors)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var (name, version, description) in extractors)
            {
                command.CommandText = @"
                    DELETE FROM extractors
                    WHERE name = @name AND version != @version;
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@version", version);
                command.ExecuteNonQuery();

                command.CommandText = @"
                    INSERT INTO extractors (name, version, description)
                    SELECT @name, @version, @description
                    WHERE NOT EXISTS (
                        SELECT 1 FROM extractors
                        WHERE name = @name AND version = @version
                    );
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@version", version);
                command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
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

    public bool HasStaleExtractorVersions(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*) FROM edges
            WHERE snapshot_id = @sid
            AND extractor_version NOT IN (SELECT version FROM extractors);
        ";
        command.Parameters.AddWithValue("@sid", snapshotId);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static List<EdgeRecord> ReadEdgeRecords(SqliteCommand command)
    {
        var results = new List<EdgeRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EdgeRecord
            {
                SourceSymbolId = reader.GetString(0),
                TargetSymbolId = reader.GetString(1),
                Kind = reader.GetString(2),
                Provenance = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                SnapshotId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                ExtractorVersion = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                SourceDocumentPath = reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceStartLine = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                SourceStartColumn = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                SourceEndLine = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                SourceEndColumn = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                IsCrossGenerated = reader.GetBoolean(11),
                TypeArgumentsJson = reader.IsDBNull(12) ? null : reader.GetString(12),
            });
        }
        return results;
    }
}