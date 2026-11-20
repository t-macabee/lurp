using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class GetSourceHandler
{
    public static void Run(string[] args, IOutputSink output)
    {
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        if (string.IsNullOrEmpty(documentArg)) HandlerBootstrap.Fail("ERROR: --document=<relative-path> is required for --mode=get-source.");

        var startLineRaw = HandlerBootstrap.GetArgValue(args, "--start-line=");
        var endLineRaw = HandlerBootstrap.GetArgValue(args, "--end-line=");

        int? startLine = string.IsNullOrEmpty(startLineRaw) ? null : HandlerBootstrap.ParsePositiveIntArg(args, "--start-line=", 1);
        int? endLine = string.IsNullOrEmpty(endLineRaw) ? null : HandlerBootstrap.ParsePositiveIntArg(args, "--end-line=", 1);
        var contextLines = HandlerBootstrap.ParseNonNegativeIntArg(HandlerBootstrap.GetArgValue(args, "--context-lines="), "--context-lines=");
        if (contextLines.HasValue && startLine == null && endLine == null)
            HandlerBootstrap.Fail("ERROR: --context-lines requires --start-line or --end-line.");
        if (startLine.HasValue && endLine.HasValue && startLine.Value > endLine.Value)
            HandlerBootstrap.Fail("ERROR: --start-line must be <= --end-line.");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            // Resolve freshness before writing any bytes so --require-fresh
            // exits 2 without emitting stale source, even when windowing.
            HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

            Lurp.Storage.SourceSlice? slice;
            try
            {
                slice = store.GetSourceSlice(documentArg, snapshotId, startLine, endLine, contextLines);
            }
            catch (Lurp.Storage.SourceLineOutOfRangeException ex)
            {
                HandlerBootstrap.Fail($"ERROR: --start-line={ex.RequestedStartLine} is beyond the last line ({ex.TotalLines}) of '{documentArg}'.");
                return;
            }

            if (slice == null) HandlerBootstrap.Fail($"ERROR: {DocumentMessages.NotFound(documentArg, snapshotId)}");

            // Raw source goes to stdout verbatim (no JSON envelope), so freshness
            // travels on the channel that exists for it: the stderr line, plus the
            // exit code when --require-fresh rejects the read.

            output.Write(slice.Source);
        }, output);
    }
}