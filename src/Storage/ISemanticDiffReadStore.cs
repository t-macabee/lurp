namespace Lurp.Storage;

public interface ISemanticDiffReadStore
{
    IReadOnlyList<SymbolTransitionCandidate> LoadTransitionCandidates(
        string snapshotId,
        IReadOnlyCollection<string> symbolIds);
}
