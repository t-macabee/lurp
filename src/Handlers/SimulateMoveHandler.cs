using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class SimulateMoveHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg))
        {
            HandlerBootstrap.Fail("ERROR: --symbol=<symbol-id> is required for --mode=simulate-move.");
        }

        var newNamespaceArg = HandlerBootstrap.GetArgValue(args, "--new-namespace=");
        if (string.IsNullOrEmpty(newNamespaceArg))
        {
            HandlerBootstrap.Fail("ERROR: --new-namespace=<namespace> is required for --mode=simulate-move.");
        }

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            var engine = new SimulationEngine(store, store, snapshotId);
            var report = engine.SimulateMove(symbolArg, newNamespaceArg);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
