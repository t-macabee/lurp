using System.Globalization;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class DiagnosticsHandler
{
    private const int DefaultLimit = 100;

    public static void Run(string[] args)
    {
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        var projectArg = HandlerBootstrap.GetArgValue(args, "--project=");
        var severityArg = HandlerBootstrap.GetArgValue(args, "--severity=");
        var idArg = HandlerBootstrap.GetArgValue(args, "--id=");
        var limitArg = HandlerBootstrap.GetArgValue(args, "--limit=");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        var projectFilter = string.IsNullOrEmpty(projectArg) ? null : projectArg;
        var severityFilter = string.IsNullOrEmpty(severityArg) ? null : severityArg;
        var idFilter = string.IsNullOrEmpty(idArg) ? null : idArg;

        var limit = DefaultLimit;
        if (!string.IsNullOrEmpty(limitArg))
        {
            if (!int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1)
                HandlerBootstrap.Fail("ERROR: --limit must be a positive integer.");
        }

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            DiagnosticsCursor? cursor = null;
            if (!string.IsNullOrEmpty(cursorArg))
            {
                cursor = DiagnosticsCursor.TryDecode(cursorArg);
                if (cursor == null)
                    HandlerBootstrap.Fail("ERROR: --cursor is not a valid continuation token.");
            }

            DiagnosticsPage page;
            try
            {
                page = store.GetDiagnosticsPage(
                    snapshotId, projectFilter, documentArg,
                    severityFilter, excludeHidden: severityFilter == null,
                    idFilter, limit, cursor);
            }
            catch (ArgumentException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var diagnostics = page.Items.Select(d => new
            {
                project_name = d.Record.ProjectName,
                document_path = d.NormalizedDocumentPath,
                in_snapshot = d.InSnapshot,
                severity = d.Record.Severity,
                id = d.Record.Id,
                message = d.Record.Message,
                start_line = LineNumbers.ToOneBased(d.Record.StartLine),
                start_column = d.Record.StartColumn,
                end_line = LineNumbers.ToOneBased(d.Record.EndLine),
                end_column = d.Record.EndColumn
            }).ToList();

            var meta = new
            {
                snapshot_id = snapshotId,
                document = documentArg,
                project = projectFilter,
                severity = severityFilter,
                id = idFilter,
                diagnostic_count = page.TotalCount,
                next_cursor = page.NextCursor,
                freshness = HandlerBootstrap.FreshnessJson(freshness)
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    foreach (var d in diagnostics)
                        Console.WriteLine($"{d.severity,-10} {d.id,-10} {d.document_path ?? "<null>"}:{d.start_line}  {d.message}");
                    Console.WriteLine($"-- {diagnostics.Count}/{page.TotalCount} diagnostic(s){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
                    break;

                case OutputMode.Jsonl:
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var d in diagnostics)
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "diagnostic", diagnostic = d }, HandlerBootstrap.CompactJson));
                    break;

                default:
                    Console.WriteLine(JsonSerializer.Serialize(
                        new { snapshot_id = snapshotId, document = documentArg, project = projectFilter, severity = severityFilter, id = idFilter, diagnostics, diagnostic_count = page.TotalCount, next_cursor = page.NextCursor, freshness = HandlerBootstrap.FreshnessJson(freshness) },
                        HandlerBootstrap.IndentedJson));
                    break;
            }
        });
    }
}
