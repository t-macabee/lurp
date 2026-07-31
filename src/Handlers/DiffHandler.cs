using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class DiffHandler
{
    public static void Run(string[] args)
    {
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var fromSnapshot = HandlerBootstrap.GetArgValue(args, "--from-snapshot=");
        var toSnapshot = HandlerBootstrap.GetArgValue(args, "--to-snapshot=");
        if (string.IsNullOrEmpty(fromSnapshot) || string.IsNullOrEmpty(toSnapshot))
        {
            Console.Error.WriteLine("ERROR: --from-snapshot=<id> and --to-snapshot=<id> are required for --mode=diff.");
            Environment.Exit(1);
        }

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var differ = new SemanticDiffer(store, store, store);
            var (changes, skippedComparisons) = differ.ComputeDiff(fromSnapshot, toSnapshot);

            var json = JsonSerializer.Serialize(new
            {
                from_snapshot = fromSnapshot,
                to_snapshot = toSnapshot,
                change_count = changes.Count,
                skipped_comparisons = skippedComparisons,
                changes = changes.Select(c => new
                {
                    change_id = c.ChangeId,
                    change_type = c.ChangeType,
                    symbol_id = c.SymbolId,
                    detail = c.DetailJson != null ? JsonSerializer.Deserialize<object>(c.DetailJson) : null,
                    created_at_utc = c.CreatedAtUtc
                })
            }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
