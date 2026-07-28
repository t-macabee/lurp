using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public sealed class EdgeStore : IEdgeStore
{
    private readonly EdgeOperationsStore _edges;
    private readonly DiagnosticStore _diagnostics;
    private readonly AnnotationStore _annotations;

    public EdgeStore(SqliteConnection connection)
    {
        if (connection == null) throw new ArgumentNullException(nameof(connection));
        _edges = new EdgeOperationsStore(connection);
        _diagnostics = new DiagnosticStore(connection);
        _annotations = new AnnotationStore(connection);
    }

    public void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
        => _edges.SaveEdges(snapshotId, edges);

    public void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics)
        => _diagnostics.SaveDiagnostics(snapshotId, diagnostics);

    public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
        => _annotations.SaveAnnotations(snapshotId, annotations);

    public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
        => _edges.GetEdges(snapshotId, symbolId);

    public List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null)
        => _diagnostics.GetDiagnostics(snapshotId, projectName);

    public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
        => _annotations.GetAnnotations(snapshotId, symbolId);

    public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
        => _edges.GetEdgesByKind(snapshotId, kind);

    public List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId)
        => _edges.GetIncomingEdges(snapshotId, symbolId);

    public List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId)
        => _edges.GetOutgoingEdges(snapshotId, symbolId);

    public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
        => _edges.DeleteEdgesByDocumentPaths(snapshotId, documentPaths);

    public void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities)
        => _edges.DeleteEdgesWithNullDocumentPathForAssemblies(snapshotId, assemblyIdentities);

    public void DeleteEdgesWithNullDocumentPathForSymbols(string snapshotId, IEnumerable<string> symbolIds)
        => _edges.DeleteEdgesWithNullDocumentPathForSymbols(snapshotId, symbolIds);

    public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
        => _edges.CopyEdgesToSnapshot(fromSnapshotId, toSnapshotId);

    public void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId)
        => _diagnostics.CopySnapshotDiagnostics(fromSnapshotId, toSnapshotId);

    public void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames)
        => _diagnostics.DeleteDiagnosticsByProjectNames(snapshotId, projectNames);

    public void CopyAnnotationsToSnapshot(string fromSnapshotId, string toSnapshotId)
        => _annotations.CopyAnnotationsToSnapshot(fromSnapshotId, toSnapshotId);

    public void DeleteOrphanEdges(string snapshotId)
        => _edges.DeleteOrphanEdges(snapshotId);

    public void UpsertExtractors(IEnumerable<(string Name, string Version, string Description)> extractors)
        => _edges.UpsertExtractors(extractors);

    public bool HasStaleExtractorVersions(string snapshotId)
        => _edges.HasStaleExtractorVersions(snapshotId);
}