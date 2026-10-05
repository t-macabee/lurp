using Lurp.Parity.Shared;

namespace Lurp.Tests;

public sealed class MethodSamplerTests
{
    [Fact]
    public void Sample_SameSeed_IsDeterministic()
    {
        var candidates = Candidates(200);
        var first = MethodSampler.Sample(candidates, seed: 12345, sampleSize: 40);
        var second = MethodSampler.Sample(candidates, seed: 12345, sampleSize: 40);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Sample_DifferentSeed_Differs()
    {
        var candidates = Candidates(1000);
        var first = MethodSampler.Sample(candidates, seed: 1, sampleSize: 100);
        var second = MethodSampler.Sample(candidates, seed: 2, sampleSize: 100);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Sample_InputOrderDoesNotMatter()
    {
        var candidates = Candidates(200);
        var reversed = Enumerable.Reverse(candidates).ToList();
        var first = MethodSampler.Sample(candidates, seed: 7, sampleSize: 50);
        var second = MethodSampler.Sample(reversed, seed: 7, sampleSize: 50);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Sample_SizeAtLeastCount_ReturnsAllInOrdinalOrder()
    {
        var candidates = Candidates(25);
        var expected = candidates.OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, MethodSampler.Sample(candidates, seed: 3, sampleSize: 25));
        Assert.Equal(expected, MethodSampler.Sample(candidates, seed: 3, sampleSize: 26));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sample_SizeZeroOrNegative_Throws(int sampleSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MethodSampler.Sample(Candidates(5), seed: 0, sampleSize: sampleSize));
    }

    private static List<string> Candidates(int count)
    {
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
            list.Add($"M:Lurp.Fixture.Type.Method{i:D5}");
        return list;
    }
}
