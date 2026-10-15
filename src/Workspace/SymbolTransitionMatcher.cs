using System.Text;

namespace Lurp.Workspace;

internal static class SymbolTransitionMatcher
{
    public static SymbolTransitionResolution MatchTransitions(
        IReadOnlyList<SymbolTransitionCandidate> removedCandidates,
        IReadOnlyList<SymbolTransitionCandidate> addedCandidates)
    {
        var transitions = new List<SymbolTransition>();
        var consumedRemoved = new HashSet<string>(StringComparer.Ordinal);
        var consumedAdded = new HashSet<string>(StringComparer.Ordinal);

        var removedByKey = GroupByPartitionKey(removedCandidates);
        var addedByKey = GroupByPartitionKey(addedCandidates);

        var allKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in removedByKey.Keys) allKeys.Add(key);
        foreach (var key in addedByKey.Keys) allKeys.Add(key);

        foreach (var key in allKeys)
        {
            if (!removedByKey.TryGetValue(key, out var removedGroup) || removedGroup.Count != 1)
                continue;
            if (!addedByKey.TryGetValue(key, out var addedGroup) || addedGroup.Count != 1)
                continue;

            var removed = removedGroup[0];
            var added = addedGroup[0];

            var kind = FqnParts.ClassifyRenameMove(removed.FullyQualifiedName, added.FullyQualifiedName);
            if (kind == null)
                continue;

            transitions.Add(new SymbolTransition(
                removed.SymbolId,
                added.SymbolId,
                removed.FullyQualifiedName,
                added.FullyQualifiedName,
                kind.Value));

            consumedRemoved.Add(removed.SymbolId);
            consumedAdded.Add(added.SymbolId);
        }

        transitions.Sort((a, b) => string.Compare(a.CurrentSymbolId, b.CurrentSymbolId, StringComparison.Ordinal));

        return new SymbolTransitionResolution(transitions, consumedRemoved, consumedAdded);
    }

    private static Dictionary<string, List<SymbolTransitionCandidate>> GroupByPartitionKey(
        IReadOnlyList<SymbolTransitionCandidate> candidates)
    {
        var groups = new Dictionary<string, List<SymbolTransitionCandidate>>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var partitionKey = $"{candidate.Kind}|{candidate.AssemblyIdentity}";
            var continuityKey = BuildContinuityKey(candidate);
            var fullKey = $"{partitionKey}||{continuityKey}";

            if (!groups.TryGetValue(fullKey, out var list))
            {
                list = [];
                groups[fullKey] = list;
            }

            list.Add(candidate);
        }

        return groups;
    }

    private static string BuildContinuityKey(SymbolTransitionCandidate candidate)
    {
        var fingerprints = candidate.Declarations
            .Select(d => (SigHash: Convert.ToHexString(d.NormalizedSignatureHash),
                BodyHash: d.BodyHash != null ? Convert.ToHexString(d.BodyHash) : ""))
            .OrderBy(f => f.SigHash, StringComparer.Ordinal)
            .ThenBy(f => f.BodyHash, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.Append(candidate.Declarations.Count);
        sb.Append(':');
        var first = true;
        foreach (var (sigHash, bodyHash) in fingerprints)
        {
            if (!first) sb.Append(',');
            sb.Append(sigHash);
            sb.Append(';');
            sb.Append(bodyHash);
            first = false;
        }

        return sb.ToString();
    }
}