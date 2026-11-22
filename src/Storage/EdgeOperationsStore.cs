using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace Lurp.Storage;

internal sealed class EdgeOperationsStore
{
    private const string SiteOrderSql =
        "document_path COLLATE lurp_ordinal, start_line, start_column, end_line, end_column";

    internal const string ProjectEvidenceSql = $"""
        UPDATE edges
        SET provenance = winner.provenance,
            extractor_version = winner.extractor_version,
            is_cross_generated = winner.is_cross_generated,
            source_document_path = winner.document_path,
            source_start_line = winner.start_line,
            source_start_column = winner.start_column,
            source_end_line = winner.end_line,
            source_end_column = winner.end_column
        FROM edge_sites AS winner
        WHERE edges.edge_id IN (SELECT value FROM json_each(@edgeIds))
          AND (@locatedOnly = 0 OR edges.source_document_path IS NOT NULL)
          AND winner.rowid = (
              SELECT rowid
              FROM edge_sites
              WHERE edge_id = edges.edge_id
              ORDER BY lurp_provenance_rank(provenance) DESC, {SiteOrderSql}, provenance COLLATE lurp_ordinal
              LIMIT 1
          )
          AND (@keepUnlocated = 0 OR edges.source_document_path IS NOT NULL
               OR lurp_provenance_rank(winner.provenance) >= lurp_provenance_rank(edges.provenance));
        """;

    private readonly SqliteConnection _connection;

    public EdgeOperationsStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        // Use the same UTF-16 path order as EdgeMerge.CompareSites.
        _connection.CreateCollation("lurp_ordinal", string.CompareOrdinal);
        _connection.CreateFunction<string?, int>("lurp_provenance_rank", EdgeMerge.ProvenanceRank, isDeterministic: true);
    }

    /// <summary>
    ///     The one place that collapses edges (D6): the batch is collapsed by
    ///     (source, target, kind), then merged into the rows of the snapshot.
    /// </summary>
    /// <returns>The number of relations in the collapsed batch.</returns>
    public int SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
    {
        var collapsed = EdgeMerge.CollapseBatch(edges);
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

            var existing = FetchExistingEdges(snapshotId, collapsed, transaction);
            var mergedEdges = new List<long>(existing.Count);
            foreach (var edge in collapsed)
            {
                var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
                if (existing.TryGetValue(key, out var edgeId))
                {
                    if (edge.TypeArgumentsJson != null || edge.ReceiverTypeConstraintsJson != null)
                        MergePayloads(transaction, edgeId, edge);
                    if (edge.SourceDocumentPath == null)
                        PromoteUnlocatedWinner(transaction, edgeId, edge);
                    mergedEdges.Add(edgeId);
                }
                else
                {
                    edgeId = insertSession.Insert(edge);
                }

                siteSession.WriteSites(edgeId, edge.Evidence);
            }

            ProjectEvidence(transaction, mergedEdges, keepUnlocated: true);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        return collapsed.Count;
    }

    private Dictionary<(string SourceSymbolId, string TargetSymbolId, string Kind), long>
        FetchExistingEdges(string snapshotId, List<EdgeRecord> edges, SqliteTransaction transaction)
    {
        var existing = new Dictionary<(string, string, string), long>();

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
            SELECT e.source_symbol_id, e.target_symbol_id, e.kind, e.edge_id
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
                existing[key] = reader.GetInt64(3);
            }
        }

        return existing;
    }

    private void MergePayloads(SqliteTransaction transaction, long edgeId, EdgeRecord edge)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT type_arguments_json, receiver_type_constraints_json FROM edges WHERE edge_id = @edgeId;
            """;
        command.Parameters.AddWithValue("@edgeId", edgeId);

        string? typeArguments;
        string? constraints;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            typeArguments = reader.IsDBNull(0) ? null : reader.GetString(0);
            constraints = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var mergedTypeArguments = EdgeMerge.MergeTypeArguments(typeArguments, edge.TypeArgumentsJson);
        var mergedConstraints = EdgeMerge.MergeReceiverTypeConstraints(constraints, edge.ReceiverTypeConstraintsJson);
        if (mergedTypeArguments == typeArguments && mergedConstraints == constraints)
            return;

        command.CommandText = """
            UPDATE edges
            SET type_arguments_json = @typeArguments, receiver_type_constraints_json = @constraints
            WHERE edge_id = @edgeId;
            """;
        command.Parameters.AddWithValue("@typeArguments", (object?)mergedTypeArguments ?? DBNull.Value);
        command.Parameters.AddWithValue("@constraints", (object?)mergedConstraints ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void PromoteUnlocatedWinner(SqliteTransaction transaction, long edgeId, EdgeRecord edge)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE edges
            SET provenance = @provenance, extractor_version = @version, is_cross_generated = @crossGenerated,
                source_document_path = NULL, source_start_line = NULL, source_start_column = NULL,
                source_end_line = NULL, source_end_column = NULL
            WHERE edge_id = @edgeId AND @rank > lurp_provenance_rank(provenance);
            """;
        command.Parameters.AddWithValue("@edgeId", edgeId);
        command.Parameters.AddWithValue("@provenance", (object?)edge.Provenance ?? DBNull.Value);
        command.Parameters.AddWithValue("@version", (object?)edge.ExtractorVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("@crossGenerated", edge.IsCrossGenerated);
        command.Parameters.AddWithValue("@rank", EdgeMerge.ProvenanceRank(edge.Provenance));
        command.ExecuteNonQuery();
    }

    private void ProjectEvidence(SqliteTransaction transaction, IReadOnlyCollection<long> edgeIds,
        bool keepUnlocated, bool locatedOnly = false)
    {
        if (edgeIds.Count == 0)
            return;

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ProjectEvidenceSql;
        command.Parameters.AddWithValue("@edgeIds", JsonSerializer.Serialize(edgeIds));
        command.Parameters.AddWithValue("@locatedOnly", locatedOnly);
        command.Parameters.AddWithValue("@keepUnlocated", keepUnlocated);
        command.ExecuteNonQuery();
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
        private readonly SqliteCommand _insertCmd;
        private readonly SqliteParameter _edgeId;
        private readonly SqliteParameter _doc;
        private readonly SqliteParameter _sl;
        private readonly SqliteParameter _sc;
        private readonly SqliteParameter _el;
        private readonly SqliteParameter _ec;
        private readonly SqliteParameter _provenance;
        private readonly SqliteParameter _version;
        private readonly SqliteParameter _crossGenerated;

        public SiteWriteSession(SqliteConnection connection, SqliteTransaction transaction)
        {
            _insertCmd = connection.CreateCommand();
            _insertCmd.Transaction = transaction;
            _insertCmd.CommandText = """
                INSERT INTO edge_sites (edge_id, provenance, document_path, start_line, start_column, end_line, end_column,
                                        extractor_version, is_cross_generated)
                VALUES (@edgeId, @provenance, @doc, @sl, @sc, @el, @ec, @version, @crossGenerated)
                ON CONFLICT (edge_id, provenance, document_path, start_line, start_column, end_line, end_column)
                DO UPDATE SET extractor_version = excluded.extractor_version, is_cross_generated = excluded.is_cross_generated
                WHERE (excluded.extractor_version COLLATE lurp_ordinal, excluded.is_cross_generated)
                    < (edge_sites.extractor_version COLLATE lurp_ordinal, edge_sites.is_cross_generated);
                """;
            _edgeId = _insertCmd.Parameters.Add(new SqliteParameter("@edgeId", 0L));
            _doc = _insertCmd.Parameters.Add(new SqliteParameter("@doc", null));
            _sl = _insertCmd.Parameters.Add(new SqliteParameter("@sl", 0));
            _sc = _insertCmd.Parameters.Add(new SqliteParameter("@sc", 0));
            _el = _insertCmd.Parameters.Add(new SqliteParameter("@el", 0));
            _ec = _insertCmd.Parameters.Add(new SqliteParameter("@ec", 0));
            _provenance = _insertCmd.Parameters.Add(new SqliteParameter("@provenance", null));
            _version = _insertCmd.Parameters.Add(new SqliteParameter("@version", null));
            _crossGenerated = _insertCmd.Parameters.Add(new SqliteParameter("@crossGenerated", false));
        }

        public void WriteSites(long edgeId, IReadOnlyList<EdgeSiteEvidence> evidence)
        {
            _edgeId.Value = edgeId;
            foreach (var row in evidence)
            {
                _doc.Value = row.Site.DocumentPath;
                _sl.Value = row.Site.StartLine;
                _sc.Value = row.Site.StartColumn;
                _el.Value = row.Site.EndLine;
                _ec.Value = row.Site.EndColumn;
                _provenance.Value = row.Provenance;
                _version.Value = row.ExtractorVersion;
                _crossGenerated.Value = row.IsCrossGenerated;
                _insertCmd.ExecuteNonQuery();
            }
        }

        public void Dispose() => _insertCmd.Dispose();
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

    // Read the path list first so SQLite seeks by document even on a small database.
    internal const string DeleteSitesByDocumentPathsSql = """
        DELETE FROM edge_sites
        WHERE rowid IN (
            SELECT edge_sites.rowid
            FROM json_each(@documentPaths) AS paths
            CROSS JOIN edge_sites INDEXED BY idx_edge_sites_document
            WHERE edge_sites.document_path = paths.value
              AND EXISTS (
                  SELECT 1 FROM edges
                  WHERE edge_id = edge_sites.edge_id AND snapshot_id = @snapshotId
              )
        )
        RETURNING edge_id;
        """;

    internal const string DeleteAffectedEdgesWithoutSitesSql = """
        DELETE FROM edges
        WHERE edge_id IN (SELECT value FROM json_each(@edgeIds))
          AND source_document_path IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM edge_sites WHERE edge_id = edges.edge_id);
        """;

    internal void RetireSitesByDocumentPaths(
        SqliteTransaction transaction, string snapshotId, IReadOnlyCollection<string> documentPaths)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = DeleteSitesByDocumentPathsSql;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@documentPaths", documentPaths);

        var affectedEdges = new HashSet<long>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                affectedEdges.Add(reader.GetInt64(0));
        }

        if (affectedEdges.Count == 0)
            return;

        command.Parameters.Clear();
        command.Parameters.AddWithValue("@edgeIds", JsonSerializer.Serialize(affectedEdges));
        command.CommandText = DeleteAffectedEdgesWithoutSitesSql;
        command.ExecuteNonQuery();

        ProjectEvidence(transaction, affectedEdges, keepUnlocated: false, locatedOnly: true);
    }

    public void DeleteEdgesWithNullDocumentPathForSymbols(string snapshotId, IEnumerable<string> symbolIds)
    {
        var idList = symbolIds as IReadOnlyCollection<string> ?? [.. symbolIds];
        if (idList.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT edge_id FROM edges
            WHERE snapshot_id = @snapshotId
              AND source_document_path IS NULL
              AND source_symbol_id IN (SELECT value FROM json_each(@symbolIds));
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@symbolIds", idList);

        var affectedEdges = new List<long>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                affectedEdges.Add(reader.GetInt64(0));
        }

        ProjectEvidence(transaction, affectedEdges, keepUnlocated: false);

        command.CommandText = """
            DELETE FROM edges
            WHERE edge_id IN (SELECT value FROM json_each(@edgeIds))
              AND source_document_path IS NULL;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@edgeIds", JsonSerializer.Serialize(affectedEdges));
        command.ExecuteNonQuery();
        transaction.Commit();
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
            INSERT OR IGNORE INTO edge_sites (edge_id, provenance, document_path, start_line, start_column, end_line, end_column,
                                             extractor_version, is_cross_generated)
            SELECT new_e.edge_id, es.provenance, es.document_path, es.start_line, es.start_column, es.end_line, es.end_column,
                   es.extractor_version, es.is_cross_generated
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

    public OrphanEdgeDropSummary DeleteOrphanEdges(string snapshotId, IEnumerable<string> excludedSymbolIds)
    {
        ArgumentNullException.ThrowIfNull(excludedSymbolIds);
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
            ),
            excluded_ids(id) AS (
                SELECT value FROM json_each(@excludedSymbolIds)
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
                    WHEN (source_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND source_symbol_id IN (SELECT id FROM excluded_ids))
                      OR (target_symbol_id NOT IN (SELECT id FROM valid_ids)
                          AND target_symbol_id IN (SELECT id FROM excluded_ids))
                    THEN 'excluded_document'
                    ELSE 'other'
                END;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@excludedSymbolIds", excludedSymbolIds);

        int external = 0, compilerSynthesized = 0, excludedDocument = 0, other = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            switch (reader.GetString(0))
            {
                case "compiler_synthesized": compilerSynthesized++; break;
                case "external": external++; break;
                case "excluded_document": excludedDocument++; break;
                case "other": other++; break;
            }

        var total = external + compilerSynthesized + excludedDocument + other;
        return new OrphanEdgeDropSummary(total, external, compilerSynthesized, excludedDocument, other);
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
        command.CommandText = $"""
            SELECT e.source_symbol_id, e.target_symbol_id, e.kind,
                   es.document_path, es.start_line, es.start_column, es.end_line, es.end_column
            FROM json_each(@triples) j
            CROSS JOIN edges e
              ON e.snapshot_id = @snapshotId
             AND e.source_symbol_id = json_extract(j.value, '$[0]')
             AND e.target_symbol_id = json_extract(j.value, '$[1]')
             AND e.kind = json_extract(j.value, '$[2]')
            JOIN edge_sites es ON es.edge_id = e.edge_id AND es.provenance = COALESCE(e.provenance, '')
            WHERE e.source_document_path IS NOT NULL
            ORDER BY e.source_symbol_id, e.target_symbol_id, e.kind, {SiteOrderSql};
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
