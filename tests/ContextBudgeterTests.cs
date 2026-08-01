using Lurp.Workspace;

namespace Lurp.Storage.Tests;

public sealed class ContextBudgeterTests
{
    [Fact]
    public void BudgetBelowAnchor_RecordsEveryTierAndReason()
    {
        var capsule = Capsule();
        var tiers = new IContextTierBuilder[]
        {
            new Tier("contracts", [Item("1234")]),
            new Tier("directCallees", []),
            new Tier("directCallers", [Item("1234")]),
        };

        ContextBudgeter.Apply(capsule, tiers, budget: 1, runningTotal: 2);

        Assert.True(capsule.Truncated);
        Assert.Contains(capsule.OmittedTiers, entry => entry.Category == "contracts" && entry.Reason == "budget_exhausted");
        Assert.Contains(capsule.OmittedTiers, entry => entry.Category == "directCallees" && entry.Reason == "empty");
        Assert.Contains(capsule.OmittedTiers, entry => entry.Category == "directCallers" && entry.Reason == "budget_exhausted");
    }

    [Fact]
    public void PartialTier_UsesGreedyPrefixAndBlocksLowerPriorityTier()
    {
        var capsule = Capsule();
        var tiers = new IContextTierBuilder[]
        {
            new Tier("contracts", [Item("12345678", "first"), Item("123456789012", "second")]),
            new Tier("directCallees", [Item("1234", "lower")]),
        };

        var total = ContextBudgeter.Apply(capsule, tiers, budget: 2, runningTotal: 0);

        Assert.Equal(2, total);
        Assert.Collection(capsule.Contracts, item => Assert.Equal("first", item.SymbolId));
        Assert.Empty(capsule.DirectCallees);
        Assert.Equal(["contracts", "directCallees"], capsule.TruncatedCategories);
    }

    private static ContextCapsule Capsule()
        => new(new CapsuleAnchor("anchor", "Anchor", "Type", ""));

    private static CapsuleItem Item(string source, string id = "item")
        => new(id, "Type", id, "compiler_proved", "test", source);

    private sealed class Tier(string name, List<CapsuleItem> items) : IContextTierBuilder
    {
        public string Name => name;
        public List<CapsuleItem> Build() => items;
    }
}
