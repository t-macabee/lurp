namespace Lurp.Storage;

public interface IEdgeStore
{
    List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null);
    List<EdgeRecord> GetEdgesTouchingSymbols(string snapshotId, IEnumerable<string> symbolIds);
    Dictionary<(string SourceSymbolId, string TargetSymbolId, string Kind), List<EdgeSite>> GetEdgeSites(
        string snapshotId,
        IEnumerable<(string SourceSymbolId, string TargetSymbolId, string Kind)> edges);
    List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null);
    List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId);
    List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId);
}
