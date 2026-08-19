using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class DiagnosticsTool
{
    private readonly McpSessionContext _session;

    public DiagnosticsTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_diagnostics", Title = "Lurp Diagnostics", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List compiler diagnostics captured at index time for the pinned snapshot. Filters: document (git-relative), project, severity (default excludes Hidden), id (diagnostic code, e.g. CS8933). Keyset pagination via limit/cursor, ordered by diagnostic_id. Reports what the compiler said at index time — not re-evaluated, ranked, or deduplicated.")]
    public string LurpDiagnostics(
        string? document = null,
        string? project = null,
        string? severity = null,
        string? id = null,
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            string? normalizedDocument = null;
            if (!string.IsNullOrEmpty(document))
            {
                normalizedDocument = HandlerBootstrap.NormalizeDocumentPath(document);
                if (string.IsNullOrEmpty(normalizedDocument))
                    throw new McpProtocolException("--document is required.", McpErrorCode.InvalidParams);
            }

            var projectFilter = string.IsNullOrEmpty(project) ? null : project;
            var severityFilter = string.IsNullOrEmpty(severity) ? null : severity;
            var idFilter = string.IsNullOrEmpty(id) ? null : id;

            var limitVal = limit ?? 100;
            if (limitVal < 1)
                throw new McpProtocolException("--limit must be a positive integer.", McpErrorCode.InvalidParams);

            DiagnosticsCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = DiagnosticsCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("--cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
            }

            DiagnosticsPage page;
            try
            {
                page = _session.Store.GetDiagnosticsPage(
                    snapshotId, projectFilter, normalizedDocument,
                    severityFilter, excludeHidden: severityFilter == null,
                    idFilter, limitVal, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            var freshness = _session.GetFreshnessJson();

            var diagnostics = page.Items.Select(static d => new
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

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                document = normalizedDocument,
                project = projectFilter,
                severity = severityFilter,
                id = idFilter,
                diagnostics,
                diagnostic_count = page.TotalCount,
                next_cursor = page.NextCursor
            };

            return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }
}
