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

        HandlerBootstrap.WithStore<object?>(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var source = store.GetSource(documentArg, snapshotId);

            if (source == null)
            {
                HandlerBootstrap.Fail($"ERROR: Document '{documentArg}' not found in snapshot.");
            }

            Console.Write(source);
            return null;
        });
    }
}
