using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal sealed class ReverseEdgeClosure(SqliteIndexStore store, string gitRoot)
{
    /// <summary>
    ///     Ratio of a project's document count past which the reverse-edge closure
    ///     stops paying for itself: bookkeeping the narrowed set costs more than the
    ///     extraction it would have saved, so <see cref="FindAffectedDocPaths" />
    ///     abandons the BFS and widens to project scope instead.
    /// </summary>
    private const double DefaultFallbackRatio = 0.6;

    /// <remarks>
    ///     The BFS follows persisted edges, so it can only reach a dependent that
    ///     already had one. A document whose reference did not bind in the previous
    ///     snapshot produced no edge at all, so an edit that makes it bind leaves the
    ///     BFS with no arc to follow (§1.4 scenario 4, and the same shape as scenarios
    ///     3 and 6). Those documents are exactly the ones that recorded binding
    ///     incompleteness, which is persisted separately — seeding the frontier with
    ///     them turns an absence the BFS cannot see into one it can.
    /// </remarks>
    internal DocumentClosureResult FindAffectedDocPaths(Solution solution, string previousSnapshotId, HashSet<string> changedPaths, double fallbackRatio = DefaultFallbackRatio)
    {
        var affectedPaths = new HashSet<string>(PathNormalizer.PathComparer);
        var visitedPaths = new HashSet<string>(changedPaths, PathNormalizer.PathComparer);
        var frontier = new HashSet<string>(changedPaths, PathNormalizer.PathComparer);
        var (pathToProject, projectDocCount) = BuildProjectDocIndex(solution);
        var fellBack = false;

        foreach (var record in store.GetBindingIncompleteness(previousSnapshotId))
        {
            if (record.DocumentPath == null)
                continue;
            if (!BindingIncompletenessReason.UnobservableReasons.Contains(record.Reason))
                continue;
            if (!visitedPaths.Add(record.DocumentPath))
                continue;

            affectedPaths.Add(record.DocumentPath);
            frontier.Add(record.DocumentPath);
        }

        while (frontier.Count > 0)
        {
            var oldDocVersionIds = store.GetDocumentVersionIdsForDocuments(previousSnapshotId, frontier);
            var symbolIds = store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIds);
            var next = new HashSet<string>(PathNormalizer.PathComparer);
            foreach (var symbolId in symbolIds)
                foreach (var edge in store.GetIncomingEdges(previousSnapshotId, symbolId))
                    foreach (var path in ResolveSourceDocumentPaths(previousSnapshotId, edge, visitedPaths))
                    {
                        affectedPaths.Add(path);
                        next.Add(path);
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
        var pathToProject = new Dictionary<string, string>(PathNormalizer.PathComparer);
        var projectDocCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var count = 0;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                pathToProject[GetRelativePath(doc.FilePath, gitRoot)] = project.Name;
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

        var denominator = touchedProjects.Sum(p => projectDocCount.GetValueOrDefault(p));
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

        var widened = new HashSet<string>(affectedPaths, PathNormalizer.PathComparer);
        foreach (var project in solution.Projects)
        {
            if (!touchedProjects.Contains(project.Name))
                continue;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                widened.Add(GetRelativePath(doc.FilePath, gitRoot));
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

        var locs = store.GetDeclarationLocations(edge.SourceSymbolId, previousSnapshotId);
        if (locs.Count > 0)
        {
            foreach (var loc in locs.Where(loc => visitedPaths.Add(loc.DocumentPath)))
                yield return loc.DocumentPath;
            yield break;
        }

        var containingTypeSymbolId = SymbolId.DeriveContainingTypeSymbolId(edge.SourceSymbolId);
        if (containingTypeSymbolId == null)
            yield break;

        var typeLocs = store.GetDeclarationLocations(containingTypeSymbolId, previousSnapshotId);
        foreach (var loc in typeLocs.Where(loc => visitedPaths.Add(loc.DocumentPath)))
            yield return loc.DocumentPath;
    }

    private static string GetRelativePath(string fullPath, string gitRoot)
    {
        return PathNormalizer.ToGitRelative(fullPath, gitRoot);
    }

    /// <param name="AffectedPaths">
    ///     The closure result: a narrowed document set when the BFS ran to
    ///     completion, or every document in every project the closure touched when
    ///     it hit <see cref="FellBackToProjectScope" />.
    /// </param>
    /// <param name="FellBackToProjectScope">
    ///     True when the BFS abandoned itself because the closure exceeded
    ///     <see cref="DefaultFallbackRatio" /> of the documents in the projects it
    ///     had touched so far. Callers that narrow extraction scope on
    ///     <see cref="AffectedPaths" /> must skip that narrowing in this case — the
    ///     widened set already is project scope, so extraction and deletion stay in
    ///     lockstep with the pre-narrowing behavior instead of drifting apart.
    /// </param>
    internal readonly record struct DocumentClosureResult(HashSet<string> AffectedPaths, bool FellBackToProjectScope);
}
