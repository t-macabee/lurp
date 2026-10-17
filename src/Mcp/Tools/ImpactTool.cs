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
internal sealed class ImpactTool
{
    private readonly McpSessionContext _session;

    public ImpactTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_impact", Title = "Lurp Impact", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Trace impact downstream or upstream for a symbol: the reached symbols with depth, shortest-path count, frontier flag and a deterministic witness path, plus first-hop groups. Supports kinds/provenance filtering and cursor pagination. The upstream direction follows every edge kind except Declares, Contains and TestedBy unless kinds names the set; the downstream direction follows every kind.")]
    public string LurpImpact(
        string? symbol = null,
        string? direction = null,
        string[]? kinds = null,
        string[]? provenance = null,
        int? max_depth = null,
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            cancellationToken.ThrowIfCancellationRequested();
            using var store = _session.OpenReadStore();

            if (string.IsNullOrEmpty(symbol))
                throw new McpProtocolException("symbol is required.", McpErrorCode.InvalidParams);

            var directionArg = direction ?? "downstream";
            if (!ImpactHandler.TryParseDirection(directionArg, out var impactDirection))
                throw new McpProtocolException("direction must be one of: downstream, upstream.", McpErrorCode.InvalidParams);

            var maxDepth = max_depth ?? ImpactHandler.DefaultMaxDepth;
            if (maxDepth < 1)
                throw new McpProtocolException("max_depth must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(max_depth, McpLimits.MaxMaxDepth, "max_depth");

            var limitValue = limit ?? ImpactHandler.DefaultLimit;
            if (limitValue < 1)
                throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(limit, McpLimits.MaxLimit, "limit");

            IReadOnlyList<string>? canonicalKinds = null;
            string? kindsRaw = null;
            if (kinds is not null)
            {
                var kindsOutcome = ImpactHandler.TryParseNameFilter(kinds, ImpactHandler.EdgeKindNames, out canonicalKinds, out var unknownKind);
                if (kindsOutcome == ImpactHandler.NameFilterParseOutcome.EmptyItem)
                    throw new McpProtocolException("kinds contains an empty item.", McpErrorCode.InvalidParams);
                if (kindsOutcome == ImpactHandler.NameFilterParseOutcome.UnknownName)
                    throw new McpProtocolException($"kinds: unknown edge kind '{unknownKind}'. Use one of: {string.Join(", ", ImpactHandler.EdgeKindNames)} (case-insensitive).", McpErrorCode.InvalidParams);
                if (canonicalKinds is null)
                    throw new McpProtocolException($"kinds must name at least one edge kind. Use one of: {string.Join(", ", ImpactHandler.EdgeKindNames)} (case-insensitive).", McpErrorCode.InvalidParams);

                kindsRaw = string.Join(",", canonicalKinds);
            }

            IReadOnlyList<string>? canonicalProvenance = null;
            string? provenanceRaw = null;
            if (provenance is not null)
            {
                var provenanceOutcome = ImpactHandler.TryParseNameFilter(provenance, Provenance.All, out canonicalProvenance, out var unknownProvenance);
                if (provenanceOutcome == ImpactHandler.NameFilterParseOutcome.EmptyItem)
                    throw new McpProtocolException("provenance contains an empty item.", McpErrorCode.InvalidParams);
                if (provenanceOutcome == ImpactHandler.NameFilterParseOutcome.UnknownName)
                    throw new McpProtocolException($"provenance: unknown provenance '{unknownProvenance}'. Use one of: {string.Join(", ", Provenance.All)} (case-insensitive).", McpErrorCode.InvalidParams);
                if (canonicalProvenance is null)
                    throw new McpProtocolException($"provenance must name at least one provenance. Use one of: {string.Join(", ", Provenance.All)} (case-insensitive).", McpErrorCode.InvalidParams);

                provenanceRaw = string.Join(",", canonicalProvenance);
            }

            HashSet<string>? allowedKinds = canonicalKinds is not null
                ? new HashSet<string>(canonicalKinds, StringComparer.Ordinal)
                : impactDirection == ImpactDirection.Upstream
                    ? new HashSet<string>(ImpactHandler.UpstreamDefaultKinds, StringComparer.Ordinal)
                    : null;
            HashSet<string>? allowedProvenance = canonicalProvenance is null ? null : new HashSet<string>(canonicalProvenance, StringComparer.Ordinal);

            var resolvedSymbolId = HandlerBootstrap.ResolveSymbolArg(store, symbol, snapshotId);

            var fingerprint = SequenceCursor.ComputeFingerprint(
                resolvedSymbolId,
                impactDirection.ToString(),
                maxDepth.ToString(CultureInfo.InvariantCulture),
                kindsRaw,
                provenanceRaw,
                "symbols");

            SequenceCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = SequenceCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);

                try
                {
                    cursorObj.Validate(snapshotId, fingerprint, ImpactHandler.CursorKind);
                }
                catch (ArgumentException ex)
                {
                    throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                }
            }

            var offset = cursorObj?.Offset ?? 0;

            var reachability = new ImpactReachability(store, snapshotId, store);
            var traced = reachability.Trace(resolvedSymbolId, impactDirection, allowedKinds, allowedProvenance, maxDepth, includeSource: true, cancellationToken: cancellationToken);

            var response = ImpactPaging.BuildSymbolsResponse(
                snapshotId,
                _session.GetFreshnessJson(),
                resolvedSymbolId,
                impactDirection,
                maxDepth,
                traced,
                offset,
                limitValue,
                fingerprint,
                ImpactHandler.CursorKind,
                pinned: true,
                includeLimitEcho: true);

            return JsonSerializer.Serialize(response, LurpJsonOptions.Indented);
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
