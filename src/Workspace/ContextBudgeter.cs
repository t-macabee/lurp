// Purpose: estimate per-tier token cost and produce the greedy-prefix tier order.
// Owns: tier costing and ordering only.
// Must not contain: capsule assembly logic or storage queries.

namespace Lurp.Workspace
{
    internal static class ContextBudgeter
    {
        internal static int Apply(ContextCapsule capsule, IEnumerable<IContextTierBuilder> tiers, int budget, int runningTotal, bool anchorBindingIsIncomplete = false)
        {
            var truncatedCategories = new List<string>();
            var omittedTiers = new List<TruncationEntry>();
            var budgetExhausted = runningTotal > budget;

            // An empty tier carries one of two very different meanings, and collapsing
            // them is what lets a capsule assert "nothing calls this" when the truth is
            // "the compiler could not tell". "empty" is a proved absence and consumers
            // may act on it; "unresolved" is an unobservable region and proves nothing.
            var emptyReason = anchorBindingIsIncomplete ? "unresolved" : "empty";

            foreach (var tier in tiers)
            {
                var items = tier.Build();
                if (items.Count == 0)
                {
                    var reason = tier.EmptyReason ?? emptyReason;
                    omittedTiers.Add(new TruncationEntry(tier.Name, reason));
                    continue;
                }
                if (budgetExhausted)
                {
                    truncatedCategories.Add(tier.Name);
                    omittedTiers.Add(new TruncationEntry(tier.Name, "budget_exhausted"));
                    continue;
                }

                var tierCost = items.Sum(item => ContextAssembler.EstimateTokens(item));
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
}
