using Microsoft.Data.Sqlite;

namespace Lurp.Storage
{
    public class SqliteIndexStore : IIndexStore, IDisposable
    {
        private readonly string _dbPath;
        private SqliteConnection? _connection;
        private bool _disposed;

        private SnapshotLifecycleStore? _lifecycle;
        private SnapshotDocumentStore? _documents;
        private SnapshotSymbolStore? _symbols;
        private SnapshotPruner? _pruner;
        private SnapshotTimingStore? _timings;
        private DeclarationWriteStore? _declWriter;
        private DeclarationReadStore? _declReader;
        private DeclarationMaintenanceStore? _declMaintenance;
        private EdgeOperationsStore? _edgeOps;
        private DiagnosticStore? _diagnostics;
        private AnnotationStore? _annotations;
        private ExtractorRegistryStore? _extractors;
        private SearchSourceStore? _searchSource;
        private SearchSymbolStore? _searchSymbols;
        private SearchIndexMaintenance? _searchMaintenance;
        private SemanticDiffStore? _semanticDiffStore;
        private BindingIncompletenessStore? _bindingIncompletenessStore;

        public SqliteIndexStore(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        }

        // ── Connection lifecycle ────────────────────────────────────────────

        public bool IsOpen => _connection != null;

        public void Open()
        {
            if (_connection != null)
                return;

            _connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False");
            _connection.Open();

            _lifecycle = new SnapshotLifecycleStore(_connection);
            _documents = new SnapshotDocumentStore(_connection);
            _symbols = new SnapshotSymbolStore(_connection);
            _pruner = new SnapshotPruner(_connection);
            _timings = new SnapshotTimingStore(_connection);
            _declWriter = new DeclarationWriteStore(_connection);
            _declReader = new DeclarationReadStore(_connection);
            _declMaintenance = new DeclarationMaintenanceStore(_connection);
            _edgeOps = new EdgeOperationsStore(_connection);
            _diagnostics = new DiagnosticStore(_connection);
            _annotations = new AnnotationStore(_connection);
            _extractors = new ExtractorRegistryStore(_connection);
            _searchSource = new SearchSourceStore(_connection);
            _searchSymbols = new SearchSymbolStore(_connection);
            _searchMaintenance = new SearchIndexMaintenance(_connection);
            _semanticDiffStore = new SemanticDiffStore(_connection);
            _bindingIncompletenessStore = new BindingIncompletenessStore(_connection);
        }

        public void Close()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_connection == null)
                return;

            _connection.Close();
            _connection.Dispose();
            _connection = null;

            _lifecycle = null;
            _documents = null;
            _symbols = null;
            _pruner = null;
            _timings = null;
            _declWriter = null;
            _declReader = null;
            _declMaintenance = null;
            _edgeOps = null;
            _diagnostics = null;
            _annotations = null;
            _extractors = null;
            _searchSource = null;
            _searchSymbols = null;
            _searchMaintenance = null;
            _semanticDiffStore = null;
            _bindingIncompletenessStore = null;
        }

        private void EnsureOpen()
        {
            if (_connection == null)
                throw new InvalidOperationException("Store is not open. Call Open() first.");
        }

        public void Dispose()
        {
            Close();
        }

        // ── Migrations (use their own connection) ──────────────────────────

        public void RunMigrations()
        {
            new MigrationRunner(_dbPath).RunMigrations();
        }

        public int GetCurrentSchemaVersion()
        {
            return new MigrationRunner(_dbPath).GetCurrentSchemaVersion();
        }

        public void ValidateSchema(int expectedVersion)
        {
            var actual = GetCurrentSchemaVersion();
            if (actual != expectedVersion)
                throw new InvalidOperationException($"Schema version mismatch: expected {expectedVersion}, got {actual}.");
        }

        // ── ISnapshotStore ──────────────────────────────────────────────────

        public void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc)
            { EnsureOpen(); _lifecycle!.SaveWorkspace(id, gitRoot, solutionPath, createdAtUtc); }
        public void SaveSnapshot(SnapshotRow manifest)
            { EnsureOpen(); _lifecycle!.SaveSnapshot(manifest); }
        public void MarkSnapshotInProgress(string snapshotId)
            { EnsureOpen(); _lifecycle!.MarkSnapshotInProgress(snapshotId); }
        public void MarkSnapshotComplete(string snapshotId)
            { EnsureOpen(); _lifecycle!.MarkSnapshotComplete(snapshotId); }
        public void MarkSnapshotFailed(string snapshotId, string reasonCode, string? message)
            { EnsureOpen(); _lifecycle!.MarkSnapshotFailed(snapshotId, reasonCode, message); }
        public SnapshotFailureRow? GetLatestSnapshotFailure(string? workspaceId = null)
            { EnsureOpen(); return _lifecycle!.GetLatestSnapshotFailure(workspaceId); }
        public SnapshotRow? LoadLatestSnapshot(string? workspaceId = null)
            { EnsureOpen(); return _lifecycle!.LoadLatestSnapshot(workspaceId); }
        public SnapshotRow? LoadSnapshotMetadata(string snapshotId)
            { EnsureOpen(); return _lifecycle!.LoadSnapshotMetadata(snapshotId); }
        public string? GetLatestSnapshotId(string? workspaceId = null)
            { EnsureOpen(); return _lifecycle!.GetLatestSnapshotId(workspaceId); }
        public string? GetSnapshotGitRoot(string snapshotId)
            { EnsureOpen(); return _lifecycle!.GetSnapshotGitRoot(snapshotId); }
        public string? GetSnapshotStatus(string snapshotId, string workspaceId)
            { EnsureOpen(); return _lifecycle!.GetSnapshotStatus(snapshotId, workspaceId); }
        public List<string> GetSnapshotIds(string workspaceId)
            { EnsureOpen(); return _lifecycle!.GetSnapshotIds(workspaceId); }
        public string? GetSource(string relativePath, string snapshotId)
            { EnsureOpen(); return _documents!.GetSource(relativePath, snapshotId); }
        public void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries)
            { EnsureOpen(); _documents!.SaveSnapshotDocuments(snapshotId, entries); }
        public Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId)
            { EnsureOpen(); return _documents!.GetDocumentVersionIdsByPath(snapshotId); }
        public List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths)
            { EnsureOpen(); return _documents!.GetDocumentVersionIdsForDocuments(snapshotId, documentPaths); }
        public void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds)
            { EnsureOpen(); _symbols!.SaveSnapshotSymbols(snapshotId, symbolIds); }
        public void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _symbols!.CopySnapshotSymbols(fromSnapshotId, toSnapshotId); }
        public void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
            { EnsureOpen(); _symbols!.DeleteSnapshotSymbolsBySymbolIds(snapshotId, symbolIds); }
        public List<string> GetSymbolIdsInSnapshot(string snapshotId)
            { EnsureOpen(); return _symbols!.GetSymbolIdsInSnapshot(snapshotId); }
        public int CountSymbolsInSnapshot(string snapshotId)
            { EnsureOpen(); return _symbols!.CountSymbolsInSnapshot(snapshotId); }
        public void DeleteIncompleteSnapshots()
            { EnsureOpen(); _pruner!.DeleteIncompleteSnapshots(); }
        public void PruneOldSnapshots(int keep = 3)
            { EnsureOpen(); _pruner!.PruneOldSnapshots(keep); }
        public void DeleteSnapshotData(string snapshotId)
            { EnsureOpen(); _pruner!.DeleteSnapshotData(snapshotId); }
        public void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings)
            { EnsureOpen(); _timings!.SaveTimings(snapshotId, timings); }
        public List<SnapshotTimingRow> GetTimings(string snapshotId)
            { EnsureOpen(); return _timings!.GetTimings(snapshotId); }
        public void SaveBindingIncompleteness(string snapshotId, IEnumerable<BindingIncompletenessRecord> records)
            { EnsureOpen(); _bindingIncompletenessStore!.SaveBindingIncompleteness(snapshotId, records); }
        public List<BindingIncompletenessRecord> GetBindingIncompleteness(string snapshotId, string? projectName = null)
            { EnsureOpen(); return _bindingIncompletenessStore!.GetBindingIncompleteness(snapshotId, projectName); }
        public void CopyBindingIncompleteness(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _bindingIncompletenessStore!.CopyBindingIncompleteness(fromSnapshotId, toSnapshotId); }
        public void DeleteBindingIncompletenessByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
            { EnsureOpen(); _bindingIncompletenessStore!.DeleteBindingIncompletenessByDocumentPaths(snapshotId, documentPaths); }

        // ── IDeclarationStore ──────────────────────────────────────────────

        public void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations)
            { EnsureOpen(); _declWriter!.SaveDeclarations(snapshotId, declarations); }
        public IndexedSymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
            { EnsureOpen(); return _declReader!.GetSymbolInfo(symbolId, snapshotId); }
        public string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false)
            { EnsureOpen(); return _declReader!.GetSymbolSource(symbolId, snapshotId, viewKind, includeGenerated); }
        public string? GetContainingTypeSource(string symbolId, string snapshotId)
            { EnsureOpen(); return _declReader!.GetContainingTypeSource(symbolId, snapshotId); }
        public string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines)
            { EnsureOpen(); return _declReader!.GetSurroundingLines(symbolId, snapshotId, contextLines); }
        public List<DeclarationLocation> GetDeclarationLocations(string symbolId, string snapshotId, bool includeGenerated = false)
            { EnsureOpen(); return _declReader!.GetDeclarationLocations(symbolId, snapshotId, includeGenerated); }
        public void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds)
            { EnsureOpen(); _declMaintenance!.DeleteDeclarationsByDocumentVersionIds(documentVersionIds); }
        public List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
            { EnsureOpen(); return _declMaintenance!.GetSymbolIdsByDocumentVersionIds(snapshotId, documentVersionIds); }
        public string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
            { EnsureOpen(); return _declMaintenance!.ResolveSymbolByLocation(relativePath, line, snapshotId, includeGenerated); }
        public NavigationTarget? NavigateToLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
            { EnsureOpen(); return _declMaintenance!.NavigateToLocation(relativePath, line, snapshotId, includeGenerated); }

        // ── IEdgeStore ─────────────────────────────────────────────────────

        public void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
            { EnsureOpen(); _edgeOps!.SaveEdges(snapshotId, edges); }
        public void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics)
            { EnsureOpen(); _diagnostics!.SaveDiagnostics(snapshotId, diagnostics); }
        public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
            { EnsureOpen(); _annotations!.SaveAnnotations(snapshotId, annotations); }
        public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
            { EnsureOpen(); return _edgeOps!.GetEdges(snapshotId, symbolId); }
        public List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null)
            { EnsureOpen(); return _diagnostics!.GetDiagnostics(snapshotId, projectName); }
        public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
            { EnsureOpen(); return _annotations!.GetAnnotations(snapshotId, symbolId); }
        public int CountEdges(string snapshotId)
            { EnsureOpen(); return _edgeOps!.CountEdges(snapshotId); }
        public int CountDiagnostics(string snapshotId)
            { EnsureOpen(); return _diagnostics!.CountDiagnostics(snapshotId); }
        public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
            { EnsureOpen(); return _edgeOps!.GetEdgesByKind(snapshotId, kind); }
        public List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId)
            { EnsureOpen(); return _edgeOps!.GetIncomingEdges(snapshotId, symbolId); }
        public List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId)
            { EnsureOpen(); return _edgeOps!.GetOutgoingEdges(snapshotId, symbolId); }
        public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
            { EnsureOpen(); _edgeOps!.DeleteEdgesByDocumentPaths(snapshotId, documentPaths); }
        public void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities)
            { EnsureOpen(); _edgeOps!.DeleteEdgesWithNullDocumentPathForAssemblies(snapshotId, assemblyIdentities); }
        public void DeleteEdgesWithNullDocumentPathForSymbols(string snapshotId, IEnumerable<string> symbolIds)
            { EnsureOpen(); _edgeOps!.DeleteEdgesWithNullDocumentPathForSymbols(snapshotId, symbolIds); }
        public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _edgeOps!.CopyEdgesToSnapshot(fromSnapshotId, toSnapshotId); }
        public void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _diagnostics!.CopySnapshotDiagnostics(fromSnapshotId, toSnapshotId); }
        public void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames)
            { EnsureOpen(); _diagnostics!.DeleteDiagnosticsByProjectNames(snapshotId, projectNames); }
        public void CopyAnnotationsToSnapshot(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _annotations!.CopyAnnotationsToSnapshot(fromSnapshotId, toSnapshotId); }
        public void DeleteAnnotationsByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
            { EnsureOpen(); _annotations!.DeleteAnnotationsByDocumentPaths(snapshotId, documentPaths); }
        public OrphanEdgeDropSummary DeleteOrphanEdges(string snapshotId)
            { EnsureOpen(); return _edgeOps!.DeleteOrphanEdges(snapshotId); }
        public void UpsertExtractors(IEnumerable<(string Name, string Version, string Description)> extractors)
            { EnsureOpen(); _extractors!.UpsertExtractors(extractors); }
        public bool HasStaleExtractorVersions(string snapshotId)
            { EnsureOpen(); return _extractors!.HasStaleExtractorVersions(snapshotId); }

        // ── ISearchStore ───────────────────────────────────────────────────

        /// <inheritdoc/>
        public void BuildSearchIndex(string snapshotId)
            { EnsureOpen(); _searchMaintenance!.BuildSearchIndex(snapshotId); }
        /// <inheritdoc/>
        public void BuildSearchIndex(string snapshotId, HashSet<string> changedDocumentPaths, HashSet<string> changedSymbolIds)
            { EnsureOpen(); _searchMaintenance!.BuildSearchIndex(snapshotId, changedDocumentPaths, changedSymbolIds); }
        /// <inheritdoc/>
        public void CopySearchIndexToSnapshot(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _searchMaintenance!.CopySearchIndexToSnapshot(fromSnapshotId, toSnapshotId); }
        public List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false, int snippetTokens = 64)
            { EnsureOpen(); return _searchSource!.SearchSource(query, snapshotId, limit, includeGenerated, snippetTokens); }
        public List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null)
            { EnsureOpen(); return _searchSymbols!.SearchSymbols(query, snapshotId, limit, includeGenerated, kind); }
        public SymbolSearchPage SearchSymbolsPage(string query, string snapshotId, int limit, bool includeGenerated, string? kind, SearchCursor? cursor)
            { EnsureOpen(); return _searchSymbols!.SearchSymbolsPage(query, snapshotId, limit, includeGenerated, kind, cursor); }
        public IndexedSymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false)
            { EnsureOpen(); return _searchSymbols!.ResolveSymbolByFqn(fqn, snapshotId, includeGenerated); }

        // ── ISemanticDiffReadStore ─────────────────────────────────────────

        public IReadOnlyList<SymbolTransitionCandidate> LoadTransitionCandidates(string snapshotId, IReadOnlyCollection<string> symbolIds)
            { EnsureOpen(); return _declReader!.LoadTransitionCandidates(snapshotId, symbolIds); }

        // ── ISemanticDiffStore ─────────────────────────────────────────────

        public void SaveSemanticChanges(string fromSnapshotId, string toSnapshotId, IEnumerable<SemanticChange> changes)
            { EnsureOpen(); _semanticDiffStore!.SaveSemanticChanges(fromSnapshotId, toSnapshotId, changes); }
        public List<SemanticChange> GetSemanticChanges(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); return _semanticDiffStore!.GetSemanticChanges(fromSnapshotId, toSnapshotId); }
        public List<SemanticChange> GetSemanticChangesToSnapshot(string toSnapshotId)
            { EnsureOpen(); return _semanticDiffStore!.GetSemanticChangesToSnapshot(toSnapshotId); }

        // ── Cross-table batched maintenance ──────────────────────────────────

        public void DeleteFactsByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
        {
            EnsureOpen();
            var pathList = documentPaths as IReadOnlyCollection<string> ?? documentPaths.ToList();
            if (pathList.Count == 0)
                return;

            using var transaction = _connection!.BeginTransaction();
            try
            {
                using (var setupCmd = _connection.CreateCommand())
                {
                    setupCmd.Transaction = transaction;
                    setupCmd.CommandText = "CREATE TEMP TABLE IF NOT EXISTS lurp_stale_doc_paths (path TEXT NOT NULL); DELETE FROM lurp_stale_doc_paths;";
                    setupCmd.ExecuteNonQuery();
                }

                using (var insertCmd = _connection.CreateCommand())
                {
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT INTO lurp_stale_doc_paths (path) VALUES (@path);";
                    var pathParam = insertCmd.Parameters.Add(new SqliteParameter("@path", System.Data.DbType.String));
                    foreach (var path in pathList)
                    {
                        pathParam.Value = path;
                        insertCmd.ExecuteNonQuery();
                    }
                }

                using (var deleteEdgesCmd = _connection.CreateCommand())
                {
                    deleteEdgesCmd.Transaction = transaction;
                    deleteEdgesCmd.CommandText = @"
                        DELETE FROM edges
                        WHERE snapshot_id = @snapshotId
                          AND source_document_path IN (SELECT path FROM lurp_stale_doc_paths);
                    ";
                    deleteEdgesCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
                    deleteEdgesCmd.ExecuteNonQuery();
                }

                using (var deleteBindingCmd = _connection.CreateCommand())
                {
                    deleteBindingCmd.Transaction = transaction;
                    deleteBindingCmd.CommandText = @"
                        DELETE FROM binding_incompleteness
                        WHERE snapshot_id = @snapshotId
                          AND document_path IN (SELECT path FROM lurp_stale_doc_paths);
                    ";
                    deleteBindingCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
                    deleteBindingCmd.ExecuteNonQuery();
                }

                using (var deleteAnnotationsCmd = _connection.CreateCommand())
                {
                    deleteAnnotationsCmd.Transaction = transaction;
                    deleteAnnotationsCmd.CommandText = @"
                        DELETE FROM annotations
                        WHERE snapshot_id = @snapshotId
                          AND document_path IN (SELECT path FROM lurp_stale_doc_paths);
                    ";
                    deleteAnnotationsCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
                    deleteAnnotationsCmd.ExecuteNonQuery();
                }

                using (var clearCmd = _connection.CreateCommand())
                {
                    clearCmd.Transaction = transaction;
                    clearCmd.CommandText = "DELETE FROM lurp_stale_doc_paths;";
                    clearCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}
