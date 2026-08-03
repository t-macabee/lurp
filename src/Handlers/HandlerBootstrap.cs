using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class HandlerBootstrap
{
    public static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }

    public static FreshnessMode ParseFreshnessMode(string[] args)
    {
        var raw = GetArgValue(args, "--freshness=");
        return raw?.ToLowerInvariant() switch
        {
            "hash" => FreshnessMode.Hash,
            "off" => FreshnessMode.Off,
            _ => FreshnessMode.Auto,
        };
    }

    public static FreshnessStamp ComputeFreshnessStamp(ISnapshotStore store, string snapshotId, string[] args)
        => WorkspaceFreshness.CheckFreshnessCheap(store, snapshotId, ParseFreshnessMode(args));

    public static object FreshnessJson(FreshnessStamp stamp) => new
    {
        state = stamp.State,
        method = stamp.Method,
        changed_document_count = stamp.ChangedDocumentCount,
        changed_documents_sample = stamp.ChangedDocumentsSample,
        checked_at_utc = stamp.CheckedAtUtc,
        snapshot_id = stamp.SnapshotId,
    };

    public static void EnforceRequireFresh(string[] args, FreshnessStamp stamp)
    {
        if (!args.Contains("--require-fresh"))
            return;

        if (stamp.State != "fresh")
        {
            Console.Error.WriteLine($"ERROR: snapshot '{stamp.SnapshotId}' is not fresh (state={stamp.State}, method={stamp.Method}, changedDocuments={stamp.ChangedDocumentCount}). Re-index, or drop --require-fresh to read it anyway.");
            Environment.Exit(2);
        }
    }

    public static void PrintFreshnessLine(FreshnessStamp stamp)
    {
        Console.Error.WriteLine($"freshness: state={stamp.State} method={stamp.Method} changedDocuments={stamp.ChangedDocumentCount} snapshot={stamp.SnapshotId}");
    }

    public static string RequireArg(string[] args, string prefix, params string[] errorLines)
    {
        var value = GetArgValue(args, prefix);
        if (string.IsNullOrEmpty(value))
        {
            foreach (var line in errorLines)
                Console.Error.WriteLine(line);
            Environment.Exit(1);
        }

        return value;
    }

    public static string ResolveOutputDir(string[] args)
    {
        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        return outputDirArg;
    }

    public static string ResolveDbPath(string outputDir)
    {
        var dbPath = Path.Combine(Path.GetFullPath(outputDir), "index.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
            Environment.Exit(1);
        }

        return dbPath;
    }

    public static SqliteIndexStore OpenStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open();
        return store;
    }

    public static string ResolveSnapshotId(SqliteIndexStore store, string? snapshotArg)
    {
        if (!string.IsNullOrEmpty(snapshotArg))
            return snapshotArg;

        var snapshotId = store.GetLatestSnapshotId();
        if (snapshotId == null)
        {
            Console.Error.WriteLine("ERROR: No snapshots found in the database.");
            Environment.Exit(1);
        }

        return snapshotId;
    }
}
