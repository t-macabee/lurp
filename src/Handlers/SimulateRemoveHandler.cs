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
            HandlerBootstrap.Fail("ERROR: --symbol=<symbol-id> is required for --mode=simulate-remove.");
        }

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            var resolvedSymbolId = HandlerBootstrap.ResolveSymbolArg(store, symbolArg!, snapshotId);

            var engine = new SimulationEngine(store, store, snapshotId);
            var report = engine.SimulateRemove(resolvedSymbolId);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
