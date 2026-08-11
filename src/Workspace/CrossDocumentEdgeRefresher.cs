using Lurp.Storage;
using Lurp.Shared;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal sealed class CrossDocumentEdgeRefresher(IIndexStore store, string gitRoot, HashSet<string> skipAdapters)
{
    private readonly IIndexStore _store = store;
    private readonly string _gitRoot = gitRoot;
    private readonly HashSet<string> _skipAdapters = skipAdapters;

    /// <summary>
    /// Ratio of a project's document count past which the reverse-edge closure
    /// stops paying for itself: bookkeeping the narrowed set costs more than the
    /// extraction it would have saved, so <see cref="FindAffectedDocPaths"/>
    /// abandons the BFS and widens to project scope instead.
    /// </summary>
    private const double DefaultFallbackRatio = 0.6;

    /// <param name="AffectedPaths">
    /// The closure result: a narrowed document set when the BFS ran to
    /// completion, or every document in every project the closure touched when
    /// it hit <see cref="FellBackToProjectScope"/>.
    /// </param>
    /// <param name="FellBackToProjectScope">
    /// True when the BFS abandoned itself because the closure exceeded
    /// <see cref="DefaultFallbackRatio"/> of the documents in the projects it
    /// had touched so far. Callers that narrow extraction scope on
    /// <see cref="AffectedPaths"/> must skip that narrowing in this case — the
    /// widened set already is project scope, so extraction and deletion stay in
    /// lockstep with the pre-narrowing behavior instead of drifting apart.
    /// </param>
    internal readonly record struct DocumentClosureResult(HashSet<string> AffectedPaths, bool FellBackToProjectScope);

    /// <param name="changedPaths">
    /// Git-root-relative paths of the genuinely-changed documents — the BFS seed.
    /// Passing a set that already absorbed the closure makes this a no-op, because
    /// <see cref="FindAffectedDocPaths"/> seeds its visited set with its own input.
    /// </param>
    /// <param name="alreadyExtractedPaths">
    /// Git-root-relative paths the caller has already re-extracted this run.
    /// Subtracted from the closure so this refresh handles exactly the residue,
    /// never re-deleting and re-extracting a document twice in one snapshot.
    /// </param>
    internal async Task<int> RefreshAsync(Solution solution, WorkspaceInfo workspaceInfo, string newSnapshotId, string previousSnapshotId, HashSet<string> changedPaths, IReadOnlySet<string>? alreadyExtractedPaths, CancellationToken cancellationToken)
    {
        var affectedPaths = FindAffectedDocPaths(solution, previousSnapshotId, changedPaths).AffectedPaths;
        if (alreadyExtractedPaths is { Count: > 0 })
            affectedPaths.ExceptWith(alreadyExtractedPaths);
        if (affectedPaths.Count == 0)
            return 0;

        var affectedProjectNames = ResolveProjectNames(solution, affectedPaths);
        if (affectedProjectNames.Count == 0)
            return 0;

        return await ProcessCompilationsAsync(solution, workspaceInfo, newSnapshotId, affectedProjectNames, affectedPaths, cancellationToken);
    }

    /// <param name="affectedPaths">
    /// Pre-computed document closure (e.g. from a prior BFS pass), already
    /// scoped to the documents that need cross-document edge re-extraction.
    /// The caller is responsible for subtracting any paths that were already
    /// re-extracted this snapshot.
    /// </param>
    internal async Task<int> RefreshWithAffectedPathsAsync(Solution solution, WorkspaceInfo workspaceInfo, string newSnapshotId, HashSet<string> affectedPaths, CancellationToken cancellationToken)
    {
        if (affectedPaths.Count == 0)
            return 0;

        var affectedProjectNames = ResolveProjectNames(solution, affectedPaths);
        if (affectedProjectNames.Count == 0)
            return 0;

        return await ProcessCompilationsAsync(solution, workspaceInfo, newSnapshotId, affectedProjectNames, affectedPaths, cancellationToken);
    }

    /// <remarks>
    /// The BFS follows persisted edges, so it can only reach a dependent that
    /// already had one. A document whose reference did not bind in the previous
    /// snapshot produced no edge at all, so an edit that makes it bind leaves the
    /// BFS with no arc to follow (§1.4 scenario 4, and the same shape as scenarios
    /// 3 and 6). Those documents are exactly the ones that recorded binding
    /// incompleteness, which is persisted separately — seeding the frontier with
    /// them turns an absence the BFS cannot see into one it can.
    /// </remarks>
    internal DocumentClosureResult FindAffectedDocPaths(Solution solution, string previousSnapshotId, HashSet<string> changedPaths, double fallbackRatio = DefaultFallbackRatio)
    {
        var affectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedPaths = new HashSet<string>(changedPaths, StringComparer.OrdinalIgnoreCase);
        var frontier = new HashSet<string>(changedPaths, StringComparer.OrdinalIgnoreCase);
        var (pathToProject, projectDocCount) = BuildProjectDocIndex(solution);
        var fellBack = false;

        foreach (var record in _store.GetBindingIncompleteness(previousSnapshotId))
        {
            if (record.DocumentPath == null)
                continue;
            if (!BindingIncompletenessReason.UnobservableReasons.Contains(record.Reason))
                continue;
            if (visitedPaths.Add(record.DocumentPath))
            {
                affectedPaths.Add(record.DocumentPath);
                frontier.Add(record.DocumentPath);
            }
        }
        while (frontier.Count > 0)
        {
            var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, frontier);
            var symbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIds);
            var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbolId in symbolIds)
            {
                foreach (var edge in _store.GetIncomingEdges(previousSnapshotId, symbolId))
                {
                    foreach (var path in ResolveSourceDocumentPaths(previousSnapshotId, edge, visitedPaths))
                    {
                        affectedPaths.Add(path);
                        next.Add(path);
                    }
                }
            }
            frontier = next;

            // A cycle in the call graph drags its whole strongly-connected
            // component into the closure with no natural stopping point. Once the
            // closure has outgrown the projects it has touched so far, document
            // scope costs more in bookkeeping than it would save in extraction —
            // stop the BFS here rather than let a cycle walk the entire solution.
            if (ExceedsFallbackRatio(affectedPaths, changedPaths, pathToProject, projectDocCount, fallbackRatio))
            {
                fellBack = true;
                break;
            }
        }

        if (fellBack)
            affectedPaths = WidenToProjectScope(affectedPaths, changedPaths, solution, pathToProject);

        return new DocumentClosureResult(affectedPaths, fellBack);
    }

    private (Dictionary<string, string> PathToProject, Dictionary<string, int> ProjectDocCount) BuildProjectDocIndex(Solution solution)
    {
        var pathToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectDocCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var count = 0;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                pathToProject[GetRelativePath(doc.FilePath, _gitRoot)] = project.Name;
                count++;
            }
            projectDocCount[project.Name] = count;
        }
        return (pathToProject, projectDocCount);
    }

    private static bool ExceedsFallbackRatio(HashSet<string> affectedPaths, HashSet<string> changedPaths, Dictionary<string, string> pathToProject, Dictionary<string, int> projectDocCount, double fallbackRatio)
    {
        var touchedProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in affectedPaths)
            if (pathToProject.TryGetValue(path, out var project))
                touchedProjects.Add(project);
        foreach (var path in changedPaths)
            if (pathToProject.TryGetValue(path, out var project))
                touchedProjects.Add(project);

        if (touchedProjects.Count == 0)
            return false;

        var denominator = touchedProjects.Sum(p => projectDocCount.TryGetValue(p, out var c) ? c : 0);
        if (denominator == 0)
            return false;

        return affectedPaths.Count > fallbackRatio * denominator;
    }

    private HashSet<string> WidenToProjectScope(HashSet<string> affectedPaths, HashSet<string> changedPaths, Solution solution, Dictionary<string, string> pathToProject)
    {
        var touchedProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in affectedPaths)
            if (pathToProject.TryGetValue(path, out var project))
                touchedProjects.Add(project);
        foreach (var path in changedPaths)
            if (pathToProject.TryGetValue(path, out var project))
                touchedProjects.Add(project);

        var widened = new HashSet<string>(affectedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (!touchedProjects.Contains(project.Name))
                continue;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                widened.Add(GetRelativePath(doc.FilePath, _gitRoot));
            }
        }
        return widened;
    }

    private IEnumerable<string> ResolveSourceDocumentPaths(string previousSnapshotId, EdgeRecord edge, HashSet<string> visitedPaths)
    {
        if (edge.SourceDocumentPath != null)
        {
            if (visitedPaths.Add(edge.SourceDocumentPath))
                yield return edge.SourceDocumentPath;
            yield break;
        }

        if (string.IsNullOrEmpty(edge.SourceSymbolId))
            yield break;

        var locs = _store.GetDeclarationLocations(edge.SourceSymbolId, previousSnapshotId);
        if (locs.Count > 0)
        {
            foreach (var loc in locs)
            {
                if (visitedPaths.Add(loc.DocumentPath))
                    yield return loc.DocumentPath;
            }
            yield break;
        }

        var containingTypeSymbolId = DeriveContainingTypeSymbolId(edge.SourceSymbolId);
        if (containingTypeSymbolId == null)
            yield break;

        var typeLocs = _store.GetDeclarationLocations(containingTypeSymbolId, previousSnapshotId);
        foreach (var loc in typeLocs)
        {
            if (visitedPaths.Add(loc.DocumentPath))
                yield return loc.DocumentPath;
        }
    }

    private static string? DeriveContainingTypeSymbolId(string sourceSymbolId)
    {
        var pipeIndex = sourceSymbolId.IndexOf('|');
        if (pipeIndex < 0)
            return null;

        var docCommentId = sourceSymbolId[..pipeIndex];
        var assemblyIdentity = sourceSymbolId[(pipeIndex + 1)..];

        var typeDocCommentId = SymbolId.DeriveContainingTypeDocCommentId(docCommentId);
        if (typeDocCommentId == null)
            return null;

        return $"{typeDocCommentId}|{assemblyIdentity}";
    }

    private HashSet<string> ResolveProjectNames(Solution solution, HashSet<string> affectedPaths)
    {
        Console.WriteLine($"  ({affectedPaths.Count} documents need cross-document edge refresh)");

        var affectedProjectNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                var relPath = GetRelativePath(doc.FilePath, _gitRoot);
                if (affectedPaths.Contains(relPath))
                {
                    affectedProjectNames.Add(project.Name);
                    break;
                }
            }
        }

        return affectedProjectNames;
    }

    private async Task<int> ProcessCompilationsAsync(Solution solution, WorkspaceInfo workspaceInfo, string newSnapshotId, HashSet<string> affectedProjectNames, HashSet<string> affectedDocPaths, CancellationToken cancellationToken)
    {
        // Compute per-project affected absolute paths for scoped re-extraction
        var affectedAbsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootDir = PathNormalizer.NormalizeRoot(_gitRoot) + Path.DirectorySeparatorChar;
        foreach (var relPath in affectedDocPaths)
            affectedAbsPaths.Add(PathNormalizer.ToForwardSlash(Path.GetFullPath(Path.Combine(rootDir, relPath))));

        var perProjectAffectedPaths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!affectedProjectNames.Contains(project.Name))
                continue;
            var projectAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                var normalized = PathNormalizer.ToForwardSlash(doc.FilePath);
                if (affectedAbsPaths.Contains(normalized))
                    projectAffected.Add(normalized);
            }
            perProjectAffectedPaths[project.Name] = projectAffected;
        }

        // Only delete edges for the affected documents within each project,
        // not the entire project's documents.
        var allAffectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPaths in perProjectAffectedPaths.Values)
        {
            foreach (var path in projectPaths)
                allAffectedPaths.Add(GetRelativePath(path, _gitRoot));
        }
        _store.DeleteEdgesByDocumentPaths(newSnapshotId, allAffectedPaths);
        _store.DeleteAnnotationsByDocumentPaths(newSnapshotId, allAffectedPaths);

        var crossDocCompilations = await LoadCompilationsAsync(solution, affectedProjectNames, cancellationToken);
        var crossDocAssemblyIdentities = crossDocCompilations.Values
            .Select(c => c.Assembly.Identity.GetDisplayName()).ToList();
        _store.DeleteEdgesWithNullDocumentPathForAssemblies(newSnapshotId, crossDocAssemblyIdentities);

        int totalEdges = 0;
        foreach (var project in solution.Projects)
        {
            if (!affectedProjectNames.Contains(project.Name))
                continue;
            if (!crossDocCompilations.TryGetValue(project.Name, out var compilation))
                continue;

            // Scope re-extraction to only the documents that have incoming
            // edges from changed symbols, rather than re-extracting the
            // entire compilation.
            IReadOnlySet<string>? scopeDocs = null;
            if (perProjectAffectedPaths.TryGetValue(project.Name, out var projectAffected) && projectAffected.Count > 0)
                scopeDocs = projectAffected;

            var options = CompilationFactExtractor.CreateOptions(_skipAdapters, scopeDocs);
            cancellationToken.ThrowIfCancellationRequested();
            var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, newSnapshotId, project.Name, options);
            result.EnsureRequiredSuccess();

            // Binding-incompleteness rows are persisted with paths relative
            // to the git root (see BindingIncompletenessCollector.Record),
            // while scopeDocs carries absolute document paths. Convert once
            // so both the delete and the scoped save share the same set.
            List<string>? scopeRelativePaths = scopeDocs?
                .Select(path => GetRelativePath(path, _gitRoot))
                .ToList();

            if (scopeRelativePaths != null)
                _store.DeleteBindingIncompletenessByDocumentPaths(newSnapshotId, scopeRelativePaths);

            _store.SaveEdges(newSnapshotId, result.Edges);
            _store.SaveDeclarations(newSnapshotId, result.Declarations);
            _store.DeleteDiagnosticsByProjectNames(newSnapshotId, [project.Name]);
            _store.SaveDiagnostics(newSnapshotId, result.Diagnostics);

            // Lockstep invariant: save binding-incompleteness only for documents
            // in the deletion scope. Out-of-scope documents — and null/empty-path
            // doc-less aggregates, which no scoped delete retires — carry forward
            // their copied-forward values unchanged (EXCLUDE-AND-CARRY-FORWARD;
            // see IncrementalIndexer.ScopeBindingIncompleteness and TRUST_KERNEL R6).
            if (scopeRelativePaths != null)
            {
                var scopedSet = new HashSet<string>(scopeRelativePaths, StringComparer.Ordinal);
                var scopedBi = result.BindingIncompleteness
                    .Where(r => r.DocumentPath is { } path && scopedSet.Contains(path))
                    .ToList();
                _store.SaveBindingIncompleteness(newSnapshotId, scopedBi);
            }
            else
            {
                _store.SaveBindingIncompleteness(newSnapshotId, result.BindingIncompleteness);
            }
            if (result.Annotations.Count > 0)
                _store.SaveAnnotations(newSnapshotId, result.Annotations);
            totalEdges += result.Edges.Count;
            Console.Write($"  [cross-doc {project.Name}] {result.Edges.Count} edges. ");
        }
        return totalEdges;
    }

    private static async Task<Dictionary<string, Compilation>> LoadCompilationsAsync(Solution solution, HashSet<string> projectNames, CancellationToken cancellationToken)
    {
        var compilations = new Dictionary<string, Compilation>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!projectNames.Contains(project.Name))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
                throw new InvalidOperationException($"Compilation loader: GetCompilationAsync returned null for project '{project.Name}' during cross-document edge refresh.");
            compilations[project.Name] = compilation;
        }
        return compilations;
    }

    private static string GetRelativePath(string fullPath, string gitRoot)
        => PathNormalizer.ToGitRelative(fullPath, gitRoot);
}
