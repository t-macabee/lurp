using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace Lurp.Storage;

internal sealed class EdgeOperationsStore
{
    private readonly SqliteConnection _connection;

    public EdgeOperationsStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    ///     The one place that collapses edges (D6): the batch is collapsed by
    ///     (source, target, kind), then merged into the rows of the snapshot.
    /// </summary>
    /// <returns>The number of relations in the collapsed batch.</returns>
    public int SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
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
            using var memberCmd = _connection.CreateCommand();
            memberCmd.Transaction = transaction;
            memberCmd.CommandText = """
                INSERT OR IGNORE INTO snapshot_graph_nodes (snapshot_id, node_id)
                VALUES (@snapshotId, @nodeId);
                """;
            memberCmd.Parameters.Add(new SqliteParameter("@snapshotId", snapshotId));
            var memberNodeParam = memberCmd.Parameters.Add(new SqliteParameter("@nodeId", null));

            foreach (var edge in collapsed)
            {
                RegisterGraphNode(memberCmd, memberNodeParam, edge.SourceSymbolId, edge.SourceIsBoundary);
                RegisterGraphNode(memberCmd, memberNodeParam, edge.TargetSymbolId, edge.TargetIsBoundary);
            }

            using var insertSession = new EdgeInsertSession(_connection, transaction, snapshotId);
            using var siteSession = new SiteWriteSession(_connection, transaction);

            if (bulkEdges.Count > 0) WriteBulkEdges(snapshotId, bulkEdges, transaction, insertSession, siteSession);

            if (splitEdges.Count > 0) WriteSplitEdges(snapshotId, splitEdges, transaction, insertSession, siteSession);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        return collapsed.Count;
    }

    private Dictionary<(string SourceSymbolId, string TargetSymbolId, string Kind), (long EdgeId, int Rank)>
        FetchExistingEdges(string snapshotId, List<EdgeRecord> edges, SqliteTransaction transaction)
    {
        var existing = new Dictionary<(string, string, string), (long, int)>();

        using (var anyCmd = _connection.CreateCommand())
        {
            anyCmd.Transaction = transaction;
            anyCmd.CommandText = "SELECT 1 FROM edges WHERE snapshot_id = @snapshotId LIMIT 1;";
            anyCmd.Parameters.Add(new SqliteParameter("@snapshotId", snapshotId));
            if (anyCmd.ExecuteScalar() == null)
                return existing;
        }

        const int chunkSize = 2000;
        using var lookupCmd = _connection.CreateCommand();
        lookupCmd.Transaction = transaction;
        lookupCmd.CommandText = """
            SELECT e.source_symbol_id, e.target_symbol_id, e.kind, e.edge_id, e.provenance
            FROM json_each(@triples) j
            CROSS JOIN edges e
              ON e.snapshot_id = @snapshotId
             AND e.source_symbol_id = json_extract(j.value, '$[0]')
             AND e.target_symbol_id = json_extract(j.value, '$[1]')
             AND e.kind = json_extract(j.value, '$[2]');
            """;
        lookupCmd.Parameters.Add(new SqliteParameter("@snapshotId", snapshotId));
        var triplesParam = lookupCmd.Parameters.Add(new SqliteParameter("@triples", string.Empty));

        for (var offset = 0; offset < edges.Count; offset += chunkSize)
        {
            triplesParam.Value = JsonSerializer.Serialize(
                edges.Skip(offset).Take(chunkSize).Select(static e => new[] { e.SourceSymbolId, e.TargetSymbolId, e.Kind }));

            using var reader = lookupCmd.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
                var edgeId = reader.GetInt64(3);
                var provenance = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                existing[key] = (edgeId, EdgeMerge.ProvenanceRank(provenance));
            }
        }

        return existing;
    }

    private void WriteBulkEdges(
        string snapshotId,
        List<EdgeRecord> edges,
        SqliteTransaction transaction,
        EdgeInsertSession insertSession,
        SiteWriteSession siteSession)
    {
        var existing = FetchExistingEdges(snapshotId, edges, transaction);

        using var updateCmd = _connection.CreateCommand();
        updateCmd.Transaction = transaction;
        updateCmd.CommandText = """
            UPDATE edges SET
                provenance = @provenance,
                extractor_version = @extractorVersion,
                source_document_path = @sourceDocumentPath,
                source_start_line = @sourceStartLine,
                source_start_column = @sourceStartColumn,
                source_end_line = @sourceEndLine,
                source_end_column = @sourceEndColumn,
                is_cross_generated = @isCrossGenerated,
                receiver_type_constraints_json = @receiverTypeConstraintsJson
            WHERE edge_id = @edgeId;
            """;
        var upEdgeId = updateCmd.Parameters.Add(new SqliteParameter("@edgeId", 0L));
        var upProv = updateCmd.Parameters.Add(new SqliteParameter("@provenance", null));
        var upVer = updateCmd.Parameters.Add(new SqliteParameter("@extractorVersion", null));
        var upDoc = updateCmd.Parameters.Add(new SqliteParameter("@sourceDocumentPath", null));
        var upSl = updateCmd.Parameters.Add(new SqliteParameter("@sourceStartLine", null));
        var upSc = updateCmd.Parameters.Add(new SqliteParameter("@sourceStartColumn", null));
        var upEl = updateCmd.Parameters.Add(new SqliteParameter("@sourceEndLine", null));
        var upEc = updateCmd.Parameters.Add(new SqliteParameter("@sourceEndColumn", null));
        var upCg = updateCmd.Parameters.Add(new SqliteParameter("@isCrossGenerated", null));
        var upRc = updateCmd.Parameters.Add(new SqliteParameter("@receiverTypeConstraintsJson", null));

        foreach (var edge in edges)
        {
            var incomingRank = EdgeMerge.ProvenanceRank(edge.Provenance ?? "");
            long edgeId;
            int? oldRank;

            if (!existing.TryGetValue((edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind), out var persisted))
            {
                edgeId = insertSession.Insert(edge);
                oldRank = null;
            }
            else
            {
                edgeId = persisted.EdgeId;
                oldRank = persisted.Rank;
                if (incomingRank > persisted.Rank)
                {
                    upEdgeId.Value = edgeId;
                    upProv.Value = (object?)edge.Provenance ?? DBNull.Value;
                    upVer.Value = (object?)edge.ExtractorVersion ?? DBNull.Value;
                    upDoc.Value = (object?)edge.SourceDocumentPath ?? DBNull.Value;
                    upSl.Value = (object?)edge.SourceStartLine ?? DBNull.Value;
                    upSc.Value = (object?)edge.SourceStartColumn ?? DBNull.Value;
                    upEl.Value = (object?)edge.SourceEndLine ?? DBNull.Value;
                    upEc.Value = (object?)edge.SourceEndColumn ?? DBNull.Value;
                    upCg.Value = edge.IsCrossGenerated;
                    upRc.Value = (object?)edge.ReceiverTypeConstraintsJson ?? DBNull.Value;
                    updateCmd.ExecuteNonQuery();
                }
            }

            siteSession.WriteSites(edgeId, incomingRank, oldRank, edge.Sites);
        }
    }

    private void WriteSplitEdges(
        string snapshotId,
        List<EdgeRecord> edges,
        SqliteTransaction transaction,
        EdgeInsertSession insertSession,
        SiteWriteSession siteSession)
    {
        using var selectCmd = _connection.CreateCommand();
        selectCmd.Transaction = transaction;
        selectCmd.CommandText = "SELECT edge_id, type_arguments_json, receiver_type_constraints_json, provenance FROM edges WHERE snapshot_id = @s AND source_symbol_id = @src AND target_symbol_id = @tgt AND kind = @k LIMIT 1;";
        selectCmd.Parameters.Add(new SqliteParameter("@s", snapshotId));
        var selectSrc = selectCmd.Parameters.Add(new SqliteParameter("@src", null));
        var selectTgt = selectCmd.Parameters.Add(new SqliteParameter("@tgt", null));
        var selectK = selectCmd.Parameters.Add(new SqliteParameter("@k", null));

        using var splitUpdateWinnerCmd = _connection.CreateCommand();
        splitUpdateWinnerCmd.Transaction = transaction;
        splitUpdateWinnerCmd.CommandText = """
            UPDATE edges SET
                type_arguments_json = @mergedTypeArguments,
                provenance = @provenance,
                extractor_version = @extractorVersion,
                source_document_path = @sourceDocumentPath,
                source_start_line = @sourceStartLine,
                source_start_column = @sourceStartColumn,
                source_end_line = @sourceEndLine,
                source_end_column = @sourceEndColumn,
                is_cross_generated = @isCrossGenerated,
                receiver_type_constraints_json = @receiverTypeConstraintsJson
            WHERE edge_id = @edgeId;
            """;
        var upEdgeId = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@edgeId", 0L));
        var upProv = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@provenance", null));
        var upVer = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@extractorVersion", null));
        var upDoc = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@sourceDocumentPath", null));
        var upSl = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@sourceStartLine", null));
        var upSc = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@sourceStartColumn", null));
        var upEl = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@sourceEndLine", null));
        var upEc = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@sourceEndColumn", null));
        var upCg = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@isCrossGenerated", null));
        var upRc = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@receiverTypeConstraintsJson", null));
        var upMerged = splitUpdateWinnerCmd.Parameters.Add(new SqliteParameter("@mergedTypeArguments", null));

        using var splitUpdatePayloadsCmd = _connection.CreateCommand();
        splitUpdatePayloadsCmd.Transaction = transaction;
        splitUpdatePayloadsCmd.CommandText = """
            UPDATE edges SET
                type_arguments_json = @mergedTypeArguments,
                receiver_type_constraints_json = @receiverTypeConstraintsJson
            WHERE edge_id = @edgeId;
            """;
        var payEdgeId = splitUpdatePayloadsCmd.Parameters.Add(new SqliteParameter("@edgeId", 0L));
        var payMerged = splitUpdatePayloadsCmd.Parameters.Add(new SqliteParameter("@mergedTypeArguments", null));
        var payRc = splitUpdatePayloadsCmd.Parameters.Add(new SqliteParameter("@receiverTypeConstraintsJson", null));

        foreach (var edge in edges)
        {
            selectSrc.Value = edge.SourceSymbolId;
            selectTgt.Value = edge.TargetSymbolId;
            selectK.Value = edge.Kind;

            long edgeId = 0;
            string? existingTypeArgs = null;
            string? existingReceiverConstraints = null;
            string? existingProvenance = null;
            bool hasRow;
            using (var reader = selectCmd.ExecuteReader())
            {
                hasRow = reader.Read();
                if (hasRow)
                {
                    edgeId = reader.GetInt64(0);
                    existingTypeArgs = reader.IsDBNull(1) ? null : reader.GetString(1);
                    existingReceiverConstraints = reader.IsDBNull(2) ? null : reader.GetString(2);
                    existingProvenance = reader.IsDBNull(3) ? null : reader.GetString(3);
                }
            }

            var incomingRank = EdgeMerge.ProvenanceRank(edge.Provenance ?? "");
            int? oldRank = hasRow ? EdgeMerge.ProvenanceRank(existingProvenance ?? "") : null;

            if (!hasRow)
            {
                edgeId = insertSession.Insert(edge);
            }
            else
            {
                var merged = EdgeMerge.MergeTypeArguments(existingTypeArgs, edge.TypeArgumentsJson);
                var mergedReceiverConstraints = EdgeMerge.MergeReceiverTypeConstraints(existingReceiverConstraints, edge.ReceiverTypeConstraintsJson);

                if (incomingRank > oldRank!.Value)
                {
                    upEdgeId.Value = edgeId;
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
                    splitUpdateWinnerCmd.ExecuteNonQuery();
                }
                else if (!string.Equals(merged, existingTypeArgs, StringComparison.Ordinal) ||
                         !string.Equals(mergedReceiverConstraints, existingReceiverConstraints, StringComparison.Ordinal))
                {
                    payEdgeId.Value = edgeId;
                    payMerged.Value = (object?)merged ?? DBNull.Value;
                    payRc.Value = (object?)mergedReceiverConstraints ?? DBNull.Value;
                    splitUpdatePayloadsCmd.ExecuteNonQuery();
                }
            }

            siteSession.WriteSites(edgeId, incomingRank, oldRank, edge.Sites);
        }
    }

    private sealed class EdgeInsertSession : IDisposable
    {
        private readonly SqliteCommand _insertCmd;
        private readonly SqliteParameter _src;
        private readonly SqliteParameter _tgt;
        private readonly SqliteParameter _kind;
        private readonly SqliteParameter _prov;
        private readonly SqliteParameter _ver;
        private readonly SqliteParameter _doc;
        private readonly SqliteParameter _sl;
        private readonly SqliteParameter _sc;
        private readonly SqliteParameter _el;
        private readonly SqliteParameter _ec;
        private readonly SqliteParameter _cg;
        private readonly SqliteParameter _ta;
        private readonly SqliteParameter _rc;

        public EdgeInsertSession(SqliteConnection connection, SqliteTransaction transaction, string snapshotId)
        {
            _insertCmd = connection.CreateCommand();
            _insertCmd.Transaction = transaction;
            _insertCmd.CommandText = """
                INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance, extractor_version, source_document_path, source_start_line, source_start_column, source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json)
                VALUES (@snapshotId, @sourceSymbolId, @targetSymbolId, @kind, @provenance, @extractorVersion, @sourceDocumentPath, @sourceStartLine, @sourceStartColumn, @sourceEndLine, @sourceEndColumn, @isCrossGenerated, @typeArgumentsJson, @receiverTypeConstraintsJson)
                RETURNING edge_id;
                """;
            _insertCmd.Parameters.Add(new SqliteParameter("@snapshotId", snapshotId));
            _src = _insertCmd.Parameters.Add(new SqliteParameter("@sourceSymbolId", null));
            _tgt = _insertCmd.Parameters.Add(new SqliteParameter("@targetSymbolId", null));
            _kind = _insertCmd.Parameters.Add(new SqliteParameter("@kind", null));
            _prov = _insertCmd.Parameters.Add(new SqliteParameter("@provenance", null));
            _ver = _insertCmd.Parameters.Add(new SqliteParameter("@extractorVersion", null));
            _doc = _insertCmd.Parameters.Add(new SqliteParameter("@sourceDocumentPath", null));
            _sl = _insertCmd.Parameters.Add(new SqliteParameter("@sourceStartLine", null));
            _sc = _insertCmd.Parameters.Add(new SqliteParameter("@sourceStartColumn", null));
            _el = _insertCmd.Parameters.Add(new SqliteParameter("@sourceEndLine", null));
            _ec = _insertCmd.Parameters.Add(new SqliteParameter("@sourceEndColumn", null));
            _cg = _insertCmd.Parameters.Add(new SqliteParameter("@isCrossGenerated", null));
            _ta = _insertCmd.Parameters.Add(new SqliteParameter("@typeArgumentsJson", null));
            _rc = _insertCmd.Parameters.Add(new SqliteParameter("@receiverTypeConstraintsJson", null));
        }

        public long Insert(EdgeRecord edge)
        {
            _src.Value = edge.SourceSymbolId;
            _tgt.Value = edge.TargetSymbolId;
            _kind.Value = edge.Kind;
            _prov.Value = (object?)edge.Provenance ?? DBNull.Value;
            _ver.Value = (object?)edge.ExtractorVersion ?? DBNull.Value;
            _doc.Value = (object?)edge.SourceDocumentPath ?? DBNull.Value;
            _sl.Value = (object?)edge.SourceStartLine ?? DBNull.Value;
            _sc.Value = (object?)edge.SourceStartColumn ?? DBNull.Value;
            _el.Value = (object?)edge.SourceEndLine ?? DBNull.Value;
            _ec.Value = (object?)edge.SourceEndColumn ?? DBNull.Value;
            _cg.Value = edge.IsCrossGenerated;
            _ta.Value = (object?)edge.TypeArgumentsJson ?? DBNull.Value;
            _rc.Value = (object?)edge.ReceiverTypeConstraintsJson ?? DBNull.Value;
            return Convert.ToInt64(_insertCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public void Dispose() => _insertCmd.Dispose();
    }

    private sealed class SiteWriteSession : IDisposable
    {
        private readonly SqliteCommand _deleteCmd;
        private readonly SqliteParameter _deleteEdgeId;
        private readonly SqliteCommand _insertCmd;
        private readonly SqliteParameter _insertEdgeId;
        private readonly SqliteParameter _insertDoc;
        private readonly SqliteParameter _insertSl;
        private readonly SqliteParameter _insertSc;
        private readonly SqliteParameter _insertEl;
        private readonly SqliteParameter _insertEc;
        private readonly SqliteCommand _firstCmd;
        private readonly SqliteParameter _firstEdgeId;
        private readonly SqliteCommand _updateCmd;
        private readonly SqliteParameter _updateEdgeId;
        private readonly SqliteParameter _updateDoc;
        private readonly SqliteParameter _updateSl;
        private readonly SqliteParameter _updateSc;
        private readonly SqliteParameter _updateEl;
        private readonly SqliteParameter _updateEc;

        public SiteWriteSession(SqliteConnection connection, SqliteTransaction transaction)
        {
            _deleteCmd = connection.CreateCommand();
            _deleteCmd.Transaction = transaction;
            _deleteCmd.CommandText = "DELETE FROM edge_sites WHERE edge_id = @edgeId;";
            _deleteEdgeId = _deleteCmd.Parameters.Add(new SqliteParameter("@edgeId", 0L));

            _insertCmd = connection.CreateCommand();
            _insertCmd.Transaction = transaction;
            _insertCmd.CommandText = """
                INSERT OR IGNORE INTO edge_sites (edge_id, document_path, start_line, start_column, end_line, end_column)
                VALUES (@edgeId, @doc, @sl, @sc, @el, @ec);
                """;
            _insertEdgeId = _insertCmd.Parameters.Add(new SqliteParameter("@edgeId", 0L));
            _insertDoc = _insertCmd.Parameters.Add(new SqliteParameter("@doc", null));
            _insertSl = _insertCmd.Parameters.Add(new SqliteParameter("@sl", 0));
            _insertSc = _insertCmd.Parameters.Add(new SqliteParameter("@sc", 0));
            _insertEl = _insertCmd.Parameters.Add(new SqliteParameter("@el", 0));
            _insertEc = _insertCmd.Parameters.Add(new SqliteParameter("@ec", 0));

            _firstCmd = connection.CreateCommand();
            _firstCmd.Transaction = transaction;
            _firstCmd.CommandText = """
                SELECT document_path, start_line, start_column, end_line, end_column
                FROM edge_sites
                WHERE edge_id = @edgeId
                ORDER BY document_path, start_line, start_column, end_line, end_column
                LIMIT 1;
                """;
            _firstEdgeId = _firstCmd.Parameters.Add(new SqliteParameter("@edgeId", 0L));

            _updateCmd = connection.CreateCommand();
            _updateCmd.Transaction = transaction;
            _updateCmd.CommandText = """
                UPDATE edges
                SET source_document_path = @doc,
                    source_start_line = @sl,
                    source_start_column = @sc,
                    source_end_line = @el,
                    source_end_column = @ec
                WHERE edge_id = @edgeId;
                """;
            _updateEdgeId = _updateCmd.Parameters.Add(new SqliteParameter("@edgeId", 0L));
            _updateDoc = _updateCmd.Parameters.Add(new SqliteParameter("@doc", null));
            _updateSl = _updateCmd.Parameters.Add(new SqliteParameter("@sl", 0));
            _updateSc = _updateCmd.Parameters.Add(new SqliteParameter("@sc", 0));
            _updateEl = _updateCmd.Parameters.Add(new SqliteParameter("@el", 0));
            _updateEc = _updateCmd.Parameters.Add(new SqliteParameter("@ec", 0));
        }

        public void WriteSites(long edgeId, int incomingRank, int? oldRank, IReadOnlyList<EdgeSite> sites)
        {
            if (oldRank.HasValue && incomingRank < oldRank.Value)
                return;

            if (oldRank.HasValue && incomingRank > oldRank.Value)
            {
                _deleteEdgeId.Value = edgeId;
                _deleteCmd.ExecuteNonQuery();
            }

            if (sites.Count == 0)
                return;

            _insertEdgeId.Value = edgeId;
            foreach (var site in sites)
            {
                _insertDoc.Value = site.DocumentPath;
                _insertSl.Value = site.StartLine;
                _insertSc.Value = site.StartColumn;
                _insertEl.Value = site.EndLine;
                _insertEc.Value = site.EndColumn;
                _insertCmd.ExecuteNonQuery();
            }

            if (oldRank.HasValue && incomingRank == oldRank.Value)
            {
                _firstEdgeId.Value = edgeId;
                using var reader = _firstCmd.ExecuteReader();
                if (reader.Read())
                {
                    _updateEdgeId.Value = edgeId;
                    _updateDoc.Value = reader.GetString(0);
                    _updateSl.Value = reader.GetInt32(1);
                    _updateSc.Value = reader.GetInt32(2);
                    _updateEl.Value = reader.GetInt32(3);
                    _updateEc.Value = reader.GetInt32(4);
                    _updateCmd.ExecuteNonQuery();
                }
            }
        }

        public void Dispose()
        {
            _deleteCmd.Dispose();
            _insertCmd.Dispose();
            _firstCmd.Dispose();
            _updateCmd.Dispose();
        }
    }

    private static void RegisterGraphNode(
        SqliteCommand memberCmd, SqliteParameter memberNodeParam,
        string nodeId, bool isBoundary)
    {
        if (!isBoundary)
            return;

        memberNodeParam.Value = nodeId;
        memberCmd.ExecuteNonQuery();
    }

    private const string EdgeRecordColumns =
        "source_symbol_id, target_symbol_id, kind, provenance, snapshot_id, extractor_version, " +
        "source_document_path, source_start_line, source_start_column, source_end_line, " +
        "source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json";

    public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
    {
        using var command = _connection.CreateCommand();
        if (symbolId != null)
        {
            command.CommandText = $"""
                SELECT {EdgeRecordColumns}
                FROM edges
                WHERE snapshot_id = @snapshotId
                  AND (source_symbol_id = @symbolId OR target_symbol_id = @symbolId)
                ORDER BY edge_id;
                """;
            command.Parameters.AddWithValue("@symbolId", symbolId);
        }
        else
        {
            command.CommandText = $"""
                SELECT {EdgeRecordColumns}
                FROM edges
                WHERE snapshot_id = @snapshotId
                ORDER BY edge_id;
                """;
        }

        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        return ReadEdgeRecords(command);
    }

    /// <summary>
    ///     Returns the snapshot's edges whose source or target is in the list, in
    ///     <c>edge_id</c> order. It returns the same rows as a full <see cref="GetEdges(string)" />
    ///     filtered in memory; the <c>CROSS JOIN</c> keeps the list as the outer loop (B47).
    /// </summary>
    internal static string EdgesTouchingSymbolsSql() => $"""
        SELECT {EdgeRecordColumns}
        FROM edges
        WHERE edge_id IN (
            SELECT e.edge_id FROM json_each(@symbolIds) j CROSS JOIN edges e
            WHERE e.snapshot_id = @snapshotId AND e.source_symbol_id = j.value
            UNION
            SELECT e.edge_id FROM json_each(@symbolIds) j CROSS JOIN edges e
            WHERE e.snapshot_id = @snapshotId AND e.target_symbol_id = j.value)
        ORDER BY edge_id;
        """;

    public List<EdgeRecord> GetEdgesTouchingSymbols(string snapshotId, IEnumerable<string> symbolIds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = EdgesTouchingSymbolsSql();
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@symbolIds", symbolIds);
        return ReadEdgeRecords(command);
    }

    public int CountEdges(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edges WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EdgeRecordColumns}
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
        command.CommandText = $"""
            SELECT {EdgeRecordColumns}
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
        command.CommandText = $"""
            SELECT {EdgeRecordColumns}
            FROM edges
            WHERE snapshot_id = @snapshotId AND source_symbol_id = @symbolId
            ORDER BY edge_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);

        return ReadEdgeRecords(command);
    }

    // The unary + keeps the planner off idx_edges_snapshot_id, so the path list drives
    // idx_edges_document (B16 U1b).
    internal static string DeleteEdgesByDocumentPathsSql() => """
        DELETE FROM edges
        WHERE +snapshot_id = @snapshotId
          AND source_document_path IN (SELECT value FROM json_each(@documentPaths));
        """;

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
              AND source_symbol_id IN (SELECT value FROM json_each(@symbolIds));
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@symbolIds", idList);
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

        using var siteCmd = _connection.CreateCommand();
        siteCmd.CommandText = """
            INSERT OR IGNORE INTO edge_sites (edge_id, document_path, start_line, start_column, end_line, end_column)
            SELECT new_e.edge_id, es.document_path, es.start_line, es.start_column, es.end_line, es.end_column
            FROM edge_sites es
            JOIN edges old_e ON old_e.edge_id = es.edge_id
            JOIN edges new_e ON new_e.snapshot_id = @toSnapshotId
                AND new_e.source_symbol_id = old_e.source_symbol_id
                AND new_e.target_symbol_id = old_e.target_symbol_id
                AND new_e.kind = old_e.kind
            WHERE old_e.snapshot_id = @fromSnapshotId;
            """;
        siteCmd.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        siteCmd.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        siteCmd.ExecuteNonQuery();

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
                -- valid_ids union. snapshot_graph_nodes registers external boundary
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
                    --   LIKE '%Microsoft.AspNetCore.OpenApi.Generated.%'  ASP.NET Core OpenAPI source-generated symbols
                    --   LIKE '%System.Runtime.CompilerServices.InterceptsLocationAttribute%'  interceptors source-generator location attribute
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
                      OR (source_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND source_symbol_id LIKE '%Microsoft.AspNetCore.OpenApi.Generated.%'
                          AND (target_symbol_id IN (SELECT id FROM valid_ids)
                               OR substr(target_symbol_id, instr(target_symbol_id,'|')+1) IN (SELECT identity FROM in_scope_assemblies)))
                      OR (target_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND target_symbol_id LIKE '%Microsoft.AspNetCore.OpenApi.Generated.%'
                          AND (source_symbol_id IN (SELECT id FROM valid_ids)
                               OR substr(source_symbol_id, instr(source_symbol_id,'|')+1) IN (SELECT identity FROM in_scope_assemblies)))
                      OR (source_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND source_symbol_id LIKE '%System.Runtime.CompilerServices.InterceptsLocationAttribute%'
                          AND (target_symbol_id IN (SELECT id FROM valid_ids)
                               OR substr(target_symbol_id, instr(target_symbol_id,'|')+1) IN (SELECT identity FROM in_scope_assemblies)))
                      OR (target_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND target_symbol_id LIKE '%System.Runtime.CompilerServices.InterceptsLocationAttribute%'
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
              AND (
                  node_id NOT IN (
                      SELECT source_symbol_id FROM edges WHERE snapshot_id = @snapshotId
                      UNION
                      SELECT target_symbol_id FROM edges WHERE snapshot_id = @snapshotId
                  )
                  OR node_id IN (
                      SELECT symbol_id FROM snapshot_symbols WHERE snapshot_id = @snapshotId
                  )
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
            results.Add(EdgeRecordReader.Read(reader, 0));
        return results;
    }

    public Dictionary<(string SourceSymbolId, string TargetSymbolId, string Kind), List<EdgeSite>> GetEdgeSites(
        string snapshotId,
        IEnumerable<(string SourceSymbolId, string TargetSymbolId, string Kind)> edges)
    {
        var result = new Dictionary<(string SourceSymbolId, string TargetSymbolId, string Kind), List<EdgeSite>>();
        var distinctTriples = edges.Distinct().ToList();
        if (distinctTriples.Count == 0)
            return result;

        var triplesJson = JsonSerializer.Serialize(
            distinctTriples.Select(static t => new[] { t.SourceSymbolId, t.TargetSymbolId, t.Kind }));

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.source_symbol_id, e.target_symbol_id, e.kind,
                   es.document_path, es.start_line, es.start_column, es.end_line, es.end_column
            FROM json_each(@triples) j
            CROSS JOIN edges e
              ON e.snapshot_id = @snapshotId
             AND e.source_symbol_id = json_extract(j.value, '$[0]')
             AND e.target_symbol_id = json_extract(j.value, '$[1]')
             AND e.kind = json_extract(j.value, '$[2]')
            JOIN edge_sites es ON es.edge_id = e.edge_id
            ORDER BY e.source_symbol_id, e.target_symbol_id, e.kind,
                     es.document_path, es.start_line, es.start_column, es.end_line, es.end_column;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@triples", triplesJson);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            list.Add(new EdgeSite(
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }

        return result;
    }
}