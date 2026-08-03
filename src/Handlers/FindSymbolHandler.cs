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
            Console.Error.WriteLine("ERROR: --fqn=<name> is required for --mode=find-symbol.");
            Environment.Exit(1);
        }

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var includeGenerated = args.Contains("--include-generated");
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            var info = store.ResolveSymbolByFqn(fqnArg, snapshotId, includeGenerated);
            if (info == null)
            {
                Console.Error.WriteLine($"ERROR: Symbol with FQN '{fqnArg}' not found in snapshot '{snapshotId}'.");
                Environment.Exit(1);
            }

            var freshness = HandlerBootstrap.ComputeFreshnessStamp(store, snapshotId, args);
            HandlerBootstrap.EnforceRequireFresh(args, freshness);
            HandlerBootstrap.PrintFreshnessLine(freshness);

            var json = JsonSerializer.Serialize(new
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
            }, new JsonSerializerOptions { WriteIndented = true });

            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
