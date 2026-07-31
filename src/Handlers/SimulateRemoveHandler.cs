using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class SimulateRemoveHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg))
        {
            Console.Error.WriteLine("ERROR: --symbol=<symbol-id> is required for --mode=simulate-remove.");
            Environment.Exit(1);
        }

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            var engine = new SimulationEngine(store, store, snapshotId);
            var report = engine.SimulateRemove(symbolArg);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
