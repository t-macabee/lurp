namespace Lurp.Handlers;

internal static class GetSourceHandler
{
    public static void Run(string[] args)
    {
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        if (string.IsNullOrEmpty(documentArg)) HandlerBootstrap.Fail("ERROR: --document=<relative-path> is required for --mode=get-source.");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var source = store.GetSource(documentArg, snapshotId);

            if (source == null) HandlerBootstrap.Fail($"ERROR: Document '{documentArg}' not found in snapshot.");

            // Raw source goes to stdout verbatim (no JSON envelope), so freshness
            // travels on the channel that exists for it: the stderr line, plus the
            // exit code when --require-fresh rejects the read. Resolve before any
            // byte is written so --require-fresh exits 2 without emitting stale source.
            HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            Console.Write(source);
        });
    }
}