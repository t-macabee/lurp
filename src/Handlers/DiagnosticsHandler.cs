using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class DiagnosticsHandler
{
    internal const int DefaultLimit = 100;

    public static void Run(string[] args, IOutputSink output)
    {
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        var projectArg = HandlerBootstrap.GetArgValue(args, "--project=");
        var severityArg = HandlerBootstrap.GetArgValue(args, "--severity=");
        var idArg = HandlerBootstrap.GetArgValue(args, "--id=");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var includeGenerated = args.Contains("--include-generated");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        var projectFilter = string.IsNullOrEmpty(projectArg) ? null : projectArg;
        var severityFilter = string.IsNullOrEmpty(severityArg) ? null : severityArg;
        var idFilter = string.IsNullOrEmpty(idArg) ? null : idArg;

        var limit = HandlerBootstrap.ParsePositiveIntArg(args, "--limit=", DefaultLimit);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var cursor = HandlerBootstrap.DecodeCursor(cursorArg, DiagnosticsCursor.TryDecode);

            DiagnosticsPage page;
            try
            {
                page = store.GetDiagnosticsPage(
                    snapshotId, projectFilter, documentArg,
                    severityFilter, excludeHidden: severityFilter == null,
                    idFilter, limit, cursor, includeGenerated);
            }
            catch (ArgumentException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

            var diagnostics = page.Items.Select(SurfacePayloads.DiagnosticRow).ToList();

            var meta = new
            {
                snapshot_id = snapshotId,
                document = documentArg,
                project = projectFilter,
                severity = severityFilter,
                id = idFilter,
                include_generated = includeGenerated,
                diagnostic_count = page.TotalCount,
                next_cursor = page.NextCursor,
                freshness = FreshnessJson.Cheap(freshness)
            };

            CliOutputWriter.Write(outputMode, output, () =>
            {
                foreach (var d in diagnostics)
                    output.WriteLine($"{d.severity,-10} {d.id,-10} {d.document_path ?? "<null>"}:{d.start_line}  {d.message}");
                output.WriteLine($"-- {diagnostics.Count}/{page.TotalCount} diagnostic(s){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
            },
            () => new { snapshot_id = snapshotId, document = documentArg, project = projectFilter, severity = severityFilter, id = idFilter, include_generated = includeGenerated, diagnostics, diagnostic_count = page.TotalCount, next_cursor = page.NextCursor, freshness = FreshnessJson.Cheap(freshness) },
            () => meta,
            CliOutputWriter.JsonlItemTypes["diagnostics"],
            diagnostics);
        }, output);
    }
}
