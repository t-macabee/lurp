using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class GetSymbolTool
{
    private readonly McpSessionContext _session;

    public GetSymbolTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_get_symbol", Title = "Lurp Get Symbol", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get symbol metadata and optional source. Accepts pipe-form docCommentId|assemblyIdentity, bare T: docCommentId, or bare FQN. View summary|source|all controls payload.")]
    public string LurpGetSymbol(
        string? symbol = null,
        string? view = null,
        int? context_lines = null,
        bool? include_generated = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            if (string.IsNullOrEmpty(symbol))
                throw new McpProtocolException("--symbol is required.", McpErrorCode.InvalidParams);

            var viewArg = string.IsNullOrEmpty(view) ? "summary" : view.ToLowerInvariant();
            if (viewArg != "summary" && viewArg != "source" && viewArg != "all")
                throw new McpProtocolException("--view must be one of: summary, source, all.", McpErrorCode.InvalidParams);

            if (context_lines.HasValue && context_lines.Value < 0)
                throw new McpProtocolException("--context-lines must be a non-negative integer.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;
            var contextLines = context_lines ?? 3;

            var info = HandlerBootstrap.ResolveSymbolInfo(_session.Store, symbol, snapshotId, includeGenerated);
            if (info == null)
                throw new McpProtocolException($"Symbol '{symbol}' not found in snapshot '{snapshotId}'.", McpErrorCode.InvalidParams);

            var freshness = _session.GetFreshnessJson();
            var locations = _session.Store.GetDeclarationLocations(info.SymbolId.Value, snapshotId, includeGenerated);
            var annotations = _session.Store.GetAnnotations(snapshotId, info.SymbolId.Value);

            string? source = null;
            if (viewArg is "source" or "all")
            {
                if (context_lines.HasValue)
                {
                    source = _session.Store.GetSurroundingLines(info.SymbolId.Value, snapshotId, contextLines);
                    if (source == null)
                        source = _session.Store.GetSymbolSource(info.SymbolId.Value, snapshotId, ViewKind.Declaration, includeGenerated);
                }
                else
                {
                    source = _session.Store.GetSymbolSource(info.SymbolId.Value, snapshotId, ViewKind.Declaration, includeGenerated);
                }
            }

            object? metadataObj = null;
            if (info.MetadataJson != null)
            {
                try { metadataObj = JsonSerializer.Deserialize<object>(info.MetadataJson); } catch { metadataObj = info.MetadataJson; }
            }

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                symbol_id = info.SymbolId.Value,
                doc_comment_id = info.SymbolId.DocCommentId,
                assembly_identity = info.SymbolId.AssemblyIdentity,
                kind = info.Kind.ToString(),
                fully_qualified_name = info.FullyQualifiedName,
                metadata_json = metadataObj,
                declaration_count = info.DeclarationCount,
                is_partial = info.IsPartial,
                locations,
                source = (viewArg is "source" or "all") ? source : null,
                annotations = annotations.Select(static a => new { symbol_id = a.SymbolId, kind = a.Kind, value = a.Value, document_path = a.DocumentPath }).ToList()
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
