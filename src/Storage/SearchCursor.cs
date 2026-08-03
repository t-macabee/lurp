using System.Text;
using System.Text.Json;

namespace Lurp.Storage;

/// <summary>
/// Opaque keyset-pagination cursor for symbol search. Snapshots are immutable, so a
/// cursor into one stays valid indefinitely and does not need to encode a timestamp.
/// The fingerprint binds the cursor to the exact request that produced it — resuming
/// with a different query, kind, snapshot, or generated-flag would silently reorder
/// or reinterpret the keyset, so that mismatch is rejected rather than guessed at.
/// </summary>
public sealed record SearchCursor(string SnapshotId, string Fingerprint, string Mode, double? LastRank, string LastFqn, string LastSymbolId)
{
    public static string ComputeFingerprint(string query, string? kind, bool includeGenerated) =>
        $"{query}{kind}{includeGenerated}";

    public string Encode()
    {
        var json = JsonSerializer.Serialize(this);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static SearchCursor? TryDecode(string encoded)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonSerializer.Deserialize<SearchCursor>(json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}

public sealed class SymbolSearchPage
{
    public List<SymbolSearchResult> Items { get; }
    public string? NextCursor { get; }

    public SymbolSearchPage(List<SymbolSearchResult> items, string? nextCursor)
    {
        Items = items;
        NextCursor = nextCursor;
    }
}
