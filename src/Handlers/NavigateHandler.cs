using System.Globalization;
using System.Text.Json;
using Lurp.Queries;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class NavigateHandler
{
    public static void Run(string[] args)
    {
        var file = HandlerBootstrap.GetArgValue(args, "--file=");
        var lineArg = HandlerBootstrap.GetArgValue(args, "--line=");
        var line = 0;
        var outputDir = HandlerBootstrap.ResolveOutputDir(args);
        if (string.IsNullOrEmpty(file) || !int.TryParse(lineArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out line) || line < 1)
        {
            Console.Error.WriteLine("ERROR: --file=<relative-path> and positive --line=<number> are required for --mode=navigate.");
            Environment.Exit(1);
        }

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDir);

        var store = HandlerBootstrap.OpenStore(dbPath);
        try
        {
            var snapshot = HandlerBootstrap.ResolveSnapshotId(store, HandlerBootstrap.GetArgValue(args, "--snapshot="));
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
}
