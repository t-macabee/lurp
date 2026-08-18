namespace Lurp.Handlers;

internal static class GetSourceHandler
{
    public static void Run(string[] args)
    {
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        if (string.IsNullOrEmpty(documentArg)) HandlerBootstrap.Fail("ERROR: --document=<relative-path> is required for --mode=get-source.");

        var startLineRaw = HandlerBootstrap.GetArgValue(args, "--start-line=");
        var endLineRaw = HandlerBootstrap.GetArgValue(args, "--end-line=");
        var contextLinesRaw = HandlerBootstrap.GetArgValue(args, "--context-lines=");

        int? startLine = null, endLine = null, contextLines = null;

        if (!string.IsNullOrEmpty(startLineRaw))
        {
            if (!int.TryParse(startLineRaw, out var v) || v < 1) HandlerBootstrap.Fail("ERROR: --start-line must be a positive integer (>=1).");
            startLine = v;
        }
        if (!string.IsNullOrEmpty(endLineRaw))
        {
            if (!int.TryParse(endLineRaw, out var v) || v < 1) HandlerBootstrap.Fail("ERROR: --end-line must be a positive integer (>=1).");
            endLine = v;
        }
        if (!string.IsNullOrEmpty(contextLinesRaw))
        {
            if (!int.TryParse(contextLinesRaw, out var v) || v < 0) HandlerBootstrap.Fail("ERROR: --context-lines must be a non-negative integer.");
            contextLines = v;
        }
        if (contextLines.HasValue && startLine == null && endLine == null)
            HandlerBootstrap.Fail("ERROR: --context-lines requires --start-line or --end-line.");
        if (startLine.HasValue && endLine.HasValue && startLine.Value > endLine.Value)
            HandlerBootstrap.Fail("ERROR: --start-line must be <= --end-line.");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            // Resolve freshness before writing any bytes so --require-fresh
            // exits 2 without emitting stale source, even when windowing.
            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            Lurp.Storage.SourceSlice? slice;
            try
            {
                slice = store.GetSourceSlice(documentArg, snapshotId, startLine, endLine, contextLines);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }
            catch (ArgumentException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }

            if (slice == null) HandlerBootstrap.Fail($"ERROR: Document '{documentArg}' not found in snapshot.");

            // Raw source goes to stdout verbatim (no JSON envelope), so freshness
            // travels on the channel that exists for it: the stderr line, plus the
            // exit code when --require-fresh rejects the read.

            Console.Write(slice.Source);
        });
    }
}