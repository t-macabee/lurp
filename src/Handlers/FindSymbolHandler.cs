using System.Text.Json;
using System.Text.Json.Nodes;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class FindSymbolHandler
{
    public static void Run(string[] args, IOutputSink output)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg)) HandlerBootstrap.Fail("ERROR: --symbol=<name> is required for --mode=find-symbol.");

        var includeGenerated = args.Contains("--include-generated");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var info = HandlerBootstrap.ResolveSymbolInfo(store, symbolArg, snapshotId, includeGenerated);
            if (info == null)
                HandlerBootstrap.Fail(
                    $"ERROR: Symbol '{symbolArg}' not found in snapshot '{snapshotId}'. " +
                    "Pass the full 'docCommentId|assemblyIdentity' symbol ID, a doc-comment ID (e.g. T:Some.Type), " +
                    "or a fully-qualified name (e.g. Some.Namespace.Type).");

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

            var locations = store.GetDeclarationLocations(info.SymbolId.Value, snapshotId, includeGenerated);

            var payload = new
            {
                symbol_id = info.SymbolId.Value,
                doc_comment_id = info.SymbolId.DocCommentId,
                assembly_identity = info.SymbolId.AssemblyIdentity,
                kind = info.Kind.ToString(),
                fully_qualified_name = info.FullyQualifiedName,
                metadata = SymbolMetadata.Parse(info.MetadataJson, info.SymbolId.Value),
                declaration_count = info.DeclarationCount,
                is_partial = info.IsPartial,
                snapshot_id = snapshotId,
                freshness = FreshnessJson.Cheap(freshness),
                locations
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    output.WriteLine($"{payload.fully_qualified_name} ({payload.kind})");
                    output.WriteLine($"  symbol_id: {payload.symbol_id}");
                    if (locations.Count > 0)
                        output.WriteLine($"  location: {locations[0].DocumentPath}:{locations[0].StartLine}");
                    output.WriteLine($"  declarations: {payload.declaration_count}  partial: {payload.is_partial}");
                    output.WriteLine($"  snapshot: {snapshotId}  freshness: {freshness.State}");
                    break;

                case OutputMode.Jsonl:
                    var node = JsonSerializer.SerializeToNode(payload, HandlerBootstrap.CompactJson)!.AsObject();
                    var meta = new JsonObject
                    {
                        ["snapshot_id"] = node["snapshot_id"]!.DeepClone(),
                        ["freshness"] = node["freshness"]!.DeepClone()
                    };
                    node.Remove("snapshot_id");
                    node.Remove("freshness");
                    output.WriteLine(JsonSerializer.Serialize(new JsonObject { ["type"] = "meta", ["meta"] = meta }, HandlerBootstrap.CompactJson));
                    output.WriteLine(JsonSerializer.Serialize(new JsonObject { ["type"] = "symbol", ["symbol"] = node }, HandlerBootstrap.CompactJson));
                    break;

                // default: Json is the historical default — intentional fallback for OutputMode.Json and future values
                default:
                    output.WriteLine(JsonSerializer.Serialize(payload, HandlerBootstrap.IndentedJson));
                    break;
            }

        }, output);
    }
}