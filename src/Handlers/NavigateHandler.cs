using Lurp.Queries;
using System.Globalization;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class NavigateHandler
{
    public static void Run(string[] args)
    {
        var file = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--file="));
        var lineArg = HandlerBootstrap.GetArgValue(args, "--line=");
        var line = 0;
        if (string.IsNullOrEmpty(file) || !int.TryParse(lineArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out line) || line < 1)
            HandlerBootstrap.Fail("ERROR: --file=<relative-path> and positive --line=<number> are required for --mode=navigate.");

        HandlerBootstrap.WithStore<object?>(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshot) =>
        {
            var target = new FastTravelQueries(store).Navigate(file!, line, snapshot, args.Contains("--include-generated"));
            if (target == null) HandlerBootstrap.Fail($"ERROR: No indexed declaration contains {file}:{line} in snapshot '{snapshot}'.");

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshot);
            Console.WriteLine(JsonSerializer.Serialize(new { snapshot_id = snapshot, freshness = HandlerBootstrap.FreshnessJson(freshness), target }, HandlerBootstrap.IndentedJson));
            return null;
        });
    }
}