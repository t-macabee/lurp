using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class FindSymbolHandler
{
    public static void Run(string[] args)
    {
        var fqnArg = HandlerBootstrap.GetArgValue(args, "--fqn=");
        if (string.IsNullOrEmpty(fqnArg))
        {
            HandlerBootstrap.Fail("ERROR: --fqn=<name> is required for --mode=find-symbol.");
        }

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var includeGenerated = args.Contains("--include-generated");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            var info = store.ResolveSymbolByFqn(fqnArg, snapshotId, includeGenerated);
            if (info == null)
            {
                HandlerBootstrap.Fail($"ERROR: Symbol with FQN '{fqnArg}' not found in snapshot '{snapshotId}'.");
            }

            var freshness = HandlerBootstrap.ComputeFreshnessStamp(store, store, snapshotId, args);
            HandlerBootstrap.EnforceRequireFresh(args, freshness);
            HandlerBootstrap.PrintFreshnessLine(args, freshness);

            var payload = new
            {
                symbolId = info.SymbolId.Value,
                docCommentId = info.SymbolId.DocCommentId,
                assemblyIdentity = info.SymbolId.AssemblyIdentity,
                kind = info.Kind.ToString(),
                fullyQualifiedName = info.FullyQualifiedName,
                metadataJson = info.MetadataJson,
                declarationCount = info.DeclarationCount,
                isPartial = info.IsPartial,
                snapshotId,
                freshness = HandlerBootstrap.FreshnessJson(freshness)
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    HandlerBootstrap.Out.WriteLine($"{payload.fullyQualifiedName} ({payload.kind})");
                    HandlerBootstrap.Out.WriteLine($"  symbolId: {payload.symbolId}");
                    HandlerBootstrap.Out.WriteLine($"  declarations: {payload.declarationCount}  partial: {payload.isPartial}");
                    HandlerBootstrap.Out.WriteLine($"  snapshot: {snapshotId}  freshness: {freshness.State}");
                    break;

                // A single symbol is one record, so jsonl is that record on one line :
                // the same field contract, just streamable alongside other jsonl output.
                case OutputMode.Jsonl:
                    HandlerBootstrap.Out.WriteLine(JsonSerializer.Serialize(payload, HandlerBootstrap.CompactJson));
                    break;

                default:
                    HandlerBootstrap.Out.WriteLine(JsonSerializer.Serialize(payload, HandlerBootstrap.IndentedJson));
                    break;
            }
        }
        finally
        {
            store.Close();
        }
    }
}
