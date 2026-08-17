using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class FindSymbolTool
{
    private readonly McpSessionContext _session;

    public FindSymbolTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_find_symbol", Title = "Lurp Find Symbol", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Resolve a symbol in the pinned snapshot. Accepts pipe-form docCommentId|assemblyIdentity, bare T: docCommentId, or bare FQN.")]
    public string LurpFindSymbol(
        string? symbol = null,
        bool? include_generated = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            if (string.IsNullOrEmpty(symbol))
                throw new McpProtocolException("--symbol is required.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;

            var info = HandlerBootstrap.ResolveSymbolInfo(_session.Store, symbol, snapshotId, includeGenerated);
            if (info == null)
                throw McpErrorMapper.Map(new CliExitException($"ERROR: Symbol '{symbol}' not found in snapshot '{snapshotId}'. Pass the full 'docCommentId|assemblyIdentity' symbol ID, a doc-comment ID (e.g. T:Some.Type), or a fully-qualified name (e.g. Some.Namespace.Type).", 1));

            var locations = _session.Store.GetDeclarationLocations(info.SymbolId.Value, snapshotId, includeGenerated);
            var freshness = _session.GetFreshnessJson();

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
                metadata_json = info.MetadataJson,
                declaration_count = info.DeclarationCount,
                is_partial = info.IsPartial,
                locations
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
