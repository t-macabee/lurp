namespace Lurp.Storage;

public interface IEdgeStore
{
    /// <summary>Collapses the batch (<see cref="EdgeMerge.CollapseBatch" />) and merges it into the snapshot.</summary>
    /// <returns>The number of relations in the collapsed batch.</returns>
    int SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges);
    void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics);
    void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations);

    List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null);
    List<EdgeRecord> GetEdgesTouchingSymbols(string snapshotId, IEnumerable<string> symbolIds);
    Dictionary<(string SourceSymbolId, string TargetSymbolId, string Kind), List<EdgeSite>> GetEdgeSites(
        string snapshotId,
        IEnumerable<(string SourceSymbolId, string TargetSymbolId, string Kind)> edges);
    List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null);
    List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null);

    int CountEdges(string snapshotId);
    int CountDiagnostics(string snapshotId);

    List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind);
    List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId);
    List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId);

    void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths);
    void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities);
    void DeleteEdgesWithNullDocumentPathForSymbols(string snapshotId, IEnumerable<string> symbolIds);
    void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId);

    void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId);
    void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames);

    void CopyAnnotationsToSnapshot(string fromSnapshotId, string toSnapshotId);
    bool TryRetractAnnotation(string snapshotId, long annotationId);
    void DeleteAnnotationsByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths);

    OrphanEdgeDropSummary DeleteOrphanEdges(string snapshotId);

    /// <summary>
    ///     Removes <c>snapshot_graph_nodes</c> rows whose node_id is no longer
    ///     referenced by this snapshot's edges or is a symbol declared in this snapshot
    ///     (<c>snapshot_symbols</c>, D153.3). Without this, stale synthetic/route nodes
    ///     linger across incremental passes and keep masking orphaned edges, and
    ///     cross-project types remain marked as boundary nodes.
    /// </summary>
    void PruneSnapshotGraphNodes(string snapshotId);

    void UpsertExtractors(IEnumerable<(string Name, string Version, string Description)> extractors);
    bool HasStaleExtractorVersions(string snapshotId);
}