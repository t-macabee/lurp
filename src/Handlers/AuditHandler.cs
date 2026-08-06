using System.Globalization;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class AuditHandler
{
    public static void Run(string[] args)
    {
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var checksArg = HandlerBootstrap.GetArgValue(args, "--checks=") ?? "all";
        var fanOutThresholdArg = HandlerBootstrap.GetArgValue(args, "--fan-out-threshold=");

        int fanOutThreshold = 20;
        if (!string.IsNullOrEmpty(fanOutThresholdArg) && !int.TryParse(fanOutThresholdArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out fanOutThreshold))
        {
            HandlerBootstrap.Fail("ERROR: --fan-out-threshold must be an integer.");
        }

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            var checks = new HashSet<string>(checksArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
            var options = new AuditOptions(checks, fanOutThreshold);
            var engine = new AuditEngine(store, snapshotId);
            var report = engine.RunAudit(options);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            HandlerBootstrap.Out.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }

}
