namespace Lurp.Storage;

/// <summary>
/// Opaque continuation cursor for a deterministically-ordered result sequence that is
/// recomputed in memory on every request (impact paths, context-capsule tier items).
///
/// Deliberately NOT the same shape as <see cref="SearchCursor"/>. That one is a keyset
/// cursor because its sequence comes from a SQL <c>ORDER BY</c> whose sort key can be
/// pushed back into the next query. These sequences have no such key: they are produced
/// by graph traversal and tier-builder relevance ordering, so the continuation is an
/// offset.
///
/// An offset is sound here for exactly one reason, and it is worth stating because it
/// does not generalize: the sequence is derived from an immutable snapshot by a
/// deterministic computation, so offset N addresses the same element on every request.
/// There are no phantom reads and no skipped rows. Over mutable state an offset cursor
/// would silently drop or duplicate elements : do not reuse this type for one.
/// </summary>
public sealed record SequenceCursor(string SnapshotId, string Fingerprint, string Kind, int Offset)
{
    private const string FingerprintSeparator = "\u0001";

    /// <summary>
    /// Binds a cursor to the exact request that produced it. Resuming a cursor against a
    /// different symbol, direction, depth, or filter would reinterpret the offset against
    /// a different sequence, so the mismatch is rejected rather than guessed at.
    /// </summary>
    public static string ComputeFingerprint(params string?[] parts)
        => string.Join(FingerprintSeparator, parts.Select(static part => part ?? string.Empty));

    public string Encode() => CursorUtils.EncodeBase64(this);

    public static SequenceCursor? TryDecode(string encoded)
    {
        var cursor = CursorUtils.TryDecodeBase64<SequenceCursor>(encoded);
        return cursor is { Offset: >= 0 } ? cursor : null;
    }

    /// <summary>
    /// Throws when the cursor does not describe this request. Returning wrong rows
    /// silently is the failure this guards against.
    /// </summary>
    public void Validate(string snapshotId, string fingerprint, string kind)
    {
        if (!string.Equals(Kind, kind, StringComparison.Ordinal))
            throw new ArgumentException($"Cursor was issued for '{Kind}', not '{kind}'.");
        if (!string.Equals(SnapshotId, snapshotId, StringComparison.Ordinal))
            throw new ArgumentException($"Cursor was issued for snapshot '{SnapshotId}', not '{snapshotId}'.");
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Cursor was issued for a different request; re-run without --cursor.");
    }
}
