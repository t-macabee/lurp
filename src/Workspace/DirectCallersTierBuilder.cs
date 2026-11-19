namespace Lurp.Workspace;

internal sealed class DirectCallersTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "direct_callers";

    string IContextTierBuilder.InclusionReason => "Direct callers and framework entry points that can reach the anchor.";

    TierBindingScope IContextTierBuilder.BindingScope => TierBindingScope.CallerProjects;

    List<CapsuleItem> IContextTierBuilder.Build() => context.Upstream.BuildCallers(1, 1);
}
