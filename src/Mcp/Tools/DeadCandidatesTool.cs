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

    [McpServerTool(Name = "lurp_dead_candidates", Title = "Lurp Dead Candidates", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            cancellationToken.ThrowIfCancellationRequested();
            using var store = _session.OpenReadStore();

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

            var limitVal = limit ?? DeadCandidateLiveness.DefaultPageSize;
            if (limitVal < 1)
                throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(limitVal, McpLimits.MaxDeadCandidatesLimit, "limit");

            DeadCandidateCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = DeadCandidateCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
            }

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
            var shared = DeadCandidatePayload.Envelope(page, filters, snapshotId, freshness);

            // Preserve the MCP key order (freshness and pinned right after snapshot_id) while
            // sourcing every shared field from DeadCandidatePayload.Envelope.
            var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["snapshot_id"] = shared["snapshot_id"],
                ["freshness"] = shared["freshness"],
                ["pinned"] = true
            };
            foreach (var (key, value) in shared)
            {
                if (key is "snapshot_id" or "freshness")
                    continue;
                envelope[key] = value;
            }

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }
}
