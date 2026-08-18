using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Mcp;

internal sealed class McpSessionContext : IAsyncDisposable
{
    public SqliteIndexStore Store { get; private set; }
    public string PinnedSnapshotId { get; private set; }
    public FreshnessStamp FreshnessStamp { get; private set; }
    public string DbPath { get; }
    public string OutputDir { get; }
    public string? SolutionPath { get; }

    private McpSessionContext(SqliteIndexStore store, string pinnedSnapshotId, FreshnessStamp freshnessStamp, string dbPath, string outputDir, string? solutionPath)
    {
        Store = store;
        PinnedSnapshotId = pinnedSnapshotId;
        FreshnessStamp = freshnessStamp;
        DbPath = dbPath;
        OutputDir = outputDir;
        SolutionPath = solutionPath;
    }

    public static McpSessionContext Create(string[] args)
    {
        var outputDir = HandlerBootstrap.ResolveOutputDir(args);
        var dbPath = HandlerBootstrap.ResolveDbPath(outputDir);
        var solutionPath = HandlerBootstrap.GetArgValue(args, "--solution=") ?? Environment.GetEnvironmentVariable("LURP_SOLUTION_PATH");
        // Normalize solution path if present
        if (!string.IsNullOrEmpty(solutionPath))
        {
            try { solutionPath = Path.GetFullPath(solutionPath); } catch { /* keep raw */ }
        }
        else
        {
            solutionPath = null;
        }

        var store = HandlerBootstrap.OpenStore(dbPath);
        store.EnableQueryOnly();

        var snapshotId = store.GetLatestSnapshotId();
        if (snapshotId == null)
            throw new CliExitException("ERROR: No snapshots found in the database.", 1);

        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, store, snapshotId!, FreshnessMode.Auto);

        // Only sanctioned console call in src/Mcp/** — before stdio handshake.
        var shortId = snapshotId.Length > 12 ? snapshotId[..12] : snapshotId;
        Console.Error.WriteLine($"mcp: pinned snapshot {shortId} at {stamp.CheckedAtUtc:O} freshness:{stamp.State}");

        return new McpSessionContext(store, snapshotId, stamp, dbPath, outputDir, solutionPath);
    }

    /// <summary>
    ///     Validates that <paramref name="requestedSnapshotId"/> matches the pinned snapshot.
    ///     Returns the pinned snapshot id when valid, otherwise throws <see cref="McpProtocolException"/> with <c>InvalidParams</c>.
    ///     While the session is pinned, per-call <c>latest</c> is disabled: callers must either omit <c>snapshot_id</c>
    ///     or pass the pinned id; to advance, call <c>lurp_refresh</c> with an ack. CLI mode outside <c>serve</c> keeps its old behavior.
    /// </summary>
    public string RequirePinnedSnapshot(string? requestedSnapshotId)
    {
        if (!string.IsNullOrEmpty(requestedSnapshotId) && !string.Equals(requestedSnapshotId, PinnedSnapshotId, StringComparison.Ordinal))
            throw new McpProtocolException($"snapshot mismatch: session pinned to {PinnedSnapshotId}; call lurp_refresh to advance.", McpErrorCode.InvalidParams);
        return PinnedSnapshotId;
    }

    public FreshnessStamp GetFreshness()
    {
        return WorkspaceFreshness.CheckFreshnessCheap(Store, Store, PinnedSnapshotId, FreshnessMode.Auto);
    }

    public object GetFreshnessJson(int maxDocuments = 10)
    {
        return GetFreshnessJsonInternal(GetFreshness(), maxDocuments);
    }

    internal object GetFreshnessJsonUncapped()
    {
        // Kept for backward compat: uncapped is now just a large maxDocuments
        return GetFreshnessJson(int.MaxValue);
    }

    internal FreshnessStamp GetFreshnessUncappedStamp()
    {
        // Full list is now returned by GetFreshness directly (no cap)
        return GetFreshness();
    }

    internal object GetFreshnessJsonWithStamp(FreshnessStamp stamp, int maxDocuments)
    {
        return GetFreshnessJsonInternal(stamp, maxDocuments);
    }

    private static object GetFreshnessJsonInternal(FreshnessStamp stamp, int maxDocuments)
    {
        var fullSample = stamp.ChangedDocumentsSample;
        var cappedSample = fullSample.Take(maxDocuments).ToList();
        var truncated = fullSample.Count > cappedSample.Count;
        return new
        {
            state = stamp.State,
            method = stamp.Method,
            changed_document_count = stamp.ChangedDocumentCount,
            changed_documents_sample = cappedSample,
            changed_documents_sample_truncated = truncated ? true : (bool?)null,
            checked_at_utc = stamp.CheckedAtUtc,
            snapshot_id = stamp.SnapshotId,
            scope = stamp.Scope
        };
    }

    public string? GetLatestSnapshotId()
    {
        // Query the current store; if a concurrent writer added a snapshot, we should see it.
        // If the single connection is stale due to WAL snapshot isolation, the caller (RefreshTool)
        // will handle reopening on ack. For the no-ack path we attempt a fresh temporary connection
        // to ensure we observe the latest committed snapshot without moving the pin.
        var latest = Store.GetLatestSnapshotId();
        if (latest != null)
        {
            // Also probe via a temporary store to detect a newer snapshot that the pinned
            // connection hasn't observed yet (WAL). This keeps lurp_refresh {} accurate
            // without requiring the pin to move.
            try
            {
                var tmp = new SqliteIndexStore(DbPath);
                tmp.Open();
                try
                {
                    var tmpLatest = tmp.GetLatestSnapshotId();
                    if (tmpLatest != null && !string.Equals(tmpLatest, latest, StringComparison.Ordinal))
                    {
                        // Prefer the fresher value from the new connection.
                        latest = tmpLatest;
                    }
                }
                finally
                {
                    tmp.Close();
                }
            }
            catch
            {
                // Fall back to the pinned connection's view.
            }
        }
        return latest;
    }

    public void AdvancePin(string newSnapshotId)
    {
        if (string.Equals(newSnapshotId, PinnedSnapshotId, StringComparison.Ordinal))
            return;

        // Close the old store (query_only connection) and reopen to observe the new snapshot row.
        Store.Close();
        var newStore = new SqliteIndexStore(DbPath);
        newStore.Open();
        newStore.EnableQueryOnly();
        // Verify the new snapshot exists
        var verified = newStore.GetLatestSnapshotId();
        // If verification fails, keep the new store anyway; the pin will be the requested id.
        Store = newStore;
        PinnedSnapshotId = newSnapshotId;
        // Recompute freshness stamp for the new pin
        FreshnessStamp = WorkspaceFreshness.CheckFreshnessCheap(Store, Store, PinnedSnapshotId, FreshnessMode.Auto);
    }

    public ValueTask DisposeAsync()
    {
        Store.Close();
        return ValueTask.CompletedTask;
    }
}
