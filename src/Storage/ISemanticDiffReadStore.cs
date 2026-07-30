namespace Lurp.Storage;

public interface ISemanticDiffReadStore : IDeclarationStore
{
    IReadOnlyList<SymbolTransitionCandidate> LoadTransitionCandidates(
        string snapshotId,
        IReadOnlyCollection<string> symbolIds);
}
