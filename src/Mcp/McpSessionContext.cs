using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Mcp;

internal sealed class McpSessionContext : IAsyncDisposable
{
    public SqliteIndexStore Store { get; }
    public string PinnedSnapshotId { get; }
    public FreshnessStamp FreshnessStamp { get; }

    private McpSessionContext(SqliteIndexStore store, string pinnedSnapshotId, FreshnessStamp freshnessStamp)
    {
        Store = store;
        PinnedSnapshotId = pinnedSnapshotId;
        FreshnessStamp = freshnessStamp;
    }

    public static McpSessionContext Create(string[] args)
    {
        var outputDir = HandlerBootstrap.ResolveOutputDir(args);
        var dbPath = HandlerBootstrap.ResolveDbPath(outputDir);
        var store = HandlerBootstrap.OpenStore(dbPath);
        store.EnableQueryOnly();

        var snapshotId = store.GetLatestSnapshotId();
        if (snapshotId == null)
            throw new CliExitException("ERROR: No snapshots found in the database.", 1);

        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, store, snapshotId!, FreshnessMode.Auto);

        // Only sanctioned console call in src/Mcp/** — before stdio handshake.
        var shortId = snapshotId.Length > 12 ? snapshotId[..12] : snapshotId;
        Console.Error.WriteLine($"mcp: pinned snapshot {shortId} at {stamp.CheckedAtUtc:O} freshness:{stamp.State}");

        return new McpSessionContext(store, snapshotId, stamp);
    }

    /// <summary>
    ///     Validates that <paramref name="requestedSnapshotId"/> matches the pinned snapshot.
    ///     Returns the pinned snapshot id when valid, otherwise throws <see cref="McpProtocolException"/> with <c>InvalidParams</c>.
    ///     Shared across all MCP tools to avoid duplicated checks.
    /// </summary>
    public string RequirePinnedSnapshot(string? requestedSnapshotId)
    {
        if (!string.IsNullOrEmpty(requestedSnapshotId) && !string.Equals(requestedSnapshotId, PinnedSnapshotId, StringComparison.Ordinal))
            throw new McpProtocolException($"snapshot mismatch: session pinned to '{PinnedSnapshotId}'; got '{requestedSnapshotId}'. Call with pinned snapshot or omit snapshot_id.", McpErrorCode.InvalidParams);
        return PinnedSnapshotId;
    }

    public FreshnessStamp GetFreshness()
    {
        return WorkspaceFreshness.CheckFreshnessCheap(Store, Store, PinnedSnapshotId, FreshnessMode.Auto);
    }

    public object GetFreshnessJson()
    {
        return HandlerBootstrap.FreshnessJson(GetFreshness());
    }

    public ValueTask DisposeAsync()
    {
        Store.Close();
        return ValueTask.CompletedTask;
    }
}
