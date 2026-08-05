using System;
using System.Collections.Generic;
using System.Linq;
using Lurp.Handlers;
using Lurp.Workspace;

namespace Lurp;

public class Program
{
    internal sealed record ModeRegistryEntry(string Name, string HelpText, Func<string[], Task> Handler);

    /// <summary>
    /// Single source of truth for the CLI mode registry : mode name, one-line help
    /// text, and dispatch handler. <see cref="ModeHandlers"/>, the unknown-mode
    /// error's mode list, and the <c>--help</c> MODES block are all derived from it,
    /// so adding a mode is exactly one edit. The order is the presentation order
    /// used by both <c>--help</c> and the unknown-mode error.
    /// </summary>
    internal static readonly ModeRegistryEntry[] ModeRegistry =
    [
        new("index", "Index a solution and store facts in the database.", a => IndexHandler.Run(a)),
        new("get-source", "Retrieve source text for a document by relative path (--document=).", Sync(GetSourceHandler.Run)),
        new("get-symbol", "Look up symbol metadata.", Sync(GetSymbolHandler.Run)),
        new("search", "Full-text search over source and symbols.", Sync(SearchHandler.Run)),
        new("find-symbol", "Resolve a symbol by FQN.", Sync(FindSymbolHandler.Run)),
        new("navigate", "Resolve an indexed declaration by file and line.", Sync(NavigateHandler.Run)),
        new("diff", "Show semantic changes between two snapshots.", Sync(DiffHandler.Run)),
        new("impact", "Trace the impact path of a changed symbol.", Sync(ImpactHandler.Run)),
        new("context", "Assemble a context capsule for a symbol.", Sync(ContextHandler.Run)),
        new("status", "Show the current database status.", StatusHandler.Run),
        new("timings", "Show step-by-step timing data for a snapshot.", Sync(TimingsHandler.Run)),
        new("simulate-rename", "Simulate renaming a symbol and show affected references.", Sync(SimulateRenameHandler.Run)),
        new("simulate-move", "Simulate moving a symbol to a new namespace.", Sync(SimulateMoveHandler.Run)),
        new("simulate-remove", "Simulate removing a symbol and show cascading impact.", Sync(SimulateRemoveHandler.Run)),
        new("audit", "Run static analysis checks on the index.", Sync(AuditHandler.Run)),
        new("annotate", "Attach a user-authored annotation to a symbol.", Sync(AnnotationHandler.RunAnnotate)),
        new("get-annotations", "Retrieve annotations for a symbol.", Sync(AnnotationHandler.RunGetAnnotations)),
    ];

    private static readonly Dictionary<string, Func<string[], Task>> ModeHandlers =
        ModeRegistry.ToDictionary(entry => entry.Name, entry => entry.Handler, StringComparer.Ordinal);

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
