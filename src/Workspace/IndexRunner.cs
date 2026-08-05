using Lurp.Storage;
using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Lurp.Workspace;

public static class IndexRunner
{
    private const string FullStrategy = "full";
    private const string IncrementalStrategy = "incremental";
    public static async Task RunAsync(IIndexStore store, string solutionPath, string outputDir, HashSet<string> skipAdapters, string? jsonExportPath, string? strategyArg, CancellationToken cancellationToken = default, bool verbose = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var loader = new WorkspaceLoader();

        string strategy = ResolveStrategy(store, strategyArg);

        Console.WriteLine($"Strategy: {strategy}");

        if (strategy == FullStrategy)
        {
            Console.WriteLine("  (Use --strategy=full to force a full rebuild when something looks wrong.)");
        }

        var totalSw = Stopwatch.StartNew();

        var loaded = await loader.LoadAsync(solutionPath, cancellationToken);
        var solution = loaded.Solution;

        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        var swWorkspaceInfo = Stopwatch.StartNew();
        Console.Write("Building workspace info... ");

        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        Console.WriteLine("done.");
        swWorkspaceInfo.Stop();

        if (strategy == IncrementalStrategy)
        {
            var previousStorageManifest = store.LoadLatestSnapshot(workspaceInfo.Id.Value);

            if (previousStorageManifest == null)
            {
                Console.WriteLine("No previous snapshot found. Falling back to full index.");
                strategy = FullStrategy;
            }
            else
            {
                try
                {
                    var incrementalIndexer = new IncrementalIndexer(store, gitRoot, skipAdapters, jsonExportPath, verbose);
                    var result = await incrementalIndexer.RunIncrementalAsync(solution, workspaceInfo, previousStorageManifest, cancellationToken);

                    Console.WriteLine();
                    Console.WriteLine($"Incremental index complete. Snapshot: {result.NewSnapshotId}");
                    Console.WriteLine($"  Previous snapshot: {result.PreviousSnapshotId}");
                    Console.WriteLine($"  documents_changed_this_run:          {result.ChangedDocumentCount}");
                    Console.WriteLine($"  declarations_extracted_this_run:     {result.DeclarationsExtracted}      declarations_in_snapshot: {store.CountSymbolsInSnapshot(result.NewSnapshotId)}");
                    Console.WriteLine($"  edge_relations_after_dedup_this_run: {result.EdgesExtracted}      edge_relations_in_snapshot: {store.CountEdges(result.NewSnapshotId)}");
                    Console.WriteLine($"  diagnostics_extracted_this_run:      {result.DiagnosticsExtracted}      diagnostics_in_snapshot: {store.CountDiagnostics(result.NewSnapshotId)}");
                    Console.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");
                    Console.Write("Pruning old snapshots... ");

                    store.DeleteIncompleteSnapshots();
                    store.PruneOldSnapshots(keep: 3);

                    Console.WriteLine("done.");

                    totalSw.Stop();

                    Console.WriteLine($"  Total time (incremental): {totalSw.ElapsedMilliseconds} ms");
                    return;
                }
                catch (FullRebuildRequiredException ex)
                {
                    Console.WriteLine($"Full rebuild required: {ex.Message}");
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
            await RunFullIndexAsync(store, solution, workspaceInfo, skipAdapters, jsonExportPath, setupTimings, cancellationToken, verbose);
        }

        Console.Write("Pruning old snapshots... ");

        store.DeleteIncompleteSnapshots();
        store.PruneOldSnapshots(keep: 3);

        Console.WriteLine("done.");

        totalSw.Stop();

        Console.WriteLine($"  Total time (full rebuild): {totalSw.ElapsedMilliseconds} ms");
    }

    private static async Task RunFullIndexAsync(IIndexStore store, Solution solution, WorkspaceInfo workspaceInfo, HashSet<string> skipAdapters, string? jsonExportPath, List<SnapshotTimingRow>? setupTimings, CancellationToken cancellationToken, bool verbose)
    {
        var snapshotId = SnapshotIdentity.Create(workspaceInfo, skipAdapters);
        var snapshotIdStr = snapshotId.ToString();

        // Deterministic identity: an identical complete snapshot for this
        // workspace must not be duplicated. A non-complete row with the same
        // identity (a crashed or failed attempt) must not block a retry.
        var existingStatus = store.GetSnapshotStatus(snapshotIdStr, workspaceInfo.Id.Value);
        if (existingStatus == SnapshotStatusValues.Complete)
        {
            Console.WriteLine($"Identical complete snapshot {snapshotIdStr} already exists for this workspace; no new snapshot written.");
            return;
        }
        if (existingStatus != null)
        {
            Console.WriteLine($"Snapshot {snapshotIdStr} exists with status '{existingStatus}'; removing it and retrying full index.");
            store.DeleteSnapshotData(snapshotIdStr);
        }

        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId, skipAdapters: skipAdapters);
        var timings = setupTimings != null ? new List<SnapshotTimingRow>(setupTimings) : new List<SnapshotTimingRow>();

        // Step: Manifest Save (includes initial FTS build)
        var swManifest = Stopwatch.StartNew();
        Console.Write("Saving snapshot to database... ");

        manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath);

        Console.WriteLine("done.");
        swManifest.Stop();
        timings.Add(new SnapshotTimingRow("manifest_save", swManifest.ElapsedMilliseconds, DateTime.UtcNow));

        // Populate extractor registry (idempotent : no-op on re-runs)
        store.UpsertExtractors(ExtractorRegistry.All);

        try
        {
            int totalDeclarations = 0;
            int totalEdges = 0;
            int totalDiagnostics = 0;
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

                Console.Write($"  [{projectName}] ");

                // A blind compilation yields declarations with almost no edges. Indexing
                // it would publish that emptiness as a proved absence of callers, so the
                // project is recorded as unreadable and skipped instead.
                if (WorkspaceLoadGate.Classify(compilation) == CompilationReadability.Blind)
                {
                    blindProjects.Add(projectName);
                    store.SaveBindingIncompleteness(
                        snapshotIdStr,
                        WorkspaceLoadGate.DescribeBlindProject(compilation, projectName, workspaceInfo.Id.GitRoot));
                    Console.WriteLine("UNREADABLE: no metadata references resolved; skipped.");
                    continue;
                }

                try
                {
                    var options = CompilationFactExtractor.CreateOptions(skipAdapters);
                    var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, snapshotIdStr, projectName, options);
                    result.EnsureRequiredSuccess();

                    store.SaveDeclarations(snapshotIdStr, result.Declarations);
                    totalDeclarations += result.Declarations.Count;

                    allEdges.AddRange(result.Edges);

                    store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
                    totalDiagnostics += result.Diagnostics.Count;
                    store.SaveBindingIncompleteness(snapshotIdStr, result.BindingIncompleteness);
                    foreach (var measurement in result.Measurements)
                    {
                        if (verbose)
                            Console.Error.WriteLine($"    [measure] {measurement.Extractor}: {measurement.ElapsedMilliseconds} ms, {measurement.AllocatedBytes} bytes");
                    }

                    extractedProjects++;

                    Console.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
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
                    Console.Error.WriteLine($"FAILED: {ex.Message}");
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
                        Console.Error.WriteLine($"WARNING: Failed to record unreadable project '{projectName}': {recordEx.Message}");
                    }
                }
            }

            var dedupedEdges = EdgeDedup.Deduplicate(allEdges);
            store.SaveEdges(snapshotIdStr, dedupedEdges);
            totalEdges = dedupedEdges.Count;

            // Hard stop only when nothing was readable. There is no lit ground to stand
            // on, so every capsule the snapshot could serve would be an empty graph
            // presented as fact.
            if (extractedProjects == 0 && blindProjects.Count > 0)
            {
                throw new WorkspaceUnreadableException(WorkspaceLoadGate.DescribeRemediation(blindProjects));
            }

            if (projectErrors.Count > 0 || blindProjects.Count > 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"WARNING: {blindProjects.Count} of {blindProjects.Count + extractedProjects} project(s) were unreadable and are excluded from the graph:");
                foreach (var name in blindProjects.OrderBy(static name => name, StringComparer.Ordinal))
                    Console.Error.WriteLine($"  - {name}");
                Console.Error.WriteLine("Capsules anchored in those projects report 'unresolved', not 'empty'.");
            }
            swExtract.Stop();
            timings.Add(new SnapshotTimingRow("extraction_loop", swExtract.ElapsedMilliseconds, DateTime.UtcNow));

            var previousManifest = store.LoadLatestSnapshot(manifest.WorkspaceId.Value);

            if (previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
            {
                // Step: Semantic Diff
                cancellationToken.ThrowIfCancellationRequested();
                var swDiff = Stopwatch.StartNew();
                Console.WriteLine();
                Console.Write("Computing semantic diff from previous snapshot... ");

                var differ = new SemanticDiffer(store, store, store);
                var (diffChanges, skippedComparisons) = differ.ComputeDiff(previousManifest.SnapshotId, snapshotIdStr);

                store.SaveSemanticChanges(previousManifest.SnapshotId, snapshotIdStr, diffChanges);

                Console.WriteLine($"done ({diffChanges.Count} changes, {skippedComparisons} comparisons skipped).");
                swDiff.Stop();
                timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.SemanticDiff, swDiff.ElapsedMilliseconds, DateTime.UtcNow));
            }

            // Step: Remove edges targeting symbols not declared in this snapshot
            cancellationToken.ThrowIfCancellationRequested();
            store.DeleteOrphanEdges(snapshotIdStr);

            var totalProjects = extractedProjects + blindProjects.Count;
            var declarationsInSnapshot = store.CountSymbolsInSnapshot(snapshotIdStr);
            var edgesInSnapshot = store.CountEdges(snapshotIdStr);
            var diagnosticsInSnapshot = store.CountDiagnostics(snapshotIdStr);

            Console.WriteLine();
            Console.WriteLine($"Index complete for snapshot {snapshotIdStr}");
            Console.WriteLine($"  projects_reextracted_this_run:       {extractedProjects}/{totalProjects}");
            Console.WriteLine($"  declarations_extracted_this_run:     {totalDeclarations}      declarations_in_snapshot: {declarationsInSnapshot}");
            Console.WriteLine($"  edge_relations_after_dedup_this_run: {totalEdges}      edge_relations_in_snapshot: {edgesInSnapshot}");
            Console.WriteLine($"  diagnostics_extracted_this_run:      {totalDiagnostics}      diagnostics_in_snapshot: {diagnosticsInSnapshot}");
            Console.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");

            // Step: Build FTS search index
            // Preconditions: declarations, document/symbol snapshot membership,
            // semantic diff, and orphan cleanup are all persisted. Full FTS must
            // finish before MarkSnapshotComplete below.
            var swFts = Stopwatch.StartNew();
            Console.Write("Building search index... ");
            store.BuildSearchIndex(snapshotIdStr);
            Console.WriteLine("done.");
            swFts.Stop();
            timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.FtsBuild, swFts.ElapsedMilliseconds, DateTime.UtcNow));

            cancellationToken.ThrowIfCancellationRequested();
            store.MarkSnapshotComplete(snapshotIdStr);

            // Persist all timings
            try { store.SaveTimings(snapshotIdStr, timings); }
            catch (Exception ex) { Console.Error.WriteLine($"WARNING: Failed to save timings: {ex.Message}"); }

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
            Console.Error.WriteLine($"ERROR: Full index failed, snapshot {snapshotIdStr} marked '{SnapshotStatusValues.Failed}' ({reasonCode}): {ex.Message}");

            // Try to save whatever timings we have
            try { store.SaveTimings(snapshotIdStr, timings); }
            catch { }

            throw;
        }
    }

    private static string ResolveStrategy(IIndexStore store, string? strategyArg)
    {
        if (strategyArg != null)
        {
            var strategy = strategyArg.ToLowerInvariant();

            if (strategy != IncrementalStrategy && strategy != FullStrategy)
            {
                Console.Error.WriteLine("ERROR: --strategy must be 'incremental' or 'full'.");
                Environment.Exit(1);
            }
            return strategy;
        }

        var latestSnapshotId = store.GetLatestSnapshotId();

        if (latestSnapshotId == null)
        {
            Console.WriteLine("No existing snapshot found. Defaulting to --strategy=full for initial index.");
            return FullStrategy;
        }

        return IncrementalStrategy;
    }
}
