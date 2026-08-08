using System.Globalization;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class AuditHandler
{
    public static void Run(string[] args)
    {
        var checksArg = HandlerBootstrap.GetArgValue(args, "--checks=") ?? "all";
        var fanOutThresholdArg = HandlerBootstrap.GetArgValue(args, "--fan-out-threshold=");

        int fanOutThreshold = 20;
        if (!string.IsNullOrEmpty(fanOutThresholdArg) && !int.TryParse(fanOutThresholdArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out fanOutThreshold))
        {
            HandlerBootstrap.Fail("ERROR: --fan-out-threshold must be an integer.");
        }

        HandlerBootstrap.WithStore<object?>(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var checks = new HashSet<string>(checksArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
            var options = new AuditOptions(checks, fanOutThreshold);
            var engine = new AuditEngine(store, snapshotId);
            var report = engine.RunAudit(options);
            var json = JsonSerializer.Serialize(report, HandlerBootstrap.IndentedJson);
            Console.WriteLine(json);
            return null;
        });
    }

}
