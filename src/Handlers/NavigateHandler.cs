using System.Globalization;
using System.Text.Json;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class NavigateHandler
{
    public static void Run(string[] args, IOutputSink output)
    {
        var file = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--file="));
        var lineArg = HandlerBootstrap.GetArgValue(args, "--line=");
        var line = 0;
        if (string.IsNullOrEmpty(file) || !int.TryParse(lineArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out line) || line < 1)
            HandlerBootstrap.Fail("ERROR: --file=<relative-path> and positive --line=<number> are required for --mode=navigate.");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshot) =>
        {
            if (!store.DocumentExists(file!, snapshot))
                HandlerBootstrap.Fail($"ERROR: {DocumentMessages.NotFound(file!, snapshot)}");

            var target = store.NavigateToLocation(file!, line, snapshot, args.Contains("--include-generated"));
            if (target == null) HandlerBootstrap.Fail($"ERROR: No indexed declaration contains {file}:{line} in snapshot '{snapshot}'.");

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshot, output);
            output.WriteLine(JsonSerializer.Serialize(new { snapshot_id = snapshot, freshness = FreshnessJson.Cheap(freshness), target }, HandlerBootstrap.IndentedJson));
        }, output);
    }
}