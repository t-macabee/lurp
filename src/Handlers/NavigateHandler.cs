using System.Globalization;
using System.Text.Json;
using Lurp.Queries;

namespace Lurp.Handlers;

internal static class NavigateHandler
{
    public static void Run(string[] args)
    {
        var file = GetArgValue(args, "--file=");
        var lineArg = GetArgValue(args, "--line=");
        var line = 0;
        var outputDir = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(file) || !int.TryParse(lineArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out line) || line < 1)
        {
            Console.Error.WriteLine("ERROR: --file=<relative-path> and positive --line=<number> are required for --mode=navigate.");
            Environment.Exit(1);
        }
        if (string.IsNullOrEmpty(outputDir))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var dbPath = Path.Combine(Path.GetFullPath(outputDir), "index.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
            Environment.Exit(1);
        }

        var store = new Storage.SqliteIndexStore(dbPath);
        store.Open(dbPath);
        try
        {
            var snapshot = GetArgValue(args, "--snapshot=") ?? store.GetLatestSnapshotId();
            if (snapshot == null)
            {
                Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                Environment.Exit(1);
            }
            var target = new FastTravelQueries(store).Navigate(file!, line, snapshot!, args.Contains("--include-generated"));
            if (target == null)
            {
                Console.Error.WriteLine($"ERROR: No indexed declaration contains {file}:{line} in snapshot '{snapshot}'.");
                Environment.Exit(1);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { snapshotId = snapshot, target }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { store.Close(); }
    }

    private static string? GetArgValue(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
}
