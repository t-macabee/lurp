using Lurp.Handlers;
using Lurp.Storage;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Contract tests for the kind-aware capsule budget default: a type anchor's
/// callee/caller tiers scale with member fan-out, so the default budget is
/// larger for type anchors than for member anchors. The default only applies
/// when the caller omits --budget=; an explicit budget is always honored as-is.
/// </summary>
public sealed class ContextDefaultBudgetTests
{
    [Theory]
    [InlineData("T:Acme.Widget|asm", 16000)]
    [InlineData("M:Acme.Widget.Run|asm", 8000)]
    [InlineData("F:Acme.Widget._field|asm", 8000)]
    [InlineData("P:Acme.Widget.Value|asm", 8000)]
    public void DefaultBudgetFor_String_RoutesOnDocCommentPrefix(string symbolArg, int expected)
    {
        Assert.Equal(expected, ContextHandler.DefaultBudgetFor(symbolArg));
    }

    [Fact]
    public void DefaultBudgetFor_NullString_FallsBackToMemberBudget()
    {
        Assert.Equal(8000, ContextHandler.DefaultBudgetFor((string?)null));
    }

    [Theory]
    [InlineData("T:Acme.Widget|asm", 16000)]
    [InlineData("M:Acme.Widget.Run|asm", 8000)]
    public void DefaultBudgetFor_SymbolId_DelegatesToIsType(string symbolId, int expected)
    {
        Assert.Equal(expected, ContextHandler.DefaultBudgetFor(SymbolId.Parse(symbolId)));
    }

    [Theory]
    [InlineData("T:Acme.Widget|asm", true)]
    [InlineData("M:Acme.Widget.Run|asm", false)]
    [InlineData("P:Acme.Widget.Value|asm", false)]
    [InlineData("F:Acme.Widget._field|asm", false)]
    public void IsType_ReflectsDocCommentPrefix(string symbolId, bool expected)
    {
        Assert.Equal(expected, SymbolId.Parse(symbolId).IsType);
    }
}
