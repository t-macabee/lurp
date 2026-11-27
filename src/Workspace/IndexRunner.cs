using Lurp.Helpers;
using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Lurp.Workspace;

public static class IndexRunner
{
    internal static readonly string[] Strategies = [SnapshotBuildStrategies.Full, SnapshotBuildStrategies.Incremental];

    public static Task<string> RunAsync(SqliteIndexStore store, string solutionPath, HashSet<string> skipAdapters, string? strategyArg, bool verbose = false, IOutputSink? output = null, bool skipDiff = false,
        bool force = false, CancellationToken cancellationToken = default) =>
        RunAsync(store, solutionPath, skipAdapters, strategyArg, verbose, output, skipDiff, force, skipAnalyzers: false, cancellationToken);

    public static async Task<string> RunAsync(SqliteIndexStore store, string solutionPath, HashSet<string> skipAdapters, string? strategyArg, bool verbose, IOutputSink? output, bool skipDiff,
        bool force, bool skipAnalyzers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runStartedAtUtc = DateTime.UtcNow;

        var sink = output ?? ConsoleOutputSink.Instance;

        using var loader = new WorkspaceLoader(sink);

        var strategy = ResolveStrategy(store, strategyArg, sink);

        sink.WriteLine($"Strategy: {strategy}");

        if (strategy == SnapshotBuildStrategies.Incremental) sink.WriteLine("  (If something looks wrong, run --strategy=full: it rebuilds a snapshot that an incremental run built.)");

        var totalSw = Stopwatch.StartNew();

        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        long? precheckMs = null;
        var inputScan = Stopwatch.StartNew();
        var precheckWorkspaceId = WorkspaceId.Create(gitRoot, Path.GetFullPath(solutionPath));
        var previous = store.LoadSelectedLatestSnapshot(precheckWorkspaceId.Value);
        var capturedBuildInputs = new WorkspaceBuildInputs(gitRoot, previous, cancellationToken);

        if (strategy == SnapshotBuildStrategies.Incremental && !force)
        {
            var currentSnapshot = previous is null ? null
                : WorkspaceFreshness.CanSkipIncrementalIndex(store, previous, gitRoot, capturedBuildInputs, sink, skipAnalyzers);
            inputScan.Stop();
            precheckMs = inputScan.ElapsedMilliseconds;

            if (currentSnapshot is { } builtSnapshot)
            {
                sink.WriteLine("No changes detected. Skipping incremental index.");
                sink.WriteLine("Workspace load skipped: stored snapshot is still current.");

                WriteIncrementalSummary(store, sink, new IncrementalIndexer.IncrementalResult(builtSnapshot.SnapshotId, builtSnapshot.SnapshotId, 0, 0, 0, 0, OrphanEdgeDropSummary.Empty)
                {
                    DocumentsInSnapshot = store.CountDocumentsInSnapshot(builtSnapshot.SnapshotId),
                    ProjectsInSnapshot = store.CountProjectsInSnapshot(builtSnapshot.SnapshotId)
                });
                store.MarkSnapshotSelected(builtSnapshot.SnapshotId, runStartedAtUtc);
                PruneAndRecord(store, sink, null);

                totalSw.Stop();
                sink.WriteLine($"  Total time (incremental): {totalSw.ElapsedMilliseconds} ms");
                return builtSnapshot.SnapshotId;
            }
        }

        var loaded = await loader.LoadAsync(solutionPath, gitRoot, cancellationToken, capturedBuildInputs.CaptureExternalProjects);
        var (solution, loadElapsed) = loaded;

        var swWorkspaceInfo = Stopwatch.StartNew();
        sink.Write("Building workspace info... ");

        var workspaceInfo = new WorkspaceInfo(solution, gitRoot, sink, loaded.UnloadedProjects);
        var buildInputs = capturedBuildInputs.ForLoadedProjects(workspaceInfo.ProjectPaths.Values);

        sink.WriteLine("done.");
        swWorkspaceInfo.Stop();

        var unrestoredProjects = WorkspaceLoadGate.GetUnrestoredProjectNames(solution);
        if (unrestoredProjects.Count > 0)
        {
            sink.WriteErrorLine();
            sink.WriteErrorLine($"WARNING: {unrestoredProjects.Count} of {solution.Projects.Count()} project(s) have not been restored:");
            foreach (var name in unrestoredProjects.OrderBy(static n => n, StringComparer.Ordinal))
                sink.WriteErrorLine($"  - {name}");
            sink.WriteErrorLine(WorkspaceLoadGate.DescribeUnrestored(unrestoredProjects));
            sink.WriteErrorLine();
        }

        if (strategy == SnapshotBuildStrategies.Incremental)
        {
            var previousStorageManifest = store.LoadSelectedLatestSnapshot(workspaceInfo.Id.Value);

            if (previousStorageManifest == null)
            {
                sink.WriteLine("No previous snapshot found. Falling back to full index.");
            }
            else
            {
                try
                {
                    var incrementalIndexer = new IncrementalIndexer(store, gitRoot, skipAdapters, verbose, sink, skipDiff, force, skipAnalyzers);
                    var result = await incrementalIndexer.RunIncrementalAsync(
                        solution, workspaceInfo, previousStorageManifest, buildInputs, loadElapsed, cancellationToken);

                    WriteIncrementalSummary(store, sink, result);
                    store.MarkSnapshotSelected(result.NewSnapshotId, runStartedAtUtc, result.SnapshotWritten ? null : buildInputs);
                    var incrementalTimings = new List<SnapshotTimingRow>
                    {
                        new(SnapshotTimingSteps.WorkspaceInfo, swWorkspaceInfo.ElapsedMilliseconds)
                    };
                    if (precheckMs is { } precheckElapsed)
                        incrementalTimings.Add(new SnapshotTimingRow(SnapshotTimingSteps.IncrementalPrecheck, precheckElapsed));
                    PruneAndRecord(store, sink, result.SnapshotWritten ? result.NewSnapshotId : null, incrementalTimings);

                    totalSw.Stop();

                    sink.WriteLine($"  Total time (incremental): {totalSw.ElapsedMilliseconds} ms");
                    return result.NewSnapshotId;
                }
                catch (FullRebuildRequiredException ex)
                {
                    sink.WriteLine(ex.Message);
                }
            }
        }

        var setupTimings = new List<SnapshotTimingRow>
        {
            new("solution_load", loadElapsed),
            new(SnapshotTimingSteps.WorkspaceInfo, swWorkspaceInfo.ElapsedMilliseconds)
        };
        if (precheckMs is { } fullPrecheckElapsed)
            setupTimings.Add(new SnapshotTimingRow(SnapshotTimingSteps.IncrementalPrecheck, fullPrecheckElapsed));
        var (selectedId, built) = await RunFullIndexAsync(
            store, solution, workspaceInfo, skipAdapters, buildInputs, setupTimings, verbose, sink, skipDiff, force, skipAnalyzers, cancellationToken);
        var newSnapshotId = built ? selectedId : null;
        store.MarkSnapshotSelected(selectedId, runStartedAtUtc, built ? null : buildInputs);

        PruneAndRecord(store, sink, newSnapshotId);

        totalSw.Stop();

        var runKind = newSnapshotId is null ? "snapshot reused, nothing rebuilt" : "full rebuild";
        sink.WriteLine($"  Total time ({runKind}): {totalSw.ElapsedMilliseconds} ms");
        return selectedId;
    }

    private static async Task<(string SelectedSnapshotId, bool Built)> RunFullIndexAsync(
        SqliteIndexStore store, Solution solution, WorkspaceInfo workspaceInfo, HashSet<string> skipAdapters,
        IReadOnlyDictionary<string, string> buildInputs, List<SnapshotTimingRow>? setupTimings, bool verbose, IOutputSink sink,
        bool skipDiff = false, bool force = false, bool skipAnalyzers = false, CancellationToken cancellationToken = default)
    {
        var snapshotId = SnapshotIdentity.Create(workspaceInfo, skipAdapters, skipAnalyzers);
        var snapshotIdStr = snapshotId.ToString();
        var workspaceId = workspaceInfo.Id.Value;

        var disposition = ExistingSnapshotPolicy.Resolve(store, snapshotIdStr, workspaceId, SnapshotBuildStrategies.Full, force, sink);
        if (disposition == ExistingSnapshotDisposition.Reuse)
            return (snapshotIdStr, false);

        var isRebuild = disposition == ExistingSnapshotDisposition.Rebuild;
        // A same-ID rebuild keeps the existing user annotations in place.
        var annotationSourceId = isRebuild ? null : store.GetSelectedLatestSnapshotId(workspaceId);
        var buildId = isRebuild ? new SnapshotId(Guid.NewGuid()) : snapshotId;
        var buildSnapshotId = buildId.ToString();
        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, buildId, skipAdapters: skipAdapters, skipAnalyzers: skipAnalyzers);
        var timings = setupTimings != null ? new List<SnapshotTimingRow>(setupTimings) : new List<SnapshotTimingRow>();

        // Step: Manifest Save
        var swManifest = Stopwatch.StartNew();
        sink.Write("Saving snapshot to database... ");

        manifest.Save(store, SnapshotBuildStrategies.Full, workspaceInfo.DocumentContents,
            replaceExisting: disposition == ExistingSnapshotDisposition.Retry, buildInputs);

        try
        {
            sink.WriteLine("done.");
            swManifest.Stop();
            timings.Add(new SnapshotTimingRow("manifest_save", swManifest.ElapsedMilliseconds));

            // Populate extractor registry (idempotent : no-op on re-runs)
            store.UpsertExtractors(ExtractorRegistry.All);

            var totalDeclarations = 0;
            int totalEdges;
            var totalDiagnostics = 0;
            var allAnnotations = new List<AnnotationRecord>();

            // Step: Full Extraction Loop (compilation load + fact extraction + db writes)
            var swExtract = Stopwatch.StartNew();
            var allEdges = new List<EdgeRecord>();
            var blindProjects = new List<string>(workspaceInfo.UnloadedProjects);
            var extractedProjects = 0;

            if (workspaceInfo.UnloadedProjects.Count > 0)
            {
                store.SaveBindingIncompleteness(buildSnapshotId, workspaceInfo.UnloadedProjects.Select(path =>
                    new BindingIncompletenessRecord(path, null, BindingIncompletenessReason.ProjectUnreadable, 1, VersionConstants.ExtractorVersion)));
            }

            await foreach (var (project, compilation) in CompilationHelper.GetAllAsync(
                               solution,
                               msg => sink.WriteErrorLine($"WARNING: {msg}"),
                               cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projectName = project.Name;

                sink.Write($"  [{projectName}] ");

                // A blind compilation yields declarations with almost no edges. Indexing
                // it would publish that emptiness as a proved absence of callers, so the
                // project is recorded as unreadable and skipped instead.
                if (WorkspaceLoadGate.Classify(compilation) == CompilationReadability.Blind)
                {
                    blindProjects.Add(projectName);
                    store.SaveBindingIncompleteness(
                        buildSnapshotId,
                        WorkspaceLoadGate.DescribeBlindProject(compilation, projectName, workspaceInfo.Id.GitRoot));
                    sink.WriteLine("UNREADABLE: no metadata references resolved; skipped.");
                    continue;
                }

                var options = CompilationFactExtractor.CreateOptions(skipAdapters, output: sink, skipAnalyzers: skipAnalyzers);
                CompilationFactExtractor.ExtractionResult result;
                try
                {
                    result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, buildSnapshotId, projectName, project, options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failing project is isolated rather than fatal: discarding the
                    // siblings that extracted cleanly would cost the whole solution for
                    // one unreadable project. The failure is recorded against this
                    // project's documents so capsules anchored there report unresolved
                    // rather than empty.
                    sink.WriteErrorLine($"FAILED: {ex.Message}");
                    blindProjects.Add(projectName);
                    store.SaveBindingIncompleteness(
                        buildSnapshotId,
                        WorkspaceLoadGate.DescribeBlindProject(compilation, projectName, workspaceInfo.Id.GitRoot));
                    continue;
                }

                store.SaveDeclarations(buildSnapshotId, result.Declarations);
                totalDeclarations += result.Declarations.Count;
                allEdges.AddRange(result.Edges);
                store.SaveDiagnostics(buildSnapshotId, result.Diagnostics);
                totalDiagnostics += result.Diagnostics.Count;
                store.SaveBindingIncompleteness(buildSnapshotId, result.BindingIncompleteness);
                allAnnotations.AddRange(result.Annotations);
                foreach (var measurement in result.Measurements.Where(_ => verbose))
                    sink.WriteErrorLine($"    [measure] {measurement.Extractor}: {measurement.ElapsedMilliseconds} ms, {measurement.AllocatedBytes} bytes");

                extractedProjects++;
                sink.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
            }

            totalEdges = store.SaveEdges(buildSnapshotId, allEdges);
            if (allAnnotations.Count > 0)
                store.SaveAnnotations(buildSnapshotId, allAnnotations);

            // Hard stop only when nothing was readable. There is no lit ground to stand
            // on, so every capsule the snapshot could serve would be an empty graph
            // presented as fact.
            if (extractedProjects == 0 && blindProjects.Count > 0)
            {
                if (workspaceInfo.UnloadedProjects.Count == 0)
                    throw new WorkspaceUnreadableException(WorkspaceLoadGate.DescribeRemediation(blindProjects));

                var names = string.Join(", ", blindProjects.Order(StringComparer.Ordinal));
                const string remediation = "Check that the project files listed in the solution exist, run 'dotnet restore', and re-index.";
                throw new WorkspaceUnreadableException(
                    $"No C# projects could be indexed. Unreadable projects: {names}. {remediation}",
                    remediation: remediation);
            }

            if (blindProjects.Count > 0)
            {
                sink.WriteErrorLine();
                sink.WriteErrorLine($"WARNING: {blindProjects.Count} of {blindProjects.Count + extractedProjects} project(s) were unreadable and are excluded from the graph:");
                foreach (var name in blindProjects.OrderBy(static name => name, StringComparer.Ordinal))
                    sink.WriteErrorLine($"  - {name}");
                sink.WriteErrorLine("Capsules anchored in those projects report 'unresolved', not 'empty'.");
            }

            swExtract.Stop();
            timings.Add(new SnapshotTimingRow("extraction_loop", swExtract.ElapsedMilliseconds));

            var previousManifest = store.LoadSelectedLatestSnapshot(workspaceId, excludedSnapshotId: snapshotIdStr);

            // Step: Remove edges targeting symbols not declared in this snapshot
            // Runs before the semantic diff so the diff compares the new snapshot's
            // post-cleanup edge set against the previous snapshot's post-cleanup set,
            // matching the incremental path's phase order.
            var swOrphans = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            var excludedSymbolIds = await ExcludedDocumentSymbols.CollectAsync(solution, workspaceInfo, cancellationToken);
            var orphanEdgesDropped = store.DeleteOrphanEdges(buildSnapshotId, excludedSymbolIds);
            store.PruneSnapshotGraphNodes(buildSnapshotId);
            swOrphans.Stop();
            timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.OrphanEdgeCleanup, swOrphans.ElapsedMilliseconds));

            (string FromSnapshotId, IReadOnlyList<SemanticChange> Changes)? pendingChanges = null;
            if (!skipDiff && previousManifest != null)
            {
                // Step: Semantic Diff — skipped when the caller passed --skip-diff.
                cancellationToken.ThrowIfCancellationRequested();
                sink.WriteLine();
                var changes = SemanticDiffStep.ComputeAndPersist(
                    store, sink, previousManifest.SnapshotId, buildSnapshotId,
                    null, timings, isRebuild ? snapshotIdStr : null);
                if (isRebuild) pendingChanges = (previousManifest.SnapshotId, changes);
            }

            var totalProjects = extractedProjects + blindProjects.Count;
            var symbolsInSnapshot = store.CountSymbolsInSnapshot(buildSnapshotId);
            var edgesInSnapshot = store.CountEdges(buildSnapshotId);
            var diagnosticsInSnapshot = store.CountDiagnostics(buildSnapshotId);

            sink.WriteLine();
            sink.WriteLine($"  projects_reextracted_this_run:       {extractedProjects}/{totalProjects}");
            sink.WriteLine(FormatIndexSummary(totalDeclarations, symbolsInSnapshot));
            sink.WriteLine($"  edge_relations_after_dedup_this_run: {totalEdges}      edges_dropped_outside_snapshot_scope: {orphanEdgesDropped.FormatDropSummary()}");
            sink.WriteLine($"                                     {"",27}  edge_relations_in_snapshot:          {edgesInSnapshot}");
            var fullWarning = orphanEdgesDropped.FormatWarning();
            if (fullWarning != null)
                sink.WriteErrorLine(fullWarning);
            sink.WriteLine($"  diagnostics_extracted_this_run:      {totalDiagnostics}      diagnostics_in_snapshot: {diagnosticsInSnapshot}");
            sink.WriteLine($"  Schema v{MigrationRunner.LatestVersion}");

            var swFts = Stopwatch.StartNew();
            sink.Write("Building search index... ");
            cancellationToken.ThrowIfCancellationRequested();
            if (isRebuild)
            {
                store.PromoteAndCompleteIndex(buildSnapshotId, snapshotIdStr, annotationSourceId, cancellationToken, pendingChanges);
            }
            else
            {
                store.BuildSearchIndex(snapshotIdStr);
                cancellationToken.ThrowIfCancellationRequested();
                store.CompleteIndex(snapshotIdStr, annotationSourceId);
            }

            sink.WriteLine("done.");
            swFts.Stop();
            timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.FtsBuild, swFts.ElapsedMilliseconds));
            sink.WriteLine($"Index complete for snapshot {snapshotIdStr}");

            IndexFinalization.RefreshPlannerStatistics(store, sink, timings);

            // Persist all timings
            try
            {
                store.SaveMetrics(snapshotIdStr, ProcessMetrics.PeakWorkingSetSnapshotMetrics());
                store.SaveTimings(snapshotIdStr, timings);
            }
            catch (Exception ex)
            {
                sink.WriteErrorLine($"WARNING: Failed to save timings: {ex.Message}");
            }

            return (snapshotIdStr, true);
        }
        catch (Exception ex)
        {
            var reasonCode = ex switch
            {
                OperationCanceledException => "cancelled",
                WorkspaceUnreadableException => "workspace_unreadable",
                _ => "full_index_failure"
            };
            var markedFailed = false;
            try
            {
                markedFailed = store.TryMarkSnapshotFailed(buildSnapshotId, reasonCode, ex.Message);
            }
            catch (Exception markEx)
            {
                sink.WriteErrorLine($"WARNING: Failed to mark snapshot '{snapshotIdStr}' as failed: {markEx.Message}");
            }

            var operation = isRebuild ? $"rebuild attempt for snapshot {snapshotIdStr}" : $"snapshot {snapshotIdStr}";
            sink.WriteErrorLine(markedFailed
                ? $"ERROR: Full index failed, {operation} marked '{SnapshotStatusValues.Failed}' ({reasonCode}): {ex.Message}"
                : $"ERROR: Full index failed, and {operation} was left unmarked ({reasonCode}): {ex.Message}");

            // A failed rebuild must leave the target's previous measurements intact.
            if (!isRebuild)
            {
                try
                {
                    store.SaveMetrics(snapshotIdStr, ProcessMetrics.PeakWorkingSetSnapshotMetrics());
                    store.SaveTimings(snapshotIdStr, timings);
                }
                catch (Exception tex)
                {
                    sink.WriteErrorLine($"WARNING: Failed to save timings: {tex.Message}");
                }
            }

            throw;
        }
    }

    private static void PruneAndRecord(SqliteIndexStore store, IOutputSink sink, string? snapshotId, IReadOnlyList<SnapshotTimingRow>? extraRows = null)
    {
        var sw = Stopwatch.StartNew();
        sink.Write("Pruning old snapshots... ");
        store.DeleteIncompleteSnapshots();
        store.PruneOldSnapshots();
        sink.WriteLine("done.");
        sw.Stop();

        if (snapshotId == null) return;

        var rows = new List<SnapshotTimingRow>();
        if (extraRows != null) rows.AddRange(extraRows);
        rows.Add(new SnapshotTimingRow(SnapshotTimingSteps.PruneSnapshots, sw.ElapsedMilliseconds));

        try
        {
            store.SaveTimings(snapshotId, rows);
        }
        catch (Exception ex)
        {
            sink.WriteErrorLine($"WARNING: Failed to save timings: {ex.Message}");
        }
    }

    private static string ResolveStrategy(SqliteIndexStore store, string? strategyArg, IOutputSink sink)
    {
        if (strategyArg != null)
        {
            var strategy = strategyArg.ToLowerInvariant();

            if (!Strategies.Contains(strategy, StringComparer.Ordinal))
                // A caller-contract violation, not a CLI refusal: the Workspace layer
                // must not exit the process. The CLI validates --strategy before
                // reaching here (IndexHandler), so a throw means a misbehaving host.
                throw new InvalidOperationException($"Invalid strategy '{strategyArg}'. Must be '{SnapshotBuildStrategies.Incremental}' or '{SnapshotBuildStrategies.Full}'.");
            return strategy;
        }

        var latestSnapshotId = store.GetSelectedLatestSnapshotId();

        if (latestSnapshotId == null)
        {
            sink.WriteLine("No existing snapshot found. Defaulting to --strategy=full for initial index.");
            return SnapshotBuildStrategies.Full;
        }

        return SnapshotBuildStrategies.Incremental;
    }

    /// <summary>
    ///     The two counters measure different things: extracted declaration records for
    ///     this run, and the distinct symbols the snapshot holds (a symbol declared in
    ///     several documents is one symbol and several records).
    /// </summary>
    private static string FormatIndexSummary(int extractedDeclarations, int symbolsInSnapshot)
    {
        return $"  declarations_extracted_this_run:     {extractedDeclarations}      symbols_in_snapshot: {symbolsInSnapshot}";
    }

    private static void WriteIncrementalSummary(SqliteIndexStore store, IOutputSink sink, IncrementalIndexer.IncrementalResult result)
    {
        sink.WriteLine();
        sink.WriteLine($"Incremental index complete. Snapshot: {result.NewSnapshotId}");
        sink.WriteLine($"  Previous snapshot: {result.PreviousSnapshotId}");
        sink.WriteLine($"  documents_changed_this_run:          {result.ChangedDocumentCount}      documents_in_snapshot: {result.DocumentsInSnapshot}");
        sink.WriteLine($"  projects_reextracted_this_run:       {result.ProjectsReextracted}/{result.ProjectsInSnapshot}");
        sink.WriteLine(FormatIndexSummary(result.DeclarationsExtracted, store.CountSymbolsInSnapshot(result.NewSnapshotId)));
        sink.WriteLine($"  edge_relations_after_dedup_this_run: {result.EdgesExtracted}      edges_dropped_outside_snapshot_scope: {result.OrphanEdgesDropped.FormatDropSummary()}");
        sink.WriteLine($"                                     {"",27}  edge_relations_in_snapshot:          {store.CountEdges(result.NewSnapshotId)}");
        var warning = result.OrphanEdgesDropped.FormatWarning();
        if (warning != null)
            sink.WriteErrorLine(warning);
        sink.WriteLine($"  diagnostics_extracted_this_run:      {result.DiagnosticsExtracted}      diagnostics_in_snapshot: {store.CountDiagnostics(result.NewSnapshotId)}");
        sink.WriteLine($"  Schema v{MigrationRunner.LatestVersion}");
    }
}
