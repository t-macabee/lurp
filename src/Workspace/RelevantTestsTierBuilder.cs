namespace Lurp.Workspace;

internal sealed class RelevantTestsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "relevant_tests";

    string IContextTierBuilder.InclusionReason => "Persisted TestedBy evidence connected to the anchor or its upstream callers.";

    TierBindingScope IContextTierBuilder.BindingScope => TierBindingScope.CallerProjects;

    List<CapsuleItem> IContextTierBuilder.Build() => context.Upstream.BuildTests();
}
