// Purpose: estimate per-tier token cost and produce the greedy-prefix tier order.
// Owns: tier costing and ordering only.
// Must not contain: capsule assembly logic or storage queries.

namespace Lurp.Workspace;

internal static class ContextBudgeter
{
    internal static int Apply(ContextCapsule capsule, IEnumerable<IContextTierBuilder> tiers, int budget, int runningTotal, BindingObservation observation)
    {
        var truncatedCategories = new List<string>();
        var omittedTiers = new List<TruncationEntry>();
        var budgetExhausted = runningTotal > budget;

        foreach (var tier in tiers)
        {
            var items = tier.Build();
            if (items.Count == 0)
            {
                var reason = tier.EmptyReason ?? (observation.IsUnobservable(tier.BindingScope) ? "unresolved" : "empty");
                omittedTiers.Add(new TruncationEntry(tier.Name, reason));
                continue;
            }

            if (budgetExhausted)
            {
                truncatedCategories.Add(tier.Name);
                omittedTiers.Add(new TruncationEntry(tier.Name, "budget_exhausted"));
                continue;
            }

            var tierCost = items.Sum(ContextAssembler.EstimateTokens);
            if (runningTotal + tierCost <= budget)
            {
                ContextAssembler.AddTierToCapsule(capsule, tier.Name, items);
                runningTotal += tierCost;
                continue;
            }

            // Deliberate greedy-prefix policy: preserve the builder's relevance
            // order and never let a lower-priority item or tier leapfrog the
            // first item that cannot fit.
            foreach (var item in items)
            {
                var itemCost = ContextAssembler.EstimateTokens(item);
                if (runningTotal + itemCost > budget)
                {
                    budgetExhausted = true;
                    break;
                }

                ContextAssembler.AddTierToCapsule(capsule, tier.Name, [item]);
                runningTotal += itemCost;
            }

            truncatedCategories.Add(tier.Name);
            omittedTiers.Add(new TruncationEntry(tier.Name, "budget_exhausted"));
        }

        capsule.Truncated = truncatedCategories.Count > 0;
        capsule.TruncatedCategories = truncatedCategories;
        capsule.OmittedTiers = omittedTiers;
        return runningTotal;
    }
}