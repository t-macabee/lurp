using System.Text.Json;

namespace Lurp.Storage;

/// <summary>
/// Opaque keyset-pagination cursor for dead candidates. Mirrors DiagnosticsCursor / OutlineCursor idiom.
/// Snapshot immutability makes cursor indefinitely valid. Fingerprint binds to exact filter set.
/// </summary>
public sealed record DeadCandidateCursor(string SnapshotId, string Fingerprint, string LastSymbolId)
{
    public static string ComputeFingerprint(string? project, string? document, string? kind, bool includePublic, bool includeGenerated, bool includeTests)
    {
        return $"{project ?? string.Empty}\u0001{document ?? string.Empty}\u0001{kind ?? string.Empty}\u0001{includePublic}\u0001{includeGenerated}\u0001{includeTests}";
    }

    public string Encode()
    {
        return CursorUtils.EncodeBase64(this);
    }

    public static DeadCandidateCursor? TryDecode(string encoded)
    {
        return CursorUtils.TryDecodeBase64<DeadCandidateCursor>(encoded);
    }

    public void Validate(string snapshotId, string fingerprint)
    {
        if (!string.Equals(SnapshotId, snapshotId, StringComparison.Ordinal))
            throw new ArgumentException($"Cursor was issued for snapshot '{SnapshotId}', not '{snapshotId}'.");
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Cursor does not match the current project/document/kind/include_* filters; request a fresh page instead of resuming with a different query.");
    }
}

public sealed class DeadCandidatePage
{
    public DeadCandidatePage(List<DeadCandidateEntry> candidates, string? nextCursor, int candidateCount, int deadCount, int uncertainCount, int unresolvedCount)
    {
        Candidates = candidates;
        NextCursor = nextCursor;
        CandidateCount = candidateCount;
        DeadCount = deadCount;
        UncertainCount = uncertainCount;
        UnresolvedCount = unresolvedCount;
    }

    public List<DeadCandidateEntry> Candidates { get; }
    public string? NextCursor { get; }
    public int CandidateCount { get; }
    public int DeadCount { get; }
    public int UncertainCount { get; }
    public int UnresolvedCount { get; }
}
