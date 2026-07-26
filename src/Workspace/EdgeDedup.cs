using Lurp.Storage;

namespace Lurp.Workspace;

internal static class EdgeDedup
{
    public static List<EdgeRecord> Deduplicate(IEnumerable<EdgeRecord> edges)
    {
        var best = new Dictionary<(string source, string target, string kind), EdgeRecord>();

        foreach (var edge in edges)
        {
            var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
            if (best.TryGetValue(key, out var existing))
            {
                if (ProvenanceRank(edge.Provenance) > ProvenanceRank(existing.Provenance))
                    best[key] = edge;
            }
            else
            {
                best[key] = edge;
            }
        }

        return new List<EdgeRecord>(best.Values);
    }

    private static int ProvenanceRank(string provenance) => provenance switch
    {
        Provenance.CompilerProved => 5,
        Provenance.FrameworkDerived => 4,
        Provenance.Possible => 3,
        Provenance.Convention => 2,
        Provenance.NameCandidate => 1,
        Provenance.RuntimeUnknown => 0,
        _ => -1,
    };
}
