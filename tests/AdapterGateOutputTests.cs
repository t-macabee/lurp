using Lurp.Adapters;
using Lurp.Storage;

namespace Lurp.Tests;

[Trait("Category", "Slow")]
[Collection("GroundTruth")]
public sealed class AdapterGateOutputTests(ExtractorGateFixture gate)
{
    [Fact]
    public void AspNetCoreAdapter_WritesRoutesAndMiddlewareEdges()
    {
        var adapterVersion = new AspNetCoreAdapter().Version;
        var kinds = gate.Results
            .SelectMany(result => result.Edges)
            .Where(edge => string.Equals(edge.ExtractorVersion, adapterVersion, StringComparison.Ordinal))
            .Select(edge => edge.Kind)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal([nameof(EdgeKind.RoutesTo), nameof(EdgeKind.Constructs), nameof(EdgeKind.Calls)], kinds);
    }
}
