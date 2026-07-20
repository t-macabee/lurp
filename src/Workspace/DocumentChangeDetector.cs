using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class DocumentChangeDetector(string gitRoot)
{
    private readonly string _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));

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
        Console.Write("Hashing documents and detecting changes... ");
        var docChanges = DetectChanges(workspaceInfo, previousRichManifest);
        var changedDocs = docChanges.Where(c => c.ChangeKind != DocumentChangeKind.Unchanged).ToList();
        var changedPaths = changedDocs.Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"done ({changedDocs.Count} changed, {docChanges.Count - changedDocs.Count} unchanged).");

        if (changedDocs.Count == 0)
        {
            Console.WriteLine("No changes detected. Skipping incremental index.");
        }
        else
        {
            foreach (var change in changedDocs)
                Console.WriteLine($"  {change.ChangeKind}: {change.RelativePath}");
        }

        return (changedDocs, changedPaths);
    }

    public HashSet<string> IdentifyAffectedProjects(Solution solution, HashSet<string> changedPaths)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null)
                    continue;

                var relPath = GetRelativePath(document.FilePath, _gitRoot);

                if (changedPaths.Contains(relPath))
                {
                    affected.Add(project.Name);
                    break;
                }
            }
        }

        return affected;
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

    public static string GetRelativePath(string fullPath, string gitRoot)
    {
        var normalizedRoot = Path.GetFullPath(gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var root = normalizedRoot + Path.DirectorySeparatorChar;

        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
