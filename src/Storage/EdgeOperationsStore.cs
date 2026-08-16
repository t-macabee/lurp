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
        var collapsed = EdgeMerge.CollapseBatch(edges);
        // Both TypeArgumentsJson and ReceiverTypeConstraintsJson need an app-side JSON
        // union merge on conflict (see WriteSplitEdges), which requires reading the
        // existing row first. An edge carrying either one must take that path — routing
        // only on TypeArgumentsJson left Calls edges (which carry receiver constraints
        // but never type arguments) on the bulk path, where receiver_type_constraints_json
        // was still a rank-gated overwrite instead of a merge (ARCH-003).
        var bulkEdges = collapsed.Where(e => e.TypeArgumentsJson == null && e.ReceiverTypeConstraintsJson == null).ToList();
        var splitEdges = collapsed.Where(e => e.TypeArgumentsJson != null || e.ReceiverTypeConstraintsJson != null).ToList();

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var nodeCmd = _connection.CreateCommand();
            nodeCmd.Transaction = transaction;
            nodeCmd.CommandText = """
                INSERT INTO graph_nodes (node_id, node_kind)
                VALUES (@nodeId, @nodeKind)
                ON CONFLICT(node_id) DO UPDATE SET node_kind = excluded.node_kind;
                """;
            var nodeIdParam = nodeCmd.Parameters.Add(new SqliteParameter("@nodeId", null));
            var nodeKindParam = nodeCmd.Parameters.Add(new SqliteParameter("@nodeKind", null));

            using var memberCmd = _connection.CreateCommand();
            memberCmd.Transaction = transaction;
            memberCmd.CommandText = """
                INSERT OR IGNORE INTO snapshot_graph_nodes (snapshot_id, node_id)
                VALUES (@snapshotId, @nodeId);
                """;
            var memberSnapshotParam = memberCmd.Parameters.Add(new SqliteParameter("@snapshotId", snapshotId));
            var memberNodeParam = memberCmd.Parameters.Add(new SqliteParameter("@nodeId", null));

            foreach (var edge in collapsed)
            {
                RegisterGraphNode(nodeCmd, nodeIdParam, nodeKindParam, memberCmd, memberNodeParam, edge.SourceSymbolId, edge.SourceNodeKind);
                RegisterGraphNode(nodeCmd, nodeIdParam, nodeKindParam, memberCmd, memberNodeParam, edge.TargetSymbolId, edge.TargetNodeKind);
            }

            if (bulkEdges.Count > 0) WriteBulkEdges(snapshotId, bulkEdges, transaction);

            if (splitEdges.Count > 0) WriteSplitEdges(snapshotId, splitEdges, transaction);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void WriteBulkEdges(string snapshotId, List<EdgeRecord> edges, SqliteTransaction transaction)
    {
        const string persistedRank =
            "(CASE edges.provenance WHEN 'compiler_proved' THEN 6 WHEN 'framework_derived' THEN 5 WHEN 'global_implementation_relation' THEN 4 WHEN 'possible' THEN 3 WHEN 'convention' THEN 2 WHEN 'name_candidate' THEN 1 WHEN 'runtime_unknown' THEN 0 ELSE -1 END)";
        const string winnerWins = $"@incomingRank > {persistedRank}";

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json)
            VALUES (@snapshotId, @sourceSymbolId, @targetSymbolId, @kind, @provenance,@extractorVersion, @sourceDocumentPath,@sourceStartLine, @sourceStartColumn,@sourceEndLine, @sourceEndColumn, @isCrossGenerated, @typeArgumentsJson, @receiverTypeConstraintsJson)
            ON CONFLICT(snapshot_id, source_symbol_id, target_symbol_id, kind) DO UPDATE SET
                provenance = CASE WHEN {winnerWins} THEN excluded.provenance ELSE edges.provenance END,
                extractor_version = CASE WHEN {winnerWins} THEN excluded.extractor_version ELSE edges.extractor_version END,
                source_document_path = CASE WHEN {winnerWins} THEN excluded.source_document_path ELSE edges.source_document_path END,
                source_start_line = CASE WHEN {winnerWins} THEN excluded.source_start_line ELSE edges.source_start_line END,
                source_start_column = CASE WHEN {winnerWins} THEN excluded.source_start_column ELSE edges.source_start_column END,
                source_end_line = CASE WHEN {winnerWins} THEN excluded.source_end_line ELSE edges.source_end_line END,
                source_end_column = CASE WHEN {winnerWins} THEN excluded.source_end_column ELSE edges.source_end_column END,
                is_cross_generated = CASE WHEN {winnerWins} THEN excluded.is_cross_generated ELSE edges.is_cross_generated END,
                receiver_type_constraints_json = CASE WHEN {winnerWins} THEN excluded.receiver_type_constraints_json ELSE edges.receiver_type_constraints_json END;
            """;

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
        var incomingRankParam = command.Parameters.Add(new SqliteParameter("@incomingRank", 0));

        foreach (var edge in edges)
        {
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
            typeArgumentsJsonParam.Value = DBNull.Value;
            receiverTypeConstraintsJsonParam.Value = (object?)edge.ReceiverTypeConstraintsJson ?? DBNull.Value;
            incomingRankParam.Value = EdgeMerge.ProvenanceRank(edge.Provenance ?? "");
            command.ExecuteNonQuery();
        }
    }

    private void WriteSplitEdges(string snapshotId, List<EdgeRecord> edges, SqliteTransaction transaction)
    {
        const string persistedRank =
            "(CASE edges.provenance WHEN 'compiler_proved' THEN 6 WHEN 'framework_derived' THEN 5 WHEN 'global_implementation_relation' THEN 4 WHEN 'possible' THEN 3 WHEN 'convention' THEN 2 WHEN 'name_candidate' THEN 1 WHEN 'runtime_unknown' THEN 0 ELSE -1 END)";
        const string winnerWins = $"@incomingRank > {persistedRank}";

        using var selectCmd = _connection.CreateCommand();
        selectCmd.Transaction = transaction;
        selectCmd.CommandText = "SELECT type_arguments_json, receiver_type_constraints_json, provenance FROM edges WHERE snapshot_id = @s AND source_symbol_id = @src AND target_symbol_id = @tgt AND kind = @k LIMIT 1;";
        var selectS = selectCmd.Parameters.Add(new SqliteParameter("@s", snapshotId));
        var selectSrc = selectCmd.Parameters.Add(new SqliteParameter("@src", null));
        var selectTgt = selectCmd.Parameters.Add(new SqliteParameter("@tgt", null));
        var selectK = selectCmd.Parameters.Add(new SqliteParameter("@k", null));

        using var splitInsertCmd = _connection.CreateCommand();
        splitInsertCmd.Transaction = transaction;
        splitInsertCmd.CommandText = """
            INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json)
            VALUES (@snapshotId, @sourceSymbolId, @targetSymbolId, @kind, @provenance,@extractorVersion, @sourceDocumentPath,@sourceStartLine, @sourceStartColumn,@sourceEndLine, @sourceEndColumn, @isCrossGenerated, @typeArgumentsJson, @receiverTypeConstraintsJson);
            """;
        var splitInsSid = splitInsertCmd.Parameters.Add(new SqliteParameter("@snapshotId", snapshotId));
        var splitInsSrc = splitInsertCmd.Parameters.Add(new SqliteParameter("@sourceSymbolId", null));
        var splitInsTgt = splitInsertCmd.Parameters.Add(new SqliteParameter("@targetSymbolId", null));
        var splitInsKind = splitInsertCmd.Parameters.Add(new SqliteParameter("@kind", null));
        var splitInsProv = splitInsertCmd.Parameters.Add(new SqliteParameter("@provenance", null));
        var splitInsVer = splitInsertCmd.Parameters.Add(new SqliteParameter("@extractorVersion", null));
        var splitInsDoc = splitInsertCmd.Parameters.Add(new SqliteParameter("@sourceDocumentPath", null));
        var splitInsSl = splitInsertCmd.Parameters.Add(new SqliteParameter("@sourceStartLine", null));
        var splitInsSc = splitInsertCmd.Parameters.Add(new SqliteParameter("@sourceStartColumn", null));
        var splitInsEl = splitInsertCmd.Parameters.Add(new SqliteParameter("@sourceEndLine", null));
        var splitInsEc = splitInsertCmd.Parameters.Add(new SqliteParameter("@sourceEndColumn", null));
        var splitInsCg = splitInsertCmd.Parameters.Add(new SqliteParameter("@isCrossGenerated", null));
        var splitInsTa = splitInsertCmd.Parameters.Add(new SqliteParameter("@typeArgumentsJson", null));
        var splitInsRc = splitInsertCmd.Parameters.Add(new SqliteParameter("@receiverTypeConstraintsJson", null));

        using var splitUpdateCmd = _connection.CreateCommand();
        splitUpdateCmd.Transaction = transaction;
        splitUpdateCmd.CommandText = $"""
            UPDATE edges SET
                type_arguments_json = @mergedTypeArguments,
                provenance = CASE WHEN {winnerWins} THEN @provenance ELSE edges.provenance END,
                extractor_version = CASE WHEN {winnerWins} THEN @extractorVersion ELSE edges.extractor_version END,
                source_document_path = CASE WHEN {winnerWins} THEN @sourceDocumentPath ELSE edges.source_document_path END,
                source_start_line = CASE WHEN {winnerWins} THEN @sourceStartLine ELSE edges.source_start_line END,
                source_start_column = CASE WHEN {winnerWins} THEN @sourceStartColumn ELSE edges.source_start_column END,
                source_end_line = CASE WHEN {winnerWins} THEN @sourceEndLine ELSE edges.source_end_line END,
                source_end_column = CASE WHEN {winnerWins} THEN @sourceEndColumn ELSE edges.source_end_column END,
                is_cross_generated = CASE WHEN {winnerWins} THEN @isCrossGenerated ELSE edges.is_cross_generated END,
                receiver_type_constraints_json = @receiverTypeConstraintsJson
            WHERE snapshot_id = @s AND source_symbol_id = @src AND target_symbol_id = @tgt AND kind = @k;
            """;
        var upSid = splitUpdateCmd.Parameters.Add(new SqliteParameter("@s", snapshotId));
        var upSrc = splitUpdateCmd.Parameters.Add(new SqliteParameter("@src", null));
        var upTgt = splitUpdateCmd.Parameters.Add(new SqliteParameter("@tgt", null));
        var upKind = splitUpdateCmd.Parameters.Add(new SqliteParameter("@k", null));
        var upProv = splitUpdateCmd.Parameters.Add(new SqliteParameter("@provenance", null));
        var upVer = splitUpdateCmd.Parameters.Add(new SqliteParameter("@extractorVersion", null));
        var upDoc = splitUpdateCmd.Parameters.Add(new SqliteParameter("@sourceDocumentPath", null));
        var upSl = splitUpdateCmd.Parameters.Add(new SqliteParameter("@sourceStartLine", null));
        var upSc = splitUpdateCmd.Parameters.Add(new SqliteParameter("@sourceStartColumn", null));
        var upEl = splitUpdateCmd.Parameters.Add(new SqliteParameter("@sourceEndLine", null));
        var upEc = splitUpdateCmd.Parameters.Add(new SqliteParameter("@sourceEndColumn", null));
        var upCg = splitUpdateCmd.Parameters.Add(new SqliteParameter("@isCrossGenerated", null));
        var upRc = splitUpdateCmd.Parameters.Add(new SqliteParameter("@receiverTypeConstraintsJson", null));
        var upMerged = splitUpdateCmd.Parameters.Add(new SqliteParameter("@mergedTypeArguments", null));
        var upRank = splitUpdateCmd.Parameters.Add(new SqliteParameter("@incomingRank", 0));

        foreach (var edge in edges)
        {
            selectSrc.Value = edge.SourceSymbolId;
            selectTgt.Value = edge.TargetSymbolId;
            selectK.Value = edge.Kind;

            object? existingTypeArgs = null;
            object? existingReceiverConstraints = null;
            var hasRow = false;
            using (var reader = selectCmd.ExecuteReader())
            {
                hasRow = reader.Read();
                if (hasRow)
                {
                    existingTypeArgs = reader.IsDBNull(0) ? null : reader.GetString(0);
                    existingReceiverConstraints = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            if (!hasRow)
            {
                splitInsSrc.Value = edge.SourceSymbolId;
                splitInsTgt.Value = edge.TargetSymbolId;
                splitInsKind.Value = edge.Kind;
                splitInsProv.Value = (object?)edge.Provenance ?? DBNull.Value;
                splitInsVer.Value = (object?)edge.ExtractorVersion ?? DBNull.Value;
                splitInsDoc.Value = (object?)edge.SourceDocumentPath ?? DBNull.Value;
                splitInsSl.Value = (object?)edge.SourceStartLine ?? DBNull.Value;
                splitInsSc.Value = (object?)edge.SourceStartColumn ?? DBNull.Value;
                splitInsEl.Value = (object?)edge.SourceEndLine ?? DBNull.Value;
                splitInsEc.Value = (object?)edge.SourceEndColumn ?? DBNull.Value;
                splitInsCg.Value = edge.IsCrossGenerated;
                splitInsTa.Value = (object?)edge.TypeArgumentsJson ?? DBNull.Value;
                splitInsRc.Value = (object?)edge.ReceiverTypeConstraintsJson ?? DBNull.Value;
                splitInsertCmd.ExecuteNonQuery();
            }
            else
            {
                var merged = EdgeMerge.MergeTypeArguments(existingTypeArgs as string, edge.TypeArgumentsJson);
                var mergedReceiverConstraints = EdgeMerge.MergeReceiverTypeConstraints(existingReceiverConstraints as string, edge.ReceiverTypeConstraintsJson);
                var incomingRank = EdgeMerge.ProvenanceRank(edge.Provenance ?? "");

                upSrc.Value = edge.SourceSymbolId;
                upTgt.Value = edge.TargetSymbolId;
                upKind.Value = edge.Kind;
                upProv.Value = (object?)edge.Provenance ?? DBNull.Value;
                upVer.Value = (object?)edge.ExtractorVersion ?? DBNull.Value;
                upDoc.Value = (object?)edge.SourceDocumentPath ?? DBNull.Value;
                upSl.Value = (object?)edge.SourceStartLine ?? DBNull.Value;
                upSc.Value = (object?)edge.SourceStartColumn ?? DBNull.Value;
                upEl.Value = (object?)edge.SourceEndLine ?? DBNull.Value;
                upEc.Value = (object?)edge.SourceEndColumn ?? DBNull.Value;
                upCg.Value = edge.IsCrossGenerated;
                upRc.Value = (object?)mergedReceiverConstraints ?? DBNull.Value;
                upMerged.Value = (object?)merged ?? DBNull.Value;
                upRank.Value = incomingRank;
                splitUpdateCmd.ExecuteNonQuery();
            }
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
            command.CommandText = """
                SELECT source_symbol_id, target_symbol_id, kind, provenance,
                       snapshot_id, extractor_version,
                       source_document_path, source_start_line, source_start_column,
                       source_end_line, source_end_column,
                       is_cross_generated, type_arguments_json, receiver_type_constraints_json
                FROM edges
                WHERE snapshot_id = @snapshotId
                  AND (source_symbol_id = @symbolId OR target_symbol_id = @symbolId)
                ORDER BY edge_id;
                """;
            command.Parameters.AddWithValue("@symbolId", symbolId);
        }
        else
        {
            command.CommandText = """
                SELECT source_symbol_id, target_symbol_id, kind, provenance,
                       snapshot_id, extractor_version,
                       source_document_path, source_start_line, source_start_column,
                       source_end_line, source_end_column,
                       is_cross_generated, type_arguments_json, receiver_type_constraints_json
                FROM edges
                WHERE snapshot_id = @snapshotId
                ORDER BY edge_id;
                """;
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
        command.CommandText = """
            SELECT source_symbol_id, target_symbol_id, kind, provenance,
                   snapshot_id, extractor_version,
                   source_document_path, source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json, receiver_type_constraints_json
            FROM edges
            WHERE snapshot_id = @snapshotId AND kind = @kind
            ORDER BY edge_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@kind", kind);

        return ReadEdgeRecords(command);
    }

    public List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT source_symbol_id, target_symbol_id, kind, provenance,
                   snapshot_id, extractor_version,
                   source_document_path, source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json, receiver_type_constraints_json
            FROM edges
            WHERE snapshot_id = @snapshotId AND target_symbol_id = @symbolId
            ORDER BY edge_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);

        return ReadEdgeRecords(command);
    }

    public List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT source_symbol_id, target_symbol_id, kind, provenance,
                   snapshot_id, extractor_version,
                   source_document_path, source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json, receiver_type_constraints_json
            FROM edges
            WHERE snapshot_id = @snapshotId AND source_symbol_id = @symbolId
            ORDER BY edge_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);

        return ReadEdgeRecords(command);
    }

    public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
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
                DELETE FROM edges
                WHERE snapshot_id = @snapshotId
                  AND source_document_path IN (
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

    public void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities)
    {
        var identityList = assemblyIdentities as IReadOnlyCollection<string> ?? [.. assemblyIdentities];
        if (identityList.Count == 0)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM edges
            WHERE snapshot_id = @snapshotId
              AND source_document_path IS NULL
              AND (
            """ + string.Join(" OR ", identityList.Select((_, i) => $"source_symbol_id LIKE @p{i} ESCAPE '\\'")) + """
        );
        """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var i = 0;
        foreach (var identity in identityList)
        {
            var escaped = identity.Replace(@"\", @"\\").Replace(@"%", @"\%").Replace(@"_", @"\_");
            command.Parameters.AddWithValue($"@p{i++}", "%|" + escaped);
        }

        command.ExecuteNonQuery();
    }

    public void DeleteEdgesWithNullDocumentPathForSymbols(string snapshotId, IEnumerable<string> symbolIds)
    {
        var idList = symbolIds as IReadOnlyCollection<string> ?? [.. symbolIds];
        if (idList.Count == 0)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM edges
            WHERE snapshot_id = @snapshotId
              AND source_document_path IS NULL
              AND source_symbol_id IN (
            """ + string.Join(", ", idList.Select((_, i) => $"@p{i}")) + """
        );
        """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var i = 0;
        foreach (var id in idList)
            command.Parameters.AddWithValue($"@p{i++}", id);
        command.ExecuteNonQuery();
    }

    public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json)
            SELECT @toSnapshotId, source_symbol_id, target_symbol_id, kind, provenance,
                   extractor_version, source_document_path,
                   source_start_line, source_start_column,
                   source_end_line, source_end_column,
                   is_cross_generated, type_arguments_json, receiver_type_constraints_json
            FROM edges
            WHERE snapshot_id = @fromSnapshotId;
            """;
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();

        using var memberCmd = _connection.CreateCommand();
        memberCmd.CommandText = """
            INSERT OR IGNORE INTO snapshot_graph_nodes (snapshot_id, node_id)
            SELECT @toSnapshotId, node_id
            FROM snapshot_graph_nodes
            WHERE snapshot_id = @fromSnapshotId;
            """;
        memberCmd.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        memberCmd.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        memberCmd.ExecuteNonQuery();
    }

    public OrphanEdgeDropSummary DeleteOrphanEdges(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            WITH valid_ids(id) AS (
                SELECT symbol_id FROM snapshot_symbols WHERE snapshot_id = @snapshotId
                UNION
                SELECT node_id FROM snapshot_graph_nodes WHERE snapshot_id = @snapshotId
            ),
            in_scope_assemblies(identity) AS (
                -- Derive in-scope assemblies from DECLARATIONS only, never the full
                -- valid_ids union. snapshot_graph_nodes registers external ExternalType
                -- boundary nodes (e.g. FluentValidation, EF Core, MassTransit), so unioning
                -- them here pollutes the set with dependency assemblies and makes genuinely
                -- external orphan endpoints fail the 'external' test and fall through to
                -- 'other'. Declarations are only ever the solution's own compiled symbols.
                SELECT DISTINCT substr(symbol_id, instr(symbol_id,'|')+1)
                FROM snapshot_symbols
                WHERE snapshot_id = @snapshotId AND instr(symbol_id,'|') > 0
            )
            DELETE FROM edges
            WHERE snapshot_id = @snapshotId
              AND (
                  source_symbol_id NOT IN (SELECT id FROM valid_ids)
                  OR target_symbol_id NOT IN (SELECT id FROM valid_ids)
              )
            RETURNING
                CASE
                    -- 'compiler_synthesized' covers orphan endpoints that are not user-declared
                    -- source symbols and could never be declarations, so their absence is
                    -- expected rather than a gap:
                    --   LIKE '%<%'   compiler-generated names (<>c display classes, k__BackingField)
                    --   LIKE '_:.%'  anonymous-type members — empty containing type, e.g. P:.Id
                    --   LIKE '!:%'   Roslyn unresolved/error symbols, e.g. unbound type params !:T
                    --   AutoGeneratedProgram  synthesized top-level-statements entry point
                    -- Source-generator output ([GeneratedRegex]) in the generated namespace is
                    -- never surfaced as a project document, so its symbols are never declared.
                    -- Classify it as compiler-synthesized only when the OTHER endpoint is not
                    -- an out-of-scope orphan: an edge whose other endpoint is external keeps
                    -- its 'external' reason.
                    WHEN (source_symbol_id NOT IN (SELECT id FROM valid_ids) AND (
                              source_symbol_id LIKE '%<%'
                           OR source_symbol_id LIKE '_:.%'
                           OR source_symbol_id LIKE '!:%'
                           OR source_symbol_id LIKE '%AutoGeneratedProgram%'))
                      OR (target_symbol_id NOT IN (SELECT id FROM valid_ids) AND (
                              target_symbol_id LIKE '%<%'
                           OR target_symbol_id LIKE '_:.%'
                           OR target_symbol_id LIKE '!:%'
                           OR target_symbol_id LIKE '%AutoGeneratedProgram%'))
                      OR (source_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND source_symbol_id LIKE '%System.Text.RegularExpressions.Generated%'
                          AND (target_symbol_id IN (SELECT id FROM valid_ids)
                               OR substr(target_symbol_id, instr(target_symbol_id,'|')+1) IN (SELECT identity FROM in_scope_assemblies)))
                      OR (target_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND target_symbol_id LIKE '%System.Text.RegularExpressions.Generated%'
                          AND (source_symbol_id IN (SELECT id FROM valid_ids)
                               OR substr(source_symbol_id, instr(source_symbol_id,'|')+1) IN (SELECT identity FROM in_scope_assemblies)))
                    THEN 'compiler_synthesized'
                    WHEN (source_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND substr(source_symbol_id, instr(source_symbol_id,'|')+1) NOT IN (SELECT identity FROM in_scope_assemblies))
                      OR (target_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND substr(target_symbol_id, instr(target_symbol_id,'|')+1) NOT IN (SELECT identity FROM in_scope_assemblies))
                    THEN 'external'
                    ELSE 'other'
                END;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        int external = 0, compilerSynthesized = 0, other = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            switch (reader.GetString(0))
            {
                case "compiler_synthesized": compilerSynthesized++; break;
                case "external": external++; break;
                case "other": other++; break;
            }

        var total = external + compilerSynthesized + other;
        return new OrphanEdgeDropSummary(total, external, compilerSynthesized, other);
    }

    public void PruneSnapshotGraphNodes(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM snapshot_graph_nodes
            WHERE snapshot_id = @snapshotId
              AND node_id NOT IN (
                  SELECT source_symbol_id FROM edges WHERE snapshot_id = @snapshotId
                  UNION
                  SELECT target_symbol_id FROM edges WHERE snapshot_id = @snapshotId
                  UNION
                  SELECT symbol_id FROM snapshot_symbols WHERE snapshot_id = @snapshotId
              );
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    private static List<EdgeRecord> ReadEdgeRecords(SqliteCommand command)
    {
        var results = new List<EdgeRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
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
                ReceiverTypeConstraintsJson = reader.IsDBNull(13) ? null : reader.GetString(13)
            });
        return results;
    }
}