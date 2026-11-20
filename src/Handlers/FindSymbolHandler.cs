using System.Text.Json;
using System.Text.Json.Nodes;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class FindSymbolHandler
{
    public static void Run(string[] args, IOutputSink output)
    {
        var symbolArg = HandlerBootstrap.RequireFlag(args, FlagCatalog.Symbol, "find-symbol");

        var includeGenerated = args.Contains("--include-generated");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var info = HandlerBootstrap.ResolveSymbolInfo(store, symbolArg, snapshotId, includeGenerated);

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

            var locations = store.GetDeclarationLocations(info.SymbolId.Value, snapshotId, includeGenerated: true);

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

            JsonObject? node = null;
            JsonObject? meta = null;
            if (outputMode == OutputMode.Jsonl)
            {
                node = JsonSerializer.SerializeToNode(payload, HandlerBootstrap.CompactJson)!.AsObject();
                var snapshot = node["snapshot_id"]!;
                var stamp = node["freshness"]!;
                node.Remove("snapshot_id");
                node.Remove("freshness");
                meta = new JsonObject { ["snapshot_id"] = snapshot, ["freshness"] = stamp };
            }

            CliOutputWriter.Write(outputMode, output, () =>
            {
                output.WriteLine($"{payload.fully_qualified_name} ({payload.kind})");
                output.WriteLine($"  symbol_id: {payload.symbol_id}");
                if (locations.Count > 0)
                    output.WriteLine($"  location: {locations[0].DocumentPath}:{locations[0].StartLine}");
                output.WriteLine($"  declarations: {payload.declaration_count}  partial: {payload.is_partial}");
                output.WriteLine($"  snapshot: {snapshotId}  freshness: {freshness.State}");
            },
            () => payload,
            () => meta!,
            CliOutputWriter.JsonlItemTypes["find-symbol"],
            node is null ? [] : new[] { node });

        }, output);
    }
}
