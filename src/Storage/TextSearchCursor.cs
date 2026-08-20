namespace Lurp.Storage;

/// <summary>
///     Opaque keyset-pagination cursor for literal text search. Snapshots are immutable, so a
///     cursor into one stays valid indefinitely. The fingerprint binds the cursor to the exact
///     request that produced it: query, includeGenerated, and case-sensitivity must match,
///     otherwise resuming would silently reorder or reinterpret the result set.
///     Ordering is deterministic: document_path ascending, then byte offset ascending.
/// </summary>
public sealed record TextSearchCursor(string SnapshotId, string Fingerprint, string LastDocumentPath, int LastOffset)
{
    public static string ComputeFingerprint(string query, bool includeGenerated, bool ignoreCase)
    {
        return $"{query}\u0001{includeGenerated}\u0001{ignoreCase}";
    }

    public string Encode()
    {
        return CursorUtils.EncodeBase64(this);
    }

    public static TextSearchCursor? TryDecode(string encoded)
    {
        return CursorUtils.TryDecodeBase64<TextSearchCursor>(encoded);
    }

    public void Validate(string snapshotId, string fingerprint)
    {
        if (!string.Equals(SnapshotId, snapshotId, StringComparison.Ordinal))
            throw new ArgumentException($"Cursor was issued for snapshot '{SnapshotId}', not '{snapshotId}'.");
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Cursor does not match the current snapshot/query/includeGenerated/ignoreCase; request a fresh page instead of resuming with a different query.");
    }
}

public sealed class TextSearchPage(List<TextSearchResult> items, string? nextCursor, int totalCount)
{
    public List<TextSearchResult> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
    public int TotalCount { get; } = totalCount;
}
