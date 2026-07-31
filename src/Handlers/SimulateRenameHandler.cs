using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class SimulateRenameHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg))
        {
            Console.Error.WriteLine("ERROR: --symbol=<symbol-id> is required for --mode=simulate-rename.");
            Environment.Exit(1);
        }

        var newNameArg = HandlerBootstrap.GetArgValue(args, "--new-name=");
        if (string.IsNullOrEmpty(newNameArg))
        {
            Console.Error.WriteLine("ERROR: --new-name=<name> is required for --mode=simulate-rename.");
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
            var report = engine.SimulateRename(symbolArg, newNameArg);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
