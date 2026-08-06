using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class AnnotationHandler
{
    public static void RunAnnotate(string[] args)
    {
        var symbolArg = HandlerBootstrap.RequireArg(args, "--symbol=", "ERROR: --symbol=<symbolId> is required for --mode=annotate.");
        var kindArg = HandlerBootstrap.RequireArg(args, "--annotation-kind=", "ERROR: --annotation-kind=<kind> is required for --mode=annotate.");
        var valueArg = HandlerBootstrap.RequireArg(args, "--value=", "ERROR: --value=<text> is required for --mode=annotate.");

        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);
            var annotation = new AnnotationRecord(symbolArg!, kindArg!, valueArg!);
            store.SaveAnnotations(snapshotId, new[] { annotation });

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                snapshot_id = snapshotId,
                symbol_id = symbolArg,
                kind = kindArg,
                value = valueArg
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            store.Close();
        }
    }

    public static void RunGetAnnotations(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        // --symbol is optional for get-annotations; when absent, list all annotations

        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);
            var annotations = store.GetAnnotations(snapshotId, string.IsNullOrEmpty(symbolArg) ? null : symbolArg);

            var result = new
            {
                snapshot_id = snapshotId,
                symbol_id = symbolArg,
                annotations = annotations.Select(a => new
                {
                    symbol_id = a.SymbolId,
                    kind = a.Kind,
                    value = a.Value
                }).ToList()
            };

            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            store.Close();
        }
    }
}
