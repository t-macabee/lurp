using System.Text;
using System.Text.Json;

namespace Lurp.Storage;

/// <summary>
/// Opaque keyset-pagination cursor for symbol search. Snapshots are immutable, so a
/// cursor into one stays valid indefinitely and does not need to encode a timestamp.
/// The fingerprint binds the cursor to the exact request that produced it : resuming
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

    /// <summary>
    /// Throws when the cursor does not describe this request. Parity with
    /// <see cref="SequenceCursor.Validate"/>: returning wrong rows silently is
    /// the failure this guards against. The mode check matters as much as the
    /// fingerprint : mode selects which keyset decoder reads the cursor's sort
    /// key, so an unrecognised mode would fall through to the substring decoder
    /// and reinterpret a rank-keyed cursor as an FQN-keyed one.
    /// </summary>
    public void Validate(string snapshotId, string fingerprint)
    {
        if (!string.Equals(SnapshotId, snapshotId, StringComparison.Ordinal))
            throw new ArgumentException($"Cursor was issued for snapshot '{SnapshotId}', not '{snapshotId}'.");
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException(
                "Cursor does not match the current snapshot/query/kind/includeGenerated; request a fresh page instead of resuming with a different query.");
        if (Mode is not ("fts" or "substring"))
            throw new ArgumentException($"Cursor carries an unknown search mode '{Mode}'.");
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
