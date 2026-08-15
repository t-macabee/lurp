using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class DocumentChangeDetector(string gitRoot, IOutputSink output)
{
    private readonly string _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
    private readonly IOutputSink _output = output ?? ConsoleOutputSink.Instance;

    public sealed record DocumentChangeInfo(string RelativePath, DocumentChangeKind ChangeKind, string? OldDocumentVersionId = null);

    public enum DocumentChangeKind
    {
        Unchanged,
        Changed,
        New,
        Deleted
    }

    public (List<DocumentChangeInfo> ChangedDocs, HashSet<string> ChangedPaths) DetectAndLogChanges(
        WorkspaceInfo workspaceInfo, SnapshotManifest previousRichManifest)
    {
        _output.Write("Hashing documents and detecting changes... ");
        var docChanges = DetectChanges(workspaceInfo, previousRichManifest);
        var changedDocs = docChanges.Where(c => c.ChangeKind != DocumentChangeKind.Unchanged).ToList();
        var changedPaths = changedDocs.Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"done ({changedDocs.Count} changed, {docChanges.Count - changedDocs.Count} unchanged).");

        if (changedDocs.Count == 0)
        {
            _output.WriteLine("No changes detected. Skipping incremental index.");
        }
        else
        {
            foreach (var change in changedDocs)
                _output.WriteLine($"  {change.ChangeKind}: {change.RelativePath}");
        }

        return (changedDocs, changedPaths);
    }

    public HashSet<string> IdentifyAffectedProjects(Solution solution, HashSet<string> changedPaths)
    {
        var affectedIds = new HashSet<ProjectId>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null)
                    continue;

                var relPath = PathNormalizer.ToGitRelative(document.FilePath, _gitRoot);

                if (changedPaths.Contains(relPath))
                {
                    affectedIds.Add(project.Id);
                    break;
                }
            }

            if (affectedIds.Contains(project.Id) || string.IsNullOrEmpty(project.FilePath))
                continue;
            var projectDirectory = Path.GetDirectoryName(project.FilePath);
            if (projectDirectory != null && changedPaths.Any(path =>
                    Path.GetFullPath(Path.Combine(_gitRoot, path)).StartsWith(
                        Path.GetFullPath(projectDirectory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
            {
                affectedIds.Add(project.Id);
            }
        }

        var dependencyGraph = solution.GetProjectDependencyGraph();
        var queue = new Queue<ProjectId>(affectedIds);
        while (queue.Count > 0)
        {
            var referencedProject = queue.Dequeue();
            foreach (var dependent in dependencyGraph.GetProjectsThatDirectlyDependOnThisProject(referencedProject))
            {
                if (affectedIds.Add(dependent))
                    queue.Enqueue(dependent);
            }
        }

        return affectedIds
            .Select(solution.GetProject)
            .Where(static project => project != null)
            .Select(static project => project!.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static List<DocumentChangeInfo> DetectChanges(WorkspaceInfo workspaceInfo, SnapshotManifest previousManifest)
    {
        var results = new List<DocumentChangeInfo>();
        var currentDocs = workspaceInfo.Documents;
        var previousDocs = previousManifest.DocumentVersions;
        var processed = new HashSet<DocumentId>();

        foreach (var (docId, currentHash) in currentDocs)
        {
            processed.Add(docId);

            if (!previousDocs.TryGetValue(docId, out var previousHash))
            {

                results.Add(new DocumentChangeInfo(docId.ToString(), DocumentChangeKind.New));
            }
            else if (currentHash != previousHash)
            {

                results.Add(new DocumentChangeInfo(docId.ToString(), DocumentChangeKind.Changed));
            }
            else
            {

                results.Add(new DocumentChangeInfo(docId.ToString(), DocumentChangeKind.Unchanged));
            }
        }

        foreach (var (docId, _) in previousDocs)
        {
            if (!processed.Contains(docId))
            {
                results.Add(new DocumentChangeInfo(docId.ToString(), DocumentChangeKind.Deleted));
            }
        }

        return results;
    }
}
