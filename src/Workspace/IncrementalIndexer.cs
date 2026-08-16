using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Lurp.Workspace;

public sealed class IncrementalIndexer(IIndexStore store, string gitRoot, HashSet<string> skipAdapters, string? jsonExportPath = null, bool verbose = false, IOutputSink? output = null, bool skipDiff = false, bool force = false)
{
    private readonly DocumentChangeDetector _changeDetector = new(gitRoot, output ?? ConsoleOutputSink.Instance);
    private readonly bool _force = force;
    private readonly string _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
    private readonly string? _jsonExportPath = jsonExportPath;
    private readonly IOutputSink _output = output ?? ConsoleOutputSink.Instance;
    private readonly HashSet<string> _skipAdapters = skipAdapters;
    private readonly bool _skipDiff = skipDiff;
    private readonly IIndexStore _store = store ?? throw new ArgumentNullException(nameof(store));

    // Incremental indexing strategy:
    //   1. Hash all documents and compare against the previous manifest to find changed/new/deleted files.
    //   2. Identify which projects are affected by the changes.
    //   3. Load compilations only for affected projects (skip unchanged ones).
    //   4. Create a new snapshot manifest, copy forward edges/diagnostics/symbols from the previous snapshot.
    //   5. Remove stale data (edges for changed documents, declarations by old version ids, diagnostics).
    //   6. Re-extract declarations, edges, and diagnostics from affected compilations.
    //   7. Refresh cross-document edges for documents that reference changed symbols.
    //   8. Rebuild the FTS5 search index and compute a semantic diff against the previous snapshot.
    public async Task<IncrementalResult> RunIncrementalAsync(Solution solution, WorkspaceInfo workspaceInfo, SnapshotRow previousManifest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previousSnapshotId = previousManifest.SnapshotId;
        var previousRichManifest = SnapshotManifest.FromStorageManifest(previousManifest);
        var timings = new List<SnapshotTimingRow>();

        // Step 0: Configuration freshness check : must run before document change
        // detection so that config-only changes (SDK, compiler, TFM, project graph,
        // extractor version) trigger a full rebuild even when no document changed.
        var configMismatches = WorkspaceFreshness.GetFullRebuildMismatches(workspaceInfo, previousRichManifest);
        if (configMismatches.Count > 0)
            throw new FullRebuildRequiredException(configMismatches);

        _store.UpsertExtractors(ExtractorRegistry.All);
        if (_store.HasStaleExtractorVersions(previousSnapshotId))
            throw new FullRebuildRequiredException(
            [
                new SnapshotMismatch(MismatchKind.VersionChanged,
                    "Extractor version staleness detected : some edges in the previous snapshot reference extractor versions not in the current registry.",
                    null,
                    null)
            ]);

        // Step 1: Change Detection
        cancellationToken.ThrowIfCancellationRequested();
        var sw1 = Stopwatch.StartNew();
        var (changedDocs, changedPaths) = _changeDetector.DetectAndLogChanges(workspaceInfo, previousRichManifest);
        sw1.Stop();
        timings.Add(new SnapshotTimingRow("change_detection", sw1.ElapsedMilliseconds));

        if (changedDocs.Count == 0)
            return new IncrementalResult(previousSnapshotId, previousSnapshotId, 0, 0, 0, 0, OrphanEdgeDropSummary.Empty);

        // Deterministic identity: identical indexed state must produce the
        // identical snapshot id, so a complete snapshot with this id must be
        // reused rather than duplicated, and a crashed or failed attempt with
        // the same id must not block a retry.
        var snapshotId = SnapshotIdentity.Create(workspaceInfo, _skipAdapters);
        var newSnapshotIdStr = snapshotId.ToString();
        var existing = _store.ResolveExistingSnapshot(newSnapshotIdStr, workspaceInfo.Id.Value);
        switch (existing.Disposition)
        {
            case ExistingSnapshotDisposition.Reuse:
                if (_force)
                {
                    _output.WriteLine($"Identical complete snapshot {newSnapshotIdStr} already exists; --force given, re-extracting in place.");
                }
                else
                {
                    _output.WriteLine($"Identical complete snapshot {newSnapshotIdStr} already exists for this workspace; reusing it.");
                    return new IncrementalResult(newSnapshotIdStr, previousSnapshotId, changedDocs.Count, 0, 0, 0, OrphanEdgeDropSummary.Empty);
                }
                break;
            case ExistingSnapshotDisposition.Retry:
                _output.WriteLine($"Snapshot {newSnapshotIdStr} exists with status '{existing.ExistingStatus}'; removing it and retrying incremental index.");
                break;
        }

        // Step 2: Affected Project Resolution
        cancellationToken.ThrowIfCancellationRequested();
        var sw2 = Stopwatch.StartNew();
        _output.Write("Identifying affected projects... ");

        // New-file widening (R3): a NEWLY ADDED file can change overload
        // resolution for unedited callers in other documents — a new overload
        // can steal a binding, or a new member can make a previously-unbound
        // call bind. The BFS can only follow persisted arcs and a new file has
        // none, so seed it with every document of each added file's project:
        // their declarations exist in the previous snapshot, so the BFS follows
        // the old arc to the callers that must be rebound. Guarded to added
        // files — edits keep today's narrow seed, preserving incremental
        // performance for the common case.
        var addedFilePaths = changedDocs
            .Where(static c => c.ChangeKind == DocumentChangeDetector.DocumentChangeKind.New)
            .Select(static c => c.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newFileProjectPaths = addedFilePaths.Count == 0
            ? null
            : GetProjectDocumentPaths(solution, GetProjectsForPaths(solution, addedFilePaths));

        var invalidationPaths = new HashSet<string>(changedPaths, StringComparer.OrdinalIgnoreCase);
        var dependencyRefresher = new CrossDocumentEdgeRefresher(_store, _gitRoot, _skipAdapters);
        var closureSeed = new HashSet<string>(changedPaths, StringComparer.OrdinalIgnoreCase);
        if (newFileProjectPaths != null)
            closureSeed.UnionWith(newFileProjectPaths);
        var (closurePaths, fellBackToProjectScope) = dependencyRefresher.FindAffectedDocPaths(solution, previousSnapshotId, closureSeed);
        invalidationPaths.UnionWith(closurePaths);
        if (newFileProjectPaths != null)
        {
            invalidationPaths.UnionWith(newFileProjectPaths);
            closurePaths.UnionWith(newFileProjectPaths);
        }

        if (newFileProjectPaths != null)
            _output.WriteLine($"  {addedFilePaths.Count} new file(s); widened closure to {newFileProjectPaths.Count} document(s) in the new files' project(s).");
        var affectedProjects = _changeDetector.IdentifyAffectedProjects(solution, invalidationPaths);
        var affectedDocumentPaths = GetProjectDocumentPaths(solution, affectedProjects);
        invalidationPaths.UnionWith(affectedDocumentPaths);
        timings.Add(new SnapshotTimingRow(
            fellBackToProjectScope ? SnapshotTimingSteps.ClosureFallback : SnapshotTimingSteps.ClosureNarrowed,
            0));

        // Extraction scope: the set of documents that will be re-extracted, and
        // whose copied-forward facts must therefore be deleted before re-extraction.
        // Kept distinct from invalidationPaths — the wide set driving FTS refresh
        // and the semantic diff — because extraction and deletion must narrow in
        // lockstep while those consumers stay project-wide.  Step 7 receives the
        // pre-computed reverse-edge closure (closurePaths) directly, not a seed.
        //
        // Contents: changed documents plus every document declaring any part of a
        // type whose declaring syntax appears in a changed document. The type-part
        // expansion is required because two extraction guards are type-level
        // (SymbolStructuralEdgeExtractor.IsTypeInScope, PolymorphismExtractionContext
        // .IsTypeInScope) and StaticDispatchCallExtractor has no per-tree guard at
        // all, so a purely file-granular scope would leave partial types half in and
        // half out of scope while whole-symbol properties such as is_partial stay
        // computed across all parts.
        //
        // The reverse-edge closure (closurePaths) is intentionally excluded from the
        // extraction-scope seed: documents reached only through the closure did not
        // change — they only reference changed symbols.  Their own declarations are
        // untouched and their cross-document edges are handled by the step-7
        // cross-document edge refresh, not by re-extraction.  Expanding type parts
        // from the full closure seed was measured to pull 41 % of the tree into
        // re-extraction for a single-document change because every partial type
        // transitively touched through the call graph adds all of its partial parts.
        //
        // On the project-scope fallback, closurePaths already IS every document
        // in every touched project (CrossDocumentEdgeRefresher widened it before
        // returning), so this seed stays at changed-only and extraction stays
        // correspondingly narrow even though invalidationPaths is project-wide.
        //
        // Add-file exception (R3): the closure-exclusion premise breaks when a
        // NEW file is added — documents reached through the closure may rebind
        // to the new file's overloads even though their own text did not change.
        // The affected caller must land in the extraction scope (re-extraction),
        // not just invalidation (FTS/diff), so for this run only the seed is
        // widened to changed + new-file project documents + the closure. The
        // widening is guarded to added files; plain edits keep the narrow seed.
        var extractionScopeSeed = changedPaths;
        if (newFileProjectPaths != null)
        {
            extractionScopeSeed = new HashSet<string>(changedPaths, StringComparer.OrdinalIgnoreCase);
            extractionScopeSeed.UnionWith(newFileProjectPaths);
            extractionScopeSeed.UnionWith(closurePaths);
        }

        var extractionScopePaths = ExpandToDeclaringTypeParts(previousSnapshotId, extractionScopeSeed);

        // Extraction guards compare against SyntaxTree.FilePath, which is absolute
        // and forward-slash normalized. Passing the git-root-relative form matches
        // no tree and silently extracts almost nothing, so convert here — the same
        // conversion CrossDocumentEdgeRefresher.ProcessCompilationsAsync performs
        // for its own scope set.
        var extractionScopeAbsolutePaths = ToAbsoluteNormalizedPaths(extractionScopePaths);
        _output.WriteLine($"{affectedProjects.Count} affected: {string.Join(", ", affectedProjects)}");

        var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, changedPaths);
        var oldDocVersionIdSet = new HashSet<string>(oldDocVersionIds);
        sw2.Stop();
        timings.Add(new SnapshotTimingRow("affected_project_resolution", sw2.ElapsedMilliseconds));

        // Step 3: Compilation Load
        cancellationToken.ThrowIfCancellationRequested();
        var sw3 = Stopwatch.StartNew();
        var affectedCompilations = await LoadAffectedCompilationsAsync(solution, affectedProjects, cancellationToken);
        sw3.Stop();
        timings.Add(new SnapshotTimingRow("compilation_load", sw3.ElapsedMilliseconds));

        var newManifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId, SnapshotId.Parse(previousSnapshotId), _skipAdapters);
        // Step 4: Manifest Creation
        cancellationToken.ThrowIfCancellationRequested();
        var sw4 = Stopwatch.StartNew();
        _output.Write("Saving new snapshot manifest... ");
        newManifest.Save(_store, workspaceInfo.DocumentContents, _jsonExportPath);
        _output.WriteLine("done.");
        sw4.Stop();
        timings.Add(new SnapshotTimingRow("manifest_creation", sw4.ElapsedMilliseconds));

        var totalDeclarations = 0;
        var totalEdges = 0;
        var totalDiagnostics = 0;
        var orphanEdgesDropped = OrphanEdgeDropSummary.Empty;

        try
        {
            // Step 5: Stale-Data Removal
            cancellationToken.ThrowIfCancellationRequested();
            var sw5 = Stopwatch.StartNew();

            var unrestoredProjects = WorkspaceLoadGate.GetUnrestoredProjectNames(solution);
            if (unrestoredProjects.Count > 0)
            {
                var affectedUnrestored = unrestoredProjects
                    .Where(n => affectedProjects.Contains(n))
                    .OrderBy(static n => n, StringComparer.Ordinal)
                    .ToList();
                if (affectedUnrestored.Count > 0)
                {
                    _output.WriteErrorLine();
                    _output.WriteErrorLine($"WARNING: {affectedUnrestored.Count} affected project(s) have not been restored:");
                    foreach (var name in affectedUnrestored)
                        _output.WriteErrorLine($"  - {name}");
                    _output.WriteErrorLine(WorkspaceLoadGate.DescribeUnrestored(affectedUnrestored));
                    _output.WriteErrorLine();
                }
            }

            PrepareSnapshotData(previousSnapshotId, newSnapshotIdStr, affectedProjects, oldDocVersionIdSet, extractionScopePaths);
            sw5.Stop();
            timings.Add(new SnapshotTimingRow("stale_data_removal", sw5.ElapsedMilliseconds));

            // Step 6: Re-extraction
            cancellationToken.ThrowIfCancellationRequested();
            var sw6 = Stopwatch.StartNew();
            (totalDeclarations, totalEdges, totalDiagnostics) =
                ExtractReplacementFacts(workspaceInfo, newSnapshotIdStr, affectedCompilations, extractionScopeAbsolutePaths, extractionScopePaths);
            sw6.Stop();
            timings.Add(new SnapshotTimingRow("re_extraction", sw6.ElapsedMilliseconds));

            // Step 6b: Prune symbols that were in changed documents' old versions
            // but are no longer present after re-extraction
            cancellationToken.ThrowIfCancellationRequested();
            var prunedSymbolIds = PruneRemovedSymbols(previousSnapshotId, newSnapshotIdStr, oldDocVersionIdSet, changedPaths);

            // Compute the set of symbol IDs that need their FTS entries refreshed:
            // all symbols currently declared in the changed documents after re-extraction,
            // plus any symbols that were pruned (their stale FTS rows must be deleted).
            var changedSymbolIds = ComputeChangedSymbolIds(newSnapshotIdStr, invalidationPaths);
            foreach (var id in prunedSymbolIds)
                changedSymbolIds.Add(id);

            // Build the change scope once so downstream phases share the same sets.
            var diffAndSearchSymbolIds = new HashSet<string>(changedSymbolIds);
            var changeScope = new IncrementalChangeScope(
                invalidationPaths,
                diffAndSearchSymbolIds,
                new HashSet<string>(affectedProjects),
                closurePaths,
                extractionScopePaths);

            var finalizationContext = new SnapshotFinalizationContext(
                solution,
                workspaceInfo,
                new SnapshotPair(previousSnapshotId, newSnapshotIdStr),
                changeScope);

            // Step 7: Cross-doc Edge Refresh + Step 8: FTS Rebuild + Diff (in FinalizeSnapshotAsync)
            cancellationToken.ThrowIfCancellationRequested();
            int crossDocEdgesProcessed;
            (crossDocEdgesProcessed, orphanEdgesDropped) = await FinalizeSnapshotAsync(finalizationContext, timings, cancellationToken);
            totalEdges += crossDocEdgesProcessed;
        }
        catch (Exception ex)
        {
            var reasonCode = ex is OperationCanceledException ? "cancelled" : "incremental_index_failure";
            try
            {
                _store.MarkSnapshotFailed(newSnapshotIdStr, reasonCode, ex.Message);
            }
            catch
            {
            }

            _output.WriteErrorLine($"ERROR: Incremental index failed mid-operation, snapshot {newSnapshotIdStr} marked '{SnapshotStatusValues.Failed}' ({reasonCode}): {ex.Message}");
            throw;
        }

        // Persist all timings
        try
        {
            _store.SaveTimings(newSnapshotIdStr, timings);
        }
        catch (Exception ex)
        {
            _output.WriteErrorLine($"WARNING: Failed to save timings: {ex.Message}");
        }

        var declarationsInSnapshot = _store.CountSymbolsInSnapshot(newSnapshotIdStr);
        var edgesInSnapshot = _store.CountEdges(newSnapshotIdStr);
        var diagnosticsInSnapshot = _store.CountDiagnostics(newSnapshotIdStr);
        var projectsInSnapshot = solution.Projects.Count();

        _output.WriteLine();
        _output.WriteLine($"Incremental index complete for snapshot {newSnapshotIdStr}");
        _output.WriteLine($"  Previous snapshot: {previousSnapshotId}");
        _output.WriteLine($"  documents_changed_this_run:          {changedDocs.Count}      documents_in_snapshot: {workspaceInfo.Documents.Count}");
        _output.WriteLine($"  projects_reextracted_this_run:       {affectedProjects.Count}/{projectsInSnapshot}");
        _output.WriteLine($"  declarations_extracted_this_run:     {totalDeclarations}      declarations_in_snapshot: {declarationsInSnapshot}");
        _output.WriteLine($"  edge_relations_after_dedup_this_run: {totalEdges}      edges_dropped_outside_snapshot_scope: {orphanEdgesDropped.FormatDropSummary()}");
        _output.WriteLine($"                                     {"",27}  edge_relations_in_snapshot:          {edgesInSnapshot}");
        var warning = orphanEdgesDropped.FormatWarning();
        if (warning != null)
            _output.WriteErrorLine(warning);
        _output.WriteLine($"  diagnostics_extracted_this_run:      {totalDiagnostics}      diagnostics_in_snapshot: {diagnosticsInSnapshot}");

        return new IncrementalResult(newSnapshotIdStr, previousSnapshotId, changedDocs.Count, totalDeclarations, totalEdges, totalDiagnostics, orphanEdgesDropped);
    }

    private async Task<Dictionary<string, Compilation>> LoadAffectedCompilationsAsync(Solution solution, HashSet<string> affectedProjects, CancellationToken cancellationToken)
    {
        _output.Write("Loading compilations for affected projects... ");
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

        _output.WriteLine($"done ({result.Count} compilations).");
        return result;
    }

    private void PrepareSnapshotData(string previousSnapshotId, string newSnapshotIdStr, HashSet<string> affectedProjects, HashSet<string> oldDocVersionIdSet, HashSet<string> extractionScopePaths)
    {
        _output.Write("Preparing snapshot data (copy forward, remove stale)... ");

        _store.CopyEdgesToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.CopySnapshotDiagnostics(previousSnapshotId, newSnapshotIdStr);
        _store.CopyAnnotationsToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.CopyBindingIncompleteness(previousSnapshotId, newSnapshotIdStr);

        // Delete facts only for the documents in the extraction scope — the same
        // set that is about to be re-extracted, so deleted rows are replaced by
        // the re-extraction and nothing is silently lost. Extraction and deletion
        // must always be scoped to exactly the same set; documents outside it
        // keep their copied-forward edges intact.
        if (extractionScopePaths.Count > 0)
            // Batched: one temp table populated once, joined by all three deletes,
            // instead of each store rebuilding its own IN-list (or, for
            // binding-incompleteness, issuing one DELETE per path) over the same set.
            _store.DeleteFactsByDocumentPaths(newSnapshotIdStr, extractionScopePaths);

        // Null-path edges (from symbols with no DeclaringSyntaxReferences, e.g. an
        // implicit default constructor) can't be scoped to a document by path, so we
        // scope the delete to symbols declared in the extraction-scope documents
        // rather than the whole affected assembly : re-extraction is scoped the same
        // way, so an unchanged document elsewhere in the assembly must keep its
        // copied-forward null-path edges intact.
        var invalidatedOldVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, extractionScopePaths);
        if (invalidatedOldVersionIds.Count > 0)
        {
            var changedSymbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, invalidatedOldVersionIds);
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

        _output.WriteLine("done.");
    }

    private (int Declarations, int Edges, int Diagnostics) ExtractReplacementFacts(
        WorkspaceInfo workspaceInfo, string newSnapshotIdStr, Dictionary<string, Compilation> affectedCompilations,
        IReadOnlySet<string> extractionScopeAbsolutePaths,
        IReadOnlySet<string> extractionScopeRelativePaths)
    {
        _output.WriteLine("Extracting replacement facts for affected projects...");
        int totalDecl = 0, totalEdge = 0, totalDiag = 0;

        foreach (var (projectName, compilation) in affectedCompilations)
        {
            _output.Write($"  [{projectName}] ");

            if (WorkspaceLoadGate.Classify(compilation) == CompilationReadability.Blind)
            {
                _store.SaveBindingIncompleteness(
                    newSnapshotIdStr,
                    WorkspaceLoadGate.DescribeBlindProject(compilation, projectName, workspaceInfo.Id.GitRoot));
                _output.WriteLine("UNREADABLE: no metadata references resolved; skipped.");
                continue;
            }

            var options = CompilationFactExtractor.CreateOptions(_skipAdapters, extractionScopeAbsolutePaths);
            var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, newSnapshotIdStr, projectName, options);
            result.EnsureRequiredSuccess();

            _store.SaveDeclarations(newSnapshotIdStr, result.Declarations);
            totalDecl += result.Declarations.Count;
            _store.SaveEdges(newSnapshotIdStr, result.Edges);
            totalEdge += result.Edges.Count;
            _store.SaveDiagnostics(newSnapshotIdStr, result.Diagnostics);
            totalDiag += result.Diagnostics.Count;

            // Lockstep invariant: save binding-incompleteness only for documents
            // in the extraction scope (the same set the delete cleared). Records
            // for out-of-scope documents carry forward their copied-forward
            // values unchanged.
            var scopedBi = ScopeBindingIncompleteness(result.BindingIncompleteness, extractionScopeRelativePaths);
            _store.SaveBindingIncompleteness(newSnapshotIdStr, scopedBi);
            if (result.Annotations.Count > 0)
                _store.SaveAnnotations(newSnapshotIdStr, result.Annotations);
            foreach (var measurement in result.Measurements.Where(_ => verbose))
                _output.WriteErrorLine($"    [measure] {measurement.Extractor}: {measurement.ElapsedMilliseconds} ms, {measurement.AllocatedBytes} bytes");

            _output.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
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
        // documents were deleted), every old symbol is a prune candidate;
        // otherwise only those absent from the re-extracted document versions.
        List<string> candidateIds;
        if (newDocVersionIdSet.Count == 0)
        {
            candidateIds = oldSymbolIds;
        }
        else
        {
            var newSymbolIds = new HashSet<string>(
                _store.GetSymbolIdsByDocumentVersionIds(newSnapshotIdStr, newDocVersionIdSet));
            candidateIds = [.. oldSymbolIds.Where(id => !newSymbolIds.Contains(id))];
        }

        // A candidate may still be declared elsewhere in the new snapshot — a
        // partial type keeps its identity when one part is edited or deleted.
        // Prune only symbols that no longer have any declaration site at all,
        // or the surviving part's row is deleted along with the removed one.
        var removedSymbolIds = candidateIds;
        if (candidateIds.Count > 0)
        {
            // Survivors are read from every document version in the new snapshot
            // except the stale versions of the changed documents themselves —
            // those rows are exactly what pruning exists to remove.
            var otherVersionIds = pathToNewVersion.Values
                .Where(v => !oldDocVersionIdSet.Contains(v))
                .ToList();
            var survivingSymbolIds = otherVersionIds.Count == 0
                ? []
                : new HashSet<string>(_store.GetSymbolIdsByDocumentVersionIds(newSnapshotIdStr, otherVersionIds));
            removedSymbolIds = [.. candidateIds.Where(id => !survivingSymbolIds.Contains(id))];
        }

        if (removedSymbolIds.Count > 0)
        {
            _output.Write($"Pruning {removedSymbolIds.Count} removed symbols... ");
            _store.DeleteSnapshotSymbolsBySymbolIds(newSnapshotIdStr, removedSymbolIds);
            _output.WriteLine("done.");
            return removedSymbolIds;
        }

        return [];
    }

    private async Task<(int crossDocEdgesProcessed, OrphanEdgeDropSummary orphanEdgesDropped)> FinalizeSnapshotAsync(SnapshotFinalizationContext context, List<SnapshotTimingRow> timings, CancellationToken cancellationToken)
    {
        // Phase order is load-bearing: reverse refresh → orphan cleanup → FTS → semantic diff → complete.
        cancellationToken.ThrowIfCancellationRequested();

        // Step 7: Cross-doc Edge Refresh
        var crossDocEdgesProcessed = await RefreshCrossDocumentEdgesAsync(context, timings, cancellationToken);

        // Step 7b: Remove edges targeting symbols not declared in this snapshot
        cancellationToken.ThrowIfCancellationRequested();
        var orphanEdgesDropped = _store.DeleteOrphanEdges(context.Snapshots.ToSnapshotId);

        // Step 7c: Drop stale synthetic/route graph nodes carried forward by
        // CopyEdgesToSnapshot that no longer have any edge or declaration in this
        // snapshot. Without this they linger in snapshot_graph_nodes and keep
        // masking genuinely orphaned edges in DeleteOrphanEdges (ARCH-006).
        cancellationToken.ThrowIfCancellationRequested();
        _store.PruneSnapshotGraphNodes(context.Snapshots.ToSnapshotId);

        // Step 8a: FTS Rebuild (incremental)
        RebuildSearchIndex(context, timings);

        // Step 8b: Semantic diff persistence — skipped when the caller passed
        // --skip-diff. Only --mode=diff (which recomputes live) and --mode=impact's
        // semantic-causes annotation (which degrades to an empty list when absent)
        // consume this; a run that needs neither shouldn't pay for it.
        cancellationToken.ThrowIfCancellationRequested();
        if (!_skipDiff)
            ComputeAndPersistSemanticChanges(context, timings);

        // Completion must be last : all preceding phases must succeed.
        cancellationToken.ThrowIfCancellationRequested();
        _store.MarkSnapshotComplete(context.Snapshots.ToSnapshotId);
        return (crossDocEdgesProcessed, orphanEdgesDropped);
    }

    private async Task<int> RefreshCrossDocumentEdgesAsync(SnapshotFinalizationContext context, List<SnapshotTimingRow> timings, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _output.Write("Updating cross-document edges... ");
        var refresher = new CrossDocumentEdgeRefresher(_store, _gitRoot, _skipAdapters);
        // The reverse-edge closure was already computed in step 2 via
        // FindAffectedDocPaths on the genuinely-changed documents plus
        // binding-incompleteness seeding. Union in the documents step 6 already
        // re-extracted (changed documents + type-part expansion): an aggregated
        // cross-document edge — a MayDispatchTo edge whose TypeArgumentsJson holds
        // one type-argument variant per implementing type — is anchored on the
        // dispatch TARGET (the base/interface method's document) yet draws a
        // variant from EACH implementor. When an implementor's own document is the
        // change, the target document enters the closure through the reverse edge
        // and step 7 deletes-and-rebuilds the aggregated edge; if the changed
        // implementor were excluded from the extraction scope its variant would be
        // dropped, diverging from a full rebuild (only the changed implementor's
        // tuple, never its unchanged siblings, since those stay in the closure).
        // Keeping the changed documents in scope makes every contributor's type
        // visible; their own facts were written in step 6 and are deleted then
        // re-saved idempotently here, so extraction and deletion stay in lockstep.
        var refreshPaths = new HashSet<string>(
            (HashSet<string>)context.Changes.CrossDocumentClosurePaths,
            StringComparer.OrdinalIgnoreCase);
        refreshPaths.UnionWith(context.Changes.ExtractedPaths);
        var crossDocEdgesProcessed = await refresher.RefreshWithAffectedPathsAsync(
            context.Solution, context.Workspace,
            context.Snapshots.ToSnapshotId,
            refreshPaths,
            cancellationToken,
            context.Changes.AffectedProjects);
        _output.WriteLine($"done ({crossDocEdgesProcessed} cross-document edges processed).");
        sw.Stop();
        timings.Add(new SnapshotTimingRow("cross_doc_edge_refresh", sw.ElapsedMilliseconds));
        return crossDocEdgesProcessed;
    }

    private void RebuildSearchIndex(SnapshotFinalizationContext context, List<SnapshotTimingRow> timings)
    {
        var sw = Stopwatch.StartNew();
        _output.Write("Rebuilding FTS5 search index (incremental)... ");
        // Changed/deleted path and symbol sets are final; FTS must finish before MarkSnapshotComplete.
        _store.BuildSearchIndex(
            context.Snapshots.ToSnapshotId,
            (HashSet<string>)context.Changes.ChangedPaths,
            (HashSet<string>)context.Changes.DiffAndSearchSymbolIds);
        _output.WriteLine("done.");
        sw.Stop();
        timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.FtsBuild, sw.ElapsedMilliseconds));
    }

    private void ComputeAndPersistSemanticChanges(SnapshotFinalizationContext context, List<SnapshotTimingRow> timings)
    {
        SemanticDiffStep.ComputeAndPersist(
            _store, _output, context.Snapshots.FromSnapshotId, context.Snapshots.ToSnapshotId,
            (HashSet<string>)context.Changes.DiffAndSearchSymbolIds,
            timings);
    }

    private HashSet<string> ComputeChangedSymbolIds(string snapshotId, HashSet<string> changedPaths)
    {
        if (changedPaths.Count == 0)
            return [];

        var pathToVersion = _store.GetDocumentVersionIdsByPath(snapshotId);
        var versionIds = new HashSet<string>(
            changedPaths
                .Where(p => pathToVersion.ContainsKey(p))
                .Select(p => pathToVersion[p]));

        if (versionIds.Count == 0)
            return [];

        return [.. _store.GetSymbolIdsByDocumentVersionIds(snapshotId, versionIds)];
    }

    private HashSet<string> GetProjectDocumentPaths(Solution solution, HashSet<string> projectNames)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (!projectNames.Contains(project.Name))
                continue;
            foreach (var document in project.Documents)
                if (!string.IsNullOrEmpty(document.FilePath))
                    paths.Add(PathNormalizer.ToGitRelative(document.FilePath, _gitRoot));
        }

        return paths;
    }

    /// <summary>
    ///     Maps git-root-relative document paths to the names of the projects that
    ///     own them. Unlike <see cref="DocumentChangeDetector.IdentifyAffectedProjects" />,
    ///     this does NOT follow project references — only the projects directly
    ///     containing the given documents are returned.
    /// </summary>
    private HashSet<string> GetProjectsForPaths(Solution solution, HashSet<string> paths)
    {
        var projectNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null)
                    continue;
                if (paths.Contains(PathNormalizer.ToGitRelative(document.FilePath, _gitRoot)))
                {
                    projectNames.Add(project.Name);
                    break;
                }
            }

        return projectNames;
    }

    /// <summary>
    ///     Converts git-root-relative document paths to the absolute, forward-slash
    ///     form that every extraction scope guard compares against
    ///     (<c>SyntaxTree.FilePath.Replace('\\', '/')</c>).
    /// </summary>
    private HashSet<string> ToAbsoluteNormalizedPaths(IReadOnlySet<string> relativePaths)
    {
        var absolute = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootDir = PathNormalizer.NormalizeRoot(_gitRoot) + Path.DirectorySeparatorChar;
        foreach (var relativePath in relativePaths)
            absolute.Add(PathNormalizer.ToForwardSlash(Path.GetFullPath(Path.Combine(rootDir, relativePath))));
        return absolute;
    }

    /// <summary>
    ///     Widens a document set to whole-type granularity: for every type declared in
    ///     <paramref name="seedPaths" /> in the previous snapshot, adds every document
    ///     that declares any part of that type. Non-partial types contribute only the
    ///     document already in the seed, so the expansion is a no-op unless a partial
    ///     type spans files.
    /// </summary>
    /// <remarks>
    ///     Reads the previous snapshot, so types introduced by the current edit are not
    ///     expanded — their declaring documents are changed documents and are already in
    ///     the seed. Returned paths are git-root-relative, matching the seed's shape.
    /// </remarks>
    private HashSet<string> ExpandToDeclaringTypeParts(string previousSnapshotId, HashSet<string> seedPaths)
    {
        var expanded = new HashSet<string>(seedPaths, StringComparer.OrdinalIgnoreCase);
        if (seedPaths.Count == 0)
            return expanded;

        var docVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, seedPaths);
        if (docVersionIds.Count == 0)
            return expanded;

        foreach (var symbolId in _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, docVersionIds))
        {
            // Type symbol ids are prefixed "T:"; members cannot add parts their
            // containing type does not already declare in the same document.
            if (!SymbolId.TryParse(symbolId, out var sid) || !sid.IsType)
                continue;

            foreach (var location in _store.GetDeclarationLocations(symbolId, previousSnapshotId, true))
                expanded.Add(location.DocumentPath);
        }

        return expanded;
    }

    /// <summary>
    ///     Filters binding-incompleteness records to exactly those whose document
    ///     path is in the given scope set. Restores the lockstep invariant: the save
    ///     only writes facts for documents the delete cleared. Null/empty-path
    ///     records (doc-less aggregates, e.g. <c>extractor_failure</c>) are dropped
    ///     so their copied-forward value is preserved — EXCLUDE-AND-CARRY-FORWARD.
    ///     A document-scoped incremental pass cannot correctly refresh that bucket
    ///     (no document-scoped delete retires it and no scoped re-extraction
    ///     reproduces the whole-compilation count); a <c>--strategy=full</c> rebuild
    ///     is the reference for it. See TRUST_KERNEL R6 open finding 10.
    /// </summary>
    internal static IReadOnlyList<BindingIncompletenessRecord> ScopeBindingIncompleteness(
        IReadOnlyList<BindingIncompletenessRecord> records,
        IReadOnlySet<string> scopeRelativePaths)
    {
        return [.. records
            .Where(r => r.DocumentPath is { } path && scopeRelativePaths.Contains(path))];
    }

    private sealed record IncrementalChangeScope(
        IReadOnlySet<string> ChangedPaths,
        IReadOnlySet<string> DiffAndSearchSymbolIds,
        IReadOnlySet<string> AffectedProjects,
        IReadOnlySet<string> CrossDocumentClosurePaths,
        IReadOnlySet<string> ExtractedPaths);

    private sealed record SnapshotFinalizationContext(
        Solution Solution,
        WorkspaceInfo Workspace,
        SnapshotPair Snapshots,
        IncrementalChangeScope Changes);

    public sealed record IncrementalResult(string NewSnapshotId, string PreviousSnapshotId, int ChangedDocumentCount, int DeclarationsExtracted, int EdgesExtracted, int DiagnosticsExtracted, OrphanEdgeDropSummary OrphanEdgesDropped)
    {
        public bool HasChanges => ChangedDocumentCount > 0 || DeclarationsExtracted > 0 || EdgesExtracted > 0;
    }
}