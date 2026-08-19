namespace Lurp.Storage;

/// <summary>
///     Opaque keyset-pagination cursor for diagnostics. Snapshots are immutable, so
///     a cursor into one stays valid indefinitely. The fingerprint binds the cursor
///     to the exact request that produced it — project/document/severity/id/snapshot
///     must match, otherwise resuming would silently reorder or reinterpret.
///     Follows the SearchCursor / OutlineCursor / AnnotationCursor keyset idiom
///     (see six-gaps-in-lurp.md: diagnostics ordered by diagnostic_id).
/// </summary>
public sealed record DiagnosticsCursor(string SnapshotId, string Fingerprint, long LastDiagnosticId)
{
    public static string ComputeFingerprint(string? projectName, string? documentPath, string? severity, bool excludeHidden, string? id)
    {
        return $"{projectName ?? string.Empty}\u0001{documentPath ?? string.Empty}\u0001{severity ?? string.Empty}\u0001{excludeHidden}\u0001{id ?? string.Empty}";
    }

    public string Encode()
    {
        return CursorUtils.EncodeBase64(this);
    }

    public static DiagnosticsCursor? TryDecode(string encoded)
    {
        return CursorUtils.TryDecodeBase64<DiagnosticsCursor>(encoded);
    }

    public void Validate(string snapshotId, string fingerprint)
    {
        if (!string.Equals(SnapshotId, snapshotId, StringComparison.Ordinal))
            throw new ArgumentException($"Cursor was issued for snapshot '{SnapshotId}', not '{snapshotId}'.");
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Cursor does not match the current project/document/severity/id filters; request a fresh page instead of resuming with a different query.");
    }
}

public sealed class DiagnosticsPage(List<DiagnosticEntry> items, string? nextCursor, int totalCount)
{
    public List<DiagnosticEntry> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
    public int TotalCount { get; } = totalCount;
}
