using System;
using System.Collections.Generic;
using System.Linq;
using Lurp.Handlers;
using Lurp.Workspace;

namespace Lurp;

public class Program
{
    /// <summary>
    /// One CLI mode: name, one-line help, the flags it reads, and its dispatch target.
    /// <para>
    /// <paramref name="Flags"/> is the mode's complete flag inventory excluding the globals
    /// in <see cref="CliFlagValidation"/>. A valued flag is written with its trailing '='
    /// (<c>"--content-budget="</c>) and a bare flag without one (<c>"--quiet"</c>); the validator
    /// matches on that shape, so the two forms are not interchangeable. Flags a handler reads
    /// indirectly through a <see cref="Handlers.HandlerBootstrap"/> helper belong here too :
    /// the helper call is invisible to the validator.
    /// </para>
    /// </summary>
    internal sealed record ModeRegistryEntry(string Name, string HelpText, string[] Flags, Func<string[], Task> Handler);

    /// <summary>
    /// Single source of truth for the CLI mode registry : mode name, one-line help
    /// text, flag inventory, and dispatch handler. The unknown-mode error's mode list
    /// and the <c>--help</c> MODES block are both derived from it, so adding a mode
    /// is exactly one edit. The order is the presentation order used by both
    /// <c>--help</c> and the unknown-mode error.
    /// </summary>
    internal static readonly ModeRegistryEntry[] ModeRegistry =
    [
        new("index", "Index a solution and store facts in the database.",
            ["--solution=", "--strategy=", "--output-json=", "--skip-adapter=", "--skip-diff", "--verbose"],
            a => IndexHandler.Run(a)),
        new("get-source", "Retrieve source text for a document by relative path (--document=).",
            ["--document=", "--snapshot="],
            Sync(GetSourceHandler.Run)),
        new("get-symbol", "Look up symbol metadata.",
            ["--symbol=", "--view=", "--context-lines=", "--include-generated", "--snapshot="],
            Sync(GetSymbolHandler.Run)),
        new("search", "Full-text search over source and symbols.",
            ["--query=", "--type=", "--kind=", "--limit=", "--snippet-tokens=", "--cursor=",
             "--include-generated", "--snapshot=", "--output=", "--freshness=", "--require-fresh", "--quiet"],
            Sync(SearchHandler.Run)),
        new("find-symbol", "Resolve a symbol by FQN.",
            ["--symbol=", "--include-generated", "--snapshot=", "--output=", "--freshness=", "--require-fresh", "--quiet"],
            Sync(FindSymbolHandler.Run)),
        new("navigate", "Resolve an indexed declaration by file and line.",
            ["--file=", "--line=", "--include-generated", "--snapshot="],
            Sync(NavigateHandler.Run)),
        new("diff", "Show semantic changes between two snapshots.",
            ["--from-snapshot=", "--to-snapshot="],
            Sync(DiffHandler.Run)),
        new("impact", "Trace the impact path of a changed symbol.",
            ["--symbol=", "--direction=", "--kinds=", "--max-depth=", "--max-paths=", "--cursor=",
             "--snapshot=", "--output=", "--freshness=", "--require-fresh", "--quiet"],
            Sync(ImpactHandler.Run)),
        new("context", "Assemble a context capsule for a symbol.",
             ["--symbol=", "--file=", "--line=", "--intent=", "--content-budget=", "--max-hops=", "--scope=",
             "--change-objective=", "--affected-project=", "--constraint=", "--topology-annotation=",
             "--target-hop=", "--tier=", "--tier-limit=", "--cursor=", "--completeness-detail",
             "--include-generated", "--snapshot=", "--output=", "--freshness=", "--require-fresh", "--quiet"],
            Sync(ContextHandler.Run)),
        new("status", "Show the current database status.",
            ["--solution=", "--detail=", "--json", "--output="],
            StatusHandler.Run),
        new("timings", "Show step-by-step timing data for a snapshot.",
            ["--snapshot=", "--json"],
            Sync(TimingsHandler.Run)),
        new("annotate", "Attach a user-authored annotation to a symbol.",
            ["--symbol=", "--annotation-kind=", "--value=", "--snapshot="],
            Sync(AnnotationHandler.RunAnnotate)),
        new("get-annotations", "Retrieve annotations for a symbol.",
            ["--symbol=", "--snapshot="],
            Sync(AnnotationHandler.RunGetAnnotations)),
    ];

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
        var entry = ModeRegistry.FirstOrDefault(e => string.Equals(e.Name, mode, StringComparison.Ordinal));
        if (entry != null)
        {
            try
            {
                CliFlagValidation.Validate(entry, args);
                await entry.Handler(args);
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
