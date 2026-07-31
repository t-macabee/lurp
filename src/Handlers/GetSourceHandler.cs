using Lurp.Storage;

namespace Lurp.Handlers;

internal static class GetSourceHandler
{
    public static void Run(string[] args)
    {
        var documentArg = HandlerBootstrap.GetArgValue(args, "--document=");
        if (string.IsNullOrEmpty(documentArg))
        {
            Console.Error.WriteLine("ERROR: --document=<relative-path> is required for --mode=get-source.");
            Environment.Exit(1);
        }

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);
            var source = store.GetSource(documentArg, snapshotId);

            if (source == null)
            {
                Console.Error.WriteLine($"ERROR: Document '{documentArg}' not found in snapshot.");
                Environment.Exit(1);
            }

            Console.Write(source);
        }
        finally
        {
            store.Close();
        }
    }
}
