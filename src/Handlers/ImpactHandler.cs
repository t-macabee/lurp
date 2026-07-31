using System.Globalization;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class ImpactHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg))
        {
            Console.Error.WriteLine("ERROR: --symbol=<symbol-id> is required for --mode=impact.");
            Environment.Exit(1);
        }

        var directionArg = HandlerBootstrap.GetArgValue(args, "--direction=") ?? "downstream";
        ImpactDirection direction = directionArg.ToLowerInvariant() switch
        {
            "downstream" => ImpactDirection.Downstream,
            "upstream" => ImpactDirection.Upstream,
            _ => throw new ArgumentException($"Invalid direction '{directionArg}'. Use 'upstream' or 'downstream'.")
        };

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var maxDepthArg = HandlerBootstrap.GetArgValue(args, "--max-depth=");
        int maxDepth = 10;
        if (!string.IsNullOrEmpty(maxDepthArg) && (!int.TryParse(maxDepthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxDepth) || maxDepth < 1))
        {
            Console.Error.WriteLine("ERROR: --max-depth must be a positive integer.");
            Environment.Exit(1);
        }

        var kindsArg = HandlerBootstrap.GetArgValue(args, "--kinds=");
        HashSet<string>? allowedKinds = !string.IsNullOrEmpty(kindsArg)
            ? [.. kindsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;

        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            var traverser = new ImpactTraverser(store, snapshotId, store);
            var paths = traverser.TraceImpact(symbolId: symbolArg, direction: direction, allowedEdgeKinds: allowedKinds, maxDepth: maxDepth, includeSource: true);

            var json = JsonSerializer.Serialize(new
            {
                snapshot_id = snapshotId,
                symbol_id = symbolArg,
                direction = direction == ImpactDirection.Downstream ? "downstream" : "upstream",
                max_depth = maxDepth,
                paths = paths.Select(p => new
                {
                    truncated = p.Truncated,
                    truncation_reason = p.TruncationReason,
                    total_steps = p.TotalSteps,
                    hops = p.Hops.Select(h => new { source_symbol_id = h.SourceSymbolId, target_symbol_id = h.TargetSymbolId, edge_kind = h.EdgeKind, provenance = h.Provenance, source_document = h.SourceDocument, source_line = h.SourceLine }),
                    semantic_causes = p.SemanticCauses.Select(c => new
                    {
                        from_snapshot_id = c.FromSnapshotId,
                        to_snapshot_id = c.ToSnapshotId,
                        change_type = c.ChangeType,
                        symbol_id = c.SymbolId,
                        detail = c.DetailJson != null ? JsonSerializer.Deserialize<object>(c.DetailJson) : null
                    })
                })
            }, new JsonSerializerOptions { WriteIndented = true });

            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
