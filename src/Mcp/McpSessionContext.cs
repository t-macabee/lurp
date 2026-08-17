using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;

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

    public ValueTask DisposeAsync()
    {
        Store.Close();
        return ValueTask.CompletedTask;
    }
}
