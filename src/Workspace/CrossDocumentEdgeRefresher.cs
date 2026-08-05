using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal sealed class CrossDocumentEdgeRefresher(IIndexStore store, string gitRoot, HashSet<string> skipAdapters)
{
    private readonly IIndexStore _store = store;
    private readonly string _gitRoot = gitRoot;
    private readonly HashSet<string> _skipAdapters = skipAdapters;

    internal async Task<int> RefreshAsync(Solution solution, WorkspaceInfo workspaceInfo, string newSnapshotId, string previousSnapshotId, HashSet<string> changedPaths, CancellationToken cancellationToken)
    {
        var affectedPaths = FindAffectedDocPaths(previousSnapshotId, changedPaths);
        if (affectedPaths.Count == 0)
            return 0;

        var affectedProjectNames = ResolveProjectNames(solution, affectedPaths);
        if (affectedProjectNames.Count == 0)
            return 0;

        return await ProcessCompilationsAsync(solution, workspaceInfo, newSnapshotId, affectedProjectNames, affectedPaths, cancellationToken);
    }

    internal HashSet<string> FindAffectedDocPaths(string previousSnapshotId, HashSet<string> changedPaths)
    {
        var affectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedPaths = new HashSet<string>(changedPaths, StringComparer.OrdinalIgnoreCase);
        var frontier = new HashSet<string>(changedPaths, StringComparer.OrdinalIgnoreCase);
        while (frontier.Count > 0)
        {
            var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, frontier);
            var symbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIds);
            var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbolId in symbolIds)
            {
                foreach (var edge in _store.GetIncomingEdges(previousSnapshotId, symbolId))
                {
                    if (edge.SourceDocumentPath != null && visitedPaths.Add(edge.SourceDocumentPath))
                    {
                        affectedPaths.Add(edge.SourceDocumentPath);
                        next.Add(edge.SourceDocumentPath);
                    }
                }
            }
            frontier = next;
        }
        return affectedPaths;
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
        var rootDir = Path.GetFullPath(_gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var relPath in affectedDocPaths)
            affectedAbsPaths.Add(Path.GetFullPath(Path.Combine(rootDir, relPath)).Replace('\\', '/'));

        var perProjectAffectedPaths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!affectedProjectNames.Contains(project.Name))
                continue;
            var projectAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                var normalized = doc.FilePath.Replace('\\', '/');
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
            if (scopeDocs != null)
            {
                // Binding-incompleteness rows are persisted with paths relative
                // to the git root (see BindingIncompletenessCollector.Record),
                // while scopeDocs carries absolute document paths. Deleting with
                // the absolute form is a silent no-op, leaving stale rows for
                // reasons that no longer occur. Convert before deleting.
                var scopeRelativePaths = scopeDocs
                    .Select(path => GetRelativePath(path, _gitRoot))
                    .ToList();
                _store.DeleteBindingIncompletenessByDocumentPaths(newSnapshotId, scopeRelativePaths);
            }
            _store.SaveEdges(newSnapshotId, result.Edges);
            _store.SaveDeclarations(newSnapshotId, result.Declarations);
            _store.DeleteDiagnosticsByProjectNames(newSnapshotId, [project.Name]);
            _store.SaveDiagnostics(newSnapshotId, result.Diagnostics);
            _store.SaveBindingIncompleteness(newSnapshotId, result.BindingIncompleteness);
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
    {
        var normalizedRoot = Path.GetFullPath(gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = normalizedRoot + Path.DirectorySeparatorChar;
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
