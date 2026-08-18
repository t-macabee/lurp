namespace Lurp.Storage;

/// <summary>
///     Opaque keyset-pagination cursor for the declaration outline. Snapshots are immutable, so a
///     cursor into one stays valid indefinitely. The sequence comes from a SQL ORDER BY on
///     (full_start, symbol_id) whose sort key can be pushed back into the next query, so a
///     keyset cursor is appropriate (see six-gaps-in-lurp.md Gap 3 decision). Offset would
///     also be sound over an immutable snapshot, but keyset avoids phantom/duplicate hazards
///     if the store ever reorders under duplicate full_start values and matches the
///     SearchCursor idiom.
/// </summary>
public sealed record OutlineCursor(string SnapshotId, string Fingerprint, int LastFullStart, string LastSymbolId)
{
    public static string ComputeFingerprint(string document, bool includeGenerated)
    {
        return $"{document}\u0001{includeGenerated}";
    }

    public string Encode()
    {
        return CursorUtils.EncodeBase64(this);
    }

    public static OutlineCursor? TryDecode(string encoded)
    {
        return CursorUtils.TryDecodeBase64<OutlineCursor>(encoded);
    }

    public void Validate(string snapshotId, string fingerprint)
    {
        if (!string.Equals(SnapshotId, snapshotId, StringComparison.Ordinal))
            throw new ArgumentException($"Cursor was issued for snapshot '{SnapshotId}', not '{snapshotId}'.");
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Cursor does not match the current document/includeGenerated; request a fresh page instead of resuming with a different query.");
    }
}

public sealed class DeclarationOutlinePage(List<DeclarationOutlineEntry> items, string? nextCursor, int totalCount)
{
    public List<DeclarationOutlineEntry> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
    public int TotalCount { get; } = totalCount;
}

public sealed record DeclarationOutlineEntry(
    string SymbolId,
    string Kind,
    string? FullyQualifiedName,
    int StartLine,
    int EndLine,
    int? SignatureStartLine,
    int? NameStartLine,
    bool IsPartial,
    bool IsGenerated,
    int FullStart);
