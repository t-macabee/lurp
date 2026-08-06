namespace Lurp.Storage;

public static class SnapshotTimingSteps
{
    public const string SemanticDiff = "semantic_diff";
    public const string FtsBuild = "fts_build";

    /// <summary>
    /// Zero-duration marker rows recording which path <see cref="Lurp.Workspace.CrossDocumentEdgeRefresher.FindAffectedDocPaths"/>
    /// took for a given incremental run: narrowed document scope, or the
    /// project-scope fallback when the reverse-edge closure outgrew its ratio
    /// cutoff. Presence/absence of the fallback row across runs makes the
    /// fallback rate visible in snapshot_timings instead of inferred.
    /// </summary>
    public const string ClosureNarrowed = "closure_narrowed_document_scope";
    public const string ClosureFallback = "closure_fallback_project_scope";
}
