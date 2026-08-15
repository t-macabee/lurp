namespace Lurp.Storage;

public sealed record SnapshotPair(string FromSnapshotId, string ToSnapshotId);

public sealed record DeclarationFingerprint(
    string DocumentPath,
    byte[] NormalizedSignatureHash,
    byte[]? BodyHash);

public sealed record SymbolTransitionCandidate(
    string SymbolId,
    IndexedSymbolKind Kind,
    string AssemblyIdentity,
    string? FullyQualifiedName,
    IReadOnlyList<DeclarationFingerprint> Declarations);

public enum SymbolTransitionKind
{
    Rename,
    Move,
    RenameAndMove
}

public sealed record SymbolTransition(
    string PreviousSymbolId,
    string CurrentSymbolId,
    string? PreviousFullyQualifiedName,
    string? CurrentFullyQualifiedName,
    SymbolTransitionKind Kind);

public sealed record SymbolTransitionResolution(
    IReadOnlyList<SymbolTransition> Transitions,
    IReadOnlySet<string> ConsumedRemovedIds,
    IReadOnlySet<string> ConsumedAddedIds);