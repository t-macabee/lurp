using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Workspace;

namespace Lurp;

public static class Program
{
    /// <summary>
    ///     Single source of truth for the CLI mode registry : mode name, one-line help
    ///     text, flag inventory, and dispatch handler. The unknown-mode error's mode list
    ///     and the <c>--help</c> MODES block are both derived from it, so adding a mode
    ///     is exactly one edit. The order is the presentation order used by both
    ///     <c>--help</c> and the unknown-mode error.
    /// </summary>
    internal static readonly ModeRegistryEntry[] ModeRegistry =
    [
        new("index", "Index a solution and store facts in the database.",
            ["--solution=", "--strategy=", "--output-json=", "--skip-adapter=", "--skip-diff", "--verbose", "--force"],
            a => IndexHandler.Run(a)),
        new("get-source", "Retrieve source text for a document by relative path (--document=).",
            ["--document=", "--start-line=", "--end-line=", "--context-lines=", "--snapshot=", "--freshness=", "--require-fresh", "--quiet"],
            Sync(GetSourceHandler.Run)),
        new("get-symbol", "Look up symbol metadata.",
            [
                "--symbol=", "--view=", "--context-lines=", "--include-generated", "--snapshot=",
                "--freshness=", "--require-fresh", "--quiet"
            ],
            Sync(GetSymbolHandler.Run)),
        new("search", "Full-text search over source and symbols.",
            [
                "--query=", "--type=", "--kind=", "--limit=", "--snippet-tokens=", "--cursor=",
                "--include-generated", "--snapshot=", "--output=", "--freshness=", "--require-fresh", "--quiet"
            ],
            Sync(SearchHandler.Run)),
        new("find-symbol", "Resolve a symbol by FQN.",
            ["--symbol=", "--include-generated", "--snapshot=", "--output=", "--freshness=", "--require-fresh", "--quiet"],
            Sync(FindSymbolHandler.Run)),
        new("navigate", "Resolve an indexed declaration by file and line.",
            ["--file=", "--line=", "--include-generated", "--snapshot=", "--freshness=", "--require-fresh", "--quiet"],
            Sync(NavigateHandler.Run)),
        new("diff", "Show semantic changes between two snapshots.",
            ["--from-snapshot=", "--to-snapshot="],
            Sync(DiffHandler.Run)),
        new("impact", "Trace the impact path of a changed symbol.",
            [
                "--symbol=", "--direction=", "--kinds=", "--provenance=", "--max-depth=", "--max-paths=", "--cursor=",
                "--snapshot=", "--output=", "--freshness=", "--require-fresh", "--quiet"
            ],
            Sync(ImpactHandler.Run)),
        new("context", "Assemble a context capsule for a symbol.",
            [
                "--symbol=", "--file=", "--line=", "--intent=", "--content-budget=", "--max-hops=", "--scope=",
                "--affected-project=", "--tier=", "--tier-limit=", "--cursor=", "--completeness-detail",
                "--include-generated", "--snapshot=", "--output=", "--freshness=", "--require-fresh", "--quiet"
            ],
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
        new("serve", "Run an MCP server over the index (stdio).",
            ["--solution="],
            McpServeHandler.Run)
    ];

    private static Func<string[], Task> Sync(Action<string[]> handler)
    {
        return args =>
        {
            handler(args);
            return Task.CompletedTask;
        };
    }

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
            catch (CliExitException ex)
            {
                // A diagnosed CLI failure (e.g. bad flags, missing database).
                // HandlerBootstrap.Fail throws this instead of exiting, so every
                // failure path is testable and process control lives here.
                // Sync flush is intentional: Environment.Exit terminates the process
                // immediately; a blocking WriteLine guarantees delivery to stderr
                // (console or redirected pipe) before exit. An async write would
                // require awaiting and risks fire-and-forget loss if a future edit
                // drops the await. Qodana MethodHasAsyncOverload intentionally suppressed.
                // ReSharper disable once MethodHasAsyncOverload
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
            catch (WorkspaceUnreadableException ex)
            {
                // A diagnosed refusal, not a crash. The operator needs the remediation
                // and an exit code, not a stack trace into Lurp's internals.
                // Sync flush is intentional: see comment above. Both writes must
                // complete before the unconditional Environment.Exit(2) below.
                // ReSharper disable once MethodHasAsyncOverload
                Console.Error.WriteLine();
                // ReSharper disable once MethodHasAsyncOverload
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

    /// <summary>
    ///     One CLI mode: name, one-line help, the flags it reads, and its dispatch target.
    ///     <para>
    ///         <paramref name="Flags" /> is the mode's complete flag inventory excluding the globals
    ///         in <see cref="CliFlagValidation" />. A valued flag is written with its trailing '='
    ///         (<c>"--content-budget="</c>) and a bare flag without one (<c>"--quiet"</c>); the validator
    ///         matches on that shape, so the two forms are not interchangeable. Flags a handler reads
    ///         indirectly through a <see cref="Handlers.HandlerBootstrap" /> helper belong here too :
    ///         the helper call is invisible to the validator.
    ///     </para>
    /// </summary>
    internal sealed record ModeRegistryEntry(string Name, string HelpText, string[] Flags, Func<string[], Task> Handler);
}