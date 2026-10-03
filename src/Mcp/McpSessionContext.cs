using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Mcp;

internal sealed class McpSessionContext : IAsyncDisposable
{
    private readonly object _pinLock = new();
    private string _pinnedSnapshotId;

    public string PinnedSnapshotId
    {
        get { lock (_pinLock) return _pinnedSnapshotId; }
    }

    public string DbPath { get; }
    public string OutputDir { get; }
    public string? SolutionPath { get; }

    private McpSessionContext(string pinnedSnapshotId, string dbPath, string outputDir, string? solutionPath)
    {
        _pinnedSnapshotId = pinnedSnapshotId;
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

        HandlerBootstrap.RequireCurrentSchemaVersion(dbPath);

        string snapshotId;
        FreshnessStamp stamp;
        using (var store = new SqliteIndexStore(dbPath))
        {
            store.OpenReadOnly();
            try
            {
                var latest = store.GetLatestSnapshotId();
                if (latest == null)
                    throw new CliExitException("ERROR: No snapshots found in the database.", 1);

                snapshotId = latest;
                stamp = WorkspaceFreshness.CheckFreshnessCheap(store, store, snapshotId, FreshnessMode.Auto);
            }
            finally
            {
                store.Close();
            }
        }

        // Only sanctioned console call in src/Mcp/** — before stdio handshake.
        var shortId = snapshotId.Length > 12 ? snapshotId[..12] : snapshotId;
        Console.Error.WriteLine($"mcp: pinned snapshot {shortId} at {stamp.CheckedAtUtc:O} freshness:{stamp.State}");

        return new McpSessionContext(snapshotId, dbPath, outputDir, solutionPath);
    }

    /// <summary>
    ///     Opens a fresh read-only connection for one tool call. Snapshots are
    ///     immutable, so every concurrent call can read its own pinned snapshot
    ///     without sharing — or closing — another call's connection.
    /// </summary>
    public SqliteIndexStore OpenReadStore()
    {
        var store = new SqliteIndexStore(DbPath);
        store.OpenReadOnly();
        return store;
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
        using var store = OpenReadStore();
        return GetFreshness(store, PinnedSnapshotId);
    }

    public static FreshnessStamp GetFreshness(SqliteIndexStore store, string snapshotId)
    {
        return WorkspaceFreshness.CheckFreshnessCheap(store, store, snapshotId, FreshnessMode.Auto);
    }

    public object GetFreshnessJson(int maxDocuments = 10)
    {
        using var store = OpenReadStore();
        return GetFreshnessJson(store, PinnedSnapshotId, maxDocuments);
    }

    public static object GetFreshnessJson(SqliteIndexStore store, string snapshotId, int maxDocuments = 10)
    {
        return GetFreshnessJsonInternal(GetFreshness(store, snapshotId), maxDocuments);
    }

    internal static object GetFreshnessJsonWithStamp(FreshnessStamp stamp, int maxDocuments)
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
        // Read the newest committed snapshot through a fresh connection: the session
        // owns no store, and a WAL reader observes the latest committed state.
        try
        {
            HandlerBootstrap.RequireCurrentSchemaVersion(DbPath);
            using var store = OpenReadStore();
            return store.GetLatestSnapshotId();
        }
        catch (CliExitException ex)
        {
            throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
        }
    }

    public void AdvancePin(string newSnapshotId)
    {
        if (string.Equals(newSnapshotId, PinnedSnapshotId, StringComparison.Ordinal))
            return;

        // Guard against advancing into a database whose schema was swapped out
        // under this session: the pinned id must never point into a store this
        // build cannot read.
        HandlerBootstrap.RequireCurrentSchemaVersion(DbPath);

        lock (_pinLock)
        {
            _pinnedSnapshotId = newSnapshotId;
        }
    }

    public ValueTask DisposeAsync()
    {
        // Every tool call opens and closes its own connection; the session owns none.
        return ValueTask.CompletedTask;
    }
}
