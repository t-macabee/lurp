using Lurp.Storage;
using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Lurp.Workspace;

public static class IndexRunner
{
    private const string FullStrategy = "full";
    private const string IncrementalStrategy = "incremental";
    public static async Task RunAsync(IIndexStore store, string solutionPath, string outputDir, HashSet<string> skipAdapters, string? jsonExportPath, string? strategyArg, CancellationToken cancellationToken = default, bool verbose = false, IOutputSink? output = null, bool skipDiff = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sink = output ?? ConsoleOutputSink.Instance;

        using var loader = new WorkspaceLoader();

        string strategy = ResolveStrategy(store, strategyArg, sink);

        sink.WriteLine($"Strategy: {strategy}");

        if (strategy == FullStrategy)
        {
            sink.WriteLine("  (Use --strategy=full to force a full rebuild when something looks wrong.)");
        }

        var totalSw = Stopwatch.StartNew();

        var loaded = await loader.LoadAsync(solutionPath, cancellationToken);
        var solution = loaded.Solution;

        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        var swWorkspaceInfo = Stopwatch.StartNew();
        sink.Write("Building workspace info... ");

        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        sink.WriteLine("done.");
        swWorkspaceInfo.Stop();

        if (strategy == IncrementalStrategy)
        {
            var previousStorageManifest = store.LoadLatestSnapshot(workspaceInfo.Id.Value);

            if (previousStorageManifest == null)
            {
                sink.WriteLine("No previous snapshot found. Falling back to full index.");
                strategy = FullStrategy;
            }
            else
            {
                try
                {
                    var incrementalIndexer = new IncrementalIndexer(store, gitRoot, skipAdapters, jsonExportPath, verbose, sink, skipDiff);
                    var result = await incrementalIndexer.RunIncrementalAsync(solution, workspaceInfo, previousStorageManifest, cancellationToken);

                    sink.WriteLine();
                    sink.WriteLine($"Incremental index complete. Snapshot: {result.NewSnapshotId}");
                    sink.WriteLine($"  Previous snapshot: {result.PreviousSnapshotId}");
                    sink.WriteLine($"  documents_changed_this_run:          {result.ChangedDocumentCount}");
                    sink.WriteLine($"  declarations_extracted_this_run:     {result.DeclarationsExtracted}      declarations_in_snapshot: {store.CountSymbolsInSnapshot(result.NewSnapshotId)}");
                    sink.WriteLine($"  edge_relations_after_dedup_this_run: {result.EdgesExtracted}      edge_relations_in_snapshot: {store.CountEdges(result.NewSnapshotId)}");
                    sink.WriteLine($"  diagnostics_extracted_this_run:      {result.DiagnosticsExtracted}      diagnostics_in_snapshot: {store.CountDiagnostics(result.NewSnapshotId)}");
                    sink.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");
                    sink.Write("Pruning old snapshots... ");

                    store.DeleteIncompleteSnapshots();
                    store.PruneOldSnapshots(keep: 3);

                    sink.WriteLine("done.");

                    totalSw.Stop();

                    sink.WriteLine($"  Total time (incremental): {totalSw.ElapsedMilliseconds} ms");
                    return;
                }
                catch (FullRebuildRequiredException ex)
                {
                    sink.WriteLine($"Full rebuild required: {ex.Message}");
                    strategy = FullStrategy;
                }
            }
        }

        if (strategy == FullStrategy)
        {
            var setupTimings = new List<SnapshotTimingRow>
            {
                new SnapshotTimingRow("solution_load", loaded.LoadElapsedMilliseconds, DateTime.UtcNow),
                new SnapshotTimingRow("workspace_info", swWorkspaceInfo.ElapsedMilliseconds, DateTime.UtcNow),
            };
            await RunFullIndexAsync(store, solution, workspaceInfo, skipAdapters, jsonExportPath, setupTimings, cancellationToken, verbose, sink, skipDiff);
        }

        sink.Write("Pruning old snapshots... ");

        store.DeleteIncompleteSnapshots();
        store.PruneOldSnapshots(keep: 3);

        sink.WriteLine("done.");

        totalSw.Stop();

        sink.WriteLine($"  Total time (full rebuild): {totalSw.ElapsedMilliseconds} ms");
    }

    private static async Task RunFullIndexAsync(IIndexStore store, Solution solution, WorkspaceInfo workspaceInfo, HashSet<string> skipAdapters, string? jsonExportPath, List<SnapshotTimingRow>? setupTimings, CancellationToken cancellationToken, bool verbose, IOutputSink sink, bool skipDiff = false)
    {
        var snapshotId = SnapshotIdentity.Create(workspaceInfo, skipAdapters);
        var snapshotIdStr = snapshotId.ToString();

        // Deterministic identity: an identical complete snapshot for this
        // workspace must not be duplicated. A non-complete row with the same
        // identity (a crashed or failed attempt) must not block a retry.
        var existingStatus = store.GetSnapshotStatus(snapshotIdStr, workspaceInfo.Id.Value);
        if (existingStatus == SnapshotStatusValues.Complete)
        {
            sink.WriteLine($"Identical complete snapshot {snapshotIdStr} already exists for this workspace; no new snapshot written.");
            return;
        }
        if (existingStatus != null)
        {
            sink.WriteLine($"Snapshot {snapshotIdStr} exists with status '{existingStatus}'; removing it and retrying full index.");
            store.DeleteSnapshotData(snapshotIdStr);
        }

        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId, skipAdapters: skipAdapters);
        var timings = setupTimings != null ? new List<SnapshotTimingRow>(setupTimings) : new List<SnapshotTimingRow>();

        // Step: Manifest Save (includes initial FTS build)
        var swManifest = Stopwatch.StartNew();
        sink.Write("Saving snapshot to database... ");

        manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath);

        sink.WriteLine("done.");
        swManifest.Stop();
        timings.Add(new SnapshotTimingRow("manifest_save", swManifest.ElapsedMilliseconds, DateTime.UtcNow));

        // Populate extractor registry (idempotent : no-op on re-runs)
        store.UpsertExtractors(ExtractorRegistry.All);

        try
        {
            int totalDeclarations = 0;
            int totalEdges = 0;
            int totalDiagnostics = 0;
            var allAnnotations = new List<AnnotationRecord>();
            var projectErrors = new List<Exception>();

            // Step: Full Extraction Loop (compilation load + fact extraction + db writes)
            var swExtract = Stopwatch.StartNew();
            var allEdges = new List<EdgeRecord>();
            var blindProjects = new List<string>();
            var extractedProjects = 0;

            await foreach (var (project, compilation) in CompilationHelper.GetAllAsync(solution, cancellationToken))
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
                        snapshotIdStr,
                        WorkspaceLoadGate.DescribeBlindProject(compilation, projectName, workspaceInfo.Id.GitRoot));
                    sink.WriteLine("UNREADABLE: no metadata references resolved; skipped.");
                    continue;
                }

                try
                {
                    IndexTrace.BeginPass("full_index");
                    var options = CompilationFactExtractor.CreateOptions(skipAdapters);
                    var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, snapshotIdStr, projectName, options);
                    result.EnsureRequiredSuccess();

                    store.SaveDeclarations(snapshotIdStr, result.Declarations);
                    totalDeclarations += result.Declarations.Count;

                    allEdges.AddRange(result.Edges);

                    store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
                    totalDiagnostics += result.Diagnostics.Count;
                    store.SaveBindingIncompleteness(snapshotIdStr, result.BindingIncompleteness);
                    allAnnotations.AddRange(result.Annotations);
                    foreach (var measurement in result.Measurements)
                    {
                        if (verbose)
                            sink.WriteErrorLine($"    [measure] {measurement.Extractor}: {measurement.ElapsedMilliseconds} ms, {measurement.AllocatedBytes} bytes");
                    }

                    extractedProjects++;

                    sink.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A failing project is isolated rather than fatal: discarding the
                    // siblings that extracted cleanly would cost the whole solution for
                    // one unreadable project. The failure is recorded against this
                    // project's documents so capsules anchored there report unresolved
                    // rather than empty.
                    sink.WriteErrorLine($"FAILED: {ex.Message}");
                    projectErrors.Add(ex);
                    blindProjects.Add(projectName);
                    try
                    {
                        store.SaveBindingIncompleteness(
                            snapshotIdStr,
                            WorkspaceLoadGate.DescribeBlindProject(compilation, projectName, workspaceInfo.Id.GitRoot));
                    }
                    catch (Exception recordEx)
                    {
                        sink.WriteErrorLine($"WARNING: Failed to record unreadable project '{projectName}': {recordEx.Message}");
                    }
                }
            }

            var dedupedEdges = EdgeDedup.Deduplicate(allEdges);
            store.SaveEdges(snapshotIdStr, dedupedEdges);
            totalEdges = dedupedEdges.Count;
            if (allAnnotations.Count > 0)
                store.SaveAnnotations(snapshotIdStr, allAnnotations);

            // Hard stop only when nothing was readable. There is no lit ground to stand
            // on, so every capsule the snapshot could serve would be an empty graph
            // presented as fact.
            if (extractedProjects == 0 && blindProjects.Count > 0)
            {
                throw new WorkspaceUnreadableException(WorkspaceLoadGate.DescribeRemediation(blindProjects));
            }

            if (projectErrors.Count > 0 || blindProjects.Count > 0)
            {
                sink.WriteErrorLine();
                sink.WriteErrorLine($"WARNING: {blindProjects.Count} of {blindProjects.Count + extractedProjects} project(s) were unreadable and are excluded from the graph:");
                foreach (var name in blindProjects.OrderBy(static name => name, StringComparer.Ordinal))
                    sink.WriteErrorLine($"  - {name}");
                sink.WriteErrorLine("Capsules anchored in those projects report 'unresolved', not 'empty'.");
            }
            swExtract.Stop();
            timings.Add(new SnapshotTimingRow("extraction_loop", swExtract.ElapsedMilliseconds, DateTime.UtcNow));

            var previousManifest = store.LoadLatestSnapshot(manifest.WorkspaceId.Value);

            if (!skipDiff && previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
            {
                // Step: Semantic Diff — skipped when the caller passed --skip-diff.
                cancellationToken.ThrowIfCancellationRequested();
                sink.WriteLine();
                SemanticDiffStep.ComputeAndPersist(
                    store, sink, previousManifest.SnapshotId, snapshotIdStr,
                    changedPaths: null, changedSymbolIds: null, timings);
            }

            // Step: Remove edges targeting symbols not declared in this snapshot
            cancellationToken.ThrowIfCancellationRequested();
            store.DeleteOrphanEdges(snapshotIdStr);

            var totalProjects = extractedProjects + blindProjects.Count;
            var declarationsInSnapshot = store.CountSymbolsInSnapshot(snapshotIdStr);
            var edgesInSnapshot = store.CountEdges(snapshotIdStr);
            var diagnosticsInSnapshot = store.CountDiagnostics(snapshotIdStr);

            sink.WriteLine();
            sink.WriteLine($"Index complete for snapshot {snapshotIdStr}");
            sink.WriteLine($"  projects_reextracted_this_run:       {extractedProjects}/{totalProjects}");
            sink.WriteLine($"  declarations_extracted_this_run:     {totalDeclarations}      declarations_in_snapshot: {declarationsInSnapshot}");
            sink.WriteLine($"  edge_relations_after_dedup_this_run: {totalEdges}      edge_relations_in_snapshot: {edgesInSnapshot}");
            sink.WriteLine($"  diagnostics_extracted_this_run:      {totalDiagnostics}      diagnostics_in_snapshot: {diagnosticsInSnapshot}");
            sink.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");

            // Step: Build FTS search index
            // Preconditions: declarations, document/symbol snapshot membership,
            // semantic diff, and orphan cleanup are all persisted. Full FTS must
            // finish before MarkSnapshotComplete below.
            var swFts = Stopwatch.StartNew();
            sink.Write("Building search index... ");
            store.BuildSearchIndex(snapshotIdStr);
            sink.WriteLine("done.");
            swFts.Stop();
            timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.FtsBuild, swFts.ElapsedMilliseconds, DateTime.UtcNow));

            cancellationToken.ThrowIfCancellationRequested();
            store.MarkSnapshotComplete(snapshotIdStr);

            // Persist all timings
            try { store.SaveTimings(snapshotIdStr, timings); }
            catch (Exception ex) { sink.WriteErrorLine($"WARNING: Failed to save timings: {ex.Message}"); }

            return;
        }
        catch (Exception ex)
        {
            var reasonCode = ex switch
            {
                OperationCanceledException => "cancelled",
                WorkspaceUnreadableException => "workspace_unreadable",
                _ => "full_index_failure",
            };
            try { store.MarkSnapshotFailed(snapshotIdStr, reasonCode, ex.Message); }
            catch { }
            sink.WriteErrorLine($"ERROR: Full index failed, snapshot {snapshotIdStr} marked '{SnapshotStatusValues.Failed}' ({reasonCode}): {ex.Message}");

            // Try to save whatever timings we have
            try { store.SaveTimings(snapshotIdStr, timings); }
            catch { }

            throw;
        }
    }

    private static string ResolveStrategy(IIndexStore store, string? strategyArg, IOutputSink sink)
    {
        if (strategyArg != null)
        {
            var strategy = strategyArg.ToLowerInvariant();

            if (strategy != IncrementalStrategy && strategy != FullStrategy)
            {
                sink.WriteErrorLine("ERROR: --strategy must be 'incremental' or 'full'.");
                Environment.Exit(1);
            }
            return strategy;
        }

        var latestSnapshotId = store.GetLatestSnapshotId();

        if (latestSnapshotId == null)
        {
            sink.WriteLine("No existing snapshot found. Defaulting to --strategy=full for initial index.");
            return FullStrategy;
        }

        return IncrementalStrategy;
    }
}
