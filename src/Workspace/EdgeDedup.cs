namespace Lurp.Workspace;

internal static class EdgeDedup
{
    public static List<EdgeRecord> Deduplicate(IEnumerable<EdgeRecord> edges)
    {
        return EdgeMerge.CollapseBatch(edges);
    }

    /// <summary>
    /// Composes the effective claim provenance for a caller reached through an
    /// interface/abstract dispatch path (one or more Calls hops plus a
    /// MayDispatchTo edge).
    ///
    /// This is not the strongest : or weakest : edge in the path. The
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
