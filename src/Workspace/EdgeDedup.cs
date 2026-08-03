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

    /// <summary>
    /// Returns the rank for a provenance string. Higher = stronger evidence.
    /// Unknown provenance values rank below all canonical values so they are
    /// never selected as "best" over a known value during dedup.
    /// </summary>
    internal static int ProvenanceRank(string provenance) => provenance switch
    {
        Provenance.CompilerProved => 6,
        Provenance.FrameworkDerived => 5,
        Provenance.GlobalImplementationRelation => 4,
        Provenance.Possible => 3,
        Provenance.Convention => 2,
        Provenance.NameCandidate => 1,
        Provenance.RuntimeUnknown => 0,
        _ => -1,
    };

    /// <summary>
    /// Composes the effective claim provenance for a caller reached through an
    /// interface/abstract dispatch path (one or more Calls hops plus a
    /// MayDispatchTo edge).
    ///
    /// This is not the strongest — or weakest — edge in the path. The
    /// structural edges may each be individually compiler-proved while the
    /// projected runtime-target claim remains "possible", because the compiler
    /// establishes that an implementation exists, not that this call site
    /// selects it at runtime. The only stronger outcome is actual framework
    /// participation: when a framework, registration, routing, or DI-derived
    /// edge (framework_derived) takes part in the composed path.
    /// </summary>
    internal static string ComposeDispatchClaimProvenance(IEnumerable<string> pathProvenances, string dispatchProvenance)
    {
        if (dispatchProvenance == Provenance.FrameworkDerived
            || pathProvenances.Any(provenance => provenance == Provenance.FrameworkDerived))
        {
            return Provenance.FrameworkDerived;
        }
        return Provenance.Possible;
    }
}
