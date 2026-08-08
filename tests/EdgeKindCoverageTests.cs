using Lurp.Storage;

namespace Lurp.Storage.Tests;

public sealed class EdgeKindCoverageTests
{
    [Fact]
    public void EdgeRecord_Is_MatchesAllEnumValues()
    {
        foreach (EdgeKind kind in Enum.GetValues<EdgeKind>())
        {
            var record = new EdgeRecord { Kind = kind.ToString() };
            Assert.True(record.Is(kind), $"Kind '{kind}' ({record.Kind}) should match via Is()");
        }
    }

    [Fact]
    public void EdgeRecord_Is_RejectsWrongValue()
    {
        var record = new EdgeRecord { Kind = EdgeKind.Calls.ToString() };
        Assert.False(record.Is(EdgeKind.Inherits));
    }

    [Fact]
    public void EdgeRecord_Is_CaseSensitive()
    {
        var record = new EdgeRecord { Kind = EdgeKind.Calls.ToString().ToLowerInvariant() };
        Assert.False(record.Is(EdgeKind.Calls));
    }
}
