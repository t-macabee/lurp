using Lurp.Handlers;
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
    public string? SolutionPath { get; }

    private McpSessionContext(string pinnedSnapshotId, string dbPath, string? solutionPath)
    {
        _pinnedSnapshotId = pinnedSnapshotId;
        DbPath = dbPath;
        SolutionPath = solutionPath;
    }

    public static McpSessionContext Create(string[] args, IOutputSink? output = null)
    {
        output ??= StderrOutputSink.Instance;
        var outputDir = HandlerBootstrap.ResolveOutputDir(args, output);
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
        string? stalePin;
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
                stalePin = store.GetStalePinnedSnapshotId();
                stamp = WorkspaceFreshness.CheckFreshnessCheap(store, snapshotId, FreshnessMode.Auto);
            }
            finally
            {
                store.Close();
            }
        }

        var shortId = snapshotId.Length > 12 ? snapshotId[..12] : snapshotId;
        var startupNotice = $"mcp: pinned snapshot {shortId} at {stamp.CheckedAtUtc:O} freshness:{stamp.State}";
        if (stalePin != null)
            startupNotice += Environment.NewLine + HandlerBootstrap.StalePinWarning(stalePin, snapshotId);
        output.WriteErrorLine(startupNotice);

        return new McpSessionContext(snapshotId, dbPath, solutionPath);
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

    public static string RunTool(Func<string> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is not (McpProtocolException or OperationCanceledException))
        {
            throw McpErrorMapper.Map(ex);
        }
    }

    public string RunReadTool(string? requestedSnapshotId, Func<SqliteIndexStore, string, string> body,
        CancellationToken cancellationToken) =>
        RunTool(() =>
        {
            var snapshotId = RequirePinnedSnapshot(requestedSnapshotId);
            using var store = OpenReadStore();
            return body(store, snapshotId);
        }, cancellationToken);

    public async Task<string> RunReadToolAsync(string? requestedSnapshotId, Func<SqliteIndexStore, string, Task<string>> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var snapshotId = RequirePinnedSnapshot(requestedSnapshotId);
            using var store = OpenReadStore();
            return await body(store, snapshotId);
        }
        catch (Exception ex) when (ex is not (McpProtocolException or OperationCanceledException))
        {
            throw McpErrorMapper.Map(ex);
        }
    }

    internal static bool IsLatestAlias(string? snapshotId) =>
        string.Equals(snapshotId, "latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Validates that <paramref name="requestedSnapshotId"/> matches the pinned snapshot.
    ///     Returns the pinned snapshot id when valid, otherwise throws <see cref="McpProtocolException"/> with <c>InvalidParams</c>.
    ///     <c>latest</c> resolves to the session pin. To advance the pin, call <c>lurp_refresh</c> with an ack.
    /// </summary>
    public string RequirePinnedSnapshot(string? requestedSnapshotId)
    {
        var pinnedSnapshotId = PinnedSnapshotId;
        if (string.IsNullOrEmpty(requestedSnapshotId) || IsLatestAlias(requestedSnapshotId) ||
            string.Equals(requestedSnapshotId, pinnedSnapshotId, StringComparison.Ordinal))
            return pinnedSnapshotId;

        using (var store = OpenReadStore())
        {
            if (store.LoadSnapshotMetadata(requestedSnapshotId) == null)
                throw new McpProtocolException($"snapshot '{requestedSnapshotId}' not found.", McpErrorCode.InvalidParams);
        }

        throw new McpProtocolException($"snapshot mismatch: session pinned to {pinnedSnapshotId}; call lurp_refresh to advance.", McpErrorCode.InvalidParams);
    }

    public FreshnessStamp GetFreshness()
    {
        using var store = OpenReadStore();
        return GetFreshness(store, PinnedSnapshotId);
    }

    public static FreshnessStamp GetFreshness(SqliteIndexStore store, string snapshotId)
    {
        return WorkspaceFreshness.CheckFreshnessCheap(store, snapshotId, FreshnessMode.Auto);
    }

    public static TCursor? DecodeCursor<TCursor>(string? rawCursor, Func<string, TCursor?> decoder) where TCursor : class
    {
        if (string.IsNullOrEmpty(rawCursor))
            return null;

        return decoder(rawCursor)
            ?? throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
    }

    public object GetFreshnessJson(int maxDocuments = 10)
    {
        using var store = OpenReadStore();
        return GetFreshnessJson(store, PinnedSnapshotId, maxDocuments);
    }

    public static object GetFreshnessJson(SqliteIndexStore store, string snapshotId, int maxDocuments = 10)
    {
        return FreshnessJson.Cheap(GetFreshness(store, snapshotId), maxDocuments);
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
            throw McpErrorMapper.Map(ex);
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
