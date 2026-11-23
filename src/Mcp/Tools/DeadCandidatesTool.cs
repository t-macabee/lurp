using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class DeadCandidatesTool
{
    private readonly McpSessionContext _session;

    public DeadCandidatesTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_dead_candidates", Title = "Lurp Dead Candidates", ReadOnly = true, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(DeadCandidatePayload.McpResponse))]
    [Description(DeadCandidateLadder.ToolDescription)]
    public string LurpDeadCandidates(
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        [Description("Filter to this project (assembly simple name).")] string? project = null,
        [Description("Filter to symbols declared in this document (forward-slash relative path, relative to the solution root).")] string? document = null,
        [Description("Filter by symbol kind: Type, Method, Property, Field, Event (case-insensitive).")] string? kind = null,
        bool? include_public = null,
        bool? include_generated = null,
        bool? include_tests = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            if (!string.IsNullOrEmpty(kind) && !DeadCandidateLiveness.IsCandidateKind(kind!))
                throw new McpProtocolException($"kind must be one of: {DeadCandidateLiveness.CandidateKindsText}. Got '{kind}'.", McpErrorCode.InvalidParams);

            string? normalizedDocument = null;
            if (!string.IsNullOrEmpty(document))
            {
                normalizedDocument = HandlerBootstrap.NormalizeDocumentPath(document);
                if (string.IsNullOrEmpty(normalizedDocument))
                    throw new McpProtocolException("document is required.", McpErrorCode.InvalidParams);
            }

            var projectFilter = string.IsNullOrEmpty(project) ? null : project;
            var kindFilter = string.IsNullOrEmpty(kind) ? null : kind;
            var includePublic = include_public ?? false;
            var includeGenerated = include_generated ?? false;
            var includeTests = include_tests ?? false;

            var limitVal = McpLimits.RequirePositiveAtMost(limit, DeadCandidateLiveness.DefaultPageSize, McpLimits.MaxDeadCandidatesLimit, "limit");

            var cursorObj = McpSessionContext.DecodeCursor(cursor, DeadCandidateCursor.TryDecode);

            DeadCandidatePage page;
            try
            {
                page = store.GetDeadCandidatesPage(snapshotId, projectFilter, normalizedDocument, kindFilter, includePublic, includeGenerated, includeTests, limitVal, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            var freshness = _session.GetFreshnessJson();
            var filters = DeadCandidatePayload.Filters(projectFilter, normalizedDocument, kindFilter, includePublic, includeGenerated, includeTests);
            var envelope = DeadCandidatePayload.PinnedEnvelope(page, filters, snapshotId, freshness);

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}
