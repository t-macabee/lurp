using System.Collections.Immutable;

namespace Lurp.Workspace;

public enum FreshnessMode
{
    Auto,
    Hash,
    Off
}

public sealed record FreshnessStamp(
    string State,
    string Method,
    int ChangedDocumentCount,
    IReadOnlyList<string> ChangedDocumentsSample,
    DateTime CheckedAtUtc,
    string SnapshotId,
    string Scope); // "documents_only" (cheap read stamp) or "full" (freshness check against the loaded workspace)

/// <summary>
///     Compares a current workspace against a stored snapshot manifest to detect
///     staleness. Three entry points share the private Check* comparators:
///     <list type="bullet">
///         <item><see cref="CheckFreshnessCheap" /> — Roslyn-free read stamp; stat/hash file I/O.</item>
///         <item><see cref="CheckFreshness(WorkspaceInfo,SnapshotManifest?)" /> — full read check; includes <c>CheckDocuments</c>.</item>
///         <item><see cref="GetFullRebuildMismatches" /> — write-side rebuild gate; omits <c>CheckDocuments</c> (incremental index handles per-document changes itself).</item>
///     </list>
/// </summary>
public static class WorkspaceFreshness
{
    /// <summary>
    ///     Cheap read-path freshness check: no Roslyn workspace load. Compares each
    ///     document's on-disk last-write time against the snapshot's build time;
    ///     in <see cref="FreshnessMode.Hash" /> mode, files that look touched are
    ///     re-hashed to confirm an actual content change (avoids false "stale" on
    ///     a touch-but-unchanged file).
    /// </summary>
    public static FreshnessStamp CheckFreshnessCheap(ISnapshotManifestStore manifests, ISnapshotDocumentStore documents, string snapshotId, FreshnessMode mode)
    {
        var checkedAt = DateTime.UtcNow;

        if (mode == FreshnessMode.Off)
            return new FreshnessStamp("unknown", "skipped", 0, [], checkedAt, snapshotId, "documents_only");

        var metadata = manifests.LoadSnapshotMetadata(snapshotId);
        if (metadata == null)
            return new FreshnessStamp("unknown", "skipped", 0, [], checkedAt, snapshotId, "documents_only");

        try
        {
            var gitRoot = metadata.GitRoot;
            var builtAtUtc = metadata.CreatedAtUtc;
            var versionsByPath = documents.GetDocumentVersionIdsByPath(snapshotId);

            var changed = new List<string>();
            foreach (var (relativePath, storedVersionId) in versionsByPath)
            {
                var fullPath = Path.GetFullPath(Path.Combine(gitRoot, relativePath));

                if (!File.Exists(fullPath))
                {
                    changed.Add(relativePath);
                    continue;
                }

                var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
                if (lastWriteUtc <= builtAtUtc)
                    continue;

                if (mode == FreshnessMode.Auto)
                {
                    changed.Add(relativePath);
                    continue;
                }

                var rawBytes = File.ReadAllBytes(fullPath);
                var normalized = WorkspaceInfo.NormalizeSourceBytesForFreshnessCheck(relativePath, rawBytes);
                var currentHash = DocumentVersionId.Compute(new DocumentId(relativePath), normalized).Hash;
                // document_version_id is persisted as "{documentId}:{contentHash}"
                // (Migration_016); the hash is always the substring after the
                // final colon, since content_hash itself never contains one.
                var storedHash = storedVersionId[(storedVersionId.LastIndexOf(':') + 1)..];
                if (!string.Equals(currentHash, storedHash, StringComparison.Ordinal))
                    changed.Add(relativePath);
            }

            var method = mode == FreshnessMode.Hash ? "stat+hash" : "stat";
            var state = changed.Count == 0 ? "fresh" : "stale";
            return new FreshnessStamp(state, method, changed.Count, changed.Take(10).ToList(), checkedAt, snapshotId, "documents_only");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return new FreshnessStamp("unknown", "skipped", 0, [], checkedAt, snapshotId, "documents_only");
        }
    }

    public static FreshnessResult CheckFreshness(WorkspaceInfo current, ISnapshotManifestStore manifests)
    {
        var storageManifest = manifests.LoadLatestSnapshot(current.Id.Value);

        if (storageManifest == null)
            return new FreshnessResult(false, [
                    new SnapshotMismatch(MismatchKind.VersionChanged, "Workspace has never been indexed : no snapshot manifest found.", null, null)
                ]);

        var richManifest = SnapshotManifest.FromStorageManifest(storageManifest);
        return CheckFreshness(current, richManifest);
    }

    public static FreshnessResult CheckFreshness(WorkspaceInfo current, SnapshotManifest? stored)
    {
        if (stored == null)
            return new FreshnessResult(false, [
                    new SnapshotMismatch(MismatchKind.VersionChanged, "Workspace has never been indexed : no snapshot manifest found.", null, null)
                ]);

        var mismatches = new List<SnapshotMismatch>();
        mismatches.AddRange(CheckWorkspaceIdentity(current, stored));
        mismatches.AddRange(CheckDocuments(current, stored));
        mismatches.AddRange(CheckSdkAndCompiler(current, stored));
        mismatches.AddRange(CheckTargetFrameworks(current, stored));
        mismatches.AddRange(CheckProjectGraph(current, stored));
        mismatches.AddRange(CheckMetadataReferences(current, stored));
        mismatches.AddRange(CheckCompilationOptions(current, stored));
        mismatches.AddRange(CheckExtractorVersion(current, stored));

        return new FreshnessResult(mismatches.Count == 0, mismatches.AsReadOnly());
    }

    public static IReadOnlyList<SnapshotMismatch> GetFullRebuildMismatches(WorkspaceInfo current, SnapshotManifest stored)
    {
        var mismatches = new List<SnapshotMismatch>();
        mismatches.AddRange(CheckWorkspaceIdentity(current, stored));
        mismatches.AddRange(CheckSdkAndCompiler(current, stored));
        mismatches.AddRange(CheckTargetFrameworks(current, stored));
        mismatches.AddRange(CheckProjectGraph(current, stored));
        mismatches.AddRange(CheckMetadataReferences(current, stored));
        mismatches.AddRange(CheckCompilationOptions(current, stored));
        mismatches.AddRange(CheckExtractorVersion(current, stored));
        return mismatches;
    }

    private static IEnumerable<SnapshotMismatch> CheckWorkspaceIdentity(WorkspaceInfo current, SnapshotManifest stored)
    {
        if (current.Id.Value != stored.WorkspaceId.Value)
            yield return new SnapshotMismatch(MismatchKind.SdkChanged, $"Workspace identity mismatch: current '{current.Id.Value}' vs stored '{stored.WorkspaceId.Value}'.", null, $"{current.Id.Value} → {stored.WorkspaceId.Value}");
    }

    private static IEnumerable<SnapshotMismatch> CheckDocuments(WorkspaceInfo current, SnapshotManifest stored)
    {
        var currentDocs = current.Documents;
        var storedDocs = stored.DocumentVersions;
        var generatedDocs = current.GeneratedDocuments;

        foreach (var (docId, _) in storedDocs)
        {
            if (generatedDocs.Contains(docId))
                continue;
            if (!currentDocs.ContainsKey(docId)) yield return new SnapshotMismatch(MismatchKind.DocumentRemoved, $"Document removed: '{docId}'.", docId, null);
        }

        foreach (var (docId, currentHash) in currentDocs)
        {
            if (generatedDocs.Contains(docId))
                continue;
            if (!storedDocs.TryGetValue(docId, out var storedHash))
                yield return new SnapshotMismatch(MismatchKind.DocumentAdded, $"Document added: '{docId}'.", docId, $"hash (new) = {currentHash}");
            else if (currentHash != storedHash) yield return new SnapshotMismatch(MismatchKind.DocumentModified, $"Document content changed: '{docId}'.", docId, $"hash {storedHash} → {currentHash}");
        }
    }

    private static IEnumerable<SnapshotMismatch> CheckSdkAndCompiler(WorkspaceInfo current, SnapshotManifest stored)
    {
        if (!string.Equals(current.SdkVersion, stored.SdkVersion, StringComparison.Ordinal)) yield return new SnapshotMismatch(MismatchKind.SdkChanged, ".NET SDK version changed.", null, $"{stored.SdkVersion} → {current.SdkVersion}");

        var currentCompiler = current.CompilerVersion.ToString();
        if (!string.Equals(currentCompiler, stored.CompilerVersion, StringComparison.Ordinal)) yield return new SnapshotMismatch(MismatchKind.CompilerChanged, "Roslyn compiler version changed.", null, $"{stored.CompilerVersion} → {currentCompiler}");
    }

    private static IEnumerable<SnapshotMismatch> CheckTargetFrameworks(WorkspaceInfo current, SnapshotManifest stored)
    {
        var currentTfms = current.TargetFrameworks;
        var storedTfms = stored.TargetFrameworks;

        foreach (var (projName, _) in storedTfms)
            if (!currentTfms.ContainsKey(projName))
                yield return new SnapshotMismatch(MismatchKind.ProjectRemoved, $"Project removed: '{projName}'.", null, projName);

        foreach (var (projName, currentTfm) in currentTfms)
            if (!storedTfms.TryGetValue(projName, out var storedTfm))
                yield return new SnapshotMismatch(MismatchKind.ProjectAdded, $"Project added: '{projName}'.", null, projName);
            else if (!string.Equals(currentTfm, storedTfm, StringComparison.Ordinal)) yield return new SnapshotMismatch(MismatchKind.TargetFrameworkChanged, $"Target framework changed for project '{projName}'.", null, $"{storedTfm} → {currentTfm}");
    }

    private static IEnumerable<SnapshotMismatch> CheckProjectGraph(WorkspaceInfo current, SnapshotManifest stored)
    {
        var currentGraph = current.ProjectGraph;
        var storedGraph = stored.ProjectGraph;

        var allProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in currentGraph.Keys) allProjects.Add(k);
        foreach (var k in storedGraph.Keys) allProjects.Add(k);

        foreach (var projName in allProjects)
        {
            var currentRefs = currentGraph.TryGetValue(projName, out var c)
                ? c
                : ImmutableHashSet<string>.Empty;
            var storedRefs = storedGraph.TryGetValue(projName, out var s)
                ? s
                : [];

            if (!currentRefs.SetEquals(storedRefs))
            {
                var currentSorted = currentRefs.OrderBy(x => x, StringComparer.Ordinal);
                var storedSorted = storedRefs.OrderBy(x => x, StringComparer.Ordinal);
                yield return new SnapshotMismatch(MismatchKind.ProjectReferenceChanged, $"Project references changed for '{projName}'.", null, $"stored=[{string.Join(", ", storedSorted)}]  current=[{string.Join(", ", currentSorted)}]");
            }
        }
    }

    private static IEnumerable<SnapshotMismatch> CheckMetadataReferences(WorkspaceInfo current, SnapshotManifest stored)
    {
        var currentRefs = current.MetadataReferenceIdentities;
        var storedRefs = stored.MetadataReferenceIdentities;

        // Only projects present in both sides are compared. A project absent
        // from the stored map means its value was null (pre-027 snapshot:
        // unknown, not different) or the project itself is gone — project
        // removal is reported by CheckTargetFrameworks.
        foreach (var (projName, currentIds) in currentRefs)
        {
            if (!storedRefs.TryGetValue(projName, out var storedIds))
                continue;

            var sortedCurrent = currentIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var sortedStored = storedIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!sortedCurrent.SequenceEqual(sortedStored))
                yield return new SnapshotMismatch(MismatchKind.MetadataReferencesChanged, $"Metadata references changed for '{projName}'.", null, $"stored=[{string.Join(", ", sortedStored)}]  current=[{string.Join(", ", sortedCurrent)}]");
        }
    }

    private static IEnumerable<SnapshotMismatch> CheckCompilationOptions(WorkspaceInfo current, SnapshotManifest stored)
    {
        var currentOpts = current.CompilationOptionsFingerprints;
        var storedOpts = stored.CompilationOptionsFingerprints;

        // Same unknown-not-different semantics as CheckMetadataReferences: a
        // project absent from the stored map is skipped, not reported.
        foreach (var (projName, currentFingerprint) in currentOpts)
        {
            if (!storedOpts.TryGetValue(projName, out var storedFingerprint))
                continue;

            if (!string.Equals(currentFingerprint, storedFingerprint, StringComparison.Ordinal))
                yield return new SnapshotMismatch(MismatchKind.CompilationOptionsChanged, $"Compilation options changed for project '{projName}'.", null, $"{storedFingerprint} → {currentFingerprint}");
        }
    }

    private static IEnumerable<SnapshotMismatch> CheckExtractorVersion(WorkspaceInfo current, SnapshotManifest stored)
    {
        if (!string.Equals(current.ExtractorVersion, stored.ExtractorVersion, StringComparison.Ordinal))
            yield return new SnapshotMismatch(MismatchKind.VersionChanged, "Extractor version changed.", null, $"{stored.ExtractorVersion} → {current.ExtractorVersion}");
    }

    public sealed record FreshnessResult(bool IsFresh, IReadOnlyList<SnapshotMismatch> Mismatches);
}