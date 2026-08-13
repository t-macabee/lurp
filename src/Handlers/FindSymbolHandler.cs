using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class FindSymbolHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg))
        {
            HandlerBootstrap.Fail("ERROR: --symbol=<name> is required for --mode=find-symbol.");
        }

        var includeGenerated = args.Contains("--include-generated");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        HandlerBootstrap.WithStore<object?>(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var info = HandlerBootstrap.ResolveSymbolInfo(store, symbolArg, snapshotId, includeGenerated);
            if (info == null)
            {
                HandlerBootstrap.Fail(
                    $"ERROR: Symbol '{symbolArg}' not found in snapshot '{snapshotId}'. " +
                    "Pass the full 'docCommentId|assemblyIdentity' symbol ID, a doc-comment ID (e.g. T:Some.Type), " +
                    "or a fully-qualified name (e.g. Some.Namespace.Type).");
            }

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var locations = store.GetDeclarationLocations(info.SymbolId.Value, snapshotId, includeGenerated);

            var payload = new
            {
                symbol_id = info.SymbolId.Value,
                doc_comment_id = info.SymbolId.DocCommentId,
                assembly_identity = info.SymbolId.AssemblyIdentity,
                kind = info.Kind.ToString(),
                fully_qualified_name = info.FullyQualifiedName,
                metadata_json = info.MetadataJson,
                declaration_count = info.DeclarationCount,
                is_partial = info.IsPartial,
                snapshot_id = snapshotId,
                freshness = HandlerBootstrap.FreshnessJson(freshness),
                locations = locations
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    Console.WriteLine($"{payload.fully_qualified_name} ({payload.kind})");
                    Console.WriteLine($"  symbol_id: {payload.symbol_id}");
                    if (locations.Count > 0)
                        Console.WriteLine($"  location: {locations[0].DocumentPath}:{locations[0].StartLine}");
                    Console.WriteLine($"  declarations: {payload.declaration_count}  partial: {payload.is_partial}");
                    Console.WriteLine($"  snapshot: {snapshotId}  freshness: {freshness.State}");
                    break;

                case OutputMode.Jsonl:
                    Console.WriteLine(JsonSerializer.Serialize(payload, HandlerBootstrap.CompactJson));
                    break;

                default:
                    Console.WriteLine(JsonSerializer.Serialize(payload, HandlerBootstrap.IndentedJson));
                    break;
            }

            return null;
        });
    }
}
