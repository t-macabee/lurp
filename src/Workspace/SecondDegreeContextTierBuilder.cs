namespace Lurp.Workspace;

internal sealed class SecondDegreeContextTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "second_degree_context";

    string IContextTierBuilder.InclusionReason => "Bounded upstream paths within the requested hop limit.";

    TierBindingScope IContextTierBuilder.BindingScope => TierBindingScope.CallerProjects;

    List<CapsuleItem> IContextTierBuilder.Build() => context.Upstream.BuildCallers(2, context.MaxHops);
}
