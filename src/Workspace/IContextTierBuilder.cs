namespace Lurp.Workspace;

internal interface IContextTierBuilder
{
    string Name { get; }

    /// <summary>
    ///     One-line justification for why this tier is included in a capsule.
    ///     Owned by the builder so the capsule's inclusion-reason text has a single
    ///     source (see <c>ContextAssembler.PopulateContractSections</c>).
    /// </summary>
    string InclusionReason { get; }

    /// <summary>
    ///     The binding region that must be observable before an empty result of this tier
    ///     is a proved absence (D57). Each tier states it, so a new tier cannot fall into a
    ///     scope by default.
    /// </summary>
    TierBindingScope BindingScope { get; }

    string? EmptyReason => null;
    List<CapsuleItem> Build();
}