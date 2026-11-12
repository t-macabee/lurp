using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

/// <summary>
///     Extracts may_dispatch_to edges: from an interface member to each implementation
///     (<see cref="InterfaceDispatchExtractor" />) and from each member of an override
///     chain to each override below it (<see cref="VirtualOverrideExtractor" />, D15).
///     Provenance on may_dispatch_to:
///     - "compiler_proved"   : the type that declares the implementing member maps
///     the interface member to it, or the override is a descendant
///     of the dispatch point in its override chain.
///     - "possible"          : only a derived type maps the interface member to an
///     inherited implementation; it is a valid dispatch target but
///     a future re-implementation in a derived type could shadow it.
///     - "framework_derived" : (NOT emitted here) emitted by DependencyInjectionAdapter
///     as a separate may_dispatch_to edge when DI registration
///     evidence narrows the candidate set.  See the adapter
///     for details.
///     The StaticallyCalls marker of a call site that targets a dispatch point is written
///     by the member-body pass (<see cref="OperationShapeExtractor" />, D22), so graph
///     traversal can follow the call → dispatch point → implementation chain.
/// </summary>
public sealed class PolymorphismExtractor
{
    private readonly PolymorphismExtractionContext _context;

    internal PolymorphismExtractor(ProjectExtractionInputs inputs)
    {
        _context = new PolymorphismExtractionContext(inputs);
    }

    public List<EdgeRecord> ExtractAll()
    {
        var allTypes = _context.GetAllNamedTypes().ToList();

        // Filter to types declared in scope documents when the scope is set
        if (_context.ScopeDocuments != null)
            allTypes = [.. allTypes.Where(t => _context.IsTypeInScope(t))];

        var edges = new List<EdgeRecord>();
        edges.AddRange(new InterfaceDispatchExtractor(_context).Extract(allTypes));
        edges.AddRange(new VirtualOverrideExtractor(_context).Extract(allTypes));
        return edges;
    }
}