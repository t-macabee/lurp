namespace Lurp.Storage;

/// <summary>
/// Single source for the dead-candidate vocabulary: which edge kinds prove liveness,
/// which provenance values rank strong or uncertain, and which symbol kinds are candidates.
/// The handler and the MCP tool print these in this order.
/// </summary>
public static class DeadCandidateLiveness
{
    public static IReadOnlyList<string> LiveEdgeKinds { get; } = new[]
    {
        nameof(EdgeKind.Calls),
        nameof(EdgeKind.MethodGroupRef),
        nameof(EdgeKind.Constructs),
        nameof(EdgeKind.Reads),
        nameof(EdgeKind.Writes),
        nameof(EdgeKind.Handles),
        nameof(EdgeKind.RoutesTo),
        nameof(EdgeKind.Registers),
        nameof(EdgeKind.MapsTo),
        nameof(EdgeKind.MayDispatchTo),
        nameof(EdgeKind.StaticallyCalls),
        nameof(EdgeKind.TestedBy),
        nameof(EdgeKind.ReflectionTypeRef),
        nameof(EdgeKind.ReflectionMemberRef),
        nameof(EdgeKind.ReflectionNameCandidate)
    };

    public static IReadOnlyList<string> TypeUseEdgeKinds { get; } = new[]
    {
        nameof(EdgeKind.References),
        nameof(EdgeKind.Returns),
        nameof(EdgeKind.Inherits),
        nameof(EdgeKind.Implements),
        nameof(EdgeKind.Throws)
    };

    public static IReadOnlyList<string> StrongProvenance { get; } = new[]
    {
        Provenance.CompilerProved,
        Provenance.FrameworkDerived,
        Provenance.GlobalImplementationRelation
    };

    public static IReadOnlyList<string> UncertainProvenance { get; } = new[]
    {
        Provenance.Possible,
        Provenance.Convention,
        Provenance.NameCandidate,
        Provenance.RuntimeUnknown
    };

    public static IReadOnlyList<string> CandidateKinds { get; } = new[]
    {
        nameof(IndexedSymbolKind.Type),
        nameof(IndexedSymbolKind.Method),
        nameof(IndexedSymbolKind.Property),
        nameof(IndexedSymbolKind.Field),
        nameof(IndexedSymbolKind.Event)
    };

    public static string CandidateKindsText { get; } = string.Join(", ", CandidateKinds);

    public static bool IsCandidateKind(string kind)
    {
        foreach (var candidate in CandidateKinds)
        {
            if (string.Equals(kind, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
