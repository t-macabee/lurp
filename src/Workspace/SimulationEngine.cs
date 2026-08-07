using System.Text.Json.Serialization;
using Lurp.Storage;
namespace Lurp.Workspace;

public sealed class SimulationItem
{
    [JsonPropertyName(QueryJsonFieldNames.SymbolId)]
    public string SymbolId { get; }

    [JsonPropertyName(QueryJsonFieldNames.FullyQualifiedName)]
    public string? Fqn { get; }

    [JsonPropertyName("edge_kind")]
    public string EdgeKind { get; }

    [JsonPropertyName("document_path")]
    public string? DocumentPath { get; }

    [JsonPropertyName("line")]
    public int? Line { get; }

    public SimulationItem(string symbolId, string? fqn, string edgeKind, string? documentPath, int? line)
    {
        SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
        Fqn = fqn;
        EdgeKind = edgeKind ?? throw new ArgumentNullException(nameof(edgeKind));
        DocumentPath = documentPath;
        Line = line;
    }
}

public sealed class SimulationReport
{
    [JsonPropertyName("simulation_type")]
    public string SimulationType { get; }

    [JsonPropertyName(QueryJsonFieldNames.SymbolId)]
    public string SymbolId { get; }

    [JsonPropertyName(QueryJsonFieldNames.SnapshotId)]
    public string SnapshotId { get; }

    [JsonPropertyName("affected_count")]
    public int AffectedCount { get; }

    [JsonPropertyName("items")]
    public List<SimulationItem> Items { get; }

    public SimulationReport(string simulationType, string symbolId, string snapshotId, List<SimulationItem> items)
    {
        SimulationType = simulationType ?? throw new ArgumentNullException(nameof(simulationType));
        SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
        SnapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
        Items = items ?? throw new ArgumentNullException(nameof(items));
        AffectedCount = items.Count;
    }
}

public sealed class SimulationEngine
{
    private readonly IEdgeStore _edgeStore;
    private readonly IDeclarationStore _declarationStore;
    private readonly string _snapshotId;

    public SimulationEngine(IEdgeStore edgeStore, IDeclarationStore declarationStore, string snapshotId)
    {
        _edgeStore = edgeStore ?? throw new ArgumentNullException(nameof(edgeStore));
        _declarationStore = declarationStore ?? throw new ArgumentNullException(nameof(declarationStore));
        _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
    }

    public SimulationReport SimulateRename(string symbolId, string newSimpleName)
    {
        var outgoing = _edgeStore.GetOutgoingEdges(_snapshotId, symbolId);
        var overrideOutgoing = outgoing.Where(e => e.Kind == "Overrides");

        var items = new List<SimulationItem>();
        var seen = new HashSet<(string Source, string Kind, string? Document, int? Line)>();

        // A type name is rarely referenced as a direct edge target on the type
        // symbol itself: callers of a static member (Foo.Bar()) or a
        // constructed instance produce edges to the member/constructor, not
        // to the declaring type. Renaming the type still requires touching
        // every one of those qualified call sites, so their incoming edges
        // are pulled in alongside the type's own.
        var declaredMembers = outgoing
            .Where(e => e.Kind == EdgeKind.Declares.ToString())
            .Select(e => e.TargetSymbolId);
        var targetSymbolIds = new[] { symbolId }.Concat(declaredMembers);

        foreach (var targetSymbolId in targetSymbolIds)
        {
            var incoming = _edgeStore.GetIncomingEdges(_snapshotId, targetSymbolId);
            var filteredIncoming = incoming.Where(e => e.Kind is "Calls" or "References" or "Overrides" or "Implements" or "Registers");

            foreach (var edge in filteredIncoming)
            {
                if (!seen.Add((edge.SourceSymbolId, edge.Kind, edge.SourceDocumentPath, edge.SourceStartLine)))
                    continue;

                var info = _declarationStore.GetSymbolInfo(edge.SourceSymbolId, _snapshotId);
                items.Add(new SimulationItem(symbolId: edge.SourceSymbolId,fqn: info?.FullyQualifiedName,edgeKind: edge.Kind,documentPath: edge.SourceDocumentPath,line: edge.SourceStartLine));
            }
        }

        foreach (var edge in overrideOutgoing)
        {
            var info = _declarationStore.GetSymbolInfo(edge.TargetSymbolId, _snapshotId);
            items.Add(new SimulationItem(symbolId: edge.TargetSymbolId,fqn: info?.FullyQualifiedName,edgeKind: edge.Kind,documentPath: edge.SourceDocumentPath,line: edge.SourceStartLine));
        }

        return new SimulationReport("rename", symbolId, _snapshotId, items);
    }

    public SimulationReport SimulateMove(string symbolId, string newNamespace)
    {
        var items = new List<SimulationItem>();
        var seen = new HashSet<(string Source, string Kind, string? Document, int? Line)>();

        // Same type->member expansion as SimulateRename: callers of a static
        // member (Foo.Bar()) produce edges to the member, not the declaring
        // type, so moving the type still requires touching those call sites.
        foreach (var targetSymbolId in WithDeclaredMembers(symbolId))
        {
            var incoming = _edgeStore.GetIncomingEdges(_snapshotId, targetSymbolId);
            var filteredIncoming = incoming.Where(e => e.Kind is "Calls" or "References" or "Overrides" or "Implements" or "Registers");

            foreach (var edge in filteredIncoming)
            {
                if (!seen.Add((edge.SourceSymbolId, edge.Kind, edge.SourceDocumentPath, edge.SourceStartLine)))
                    continue;

                var info = _declarationStore.GetSymbolInfo(edge.SourceSymbolId, _snapshotId);
                items.Add(new SimulationItem(symbolId: edge.SourceSymbolId,fqn: info?.FullyQualifiedName,edgeKind: edge.Kind,documentPath: edge.SourceDocumentPath,line: edge.SourceStartLine));
            }
        }

        return new SimulationReport("move", symbolId, _snapshotId, items);
    }

    public SimulationReport SimulateRemove(string symbolId)
    {
        var allItems = new Dictionary<string, SimulationItem>();

        // 1. Upstream impact paths via ImpactTraverser
        var traverser = new ImpactTraverser(_edgeStore, _snapshotId);
        var paths = traverser.TraceImpact(symbolId, ImpactDirection.Upstream);

        foreach (var path in paths)
        {
            foreach (var hop in path.Hops)
            {
                var key = hop.SourceSymbolId;
                if (!allItems.ContainsKey(key))
                {
                    var info = _declarationStore.GetSymbolInfo(key, _snapshotId);
                    allItems[key] = new SimulationItem(symbolId: key,fqn: info?.FullyQualifiedName,edgeKind: hop.EdgeKind,documentPath: hop.SourceDocument,line: hop.SourceLine);
                }
            }
        }

        // 2. Registers (DI registrations that would orphan)
        var registers = _edgeStore.GetIncomingEdges(_snapshotId, symbolId)
            .Where(e => e.Kind == "Registers");
        foreach (var edge in registers)
        {
            var key = edge.SourceSymbolId;
            if (!allItems.ContainsKey(key))
            {
                var info = _declarationStore.GetSymbolInfo(key, _snapshotId);
                allItems[key] = new SimulationItem(symbolId: key,fqn: info?.FullyQualifiedName,edgeKind: edge.Kind,documentPath: edge.SourceDocumentPath,line: edge.SourceStartLine);
            }
        }

        // 3. TestedBy (tests that would lose their production anchor)
        // Edge direction is production -> test, so query outgoing edges from the
        // removed production symbol and report the target test method.
        var testedBy = _edgeStore.GetOutgoingEdges(_snapshotId, symbolId)
            .Where(e => e.Kind == EdgeKind.TestedBy.ToString());
        foreach (var edge in testedBy)
        {
            var key = edge.TargetSymbolId;
            if (!allItems.ContainsKey(key))
            {
                var info = _declarationStore.GetSymbolInfo(key, _snapshotId);
                allItems[key] = new SimulationItem(symbolId: key,fqn: info?.FullyQualifiedName,edgeKind: edge.Kind,documentPath: edge.SourceDocumentPath,line: edge.SourceStartLine);
            }
        }

        // 4. Implements (outgoing : interfaces this type fulfills, contract broken)
        var outgoingImplements = _edgeStore.GetOutgoingEdges(_snapshotId, symbolId)
            .Where(e => e.Kind == "Implements");
        foreach (var edge in outgoingImplements)
        {
            var key = edge.TargetSymbolId;
            if (!allItems.ContainsKey(key))
            {
                var info = _declarationStore.GetSymbolInfo(key, _snapshotId);
                allItems[key] = new SimulationItem(symbolId: key,fqn: info?.FullyQualifiedName,edgeKind: edge.Kind,documentPath: edge.SourceDocumentPath,line: edge.SourceStartLine);
            }
        }

        return new SimulationReport("remove", symbolId, _snapshotId, allItems.Values.ToList());
    }

    // Returns the type's own ID plus its declared members' IDs, so callers
    // can query incoming edges for the type and its members in one pass.
    private IEnumerable<string> WithDeclaredMembers(string symbolId)
    {
        yield return symbolId;
        if (!SymbolId.TryParse(symbolId, out var id) || !id.IsType)
            yield break;
        foreach (var e in _edgeStore.GetOutgoingEdges(_snapshotId, symbolId))
            if (e.Kind == EdgeKind.Declares.ToString())
                yield return e.TargetSymbolId;
    }
}
