namespace Lurp.Workspace;

internal sealed class FullRebuildRequiredException : InvalidOperationException
{
    public FullRebuildRequiredException(IReadOnlyList<SnapshotMismatch> mismatches)
        : base(BuildMessage(mismatches))
    {
        if (mismatches == null || mismatches.Count == 0)
            throw new ArgumentException("At least one mismatch is required.", nameof(mismatches));
        Mismatches = mismatches;
    }

    public IReadOnlyList<SnapshotMismatch> Mismatches { get; }

    private static string BuildMessage(IReadOnlyList<SnapshotMismatch> mismatches)
    {
        var descriptions = string.Join("; ", mismatches.Select(m => m.Description));
        return $"Full rebuild required: {descriptions}";
    }
}