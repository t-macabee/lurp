namespace Lurp.Storage;

/// <summary>Opaque cursor carrying the complete symbol-search sort key and its request binding.</summary>
public sealed record SearchCursor(string SnapshotId, string Fingerprint, int Tier, int IsTest,
    int KindRank, int LastRank, string LastFqn, string LastSymbolId)
{
    public static string ComputeFingerprint(string query, string? kind, bool includeGenerated)
    {
        return CursorUtils.JoinFingerprint("weighted", query, kind, includeGenerated);
    }

    public string Encode()
    {
        return CursorUtils.EncodeBase64(this);
    }

    public static SearchCursor? TryDecode(string encoded)
    {
        return CursorUtils.TryDecodeBase64<SearchCursor>(encoded);
    }

    public void Validate(string snapshotId, string fingerprint)
    {
        CursorUtils.RequireBinding(SnapshotId, Fingerprint, snapshotId, fingerprint,
            "Cursor does not match the current snapshot/query/kind/includeGenerated; request a fresh page instead of resuming with a different query.");
        if (Tier is < 1 or > 4 || IsTest is < 0 or > 1 || KindRank < 0 || LastRank < 0 ||
            LastFqn is null || string.IsNullOrEmpty(LastSymbolId))
            throw new ArgumentException("Cursor carries an invalid symbol search key.");
    }
}

public sealed class SymbolSearchPage(List<SymbolSearchResult> items, string? nextCursor)
{
    public List<SymbolSearchResult> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
}
