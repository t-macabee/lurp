using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

/// <summary>
///     D15: for every override, emit a may_dispatch_to edge from each ancestor in its
///     override chain (the member it overrides, that member's overridden member, and so
///     on up to the root) to the override. A call that names any ancestor can run the
///     override. Because the override chain is fully resolved at compile time within a
///     single compilation, all such edges are "compiler_proved". D38: a compiler-made
///     override that Lurp stores (a record's synthesized overrides) has the same edges.
/// </summary>
internal sealed class VirtualOverrideExtractor(PolymorphismExtractionContext context)
{
    internal List<EdgeRecord> Extract(List<INamedTypeSymbol> allTypes)
    {
        var edges = new List<EdgeRecord>();

        foreach (var type in allTypes)
            foreach (var member in type.GetMembers())
            {
                if (!ExtractionUtils.HasStructuralFacts(member))
                    continue;

                for (var ancestor = ExtractionUtils.OverriddenMember(member); ancestor != null;
                     ancestor = ExtractionUtils.OverriddenMember(ancestor))
                    EmitDispatchEdge(ancestor, member, edges);
            }

        return edges;
    }

    private void EmitDispatchEdge(ISymbol ancestor, ISymbol overrideMember, List<EdgeRecord> edges)
    {
        var ancestorId = context.MakeSymbolId(ancestor);
        var overrideId = context.MakeSymbolId(overrideMember);
        if (ancestorId == null || overrideId == null)
            return;

        context.RecordFilteredExternal(ancestor, BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(overrideMember));
        edges.Add(context.MakeMayDispatchEdge(ancestorId, overrideId, overrideMember, Provenance.CompilerProved));
    }
}
