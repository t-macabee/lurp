using Lurp.Storage;

namespace Lurp.Workspace;

/// <summary>
/// Store-backed dead-candidate evaluator. No Roslyn re-analysis: all facts derive from
/// snapshot_symbols + edges + annotations + binding_incompleteness + snapshot_manifest + document_versions.
/// Mirrors ImpactTraverser / ContextAssembler by exposing a traverser surface, but delegates to
/// <see cref="DeadCandidateStore"/> for the batched queries and suppression-ladder evaluation.
/// </summary>
public sealed class DeadCandidateTraverser
{
    private readonly string _snapshotId;

    public DeadCandidateTraverser(string snapshotId)
    {
        if (string.IsNullOrEmpty(snapshotId)) throw new ArgumentException("snapshotId is required.", nameof(snapshotId));
        _snapshotId = snapshotId;
    }

    public string SnapshotId => _snapshotId;

    public static IReadOnlySet<string> LiveEdgeKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(EdgeKind.Calls),
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

    public static IReadOnlySet<string> StrongProvenance { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Provenance.CompilerProved,
        Provenance.FrameworkDerived,
        Provenance.GlobalImplementationRelation
    };
}
