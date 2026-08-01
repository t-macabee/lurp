using Lurp.Storage;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

namespace Lurp.Workspace;

public static class IndexRunner
{
    private const string FullStrategy = "full";
    private const string IncrementalStrategy = "incremental";
    public static async Task RunAsync(IIndexStore store, string solutionPath, string outputDir, HashSet<string> skipAdapters, string? jsonExportPath, string? strategyArg, CancellationToken cancellationToken = default, bool verbose = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!MSBuildLocator.IsRegistered)
        {
            var instances = MSBuildLocator.RegisterDefaults();

            Console.WriteLine($"MSBuild: {instances?.MSBuildPath ?? "default"}");
        }

        string strategy = ResolveStrategy(store, strategyArg);

        Console.WriteLine($"Strategy: {strategy}");

        if (strategy == FullStrategy)
        {
            Console.WriteLine("  (Use --strategy=full to force a full rebuild when something looks wrong.)");
        }

        var totalSw = Stopwatch.StartNew();

        var swSolutionLoad = Stopwatch.StartNew();
        Console.Write("Loading solution... ");

        using var workspace = MSBuildWorkspace.Create();

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);

        Console.WriteLine($"done ({solution.Projects.Count()} projects).");
        swSolutionLoad.Stop();

        // Restore compiler fidelity: MSBuildWorkspace silently falls back to
        // C# 7.3 parse options when a project fails to evaluate. Derive each
        // affected project's effective language version from its own inputs
        // (explicit LangVersion, or the SDK-style default) so modern C# binds.
        solution = LanguageVersionRecovery.Apply(solution);

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
                    Console.WriteLine($"  Changed documents: {result.ChangedDocumentCount}");
                    Console.WriteLine($"  Declarations:      {result.DeclarationsExtracted}");
                    Console.WriteLine($"  Edges:             {result.EdgesExtracted}");
                    Console.WriteLine($"  Diagnostics:       {result.DiagnosticsExtracted}");
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
                new SnapshotTimingRow("solution_load", swSolutionLoad.ElapsedMilliseconds, DateTime.UtcNow),
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
        var snapshotId = SnapshotId.New();
        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId, skipAdapters: skipAdapters);
        var snapshotIdStr = snapshotId.ToString();
        var timings = setupTimings != null ? new List<SnapshotTimingRow>(setupTimings) : new List<SnapshotTimingRow>();

        // Step: Manifest Save (includes initial FTS build)
        var swManifest = Stopwatch.StartNew();
        Console.Write("Saving snapshot to database... ");

        manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath);

        Console.WriteLine("done.");
        swManifest.Stop();
        timings.Add(new SnapshotTimingRow("manifest_save", swManifest.ElapsedMilliseconds, DateTime.UtcNow));

        // Populate extractor registry (idempotent — no-op on re-runs)
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

            await foreach (var (project, compilation) in CompilationHelper.GetAllAsync(solution, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projectName = project.Name;

                Console.Write($"  [{projectName}] ");

                try
                {
                    var options = new CompilationFactExtractor.ExtractionOptions(skipAdapters, LogWarning: msg => Console.Error.WriteLine($"WARNING: {msg}"), LogError: msg => Console.Error.WriteLine($"ERROR: {msg}"));
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

                    Console.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FAILED: {ex.Message}");
                    projectErrors.Add(ex);
                }
            }

            var dedupedEdges = EdgeDedup.Deduplicate(allEdges);
            store.SaveEdges(snapshotIdStr, dedupedEdges);
            totalEdges = dedupedEdges.Count;

            if (projectErrors.Count > 0)
            {
                throw new AggregateException(
                    "One or more projects failed during full index.",
                    projectErrors);
            }
            swExtract.Stop();
            timings.Add(new SnapshotTimingRow("extraction_loop", swExtract.ElapsedMilliseconds, DateTime.UtcNow));

            Console.WriteLine();
            Console.WriteLine($"Index complete for snapshot {snapshotIdStr}");
            Console.WriteLine($"  Declarations: {totalDeclarations}");
            Console.WriteLine($"  Edges:        {totalEdges}");
            Console.WriteLine($"  Diagnostics:  {totalDiagnostics}");
            Console.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");

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
        }
        catch (Exception ex)
        {
            var reasonCode = ex is OperationCanceledException ? "cancelled" : "full_index_failure";
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
