namespace Lurp.Workspace;

/// <summary>
///     The single owner for the freshness object in CLI and MCP output (B22).
///     <see cref="Cheap" /> keeps the eight-key shape the read-only surfaces have
///     always emitted; the two <see cref="ForStatus" /> overloads emit the twelve-key
///     shape <c>lurp_status</c> uses, with every key always present.
/// </summary>
internal static class FreshnessJson
{
    public static object Cheap(FreshnessStamp stamp, int maxDocuments = 50)
    {
        var sample = CapSample(stamp.ChangedDocumentsSample, maxDocuments, out var truncated);
        return new
        {
            state = stamp.State,
            method = stamp.Method,
            changed_document_count = stamp.ChangedDocumentCount,
            changed_documents_sample = sample,
            changed_documents_sample_truncated = truncated ? true : (bool?)null,
            checked_at_utc = stamp.CheckedAtUtc,
            snapshot_id = stamp.SnapshotId,
            scope = stamp.Scope
        };
    }

    public readonly record struct CappedMismatches(IReadOnlyList<object> Rows, bool Truncated, int Total);

    public static CappedMismatches CapMismatches(IReadOnlyList<SnapshotMismatch> mismatches, int max)
    {
        var rows = mismatches
            .Take(max)
            .Select(m => (object)new
            {
                kind = m.Kind.ToString(),
                description = m.Description,
                document = m.Document?.ToString(),
                detail = m.Detail
            })
            .ToList();
        return new CappedMismatches(rows, mismatches.Count > rows.Count, mismatches.Count);
    }

    public static Dictionary<string, object?> ForStatus(FreshnessStamp stamp, int maxDocuments)
    {
        var sample = CapSample(stamp.ChangedDocumentsSample, maxDocuments, out var truncated);
        return Build(
            stamp.State,
            stamp.Method,
            stamp.Scope,
            stamp.SnapshotId,
            stamp.CheckedAtUtc,
            stamp.ChangedDocumentCount,
            sample,
            truncated,
            IsFresh(stamp.State),
            null,
            null,
            null);
    }

    public static Dictionary<string, object?> ForStatus(string snapshotId, WorkspaceFreshness.FreshnessResult result, DateTime checkedAtUtc, int maxDocuments, int maxMismatches, bool includeMismatches)
    {
        var documents = result.Mismatches
            .Where(m => m.Document != null)
            .Select(m => m.Document!.Value.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        List<string>? sample;
        bool sampleTruncated;
        if (includeMismatches && result.Mismatches.Count > 0)
        {
            // The sample is redundant when the mismatch rows themselves are present.
            sample = null;
            sampleTruncated = false;
        }
        else
        {
            sample = CapSample(documents, maxDocuments, out sampleTruncated);
        }

        var capped = CapMismatches(result.Mismatches, maxMismatches);
        return Build(
            result.IsFresh ? "fresh" : "stale",
            "full",
            "full",
            snapshotId,
            checkedAtUtc,
            documents.Count,
            sample,
            sampleTruncated,
            result.IsFresh,
            includeMismatches ? capped.Rows : null,
            includeMismatches && capped.Truncated ? true : (bool?)null,
            result.Mismatches.Count);
    }

    private static List<string> CapSample(IReadOnlyList<string> full, int max, out bool truncated)
    {
        var capped = full.Take(max).ToList();
        truncated = full.Count > capped.Count;
        return capped;
    }

    private static bool? IsFresh(string state)
    {
        return state switch
        {
            "fresh" => true,
            "stale" => false,
            _ => null
        };
    }

    private static Dictionary<string, object?> Build(
        string state,
        string method,
        string scope,
        string snapshotId,
        DateTime checkedAtUtc,
        int changedDocumentCount,
        IReadOnlyList<string>? sample,
        bool sampleTruncated,
        bool? isFresh,
        object? mismatches,
        object? mismatchesTruncated,
        object? mismatchesTotal)
    {
        return new Dictionary<string, object?>
        {
            ["state"] = state,
            ["method"] = method,
            ["scope"] = scope,
            ["snapshot_id"] = snapshotId,
            ["checked_at_utc"] = checkedAtUtc,
            ["changed_document_count"] = changedDocumentCount,
            ["changed_documents_sample"] = sample,
            ["changed_documents_sample_truncated"] = sampleTruncated ? true : (bool?)null,
            ["is_fresh"] = isFresh,
            ["mismatches"] = mismatches,
            ["mismatches_truncated"] = mismatchesTruncated,
            ["mismatches_total"] = mismatchesTotal
        };
    }
}
