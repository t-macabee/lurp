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

    string? EmptyReason => null;
    List<CapsuleItem> Build();
}