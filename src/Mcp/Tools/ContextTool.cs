using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class ContextTool
{
    private readonly McpSessionContext _session;

    public ContextTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_context", Title = "Lurp Context Capsule", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Assemble a context capsule for a symbol or file location. Exactly one of symbol or file+line is required. Intent: inspect | modify | diagnose (default: inspect). Supports tier continuation via tier/cursor.")]
    public string LurpContext(
        string? symbol = null,
        string? file = null,
        int? line = null,
        [Description("Intent hint: inspect | modify | diagnose (default: inspect).")]
        string? intent = null,
        [Description("Token budget for capsule content (default: 8000, or 16000 when the anchor resolves to a type (from symbol or file+line) and content_budget is omitted).")]
        int? content_budget = null,
        int? max_hops = null,
        string? scope = null,
        string[]? affected_project = null,
        bool? include_generated = null,
        bool? completeness_detail = null,
        string? tier = null,
        int? tier_limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            cancellationToken.ThrowIfCancellationRequested();
            using var store = _session.OpenReadStore();

            var (request, parseError) = ContextRequest.Parse(
                symbol,
                HandlerBootstrap.NormalizeDocumentPath(file),
                line?.ToString(CultureInfo.InvariantCulture),
                intent,
                content_budget?.ToString(CultureInfo.InvariantCulture),
                max_hops?.ToString(CultureInfo.InvariantCulture),
                scope,
                affected_project,
                include_generated ?? false,
                completeness_detail ?? false,
                tier,
                tier_limit?.ToString(CultureInfo.InvariantCulture),
                cursor);
            if (parseError != null)
                throw new McpProtocolException(parseError.FormatMcp(), McpErrorCode.InvalidParams);

            McpLimits.RequireAtMost(content_budget, McpLimits.MaxContentBudget, "content_budget");
            McpLimits.RequireAtMost(max_hops, McpLimits.MaxMaxHops, "max_hops");
            McpLimits.RequireAtMost(tier_limit, McpLimits.MaxLimit, "tier_limit");

            var (binding, bindError) = request!.Bind(store, snapshotId);
            if (bindError != null)
                throw new McpProtocolException(bindError.FormatMcp(), McpErrorCode.InvalidParams);

            if (request.Tier is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = ContextAssembler.BuildTierPage(
                    store, store, snapshotId, SymbolId.Parse(binding!.ResolvedSymbolId!), request.Tier,
                    request.MaxHops, request.IncludeGenerated, binding.Offset, request.TierLimit, cancellationToken);

                var nextCursor = page.HasMore
                    ? new SequenceCursor(snapshotId, binding.Fingerprint!, ContextRequest.CursorKind, binding.Offset + page.Items.Count).Encode()
                    : null;

                var freshness = _session.GetFreshnessJson();

                var envelope = new
                {
                    snapshot_id = snapshotId,
                    freshness,
                    pinned = true,
                    tier_page = new
                    {
                        tier = page.TierName,
                        symbol_id = page.SymbolId,
                        fully_qualified_name = page.FullyQualifiedName,
                        kind = page.Kind,
                        total_items = page.TotalItems,
                        offset = page.Offset,
                        next_cursor = nextCursor,
                        // The tier is rebuilt in isolation, so no capsule token budget applies here.
                        // Mirrors ContextHandler.RunTierContinuation: keep false so the page
                        // is not mistaken for a budgeted capsule section.
                        budget_applied = false,
                        items = page.Items
                    }
                };

                return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
            }

            var lookup = new ContextLookup(snapshotId, binding!.ResolvedSymbolId, request.File, request.Line);
            var assemblyOptions = new ContextAssemblyOptions(
                request.Intent, request.ContentBudget, request.MaxHops, request.IncludeGenerated,
                request.Scope, request.AffectedProjects, request.CompletenessDetail);

            cancellationToken.ThrowIfCancellationRequested();
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, assemblyOptions, store, store, store, cancellationToken);

            var capsuleJson = ContextCapsuleJson.Serialize(capsule);
            using var capsuleDoc = JsonDocument.Parse(capsuleJson);
            var capsuleElement = capsuleDoc.RootElement.Clone();

            var freshnessJson = _session.GetFreshnessJson();

            var capsuleEnvelope = new
            {
                snapshot_id = snapshotId,
                freshness = freshnessJson,
                pinned = true,
                capsule = capsuleElement
            };

            return JsonSerializer.Serialize(capsuleEnvelope, LurpJsonOptions.Indented);
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
