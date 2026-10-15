using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class FqnPartsTests
{
    [Theory]
    [InlineData("A.B.C", "A.B.C", null)]
    [InlineData("A.B.C", "A.B.D", SymbolTransitionKind.Rename)]
    [InlineData("A.B.C", "A.X.C", SymbolTransitionKind.Move)]
    [InlineData("A.B.C", "A.X.D", SymbolTransitionKind.RenameAndMove)]
    public void ClassifyRenameMove_ReturnsExpectedOutcome(string previous, string current, SymbolTransitionKind? expected)
    {
        Assert.Equal(expected, FqnParts.ClassifyRenameMove(previous, current));
    }

    [Fact]
    public void SimpleNameAndContainer_SplitNestedGenericFqn()
    {
        const string fqn = "global::N.Outer<T>.Inner.M";

        Assert.Equal("M", FqnParts.SimpleName(fqn));
        Assert.Equal("global::N.Outer<T>.Inner", FqnParts.Container(fqn));
    }
}
