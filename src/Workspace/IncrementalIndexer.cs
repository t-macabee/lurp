using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class IncrementalIndexer(IIndexStore store, string gitRoot, HashSet<string> skipAdapters, string? jsonExportPath = null)
{
    private readonly IIndexStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
    private readonly HashSet<string> _skipAdapters = skipAdapters;
    private readonly string? _jsonExportPath = jsonExportPath;

    private readonly DocumentChangeDetector _changeDetector = new(gitRoot);

    private sealed record IncrementalChangeScope(
        IReadOnlySet<string> ChangedPaths,
        IReadOnlySet<string> PreviousChangedSymbolIds,
        IReadOnlySet<string> DiffAndSearchSymbolIds,
        IReadOnlySet<string> AffectedProjects);

    private sealed record SnapshotFinalizationContext(
        Solution Solution,
        WorkspaceInfo Workspace,
        SnapshotPair Snapshots,
        IncrementalChangeScope Changes);

    public sealed record IncrementalResult(string NewSnapshotId, string PreviousSnapshotId, int ChangedDocumentCount, int DeclarationsExtracted, int EdgesExtracted, int DiagnosticsExtracted)
    {
        public bool HasChanges => ChangedDocumentCount > 0 || DeclarationsExtracted > 0 || EdgesExtracted > 0;
    }

    // Incremental indexing strategy:
    //   1. Hash all documents and compare against the previous manifest to find changed/new/deleted files.
    //   2. Identify which projects are affected by the changes.
    //   3. Load compilations only for affected projects (skip unchanged ones).
    //   4. Create a new snapshot manifest, copy forward edges/diagnostics/symbols from the previous snapshot.
    //   5. Remove stale data (edges for changed documents, declarations by old version ids, diagnostics).
    //   6. Re-extract declarations, edges, and diagnostics from affected compilations.
    //   7. Refresh cross-document edges for documents that reference changed symbols.
    //   8. Rebuild the FTS5 search index and compute a semantic diff against the previous snapshot.
    public async Task<IncrementalResult> RunIncrementalAsync(Solution solution, WorkspaceInfo workspaceInfo, Storage.SnapshotRow previousManifest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previousSnapshotId = previousManifest.SnapshotId;
        var previousRichManifest = SnapshotManifest.FromStorageManifest(previousManifest);
        var timings = new List<SnapshotTimingRow>();

        // Step 0: Configuration freshness check — must run before document change
        // detection so that config-only changes (SDK, compiler, TFM, project graph,
        // extractor version) trigger a full rebuild even when no document changed.
        var configMismatches = WorkspaceFreshness.GetFullRebuildMismatches(workspaceInfo, previousRichManifest);
        if (configMismatches.Count > 0)
            throw new FullRebuildRequiredException(configMismatches);

        _store.UpsertExtractors(ExtractorRegistry.All);
        if (_store.HasStaleExtractorVersions(previousSnapshotId))
        {
            throw new FullRebuildRequiredException(
            [
                new SnapshotMismatch(MismatchKind.VersionChanged,
                    "Extractor version staleness detected — some edges in the previous snapshot reference extractor versions not in the current registry.",
                    Document: null,
                    Detail: null)
            ]);
        }

        // Step 1: Change Detection
        cancellationToken.ThrowIfCancellationRequested();
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var (changedDocs, changedPaths) = _changeDetector.DetectAndLogChanges(workspaceInfo, previousRichManifest);
        sw1.Stop();
        timings.Add(new SnapshotTimingRow("change_detection", sw1.ElapsedMilliseconds, DateTime.UtcNow));

        if (changedDocs.Count == 0)
            return new IncrementalResult(NewSnapshotId: previousSnapshotId, PreviousSnapshotId: previousSnapshotId, ChangedDocumentCount: 0, DeclarationsExtracted: 0, EdgesExtracted: 0, DiagnosticsExtracted: 0);

        // Step 2: Affected Project Resolution
        cancellationToken.ThrowIfCancellationRequested();
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Identifying affected projects... ");
        var affectedProjects = _changeDetector.IdentifyAffectedProjects(solution, changedPaths);
        Console.WriteLine($"{affectedProjects.Count} affected: {string.Join(", ", affectedProjects)}");

        var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, changedPaths);
        var oldDocVersionIdSet = new HashSet<string>(oldDocVersionIds);
        sw2.Stop();
        timings.Add(new SnapshotTimingRow("affected_project_resolution", sw2.ElapsedMilliseconds, DateTime.UtcNow));

        // Step 3: Compilation Load
        cancellationToken.ThrowIfCancellationRequested();
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        var affectedCompilations = await LoadAffectedCompilationsAsync(solution, affectedProjects, cancellationToken);
        sw3.Stop();
        timings.Add(new SnapshotTimingRow("compilation_load", sw3.ElapsedMilliseconds, DateTime.UtcNow));

        var snapshotId = SnapshotId.New();
        var newSnapshotIdStr = snapshotId.ToString();
        var newManifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId, SnapshotId.Parse(previousSnapshotId), skipAdapters: _skipAdapters);

        // Step 4: Manifest Creation
        cancellationToken.ThrowIfCancellationRequested();
        var sw4 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Saving new snapshot manifest... ");
        newManifest.Save(_store, workspaceInfo.DocumentContents, _jsonExportPath);
        Console.WriteLine("done.");
        sw4.Stop();
        timings.Add(new SnapshotTimingRow("manifest_creation", sw4.ElapsedMilliseconds, DateTime.UtcNow));

        int totalDeclarations = 0;
        int totalEdges = 0;
        int totalDiagnostics = 0;

        try
        {
            // Step 5: Stale-Data Removal
            cancellationToken.ThrowIfCancellationRequested();
            var sw5 = System.Diagnostics.Stopwatch.StartNew();
            PrepareSnapshotData(solution, previousSnapshotId, newSnapshotIdStr, affectedProjects, oldDocVersionIdSet, changedPaths);
            sw5.Stop();
            timings.Add(new SnapshotTimingRow("stale_data_removal", sw5.ElapsedMilliseconds, DateTime.UtcNow));

            // Step 6: Re-extraction
            cancellationToken.ThrowIfCancellationRequested();
            var sw6 = System.Diagnostics.Stopwatch.StartNew();
            (totalDeclarations, totalEdges, totalDiagnostics) =
                ExtractReplacementFacts(workspaceInfo, newSnapshotIdStr, affectedCompilations, changedPaths);
            sw6.Stop();
            timings.Add(new SnapshotTimingRow("re_extraction", sw6.ElapsedMilliseconds, DateTime.UtcNow));

            // Step 6b: Prune symbols that were in changed documents' old versions
            // but are no longer present after re-extraction
            cancellationToken.ThrowIfCancellationRequested();
            var prunedSymbolIds = PruneRemovedSymbols(previousSnapshotId, newSnapshotIdStr, oldDocVersionIdSet, changedPaths);

            // Compute the set of symbol IDs that need their FTS entries refreshed:
            // all symbols currently declared in the changed documents after re-extraction,
            // plus any symbols that were pruned (their stale FTS rows must be deleted).
            var changedSymbolIds = ComputeChangedSymbolIds(newSnapshotIdStr, changedPaths);
            foreach (var id in prunedSymbolIds)
                changedSymbolIds.Add(id);

            // Build the change scope once so downstream phases share the same sets.
            var previousChangedSymbolIds = new HashSet<string>(oldDocVersionIds.SelectMany(
                vid => _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, [vid])));
            var diffAndSearchSymbolIds = new HashSet<string>(changedSymbolIds);
            var changeScope = new IncrementalChangeScope(
                ChangedPaths: new HashSet<string>(changedPaths),
                PreviousChangedSymbolIds: previousChangedSymbolIds,
                DiffAndSearchSymbolIds: diffAndSearchSymbolIds,
                AffectedProjects: new HashSet<string>(affectedProjects));

            var finalizationContext = new SnapshotFinalizationContext(
                Solution: solution,
                Workspace: workspaceInfo,
                Snapshots: new SnapshotPair(previousSnapshotId, newSnapshotIdStr),
                Changes: changeScope);

            // Step 7: Cross-doc Edge Refresh + Step 8: FTS Rebuild + Diff (in FinalizeSnapshotAsync)
            cancellationToken.ThrowIfCancellationRequested();
            totalEdges += await FinalizeSnapshotAsync(finalizationContext, timings, cancellationToken);
        }
        catch (Exception ex)
        {
            // Compensating delete: clean up partial snapshot data on failure
            // so no orphaned in_progress snapshot remains.
            try { _store.DeleteSnapshotData(newSnapshotIdStr); }
            catch { /* best-effort cleanup */ }
            Console.Error.WriteLine($"ERROR: Incremental index failed mid-operation, snapshot {newSnapshotIdStr} cleaned up: {ex.Message}");
            throw;
        }

        // Persist all timings
        try { _store.SaveTimings(newSnapshotIdStr, timings); }
        catch (Exception ex) { Console.Error.WriteLine($"WARNING: Failed to save timings: {ex.Message}"); }

        Console.WriteLine();
        Console.WriteLine($"Incremental index complete for snapshot {newSnapshotIdStr}");
        Console.WriteLine($"  Previous snapshot: {previousSnapshotId}");
        Console.WriteLine($"  Changed documents: {changedDocs.Count}");
        Console.WriteLine($"  Declarations:      {totalDeclarations}");
        Console.WriteLine($"  Edges:             {totalEdges}");
        Console.WriteLine($"  Diagnostics:       {totalDiagnostics}");

        return new IncrementalResult(NewSnapshotId: newSnapshotIdStr, PreviousSnapshotId: previousSnapshotId, ChangedDocumentCount: changedDocs.Count, DeclarationsExtracted: totalDeclarations, EdgesExtracted: totalEdges, DiagnosticsExtracted: totalDiagnostics);
    }

    private async Task<Dictionary<string, Compilation>> LoadAffectedCompilationsAsync(Solution solution, HashSet<string> affectedProjects, CancellationToken cancellationToken)
    {
        Console.Write("Loading compilations for affected projects... ");
        var result = new Dictionary<string, Compilation>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!affectedProjects.Contains(project.Name))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
                throw new InvalidOperationException($"Compilation loader: GetCompilationAsync returned null for project '{project.Name}' during incremental extraction.");
            result[project.Name] = compilation;
        }
        Console.WriteLine($"done ({result.Count} compilations).");
        return result;
    }

    private void PrepareSnapshotData(Solution solution, string previousSnapshotId, string newSnapshotIdStr, HashSet<string> affectedProjects, HashSet<string> oldDocVersionIdSet, HashSet<string> changedPaths)
    {
        Console.Write("Preparing snapshot data (copy forward, remove stale)... ");

        _store.CopyEdgesToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.CopySnapshotDiagnostics(previousSnapshotId, newSnapshotIdStr);
        _store.CopyAnnotationsToSnapshot(previousSnapshotId, newSnapshotIdStr);

        // Only delete edges for the changed documents, not the entire affected project.
        // We now scope re-extraction to changed documents only, so unchanged documents
        // within affected projects keep their copied-forward edges intact.
        if (changedPaths.Count > 0)
            _store.DeleteEdgesByDocumentPaths(newSnapshotIdStr, changedPaths);

        // Null-path edges (from symbols with no DeclaringSyntaxReferences, e.g. an
        // implicit default constructor) can't be scoped to a document by path, so we
        // scope the delete to symbols declared in the documents that actually changed
        // rather than the whole affected assembly — re-extraction is scoped the same
        // way, so an unchanged document elsewhere in the assembly must keep its
        // copied-forward null-path edges intact.
        if (oldDocVersionIdSet.Count > 0)
        {
            var changedSymbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIdSet);
            _store.DeleteEdgesWithNullDocumentPathForSymbols(newSnapshotIdStr, changedSymbolIds);
        }

        if (oldDocVersionIdSet.Count > 0)
            _store.DeleteDeclarationsByDocumentVersionIds(oldDocVersionIdSet);

        _store.CopySnapshotSymbols(previousSnapshotId, newSnapshotIdStr);

        // Mandatory incremental seed: copy every FTS row from the previous
        // snapshot into the new snapshot so the subsequent refresh in
        // RebuildSearchIndex only needs to delete and re-insert the changed
        // document/symbol subset.
        _store.CopySearchIndexToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.DeleteDiagnosticsByProjectNames(newSnapshotIdStr, affectedProjects);

        Console.WriteLine("done.");
    }

    private (int Declarations, int Edges, int Diagnostics) ExtractReplacementFacts(
        WorkspaceInfo workspaceInfo, string newSnapshotIdStr, Dictionary<string, Compilation> affectedCompilations, HashSet<string> changedPaths)
    {
        Console.WriteLine("Extracting replacement facts for affected projects...");
        int totalDecl = 0, totalEdge = 0, totalDiag = 0;

        foreach (var (projectName, compilation) in affectedCompilations)
        {
            // Compute per-project scope: changed paths that belong to this project's compilation
            HashSet<string>? scopeDocs = null;
            HashSet<string>? scopeRelPaths = null; // relative-path version for adapter-edge filtering
            if (changedPaths.Count > 0)
            {
                scopeDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                scopeRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    var filePath = syntaxTree.FilePath;
                    if (string.IsNullOrEmpty(filePath))
                        continue;
                    var relPath = DocumentChangeDetector.GetRelativePath(filePath, _gitRoot);
                    if (changedPaths.Contains(relPath))
                    {
                        scopeDocs.Add(filePath.Replace('\\', '/'));
                        scopeRelPaths.Add(relPath);
                    }
                }
                if (scopeDocs.Count == 0)
                {
                    scopeDocs = null;
                    scopeRelPaths = null;
                }
            }

            Console.Write($"  [{projectName}] ");
            var options = new CompilationFactExtractor.ExtractionOptions(_skipAdapters,
                LogWarning: msg => Console.Error.Write($"  WARNING: {msg} "),
                LogError: msg => Console.Error.Write($"  ERROR: {msg} "),
                ScopeDocuments: scopeDocs);
            var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, newSnapshotIdStr, projectName, options);
            result.EnsureRequiredSuccess();

            // Filter out edges anchored in unchanged documents within this project.
            // Those edges were already copied forward from the previous snapshot
            // and must not be written a second time.
            // Null-path edges (e.g. implicit constructors) cannot be scoped to a
            // document, so they pass through unfiltered.
            if (scopeRelPaths != null)
            {
                result.Edges.RemoveAll(e =>
                    e.SourceDocumentPath != null && !scopeRelPaths.Contains(e.SourceDocumentPath));
            }

            _store.SaveDeclarations(newSnapshotIdStr, result.Declarations);
            totalDecl += result.Declarations.Count;
            _store.SaveEdges(newSnapshotIdStr, result.Edges);
            totalEdge += result.Edges.Count;
            _store.SaveDiagnostics(newSnapshotIdStr, result.Diagnostics);
            totalDiag += result.Diagnostics.Count;

            Console.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
        }

        return (totalDecl, totalEdge, totalDiag);
    }

    private List<string> PruneRemovedSymbols(string previousSnapshotId, string newSnapshotIdStr, HashSet<string> oldDocVersionIdSet, HashSet<string> changedPaths)
    {
        if (oldDocVersionIdSet.Count == 0)
            return [];

        // Get symbols that were in the old document versions
        var oldSymbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIdSet);
        if (oldSymbolIds.Count == 0)
            return [];

        // After re-extraction, look up new document version IDs for the changed paths
        var pathToNewVersion = _store.GetDocumentVersionIdsByPath(newSnapshotIdStr);
        var newDocVersionIdSet = new HashSet<string>(
            changedPaths
                .Where(p => pathToNewVersion.ContainsKey(p))
                .Select(p => pathToNewVersion[p]));

        // If no new document versions exist for any changed path (all changed
        // documents were deleted), every old symbol must be removed from the snapshot.
        if (newDocVersionIdSet.Count == 0)
        {
            Console.Write($"Pruning {oldSymbolIds.Count} removed symbols (all documents deleted)... ");
            _store.DeleteSnapshotSymbolsBySymbolIds(newSnapshotIdStr, oldSymbolIds);
            Console.WriteLine("done.");
            return oldSymbolIds;
        }

        // Get symbols that are in the new document versions
        var newSymbolIds = new HashSet<string>(
            _store.GetSymbolIdsByDocumentVersionIds(newSnapshotIdStr, newDocVersionIdSet));

        // Prune symbols that were in old but not in new
        var removedSymbolIds = oldSymbolIds.Where(id => !newSymbolIds.Contains(id)).ToList();
        if (removedSymbolIds.Count > 0)
        {
            Console.Write($"Pruning {removedSymbolIds.Count} removed symbols... ");
            _store.DeleteSnapshotSymbolsBySymbolIds(newSnapshotIdStr, removedSymbolIds);
            Console.WriteLine("done.");
            return removedSymbolIds;
        }

        return [];
    }

    private async Task<int> FinalizeSnapshotAsync(SnapshotFinalizationContext context, List<SnapshotTimingRow> timings, CancellationToken cancellationToken)
    {
        // Phase order is load-bearing: reverse refresh → orphan cleanup → FTS → semantic diff → complete.
        cancellationToken.ThrowIfCancellationRequested();

        // Step 7: Cross-doc Edge Refresh
        var crossDocEdgesProcessed = await RefreshCrossDocumentEdgesAsync(context, timings, cancellationToken);

        // Step 7b: Remove edges targeting symbols not declared in this snapshot
        cancellationToken.ThrowIfCancellationRequested();
        _store.DeleteOrphanEdges(context.Snapshots.ToSnapshotId);

        // Step 8a: FTS Rebuild (incremental)
        RebuildSearchIndex(context, timings);

        // Step 8b: Semantic diff persistence
        cancellationToken.ThrowIfCancellationRequested();
        ComputeAndPersistSemanticChanges(context, timings);

        // Completion must be last — all preceding phases must succeed.
        cancellationToken.ThrowIfCancellationRequested();
        _store.MarkSnapshotComplete(context.Snapshots.ToSnapshotId);
        return crossDocEdgesProcessed;
    }

    private async Task<int> RefreshCrossDocumentEdgesAsync(SnapshotFinalizationContext context, List<SnapshotTimingRow> timings, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Updating cross-document edges... ");
        var refresher = new CrossDocumentEdgeRefresher(_store, _gitRoot, _skipAdapters);
        var crossDocEdgesProcessed = await refresher.RefreshAsync(
            context.Solution, context.Workspace,
            context.Snapshots.ToSnapshotId, context.Snapshots.FromSnapshotId,
            (HashSet<string>)context.Changes.ChangedPaths, cancellationToken);
        Console.WriteLine($"done ({crossDocEdgesProcessed} cross-document edges processed).");
        sw.Stop();
        timings.Add(new SnapshotTimingRow("cross_doc_edge_refresh", sw.ElapsedMilliseconds, DateTime.UtcNow));
        return crossDocEdgesProcessed;
    }

    private void RebuildSearchIndex(SnapshotFinalizationContext context, List<SnapshotTimingRow> timings)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Rebuilding FTS5 search index (incremental)... ");
        // Changed/deleted path and symbol sets are final; FTS must finish before MarkSnapshotComplete.
        _store.BuildSearchIndex(
            context.Snapshots.ToSnapshotId,
            (HashSet<string>)context.Changes.ChangedPaths,
            (HashSet<string>)context.Changes.DiffAndSearchSymbolIds);
        Console.WriteLine("done.");
        sw.Stop();
        timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.FtsBuild, sw.ElapsedMilliseconds, DateTime.UtcNow));
    }

    private void ComputeAndPersistSemanticChanges(SnapshotFinalizationContext context, List<SnapshotTimingRow> timings)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Computing semantic diff from previous snapshot... ");
        var differ = new SemanticDiffer(_store, _store, _store);
        var (diffChanges, skippedComparisons) = differ.ComputeDiff(
            context.Snapshots.FromSnapshotId, context.Snapshots.ToSnapshotId,
            (HashSet<string>)context.Changes.ChangedPaths,
            (HashSet<string>)context.Changes.DiffAndSearchSymbolIds);
        _store.SaveSemanticChanges(
            context.Snapshots.FromSnapshotId, context.Snapshots.ToSnapshotId, diffChanges);
        Console.WriteLine($"done ({diffChanges.Count} changes, {skippedComparisons} comparisons skipped).");
        sw.Stop();
        timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.SemanticDiff, sw.ElapsedMilliseconds, DateTime.UtcNow));
    }

    private HashSet<string> ComputeChangedSymbolIds(string snapshotId, HashSet<string> changedPaths)
    {
        if (changedPaths.Count == 0)
            return new HashSet<string>();

        var pathToVersion = _store.GetDocumentVersionIdsByPath(snapshotId);
        var versionIds = new HashSet<string>(
            changedPaths
                .Where(p => pathToVersion.ContainsKey(p))
                .Select(p => pathToVersion[p]));

        if (versionIds.Count == 0)
            return new HashSet<string>();

        return new HashSet<string>(_store.GetSymbolIdsByDocumentVersionIds(snapshotId, versionIds));
    }

}
