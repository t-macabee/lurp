using Lurp.Storage;

namespace Lurp.Handlers;

internal static class HandlerBootstrap
{
    public static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
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
        store.Open(dbPath);
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
