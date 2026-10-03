using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class ContextTool
{
    private const int DefaultBudget = 8000;
    private const int DefaultTypeAnchorBudget = 16000;
    private const int DefaultTierLimit = 25;
    private const string CursorKind = "capsule-tier";

    private readonly McpSessionContext _session;

    public ContextTool(McpSessionContext session)
    {
        _session = session;
    }

    private static int DefaultBudgetFor(string? symbolArg)
    {
        if (symbolArg is not null)
        {
            if (SymbolId.TryParse(symbolArg, out var id) && id.IsType)
                return DefaultTypeAnchorBudget;
            if (symbolArg.StartsWith("T:", StringComparison.Ordinal))
                return DefaultTypeAnchorBudget;
        }

        return DefaultBudget;
    }

    private static ContextIntent ParseIntent(string intentArg)
    {
        return intentArg.ToLowerInvariant() switch
        {
            "inspect" => ContextIntent.Inspect,
            "modify" => ContextIntent.Modify,
            "diagnose" => ContextIntent.Diagnose,
            _ => throw new McpProtocolException("intent must be one of: inspect, modify, diagnose.", McpErrorCode.InvalidParams)
        };
    }

    [McpServerTool(Name = "lurp_context", Title = "Lurp Context Capsule", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Assemble a context capsule for a symbol or file location. Exactly one of symbol or file+line is required. Intent: inspect | modify | diagnose (default: inspect). Supports tier continuation via tier/cursor.")]
    public string LurpContext(
        string? symbol = null,
        string? file = null,
        int? line = null,
        [Description("Intent hint: inspect | modify | diagnose (default: inspect).")]
        string? intent = null,
        [Description("Token budget for capsule content (default: 8000, or 16000 when symbol is a type anchor and content_budget is omitted).")]
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

            var normalizedFile = HandlerBootstrap.NormalizeDocumentPath(file);
            var hasSymbol = !string.IsNullOrEmpty(symbol);
            var hasFile = !string.IsNullOrEmpty(normalizedFile) && line.HasValue;

            if (!hasSymbol && !hasFile)
                throw new McpProtocolException("Either symbol or file+line is required.", McpErrorCode.InvalidParams);

            if (hasSymbol && hasFile)
                throw new McpProtocolException("Provide either symbol or file+line, not both.", McpErrorCode.InvalidParams);

            if (hasFile && line!.Value < 1)
                throw new McpProtocolException("line must be a positive integer.", McpErrorCode.InvalidParams);

            var intentParsed = ParseIntent(intent ?? "inspect");
            // Deferred: resolved to the canonical form before the default is picked (see
            // below) so the type-anchor budget default keys off the resolved symbol kind,
            // not whichever of the three accepted input forms the caller used.
            var budget = content_budget ?? DefaultBudget;
            if (budget < 1)
                throw new McpProtocolException("content_budget must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(content_budget, McpLimits.MaxContentBudget, "content_budget");

            var maxHops = max_hops ?? 3;
            if (maxHops < 1)
                throw new McpProtocolException("max_hops must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(max_hops, McpLimits.MaxMaxHops, "max_hops");

            var tierLimit = tier_limit ?? DefaultTierLimit;
            if (tierLimit < 1)
                throw new McpProtocolException("tier_limit must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(tier_limit, McpLimits.MaxLimit, "tier_limit");

            var includeGenerated = include_generated ?? false;
            var completenessDetail = completeness_detail ?? false;
            var affectedProjects = affected_project != null
                ? (IReadOnlyList<string>)affected_project.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray()
                : null;

            // Tier path
            if (!string.IsNullOrEmpty(tier))
            {
                if (Array.IndexOf(ContextAssembler.TierNames, tier) < 0)
                    throw new McpProtocolException($"unknown tier '{tier}'. Valid tiers: {string.Join(", ", ContextAssembler.TierNames)}.", McpErrorCode.InvalidParams);

                string resolvedSymbol;
                if (hasSymbol)
                {
                    resolvedSymbol = HandlerBootstrap.ResolveSymbolArg(store, symbol!, snapshotId, includeGenerated);
                }
                else
                {
                    resolvedSymbol = store.ResolveSymbolByLocation(normalizedFile!, line!.Value, snapshotId, includeGenerated)
                        ?? throw new McpProtocolException($"no symbol found at {normalizedFile}:{line}; tier needs an anchor symbol.", McpErrorCode.InvalidParams);
                }

                var fingerprint = SequenceCursor.ComputeFingerprint(
                    resolvedSymbol, tier,
                    maxHops.ToString(CultureInfo.InvariantCulture),
                    includeGenerated.ToString());

                SequenceCursor? cursorObj = null;
                if (!string.IsNullOrEmpty(cursor))
                {
                    cursorObj = SequenceCursor.TryDecode(cursor);
                    if (cursorObj == null)
                        throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);

                    try
                    {
                        cursorObj.Validate(snapshotId, fingerprint, CursorKind);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                var offset = cursorObj?.Offset ?? 0;
                var page = ContextAssembler.BuildTierPage(
                    store, store, snapshotId, SymbolId.Parse(resolvedSymbol), tier,
                    maxHops, includeGenerated, offset, tierLimit);

                var nextCursor = page.HasMore
                    ? new SequenceCursor(snapshotId, fingerprint, CursorKind, offset + page.Items.Count).Encode()
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

                return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
            }

            // Non-tier path
            string? symbolArg = symbol;
            if (hasSymbol)
                symbolArg = HandlerBootstrap.ResolveSymbolArg(store, symbol!, snapshotId, includeGenerated);
            if (content_budget is null && hasSymbol)
                budget = DefaultBudgetFor(symbolArg);

            var lookup = new ContextLookup(snapshotId, symbolArg, normalizedFile, line);
            var gitRoot = store.GetSnapshotGitRoot(snapshotId);
            var assemblyOptions = new ContextAssemblyOptions(
                intentParsed, budget, maxHops, includeGenerated,
                scope, affectedProjects, gitRoot, completenessDetail);

            cancellationToken.ThrowIfCancellationRequested();
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, assemblyOptions, store, store);

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

            return JsonSerializer.Serialize(capsuleEnvelope, new JsonSerializerOptions { WriteIndented = true });
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
