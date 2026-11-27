using Lurp.Helpers;
using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Lurp.Workspace;

public sealed class IncrementalIndexer(SqliteIndexStore store, string gitRoot, HashSet<string> skipAdapters, bool verbose = false, IOutputSink? output = null, bool skipDiff = false, bool force = false, bool skipAnalyzers = false)
{
    private readonly DocumentChangeDetector _changeDetector = new(gitRoot, output ?? ConsoleOutputSink.Instance);
    private readonly string _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
    private readonly IOutputSink _output = output ?? ConsoleOutputSink.Instance;
    private readonly SqliteIndexStore _store = store ?? throw new ArgumentNullException(nameof(store));

    // Incremental indexing strategy:
    //   1. Hash all documents and compare against the previous manifest to find changed/new/deleted files.
    //   2. Compute the one extraction scope (changed documents, reverse-edge closure, type parts, and any D43 declaration-level wide set) and identify affected projects.
    //   3. Load compilations only for affected projects (skip unchanged ones).
    //   4. Create a new snapshot manifest, copy forward edges/diagnostics/symbols/annotations from the previous snapshot.
    //   5. Remove stale data (edges, annotations, binding-incompleteness, declarations, TFMs, diagnostics) for the extraction scope.
    //   6. Re-extract declarations, edges, diagnostics, annotations and binding-incompleteness in one pass over the extraction scope.
    //   7. Prune removed symbols, clean up orphan edges and graph nodes, rebuild FTS5, persist semantic diff, and complete the snapshot.
    public Task<IncrementalResult> RunIncrementalAsync(Solution solution, WorkspaceInfo workspaceInfo,
        SnapshotRow previousManifest, long solutionLoadMs = 0, CancellationToken cancellationToken = default) =>
        RunIncrementalAsync(solution, workspaceInfo, previousManifest, buildInputs: null, solutionLoadMs, cancellationToken);

    internal async Task<IncrementalResult> RunIncrementalAsync(Solution solution, WorkspaceInfo workspaceInfo,
        SnapshotRow previousManifest, IReadOnlyDictionary<string, string>? buildInputs, long solutionLoadMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var swConfig = Stopwatch.StartNew();
        var previousSnapshotId = previousManifest.SnapshotId;
        var previousRichManifest = SnapshotManifest.FromStorageManifest(previousManifest);
        var timings = new List<SnapshotTimingRow>
        {
            new("solution_load", solutionLoadMs)
        };

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

        if (previousManifest.AnalyzersSkipped != skipAnalyzers)
            throw new FullRebuildRequiredException(
            [
                new SnapshotMismatch(MismatchKind.VersionChanged,
                    $"Analyzer mode changed (analyzers_skipped: {previousManifest.AnalyzersSkipped} -> {skipAnalyzers}).",
                    null,
                    null)
            ]);

        swConfig.Stop();
        timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.ConfigurationCheck, swConfig.ElapsedMilliseconds));

        // Step 1: Change Detection
        cancellationToken.ThrowIfCancellationRequested();
        var sw1 = Stopwatch.StartNew();
        var (changedDocs, changedPaths) = _changeDetector.DetectAndLogChanges(workspaceInfo, previousRichManifest);
        sw1.Stop();
        timings.Add(new SnapshotTimingRow("change_detection", sw1.ElapsedMilliseconds));

        if (changedDocs.Count == 0)
            return new IncrementalResult(previousSnapshotId, previousSnapshotId, 0, 0, 0, 0, OrphanEdgeDropSummary.Empty)
            {
                DocumentsInSnapshot = workspaceInfo.Documents.Count,
                ProjectsInSnapshot = solution.Projects.Count()
            };

        // Deterministic identity: identical indexed state must produce the
        // identical snapshot id, so a complete snapshot with this id must be
        // reused rather than duplicated, and a crashed or failed attempt with
        // the same id must not block a retry.
        var snapshotId = SnapshotIdentity.Create(workspaceInfo, skipAdapters, skipAnalyzers);
        var newSnapshotIdStr = snapshotId.ToString();
        var disposition = ExistingSnapshotPolicy.Resolve(_store, newSnapshotIdStr, workspaceInfo.Id.Value, SnapshotBuildStrategies.Incremental, force, _output);
        if (disposition == ExistingSnapshotDisposition.Reuse)
        {
            return new IncrementalResult(newSnapshotIdStr, previousSnapshotId, changedDocs.Count, 0, 0, 0, OrphanEdgeDropSummary.Empty)
            {
                DocumentsInSnapshot = workspaceInfo.Documents.Count,
                ProjectsInSnapshot = solution.Projects.Count()
            };
        }

        // Step 2: Affected Project Resolution
        cancellationToken.ThrowIfCancellationRequested();
        var sw2 = Stopwatch.StartNew();
        _output.Write("Identifying affected projects... ");

        // Declaration-level widening (D43): an added or deleted document, or an edit
        // outside member bodies (Roslyn top-level equivalence), can create or steal a
        // binding with no edge in the previous snapshot. Widen to the documents of the
        // trigger's project and every project that depends on it. Body-only edits keep
        // the narrow reverse-edge closure.
        var triggerPaths = await CollectDeclarationChangeTriggersAsync(solution, previousSnapshotId, changedDocs, cancellationToken);
        var widePaths = triggerPaths.Count == 0
            ? null
            : GetProjectDocumentPaths(solution, _changeDetector.IdentifyAffectedProjects(solution, triggerPaths, previousRichManifest.ProjectDocuments));

        var closure = new ReverseEdgeClosure(_store, _gitRoot);
        var (closurePaths, fellBackToProjectScope) = closure.FindAffectedDocPaths(solution, previousSnapshotId, changedPaths);
        if (widePaths != null)
            _output.WriteLine($"  {triggerPaths.Count} declaration-level change(s); widened scope to {widePaths.Count} document(s).");

        timings.Add(new SnapshotTimingRow(
            fellBackToProjectScope ? SnapshotTimingSteps.ClosureFallback : SnapshotTimingSteps.ClosureNarrowed,
            0));
        if (widePaths is { Count: > 0 })
            timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.ClosureWidened, 0));

        // Extraction scope: the one set of documents that will be deleted and
        // re-extracted in lockstep in steps 5 and 6.
        //
        // Contents: changed documents, the reverse-edge closure (closurePaths),
        // the declaration-level wide set (when any document is added, deleted, or
        // edited outside member bodies), and every document declaring any part of
        // a type whose declaring syntax appears in that seed (ExpandToDeclaringTypeParts).
        // The type-part expansion is required because two extraction guards are type-level
        // (SymbolStructuralEdgeExtractor.IsTypeInScope, PolymorphismExtractionContext
        // .IsTypeInScope), so a purely file-granular scope would leave partial types
        // half in and half out of scope while whole-symbol properties such as
        // is_partial stay computed across all parts.
        //
        // Keeping the changed documents together with the closure in the one
        // extraction pass ensures every contributor's type to an aggregated
        // MayDispatchTo edge is visible alongside the dispatch target's document,
        // without needing a second extraction pass.
        var extractionScopeSeed = new HashSet<string>(changedPaths, PathNormalizer.PathComparer);
        extractionScopeSeed.UnionWith(closurePaths);
        if (widePaths != null)
            extractionScopeSeed.UnionWith(widePaths);

        var extractionScopePaths = ExpandToDeclaringTypeParts(previousSnapshotId, extractionScopeSeed);
        var affectedProjects = _changeDetector.IdentifyAffectedProjects(
            solution,
            extractionScopePaths,
            previousRichManifest.ProjectDocuments,
            includeDependents: widePaths != null);

        // Extraction guards compare against SyntaxTree.FilePath, which is absolute
        // and forward-slash normalized. Passing the git-root-relative form matches
        // no tree and silently extracts almost nothing, so convert here.
        var extractionScopeAbsolutePaths = ToAbsoluteNormalizedPaths(extractionScopePaths);
        _output.WriteLine($"{affectedProjects.Count} affected: {string.Join(", ", affectedProjects)}");

        sw2.Stop();
        timings.Add(new SnapshotTimingRow("affected_project_resolution", sw2.ElapsedMilliseconds));

        // Step 3: Compilation Load
        cancellationToken.ThrowIfCancellationRequested();
        var sw3 = Stopwatch.StartNew();
        var affectedCompilations = await LoadAffectedCompilationsAsync(solution, affectedProjects, cancellationToken);
        sw3.Stop();
        timings.Add(new SnapshotTimingRow("compilation_load", sw3.ElapsedMilliseconds));

        var isRebuild = disposition == ExistingSnapshotDisposition.Rebuild;
        var buildId = isRebuild ? new SnapshotId(Guid.NewGuid()) : snapshotId;
        var buildSnapshotId = buildId.ToString();
        var newManifest = SnapshotManifest.FromWorkspace(workspaceInfo, buildId, SnapshotId.Parse(previousSnapshotId), skipAdapters, skipAnalyzers);
        // Step 4: Manifest Creation
        cancellationToken.ThrowIfCancellationRequested();
        var sw4 = Stopwatch.StartNew();
        _output.Write("Saving new snapshot manifest... ");
        newManifest.Save(_store, SnapshotBuildStrategies.Incremental, workspaceInfo.DocumentContents,
            replaceExisting: disposition == ExistingSnapshotDisposition.Retry, buildInputs);

        int totalDeclarations;
        int totalEdges;
        int totalDiagnostics;
        OrphanEdgeDropSummary orphanEdgesDropped;

        try
        {
            _output.WriteLine("done.");
            sw4.Stop();
            timings.Add(new SnapshotTimingRow("manifest_creation", sw4.ElapsedMilliseconds));

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

            var pruneCandidates = PrepareSnapshotData(previousSnapshotId, buildSnapshotId, affectedProjects, extractionScopePaths, copySearchIndex: !isRebuild);
            sw5.Stop();
            timings.Add(new SnapshotTimingRow("stale_data_removal", sw5.ElapsedMilliseconds));

            // Step 6: Re-extraction
            cancellationToken.ThrowIfCancellationRequested();
            var sw6 = Stopwatch.StartNew();
            (totalDeclarations, totalEdges, totalDiagnostics) =
                ExtractReplacementFacts(workspaceInfo, buildSnapshotId, affectedCompilations, extractionScopeAbsolutePaths, extractionScopePaths);
            sw6.Stop();
            timings.Add(new SnapshotTimingRow("re_extraction", sw6.ElapsedMilliseconds));

            // Step 6b: Prune symbols that were in the scope documents' old versions
            // and no longer have any declaration linked in the new snapshot.
            var swScope = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            var prunedSymbolIds = _store.PruneUnlinkedSnapshotSymbols(buildSnapshotId, pruneCandidates);
            if (prunedSymbolIds.Count > 0)
                _output.WriteLine($"Pruned {prunedSymbolIds.Count} removed symbols.");

            // Compute the set of symbol IDs that need their FTS entries refreshed and
            // semantic diff evaluated: all symbols currently declared in the extraction
            // scope after re-extraction, plus any symbols that were pruned.
            var diffAndSearchSymbolIds = ComputeChangedSymbolIds(buildSnapshotId, extractionScopePaths);
            foreach (var id in prunedSymbolIds)
                diffAndSearchSymbolIds.Add(id);

            // Build the change scope once so downstream phases share the same sets.
            var changeScope = new IncrementalChangeScope(
                changedPaths,
                diffAndSearchSymbolIds);

            var finalizationContext = new SnapshotFinalizationContext(
                new SnapshotPair(previousSnapshotId, buildSnapshotId),
                changeScope);

            swScope.Stop();
            timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.ChangeScope, swScope.ElapsedMilliseconds));

            // Step 7: Orphan cleanup + FTS Rebuild + Diff + Complete
            cancellationToken.ThrowIfCancellationRequested();
            var previousExcluded = _store.LoadExcludedSymbolsByProject(previousSnapshotId);
            var previousFingerprints = previousManifest.Projects.ToDictionary(p => p.Name, p => p.ExcludedDocumentsFingerprint, StringComparer.Ordinal);
            var excludedByProject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var project in solution.Projects)
            {
                if (!workspaceInfo.ExcludedDocuments.ContainsKey(project.Id)) continue;
                previousFingerprints.TryGetValue(project.Name, out var previousFingerprint);
                workspaceInfo.ExcludedDocumentsFingerprints.TryGetValue(project.Name, out var currentFingerprint);
                if (!affectedProjects.Contains(project.Name) && previousFingerprint != null && currentFingerprint != null &&
                    string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal))
                {
                    excludedByProject[project.Name] = previousExcluded.GetValueOrDefault(project.Name) ?? new HashSet<string>(StringComparer.Ordinal);
                    continue;
                }
                var compilation = affectedCompilations.TryGetValue(project.Name, out var affected)
                    ? affected.Compilation
                    : await CompilationHelper.GetCompilationAsync(project, cancellationToken);
                if (compilation != null)
                    excludedByProject[project.Name] = await ExcludedDocumentSymbols.CollectForProjectAsync(project, compilation, workspaceInfo, cancellationToken);
            }
            _store.SaveExcludedSymbols(buildSnapshotId, excludedByProject);
            var excludedSymbolIds = excludedByProject.Values.SelectMany(static symbols => symbols).ToHashSet(StringComparer.Ordinal);
            orphanEdgesDropped = FinalizeSnapshot(finalizationContext, excludedSymbolIds, timings, isRebuild ? newSnapshotIdStr : null, cancellationToken);
        }
        catch (Exception ex)
        {
            var reasonCode = ex is OperationCanceledException ? "cancelled" : "incremental_index_failure";
            var markedFailed = false;
            try
            {
                markedFailed = _store.TryMarkSnapshotFailed(buildSnapshotId, reasonCode, ex.Message);
            }
            catch (Exception markEx)
            {
                _output.WriteErrorLine($"WARNING: Failed to mark snapshot '{newSnapshotIdStr}' as failed: {markEx.Message}");
            }

            var operation = isRebuild ? $"rebuild attempt for snapshot {newSnapshotIdStr}" : $"snapshot {newSnapshotIdStr}";
            _output.WriteErrorLine(markedFailed
                ? $"ERROR: Incremental index failed mid-operation, {operation} marked '{SnapshotStatusValues.Failed}' ({reasonCode}): {ex.Message}"
                : $"ERROR: Incremental index failed mid-operation, and {operation} was left unmarked ({reasonCode}): {ex.Message}");
            throw;
        }

        // Persist all timings
        try
        {
            _store.SaveMetrics(newSnapshotIdStr, ProcessMetrics.PeakWorkingSetSnapshotMetrics());
            _store.SaveTimings(newSnapshotIdStr, timings);
        }
        catch (Exception ex)
        {
            _output.WriteErrorLine($"WARNING: Failed to save timings: {ex.Message}");
        }

        var projectsInSnapshot = solution.Projects.Count();

        return new IncrementalResult(newSnapshotIdStr, previousSnapshotId, changedDocs.Count, totalDeclarations, totalEdges, totalDiagnostics, orphanEdgesDropped)
        {
            SnapshotWritten = true,
            DocumentsInSnapshot = workspaceInfo.Documents.Count,
            ProjectsReextracted = affectedProjects.Count,
            ProjectsInSnapshot = projectsInSnapshot
        };
    }

    private async Task<Dictionary<string, (Project Project, Compilation Compilation)>> LoadAffectedCompilationsAsync(Solution solution, HashSet<string> affectedProjects, CancellationToken cancellationToken)
    {
        _output.Write("Loading compilations for affected projects... ");
        var result = new Dictionary<string, (Project Project, Compilation Compilation)>(StringComparer.Ordinal);
        await foreach (var (project, compilation) in CompilationHelper.GetAllAsync(
                           solution,
                           msg => _output.WriteErrorLine($"WARNING: {msg}"),
                           project => affectedProjects.Contains(project.Name),
                           cancellationToken))
        {
            result[project.Name] = (project, compilation);
        }

        _output.WriteLine($"done ({result.Count} compilations).");
        return result;
    }

    private List<string> PrepareSnapshotData(string previousSnapshotId, string newSnapshotIdStr, HashSet<string> affectedProjects, HashSet<string> extractionScopePaths, bool copySearchIndex)
    {
        _output.Write("Preparing snapshot data (copy forward, remove stale)... ");

        _store.CopyEdgesToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.CopySnapshotDiagnostics(previousSnapshotId, newSnapshotIdStr);
        _store.CopyExtractorAnnotationsToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.CopyBindingIncompleteness(previousSnapshotId, newSnapshotIdStr);

        // Retire facts for the documents in the extraction scope.
        // Documents outside the scope keep their copied edge sites.
        if (extractionScopePaths.Count > 0)
            _store.DeleteFactsByDocumentPaths(newSnapshotIdStr, extractionScopePaths);

        // Null-path edges (from symbols with no DeclaringSyntaxReferences, e.g. an
        // implicit default constructor) can't be scoped to a document by path, so we
        // scope the delete to symbols declared in the extraction-scope documents
        // rather than the whole affected assembly : re-extraction is scoped the same
        // way, so an unchanged document elsewhere in the assembly must keep its
        // copied-forward null-path edges intact.
        var invalidatedOldVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, extractionScopePaths);
        List<string> changedSymbolIds = invalidatedOldVersionIds.Count > 0
            ? _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, invalidatedOldVersionIds)
            : [];
        if (invalidatedOldVersionIds.Count > 0)
            _store.DeleteEdgesWithNullDocumentPathForSymbols(newSnapshotIdStr, changedSymbolIds);

        _store.CopySnapshotSymbols(previousSnapshotId, newSnapshotIdStr);
        _store.CopySnapshotDeclarations(previousSnapshotId, newSnapshotIdStr);
        if (invalidatedOldVersionIds.Count > 0)
            _store.DeleteSnapshotDeclarationsByDocumentVersionIds(newSnapshotIdStr, invalidatedOldVersionIds);
        _store.CopySymbolTargetFrameworks(previousSnapshotId, newSnapshotIdStr);

        // The TFM copy above is a union: a symbol re-extracted in this run must
        // not keep TFM rows the current sources no longer declare, so its rows
        // are removed here and written back by the extraction.
        _store.DeleteSymbolTargetFrameworksBySymbolIds(newSnapshotIdStr, changedSymbolIds);

        // A fresh incremental snapshot refreshes only the changed FTS rows.
        // A staged replacement instead builds FTS during promotion.
        if (copySearchIndex)
            _store.CopySearchIndexToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.DeleteDiagnosticsByProjectNames(newSnapshotIdStr, affectedProjects);

        _output.WriteLine("done.");
        return changedSymbolIds;
    }

    private (int Declarations, int Edges, int Diagnostics) ExtractReplacementFacts(
        WorkspaceInfo workspaceInfo, string newSnapshotIdStr, Dictionary<string, (Project Project, Compilation Compilation)> affectedCompilations,
        IReadOnlySet<string> extractionScopeAbsolutePaths,
        IReadOnlySet<string> extractionScopeRelativePaths)
    {
        _output.WriteLine("Extracting replacement facts for affected projects...");
        int totalDecl = 0, totalEdge = 0, totalDiag = 0;

        foreach (var (projectName, entry) in affectedCompilations)
        {
            var (project, compilation) = entry;
            _output.Write($"  [{projectName}] ");

            if (WorkspaceLoadGate.Classify(compilation) == CompilationReadability.Blind)
            {
                _store.SaveBindingIncompleteness(
                    newSnapshotIdStr,
                    ScopeToDocuments(
                        WorkspaceLoadGate.DescribeBlindProject(compilation, projectName, workspaceInfo.Id.GitRoot),
                        r => r.DocumentPath,
                        extractionScopeRelativePaths));
                _output.WriteLine("UNREADABLE: no metadata references resolved; skipped.");
                continue;
            }

            var options = CompilationFactExtractor.CreateOptions(skipAdapters, extractionScopeAbsolutePaths, _output, skipAnalyzers);
            CompilationFactExtractor.ExtractionResult result;
            try
            {
                result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, newSnapshotIdStr, projectName, project, options);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // D34: the declaration stage is required. Its failure makes the project
                // unreadable for this pass, as in the full pipeline.
                _output.WriteErrorLine($"FAILED: {ex.Message}");
                _store.SaveBindingIncompleteness(
                    newSnapshotIdStr,
                    ScopeToDocuments(
                        WorkspaceLoadGate.DescribeBlindProject(compilation, projectName, workspaceInfo.Id.GitRoot),
                        r => r.DocumentPath,
                        extractionScopeRelativePaths));
                continue;
            }

            _store.SaveDeclarations(newSnapshotIdStr, result.Declarations);
            totalDecl += result.Declarations.Count;
            totalEdge += _store.SaveEdges(newSnapshotIdStr, result.Edges);
            _store.SaveDiagnostics(newSnapshotIdStr, result.Diagnostics);
            totalDiag += result.Diagnostics.Count;

            // Lockstep invariant: save binding-incompleteness only for documents
            // in the extraction scope (the same set the delete cleared). Records
            // for out-of-scope documents carry forward their copied-forward
            // values unchanged.
            var scopedBi = ScopeToDocuments(result.BindingIncompleteness, r => r.DocumentPath, extractionScopeRelativePaths);
            _store.SaveBindingIncompleteness(newSnapshotIdStr, scopedBi);
            var scopedAnnotations = ScopeToDocuments(result.Annotations, a => a.DocumentPath, extractionScopeRelativePaths);
            if (scopedAnnotations.Count > 0)
                _store.SaveAnnotations(newSnapshotIdStr, scopedAnnotations);
            foreach (var measurement in result.Measurements.Where(_ => verbose))
                _output.WriteErrorLine($"    [measure] {measurement.Extractor}: {measurement.ElapsedMilliseconds} ms, {measurement.AllocatedBytes} bytes");

            _output.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
        }

        return (totalDecl, totalEdge, totalDiag);
    }

    private OrphanEdgeDropSummary FinalizeSnapshot(
        SnapshotFinalizationContext context,
        IReadOnlyCollection<string> excludedSymbolIds,
        List<SnapshotTimingRow> timings,
        string? rebuildTargetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Remove edges targeting symbols not declared in this snapshot
        var swOrphans = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        var orphanEdgesDropped = _store.DeleteOrphanEdges(context.Snapshots.ToSnapshotId, excludedSymbolIds);

        // Drop stale synthetic/route graph nodes carried forward by
        // CopyEdgesToSnapshot that no longer have any edge or declaration in this
        // snapshot. Without this they linger in snapshot_graph_nodes and keep
        // masking genuinely orphaned edges in DeleteOrphanEdges (ARCH-006).
        cancellationToken.ThrowIfCancellationRequested();
        _store.PruneSnapshotGraphNodes(context.Snapshots.ToSnapshotId);
        swOrphans.Stop();
        timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.OrphanEdgeCleanup, swOrphans.ElapsedMilliseconds));

        if (rebuildTargetId is null)
            RebuildSearchIndex(context, timings);

        // Semantic diff persistence — skipped when the caller passed
        // --skip-diff. Only --mode=diff (which recomputes live) and --mode=impact's
        // semantic-causes annotation (which degrades to an empty list when absent)
        // consume this; a run that needs neither shouldn't pay for it.
        cancellationToken.ThrowIfCancellationRequested();
        (string FromSnapshotId, IReadOnlyList<SemanticChange> Changes)? pendingChanges = null;
        if (!skipDiff)
        {
            var changes = SemanticDiffStep.ComputeAndPersist(_store, _output, context.Snapshots.FromSnapshotId,
                context.Snapshots.ToSnapshotId, (HashSet<string>)context.Changes.DiffAndSearchSymbolIds, timings, rebuildTargetId);
            if (rebuildTargetId != null) pendingChanges = (context.Snapshots.FromSnapshotId, changes);
        }

        // Completion must be last : all preceding phases must succeed.
        cancellationToken.ThrowIfCancellationRequested();
        if (rebuildTargetId is null)
        {
            _store.CompleteIndex(context.Snapshots.ToSnapshotId, context.Snapshots.FromSnapshotId);
        }
        else
        {
            var swFts = Stopwatch.StartNew();
            _output.Write("Building search index for the replacement snapshot... ");
            _store.PromoteAndCompleteIndex(context.Snapshots.ToSnapshotId, rebuildTargetId,
                context.Snapshots.FromSnapshotId, cancellationToken, pendingChanges);
            _output.WriteLine("done.");
            swFts.Stop();
            timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.FtsBuild, swFts.ElapsedMilliseconds));
        }

        IndexFinalization.RefreshPlannerStatistics(_store, _output, timings);
        return orphanEdgesDropped;
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

    private HashSet<string> ComputeChangedSymbolIds(string snapshotId, HashSet<string> changedPaths)
    {
        if (changedPaths.Count == 0)
            return [];

        var pathToVersion = _store.GetDocumentVersionIdsByPath(snapshotId);
        var versionIds = new HashSet<string>(
            changedPaths
                .Where(pathToVersion.ContainsKey)
                .Select(p => pathToVersion[p]));

        if (versionIds.Count == 0)
            return [];

        return [.. _store.GetSymbolIdsByDocumentVersionIds(snapshotId, versionIds)];
    }

    private HashSet<string> GetProjectDocumentPaths(Solution solution, HashSet<string> projectNames)
    {
        var paths = new HashSet<string>(PathNormalizer.PathComparer);
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

    private async Task<HashSet<string>> CollectDeclarationChangeTriggersAsync(
        Solution solution,
        string previousSnapshotId,
        IReadOnlyList<DocumentChangeDetector.DocumentChangeInfo> changedDocs,
        CancellationToken cancellationToken)
    {
        var triggerPaths = new HashSet<string>(PathNormalizer.PathComparer);
        Dictionary<string, List<Document>>? docsByRelPath = null;

        foreach (var change in changedDocs)
        {
            if (change.ChangeKind is DocumentChangeDetector.DocumentChangeKind.New or DocumentChangeDetector.DocumentChangeKind.Deleted)
            {
                triggerPaths.Add(change.RelativePath);
                continue;
            }

            docsByRelPath ??= BuildSolutionDocumentMap(solution);
            if (!docsByRelPath.TryGetValue(change.RelativePath, out var docs) || docs.Count == 0)
            {
                triggerPaths.Add(change.RelativePath);
                continue;
            }

            var previousText = _store.GetSource(change.RelativePath, previousSnapshotId);
            foreach (var doc in docs)
            {
                var tree = await doc.GetSyntaxTreeAsync(cancellationToken);
                if (tree == null || !DeclarationChange.IsBodyOnlyEdit(tree, previousText))
                {
                    triggerPaths.Add(change.RelativePath);
                    break;
                }
            }
        }

        return triggerPaths;
    }

    private Dictionary<string, List<Document>> BuildSolutionDocumentMap(Solution solution)
    {
        var map = new Dictionary<string, List<Document>>(PathNormalizer.PathComparer);
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (string.IsNullOrEmpty(document.FilePath))
                    continue;

                var relPath = PathNormalizer.ToGitRelative(document.FilePath, _gitRoot);
                if (!map.TryGetValue(relPath, out var list))
                {
                    list = [];
                    map[relPath] = list;
                }

                list.Add(document);
            }
        }

        return map;
    }

    /// <summary>
    ///     Converts git-root-relative document paths to the absolute, forward-slash
    ///     form that every extraction scope guard compares against
    ///     (<c>SyntaxTree.FilePath.Replace('\\', '/')</c>).
    /// </summary>
    private HashSet<string> ToAbsoluteNormalizedPaths(IReadOnlySet<string> relativePaths)
    {
        var absolute = new HashSet<string>(PathNormalizer.PathComparer);
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
        var expanded = new HashSet<string>(seedPaths, PathNormalizer.PathComparer);
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

    internal static IReadOnlyList<T> ScopeToDocuments<T>(
        IReadOnlyList<T> records,
        Func<T, string?> getDocumentPath,
        IReadOnlySet<string>? scopeRelativePaths)
    {
        if (scopeRelativePaths == null)
            return records;

        return [.. records
            .Where(r => getDocumentPath(r) is { } path && scopeRelativePaths.Contains(path))];
    }

    private sealed record IncrementalChangeScope(
        IReadOnlySet<string> ChangedPaths,
        IReadOnlySet<string> DiffAndSearchSymbolIds);

    private sealed record SnapshotFinalizationContext(
        SnapshotPair Snapshots,
        IncrementalChangeScope Changes);

    public sealed record IncrementalResult(string NewSnapshotId, string PreviousSnapshotId, int ChangedDocumentCount, int DeclarationsExtracted, int EdgesExtracted, int DiagnosticsExtracted, OrphanEdgeDropSummary OrphanEdgesDropped)
    {
        /// <summary>True when this run wrote the snapshot named by <see cref="NewSnapshotId" />; false when it returned an existing snapshot (no change, or reuse).</summary>
        public bool SnapshotWritten { get; init; }

        /// <summary>Documents stored in <see cref="NewSnapshotId" />.</summary>
        public int DocumentsInSnapshot { get; init; }

        /// <summary>Projects re-extracted by this run.</summary>
        public int ProjectsReextracted { get; init; }

        /// <summary>Projects stored in <see cref="NewSnapshotId" />.</summary>
        public int ProjectsInSnapshot { get; init; }
    }
}
