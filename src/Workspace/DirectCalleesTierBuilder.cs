using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DirectCalleesTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "direct_callees";

    string IContextTierBuilder.InclusionReason => "Direct compiler-resolved calls, constructions, or accesses that can execute accessor code.";

    TierBindingScope IContextTierBuilder.BindingScope => TierBindingScope.AnchorDocuments;

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var family = AnchorMemberFamily();
        var dispatchSites = new List<(EdgeRecord Access, string DispatchSource, bool IsMemberAccess)>();

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            context.ThrowIfCancellationRequested();
            var outgoing = context.GetOutgoingEdges(symbolId);
            var dispatchTargets = outgoing.Where(edge => edge.Kind == nameof(EdgeKind.StaticallyCalls))
                .Select(edge => edge.TargetSymbolId).ToHashSet(StringComparer.Ordinal);
            foreach (var edge in outgoing)
            {
                if (edge.TargetSymbolId == edge.SourceSymbolId || family.Contains(edge.TargetSymbolId))
                    continue;

                var memberAccess = edge.Kind is nameof(EdgeKind.Reads) or nameof(EdgeKind.Writes) &&
                    SymbolId.DocCommentIdOf(edge.TargetSymbolId) is [('P' or 'E'), ':', ..];
                if (memberAccess)
                {
                    var accessors = context.GetAccessors(edge.TargetSymbolId)
                        .Where(accessor => AccessorEdges.Inherits(accessor.AccessorKind, edge.Kind))
                        .Select(accessor => accessor.SymbolId).ToList();
                    var dispatchSources = dispatchTargets.Contains(edge.TargetSymbolId)
                        ? accessors.Where(id => context.GetMayDispatchEdges(id).Count != 0).ToList()
                        : [];
                    if (dispatchTargets.Contains(edge.TargetSymbolId) && dispatchSources.Count == 0 &&
                        context.GetMayDispatchEdges(edge.TargetSymbolId).Count != 0)
                        dispatchSources.Add(edge.TargetSymbolId);
                    if (dispatchSources.Count == 0 && !accessors.Any(id => context.GetOutgoingEdges(id).Any(IsAccessorBodyEdge)))
                        continue;

                    foreach (var source in dispatchSources)
                        dispatchSites.Add((edge, source, true));
                }
                else if (edge.Kind is not (nameof(EdgeKind.Calls) or nameof(EdgeKind.Constructs) or nameof(EdgeKind.MethodGroupRef)))
                    continue;
                else if (edge.Kind == nameof(EdgeKind.Calls))
                    dispatchSites.Add((edge, edge.TargetSymbolId, false));

                AddItem(edge.TargetSymbolId, edge.Kind, edge.Provenance,
                    memberAccess ? "Direct access to a member with executable accessor code."
                        : "Direct call or construction target of the anchor.",
                    CapsuleRelationship.DirectCallee, true);
            }
        }

        // Give direct callees priority when a dispatch projection reaches the same target.
        foreach (var (access, source, isMemberAccess) in dispatchSites)
            AddMayDispatchTargets(access, source, isMemberAccess);

        return results;

        void AddItem(string symbolId, string edgeKind, string provenance, string inclusionReason,
            string? relationship = null, bool? direct = null)
        {
            if (family.Contains(symbolId) || !seen.Add(symbolId))
                return;

            var item = context.BuildCapsuleItem(symbolId, edgeKind, provenance, inclusionReason,
                relationship, direct);
            if (item != null) results.Add(item);
        }

        void AddMayDispatchTargets(EdgeRecord callEdge, string dispatchSource, bool isMemberAccess)
        {
            context.ThrowIfCancellationRequested();
            var receiverAlternatives = ReceiverTypeConstraints.Deserialize(callEdge.ReceiverTypeConstraintsJson)
                .Select(static alternative => alternative.AsReadOnly())
                .ToList();
            if (receiverAlternatives.Count == 0 && !isMemberAccess)
                return;

            foreach (var edge in context.GetMayDispatchEdges(dispatchSource))
            {
                context.ThrowIfCancellationRequested();
                var target = SymbolId.Parse(edge.TargetSymbolId);
                if (context.ProjectFacts is { } projects &&
                    !projects.IsTestProject(MetadataReferenceIdentity.SimpleName(context.SymbolId.AssemblyIdentity)) &&
                    projects.IsTestProject(MetadataReferenceIdentity.SimpleName(target.AssemblyIdentity)))
                    continue;
                if (receiverAlternatives.Count > 0 && !context.GetDeclaringTypeIds(edge.TargetSymbolId)
                        .Any(candidateTypeId => context.IsReceiverCompatible(candidateTypeId, receiverAlternatives)))
                    continue;

                var receiverEvidence = receiverAlternatives.Count > 0
                    ? " The containing type satisfies the call site's persisted static receiver-type constraints."
                    : "";
                var associated = isMemberAccess
                    ? SymbolMetadata.GetString(context.GetSymbolMetadata(edge.TargetSymbolId), SymbolMetadataKeys.AssociatedSymbol)
                    : null;
                var targetId = associated is null ? edge.TargetSymbolId : SymbolId.Compose(associated, target.AssemblyIdentity);
                AddItem(targetId, edge.Kind, CapsuleProvenance.GlobalImplementationRelation,
                    $"The anchor reaches the interface or virtual member via {callEdge.Kind} ({callEdge.Provenance}). " +
                    $"A MayDispatchTo edge ({edge.Provenance}) identifies this candidate implementation." + receiverEvidence,
                    CapsuleRelationship.IndirectDispatchCandidate, false);
            }
        }
    }

    private HashSet<string> AnchorMemberFamily()
    {
        var anchor = context.SymbolId;
        var family = new HashSet<string>(StringComparer.Ordinal) { anchor.Value };
        if (anchor.DocCommentId is [('P' or 'E'), ':', ..])
            family.UnionWith(context.GetAccessors(anchor.Value).Select(accessor => accessor.SymbolId));
        else if (anchor.DocCommentId is ['M', ':', ..] &&
                 SymbolMetadata.GetString(context.GetSymbolMetadata(anchor.Value), SymbolMetadataKeys.AssociatedSymbol) is { } member)
        {
            var memberId = SymbolId.Compose(member, anchor.AssemblyIdentity);
            family.Add(memberId);
            family.UnionWith(context.GetAccessors(memberId).Select(accessor => accessor.SymbolId));
        }

        return family;
    }

    private static bool IsAccessorBodyEdge(EdgeRecord edge) =>
        edge.Kind is nameof(EdgeKind.Calls) or nameof(EdgeKind.Constructs) or nameof(EdgeKind.Reads) or nameof(EdgeKind.Writes) or
            nameof(EdgeKind.MethodGroupRef) or nameof(EdgeKind.Throws) or nameof(EdgeKind.StaticallyCalls);
}
