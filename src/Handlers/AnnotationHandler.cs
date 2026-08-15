using System.Text.Json;

namespace Lurp.Handlers;

internal static class AnnotationHandler
{
    public static void RunAnnotate(string[] args)
    {
        var symbolArg = HandlerBootstrap.RequireArg(args, "--symbol=", "ERROR: --symbol=<symbolId> is required for --mode=annotate.");
        var kindArg = HandlerBootstrap.RequireArg(args, "--annotation-kind=", "ERROR: --annotation-kind=<kind> is required for --mode=annotate.");
        var valueArg = HandlerBootstrap.RequireArg(args, "--value=", "ERROR: --value=<text> is required for --mode=annotate.");

        HandlerBootstrap.WithStore<object?>(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var annotation = new AnnotationRecord(symbolArg!, kindArg!, valueArg!);
            store.SaveAnnotations(snapshotId, new[] { annotation });

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                snapshot_id = snapshotId,
                symbol_id = symbolArg,
                kind = kindArg,
                value = valueArg
            }, HandlerBootstrap.IndentedJson));
            return null;
        });
    }

    public static void RunGetAnnotations(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");

        HandlerBootstrap.WithStore<object?>(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
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

            Console.WriteLine(JsonSerializer.Serialize(result, HandlerBootstrap.IndentedJson));
            return null;
        });
    }
}
