using Lurp.Storage;

namespace Lurp.Handlers;

internal static class GetSourceHandler
{
    public static void Run(string[] args)
    {
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        if (string.IsNullOrEmpty(documentArg))
        {
            HandlerBootstrap.Fail("ERROR: --document=<relative-path> is required for --mode=get-source.");
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
                HandlerBootstrap.Fail($"ERROR: Document '{documentArg}' not found in snapshot.");
            }

            HandlerBootstrap.Out.Write(source);
        }
        finally
        {
            store.Close();
        }
    }
}
