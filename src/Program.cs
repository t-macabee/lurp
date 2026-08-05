using System;
using System.Collections.Generic;
using System.Linq;
using Lurp.Handlers;
using Lurp.Workspace;

namespace Lurp;

public class Program
{
    private static readonly Dictionary<string, Func<string[], Task>> ModeHandlers = new(StringComparer.Ordinal)
    {
        ["get-source"] = Sync(GetSourceHandler.Run),
        ["get-symbol"] = Sync(GetSymbolHandler.Run),
        ["search"] = Sync(SearchHandler.Run),
        ["find-symbol"] = Sync(FindSymbolHandler.Run),
        ["navigate"] = Sync(NavigateHandler.Run),
        ["index"] = a => IndexHandler.Run(a),
        ["diff"] = Sync(DiffHandler.Run),
        ["impact"] = Sync(ImpactHandler.Run),
        ["context"] = Sync(ContextHandler.Run),
        ["status"] = StatusHandler.Run,
        ["simulate-rename"] = Sync(SimulateRenameHandler.Run),
        ["simulate-move"] = Sync(SimulateMoveHandler.Run),
        ["simulate-remove"] = Sync(SimulateRemoveHandler.Run),
        ["audit"] = Sync(AuditHandler.Run),
        ["timings"] = Sync(TimingsHandler.Run),
        ["annotate"] = Sync(AnnotationHandler.RunAnnotate),
        ["get-annotations"] = Sync(AnnotationHandler.RunGetAnnotations),
    };

    private static Func<string[], Task> Sync(Action<string[]> handler)
        => args => { handler(args); return Task.CompletedTask; };

    public static async Task Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("--mode=help") || args.Length == 0)
        {
            HelpText.PrintHelp();
            return;
        }

        var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode="));
        if (modeArg is null)
        {
            HelpText.PrintUnknownModeError();
            Environment.Exit(1);
            return;
        }

        var mode = modeArg["--mode=".Length..];
        if (ModeHandlers.TryGetValue(mode, out var handler))
        {
            try
            {
                await handler(args);
            }
            catch (WorkspaceUnreadableException ex)
            {
                // A diagnosed refusal, not a crash. The operator needs the remediation
                // and an exit code, not a stack trace into Lurp's internals.
                Console.Error.WriteLine();
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                Environment.Exit(2);
            }
        }
        else
        {
            HelpText.PrintUnknownModeError();
            Environment.Exit(1);
        }
    }
}
