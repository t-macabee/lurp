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
    [Description("List diagnostics captured at index time for the pinned snapshot: compiler diagnostics plus any diagnostics from analyzers referenced by the target project (e.g. built-in SDK analyzers like CA1822). IDE-only code-style rules (e.g. IDE0005) are not included even when the target project's own build enables them (e.g. EnforceCodeStyleInBuild plus an .editorconfig severity) — Roslyn's build-time compilation additionally gates IDE0005 behind GenerateDocumentationFile=true, independent of anything Lurp does. For unused-using-directive detection, query the compiler diagnostic instead: severity=hidden, id=CS8019 (\"Unnecessary using directive\") — Lurp always captures this regardless of the target's analyzer/code-style configuration. Filters: document (solution-relative path, not git-relative), project, severity (Hidden, Info, Warning, Error case-insensitive, or \"all\" for every severity including Hidden; default excludes Hidden), id (diagnostic code, e.g. CS8933), include_generated (default false: excludes diagnostics located in obj/**, *.g.cs, *.generated.cs, *.Designer.cs, *ModelSnapshot.cs — the same generated-file patterns every other read tool's include_generated excludes by default). Unknown severity values are rejected with an error instead of returning empty. Keyset pagination via limit/cursor, ordered by diagnostic_id. diagnostic_count is the total match count across all pages, not the page size. start_column/end_column are 0-based (unlike the 1-based start_line/end_line). Reports what the compiler and analyzers said at index time — not re-evaluated, ranked, or deduplicated.")]
    public string LurpDiagnostics(
        string? document = null,
        string? project = null,
        string? severity = null,
        string? id = null,
        int? limit = null,
        string? cursor = null,
        bool include_generated = false,
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
                    throw new McpProtocolException("document is required.", McpErrorCode.InvalidParams);
            }

            var projectFilter = string.IsNullOrEmpty(project) ? null : project;
            var severityFilter = string.IsNullOrEmpty(severity) ? null : severity;
            var idFilter = string.IsNullOrEmpty(id) ? null : id;

            var limitVal = limit ?? 100;
            if (limitVal < 1)
                throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);

            DiagnosticsCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = DiagnosticsCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
            }

            DiagnosticsPage page;
            try
            {
                page = _session.Store.GetDiagnosticsPage(
                    snapshotId, projectFilter, normalizedDocument,
                    severityFilter, excludeHidden: severityFilter == null,
                    idFilter, limitVal, cursorObj, include_generated);
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
                include_generated,
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
