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
            command.CommandText = @"
                INSERT OR IGNORE INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json) VALUES (@snapshotId, @sourceSymbolId, @targetSymbolId, @kind, @provenance,@extractorVersion, @sourceDocumentPath,@sourceStartLine, @sourceStartColumn,@sourceEndLine, @sourceEndColumn, @isCrossGenerated, @typeArgumentsJson, @receiverTypeConstraintsJson);
            ";
            var snapshotIdParam = command.Parameters.Add(new SqliteParameter("@snapshotId", snapshotId));
            var sourceSymbolIdParam = command.Parameters.Add(new SqliteParameter("@sourceSymbolId", null));
            var targetSymbolIdParam = command.Parameters.Add(new SqliteParameter("@targetSymbolId", null));
            var kindParam = command.Parameters.Add(new SqliteParameter("@kind", null));
            var provenanceParam = command.Parameters.Add(new SqliteParameter("@provenance", null));
            var extractorVersionParam = command.Parameters.Add(new SqliteParameter("@extractorVersion", null));
            var sourceDocumentPathParam = command.Parameters.Add(new SqliteParameter("@sourceDocumentPath", null));
            var sourceStartLineParam = command.Parameters.Add(new SqliteParameter("@sourceStartLine", null));
            var sourceStartColumnParam = command.Parameters.Add(new SqliteParameter("@sourceStartColumn", null));
            var sourceEndLineParam = command.Parameters.Add(new SqliteParameter("@sourceEndLine", null));
            var sourceEndColumnParam = command.Parameters.Add(new SqliteParameter("@sourceEndColumn", null));
            var isCrossGeneratedParam = command.Parameters.Add(new SqliteParameter("@isCrossGenerated", null));
            var typeArgumentsJsonParam = command.Parameters.Add(new SqliteParameter("@typeArgumentsJson", null));
            var receiverTypeConstraintsJsonParam = command.Parameters.Add(new SqliteParameter("@receiverTypeConstraintsJson", null));

            using var nodeCmd = _connection.CreateCommand();
            nodeCmd.Transaction = transaction;
            nodeCmd.CommandText = @"
                INSERT INTO graph_nodes (node_id, node_kind)
                VALUES (@nodeId, @nodeKind)
                ON CONFLICT(node_id) DO UPDATE SET node_kind = excluded.node_kind;
            ";
            var nodeIdParam = nodeCmd.Parameters.Add(new SqliteParameter("@nodeId", null));
            var nodeKindParam = nodeCmd.Parameters.Add(new SqliteParameter("@nodeKind", null));

            using var memberCmd = _connection.CreateCommand();
            memberCmd.Transaction = transaction;
            memberCmd.CommandText = @"
                INSERT OR IGNORE INTO snapshot_graph_nodes (snapshot_id, node_id)
                VALUES (@snapshotId, @nodeId);
            ";
            var memberSnapshotParam = memberCmd.Parameters.Add(new SqliteParameter("@snapshotId", snapshotId));
            var memberNodeParam = memberCmd.Parameters.Add(new SqliteParameter("@nodeId", null));

            foreach (var edge in edges)
            {
                RegisterGraphNode(nodeCmd, nodeIdParam, nodeKindParam, memberCmd, memberNodeParam, edge.SourceSymbolId, edge.SourceNodeKind);
                RegisterGraphNode(nodeCmd, nodeIdParam, nodeKindParam, memberCmd, memberNodeParam, edge.TargetSymbolId, edge.TargetNodeKind);

                sourceSymbolIdParam.Value = edge.SourceSymbolId;
                targetSymbolIdParam.Value = edge.TargetSymbolId;
                kindParam.Value = edge.Kind;
                provenanceParam.Value = (object?)edge.Provenance ?? DBNull.Value;
                extractorVersionParam.Value = (object?)edge.ExtractorVersion ?? DBNull.Value;
                sourceDocumentPathParam.Value = (object?)edge.SourceDocumentPath ?? DBNull.Value;
                sourceStartLineParam.Value = (object?)edge.SourceStartLine ?? DBNull.Value;
                sourceStartColumnParam.Value = (object?)edge.SourceStartColumn ?? DBNull.Value;
                sourceEndLineParam.Value = (object?)edge.SourceEndLine ?? DBNull.Value;
                sourceEndColumnParam.Value = (object?)edge.SourceEndColumn ?? DBNull.Value;
                isCrossGeneratedParam.Value = edge.IsCrossGenerated;
                typeArgumentsJsonParam.Value = (object?)edge.TypeArgumentsJson ?? DBNull.Value;
                receiverTypeConstraintsJsonParam.Value = (object?)edge.ReceiverTypeConstraintsJson ?? DBNull.Value;
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

    private static void RegisterGraphNode(
        SqliteCommand nodeCmd, SqliteParameter nodeIdParam, SqliteParameter nodeKindParam,
        SqliteCommand memberCmd, SqliteParameter memberNodeParam,
        string nodeId, GraphNodeKind? kind)
    {
        if (kind == null)
            return;

        nodeIdParam.Value = nodeId;
        nodeKindParam.Value = kind.Value.ToString();
        nodeCmd.ExecuteNonQuery();

        memberNodeParam.Value = nodeId;
        memberCmd.ExecuteNonQuery();
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
                       is_cross_generated, type_arguments_json, receiver_type_constraints_json
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
                       is_cross_generated, type_arguments_json, receiver_type_constraints_json
                FROM edges
                WHERE snapshot_id = @snapshotId
                ORDER BY edge_id;
            ";
        }
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        return ReadEdgeRecords(command);
    }

    public int CountEdges(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edges WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT source_symbol_id, target_symbol_id, kind, provenance,
                   snapshot_id, extractor_version,
                   source_document_path, source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json, receiver_type_constraints_json
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
                   is_cross_generated, type_arguments_json, receiver_type_constraints_json
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
                   is_cross_generated, type_arguments_json, receiver_type_constraints_json
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
            INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json)
            SELECT @toSnapshotId, source_symbol_id, target_symbol_id, kind, provenance,
                   extractor_version, source_document_path,
                   source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json, receiver_type_constraints_json
            FROM edges
            WHERE snapshot_id = @fromSnapshotId;
        ";
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();

        using var memberCmd = _connection.CreateCommand();
        memberCmd.CommandText = @"
            INSERT OR IGNORE INTO snapshot_graph_nodes (snapshot_id, node_id)
            SELECT @toSnapshotId, node_id
            FROM snapshot_graph_nodes
            WHERE snapshot_id = @fromSnapshotId;
        ";
        memberCmd.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        memberCmd.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        memberCmd.ExecuteNonQuery();
    }

    public OrphanEdgeDropSummary DeleteOrphanEdges(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            WITH valid_ids(id) AS (
                SELECT symbol_id FROM snapshot_symbols WHERE snapshot_id = @snapshotId
                UNION
                SELECT node_id FROM snapshot_graph_nodes WHERE snapshot_id = @snapshotId
            ),
            in_scope_assemblies(identity) AS (
                SELECT DISTINCT substr(id, instr(id,'|')+1) FROM valid_ids WHERE instr(id,'|') > 0
            )
            DELETE FROM edges
            WHERE snapshot_id = @snapshotId
              AND (
                  source_symbol_id NOT IN (SELECT id FROM valid_ids)
                  OR target_symbol_id NOT IN (SELECT id FROM valid_ids)
              )
            RETURNING
                CASE
                    WHEN (source_symbol_id NOT IN (SELECT id FROM valid_ids) AND source_symbol_id LIKE '%<%')
                      OR (target_symbol_id NOT IN (SELECT id FROM valid_ids) AND target_symbol_id LIKE '%<%')
                    THEN 'compiler_synthesized'
                    WHEN (source_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND substr(source_symbol_id, instr(source_symbol_id,'|')+1) NOT IN (SELECT identity FROM in_scope_assemblies))
                      OR (target_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND substr(target_symbol_id, instr(target_symbol_id,'|')+1) NOT IN (SELECT identity FROM in_scope_assemblies))
                    THEN 'external'
                    ELSE 'other'
                END;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        int external = 0, compilerSynthesized = 0, other = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            switch (reader.GetString(0))
            {
                case "compiler_synthesized": compilerSynthesized++; break;
                case "external": external++; break;
                case "other": other++; break;
            }
        }
        var total = external + compilerSynthesized + other;
        return new OrphanEdgeDropSummary(total, external, compilerSynthesized, other);
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
                ReceiverTypeConstraintsJson = reader.IsDBNull(13) ? null : reader.GetString(13),
            });
        }
        return results;
    }
}
