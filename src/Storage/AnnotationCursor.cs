namespace Lurp.Storage;

/// <summary>
///     Opaque keyset-pagination cursor for annotations. Snapshots are immutable, so
///     a cursor into one stays valid indefinitely. The fingerprint binds the cursor
///     to the exact request that produced it — symbol/document/kind/snapshot must
///     match, otherwise resuming would silently reorder or reinterpret.
///     Follows the SearchCursor / OutlineCursor keyset idiom (see six-gaps-in-lurp.md
///     Gap 2 &amp; implementation-order decision: annotations ordered by annotation_id).
/// </summary>
public sealed record AnnotationCursor(string SnapshotId, string Fingerprint, long LastAnnotationId)
{
    public static string ComputeFingerprint(string? symbolId, string? documentPath, string? kind)
    {
        return $"{symbolId ?? string.Empty}\u0001{documentPath ?? string.Empty}\u0001{kind ?? string.Empty}";
    }

    public string Encode()
    {
        return CursorUtils.EncodeBase64(this);
    }

    public static AnnotationCursor? TryDecode(string encoded)
    {
        return CursorUtils.TryDecodeBase64<AnnotationCursor>(encoded);
    }

    public void Validate(string snapshotId, string fingerprint)
    {
        if (!string.Equals(SnapshotId, snapshotId, StringComparison.Ordinal))
            throw new ArgumentException($"Cursor was issued for snapshot '{SnapshotId}', not '{snapshotId}'.");
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Cursor does not match the current snapshot/symbol/document/kind; request a fresh page instead of resuming with a different query.");
    }
}

public sealed class AnnotationPage(List<AnnotationRecord> items, string? nextCursor, int totalCount)
{
    public List<AnnotationRecord> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
    public int TotalCount { get; } = totalCount;
}
