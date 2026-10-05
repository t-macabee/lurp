namespace Lurp.Parity.Shared;

/// <summary>
///     Deterministic, order-independent sampling of candidate method ids.
///     Candidates are sorted first with <see cref="StringComparer.Ordinal" /> on
///     the full id, so the result depends only on the id set, the seed, and the
///     sample size — never on enumeration order, hash codes, or culture.
///     The PRNG is SplitMix64 (Steele, Lea &amp; Flood, 2014): a 64-bit state
///     advanced by the golden-gamma constant and two fixed xor-shift multiplies.
///     Its sequence is fully specified by <c>unchecked</c> integer arithmetic,
///     which the C# language guarantees on every runtime, unlike
///     <c>System.Random</c> whose algorithm is deliberately unspecified.
/// </summary>
public static class MethodSampler
{
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    /// <summary>
    ///     Returns a deterministic sample of at most <paramref name="sampleSize" />
    ///     ids. When <paramref name="sampleSize" /> is at least the candidate
    ///     count, every candidate is returned in ordinal order.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="candidates" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleSize" /> is less than one.</exception>
    public static IReadOnlyList<string> Sample(IReadOnlyList<string> candidates, int seed, int sampleSize)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (sampleSize < 1)
            throw new ArgumentOutOfRangeException(nameof(sampleSize), sampleSize, "Sample size must be at least one.");

        var sorted = candidates.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);

        if (sampleSize >= sorted.Length)
            return sorted;

        var state = unchecked((ulong)seed);
        for (var i = 0; i < sampleSize; i++)
        {
            var j = i + (int)(NextUInt64(ref state) % (ulong)(sorted.Length - i));
            (sorted[i], sorted[j]) = (sorted[j], sorted[i]);
        }

        return sorted[..sampleSize];
    }

    /// <summary>SplitMix64: advance the state once and return the next 64-bit value.</summary>
    private static ulong NextUInt64(ref ulong state)
    {
        unchecked
        {
            state += GoldenGamma;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
