using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Storage;
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
            [
                FlagCatalog.Solution with
                {
                    Requirement = FlagRequirement.Required,
                    EnvironmentVariable = "LURP_SOLUTION_PATH",
                },
                FlagCatalog.Strategy, FlagCatalog.OutputJson, FlagCatalog.SkipAdapter, FlagCatalog.SkipDiff, FlagCatalog.Verbose, FlagCatalog.Force
            ],
            a => IndexHandler.Run(a)),
        new("get-source", "Retrieve source text for a document by relative path (--document=).",
            [
                FlagCatalog.Document with { Requirement = FlagRequirement.Required }, FlagCatalog.StartLine, FlagCatalog.EndLine,
                FlagCatalog.ContextLines,
                FlagCatalog.Snapshot,
                FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(GetSourceHandler.Run)),
        new("outline", "List declarations in a document with line spans (--document=).",
            [
                FlagCatalog.Document with { Requirement = FlagRequirement.Required },
                FlagCatalog.IncludeGenerated with { Description = "Include source-generated declarations." },
                FlagCatalog.Limit, FlagCatalog.Cursor, FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(OutlineHandler.Run)),
        new("get-symbol", "Look up symbol metadata.",
            [
                FlagCatalog.Symbol with { Requirement = FlagRequirement.Required },
                FlagCatalog.View with { Requirement = FlagRequirement.Required },
                FlagCatalog.ContextLines with { Description = $"Lines of context for `--view=surrounding` (default: {GetSymbolHandler.DefaultContextLines})." },
                FlagCatalog.IncludeGenerated,
                FlagCatalog.Snapshot,
                FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(GetSymbolHandler.Run)),
        new("search", "Full-text search over source and symbols.",
            [
                FlagCatalog.Query with { Requirement = FlagRequirement.Required },
                FlagCatalog.Type, FlagCatalog.Kind,
                FlagCatalog.Limit with { Description = $"Max results per scope (default: {SearchHandler.DefaultLimit})." },
                FlagCatalog.SnippetTokens,
                FlagCatalog.Cursor with { Description = "Continue from a previous page's `next_cursor` (`--type=symbol` only)." },
                FlagCatalog.IncludeGenerated,
                FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(SearchHandler.Run)),
        new("find-symbol", "Resolve a symbol by FQN.",
            [
                FlagCatalog.Symbol with { ValueHint = "name", Description = "Fully qualified name to resolve.", Requirement = FlagRequirement.Required },
                FlagCatalog.IncludeGenerated,
                FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(FindSymbolHandler.Run)),
        new("navigate", "Resolve an indexed declaration by file and line.",
            [
                FlagCatalog.File with { Requirement = FlagRequirement.Required },
                FlagCatalog.Line with { Requirement = FlagRequirement.Required },
                FlagCatalog.IncludeGenerated with { Description = "Include source-generated declarations." },
                FlagCatalog.Snapshot,
                FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(NavigateHandler.Run)),
        new("diff", "Show semantic changes between two snapshots.",
            [FlagCatalog.FromSnapshot with { Requirement = FlagRequirement.Required }, FlagCatalog.ToSnapshot with { Requirement = FlagRequirement.Required }],
            Sync(DiffHandler.Run)),
        new("impact", "List the symbols a changed symbol can reach, each with a witness path.",
            [
                FlagCatalog.Symbol with { Description = "The symbol ID to trace from. Accepts the full `docCommentId|assemblyIdentity` form, a bare doc-comment ID (e.g. `T:Some.Type`), or a fully-qualified name.", Requirement = FlagRequirement.Required },
                FlagCatalog.Direction, FlagCatalog.Kinds, FlagCatalog.Provenance, FlagCatalog.MaxDepth,
                FlagCatalog.Limit with { Description = $"Symbols per page (default: {ImpactHandler.DefaultLimit}). When more exist, the response carries `truncated.{{reason,returned,total,remaining,cursor}}`." },
                FlagCatalog.Cursor with { Description = "Continue from a previous page's `truncated.cursor`." },
                FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(ImpactHandler.Run)),
        new("context", "Assemble a context capsule for a symbol.",
            [
                FlagCatalog.Symbol with { Description = "Symbol ID to anchor on. Accepts the full `docCommentId|assemblyIdentity` form, a bare doc-comment ID (e.g. `T:Some.Type`), or a fully-qualified name.", Requirement = FlagRequirement.Conditional },
                FlagCatalog.File with { Description = "Source file path (use with `--line`).", Requirement = FlagRequirement.Conditional },
                FlagCatalog.Line with { Description = "Line number in the source file.", Requirement = FlagRequirement.Conditional },
                FlagCatalog.Intent, FlagCatalog.ContentBudget, FlagCatalog.MaxHops, FlagCatalog.Scope,
                FlagCatalog.AffectedProject, FlagCatalog.Tier, FlagCatalog.TierLimit,
                FlagCatalog.Cursor with { Description = "Continue a tier from its `next_cursor` (`--tier` only)." },
                FlagCatalog.CompletenessDetail,
                FlagCatalog.IncludeGenerated,
                FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(ContextHandler.Run)),
        new("status", "Show the current database status.",
            [
                FlagCatalog.Solution with { Description = "If provided, compares the current workspace against the latest snapshot and reports freshness mismatches." },
                FlagCatalog.Detail,
                FlagCatalog.Json with { Description = "Emit structured JSON instead of plain text. Back-compat alias for `--output=json`." },
                FlagCatalog.Output with { AllowedValues = ["summary", "json"], Description = "Payload rendering (default: `summary` when neither flag is given). `jsonl` is rejected — the payload is a single document." },
                FlagCatalog.MaxMismatches
            ],
            StatusHandler.Run),
        new("timings", "Show step-by-step timing data for a snapshot.",
            [FlagCatalog.Snapshot, FlagCatalog.Json],
            Sync(TimingsHandler.Run)),
        new("pin-snapshot", "Pin which snapshot reads use by default when --snapshot= is omitted.",
            [
                FlagCatalog.Snapshot with { Requirement = FlagRequirement.Conditional, Description = "Snapshot ID to pin. Only snapshots with `status='complete'` can be pinned. The literal `latest` is an alias for `--clear`." },
                FlagCatalog.Clear,
                FlagCatalog.Json with { Description = "Emit structured JSON instead of plain text. `--output=json` is an alias." },
                FlagCatalog.Output with { AllowedValues = ["summary", "json"], Description = "Payload rendering (default: `summary` when neither flag is given). `jsonl` is rejected — the payload is a single document." }
            ],
            Sync(PinSnapshotHandler.Run)),
        new("annotate", "Attach a user-authored annotation to a symbol.",
            [
                FlagCatalog.Symbol with { Description = "The symbol ID to annotate.", Requirement = FlagRequirement.Required },
                FlagCatalog.AnnotationKind, FlagCatalog.Value,
                FlagCatalog.Snapshot
            ],
            Sync(AnnotationHandler.RunAnnotate)),
        new("retract-annotation", "Retract one annotation by id, scoped to the resolved snapshot (any provenance, not limited to user-authored rows).",
            [
                FlagCatalog.AnnotationId,
                FlagCatalog.Snapshot with { Description = "Snapshot to act on (default: latest, or the pinned snapshot when one is set). The DELETE is `WHERE snapshot_id=@snapshot AND annotation_id=@id` — one row only, no cross-snapshot effect. Copy-forward clones (`CopyAnnotationsToSnapshot`) allocate fresh ids, so retracting in one snapshot does not delete its clone in another." }
            ],
            Sync(AnnotationHandler.RunRetractAnnotation)),
        new("get-annotations", "Retrieve annotations by symbol, document, or snapshot (with kind/limit/cursor paging).",
            [
                FlagCatalog.Symbol with { Description = "The symbol ID to query. Accepts the full `docCommentId|assemblyIdentity` form, a bare doc-comment ID, or a fully-qualified name. Mutually exclusive with `--document=`." },
                FlagCatalog.Document with { Description = "Relative path of the document within the solution. Mutually exclusive with `--symbol=`." },
                FlagCatalog.Kind with { Description = "Filter to one annotation kind.", ValueHint = "kind" },
                FlagCatalog.Limit with { Description = $"Max annotations per page (default: {AnnotationHandler.DefaultLimit}; positive integer)." },
                FlagCatalog.Cursor,
                FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(AnnotationHandler.RunGetAnnotations)),
        new("diagnostics", "List compiler diagnostics captured at index time (--document=, --project=, --severity=, --id=).",
            [
                FlagCatalog.Document with { Description = "Filter to diagnostics in this document." },
                FlagCatalog.Project,
                FlagCatalog.Severity, FlagCatalog.Id,
                FlagCatalog.Limit with { Description = $"Max diagnostics per page (default: {DiagnosticsHandler.DefaultLimit})." },
                FlagCatalog.Cursor,
                FlagCatalog.IncludeGenerated with { Description = "Include diagnostics located in generated files (the same `obj/**`, `*.g.cs`, `*.generated.cs`, `*.Designer.cs`, `*ModelSnapshot.cs` patterns as every other read mode's `--include-generated`). Default excludes them." },
                FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(DiagnosticsHandler.Run)),
        new("grep", "Literal/exact-text search over source content with line numbers (--query=).",
            [
                FlagCatalog.Query with { Description = "Exact substring to find. The match is byte-exact, including punctuation and spacing. Case-sensitive by default; pass `--ignore-case` for case-insensitive.", Requirement = FlagRequirement.Required },
                FlagCatalog.Limit with { Description = $"Max matches per page (default: {GrepHandler.DefaultLimit})." },
                FlagCatalog.Cursor,
                FlagCatalog.IgnoreCase,
                FlagCatalog.IncludeGenerated with { Description = "Include source-generated documents." },
                FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet
            ],
            Sync(GrepHandler.Run)),
        new("dead-candidates", "List dead-code candidates: symbols with no incoming LIVE edge after suppression-ladder filtering.",
            [
                FlagCatalog.Project with { Description = "Filter to this project (assembly simple name, e.g. `eNote.API`)." },
                FlagCatalog.Document with { Description = "Filter to symbols declared in this document (forward-slash, relative to the solution root, not the repo root)." },
                FlagCatalog.Kind with { AllowedValues = DeadCandidateLiveness.CandidateKinds, Description = "Filter by symbol kind (case-insensitive)." },
                FlagCatalog.Limit with { Description = $"Max dead candidates per page (default: {DeadCandidatesHandler.DefaultLimit}, max: {DeadCandidateLiveness.MaxPageSize})." },
                FlagCatalog.Cursor,
                FlagCatalog.Snapshot,
                FlagCatalog.Output, FlagCatalog.Freshness, FlagCatalog.RequireFresh, FlagCatalog.Quiet,
                FlagCatalog.IncludePublic,
                FlagCatalog.IncludeGenerated with { Description = "Include source-generated symbols (default excludes `is_generated=1`). Surfaced as `uncertain` reason `generated_excluded` when no other signal." },
                FlagCatalog.IncludeTests
            ],
            Sync(DeadCandidatesHandler.Run)),
        new("serve", "Run an MCP server over the index (stdio).",
            [
                FlagCatalog.Solution with { Description = "The solution the session was started with. `lurp_index` indexes it, and `lurp_status` with `full:true` compares it to the snapshot." },
                FlagCatalog.EnableWriteTools
            ],
            McpServeHandler.Run)
    ];

    /// <summary>Finds a mode by exact name, or null. The single mode lookup for dispatch and tests.</summary>
    internal static ModeRegistryEntry? FindMode(string name) =>
        ModeRegistry.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    private static Func<string[], Task> Sync(Action<string[]> handler)
    {
        return args =>
        {
            handler(args);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    ///     Builds the two-line <c>--version</c> output: the tool version plus the
    ///     schema/extractor/contract versions this binary was built with. Extracted
    ///     from Main so the string is unit-testable without spawning a process;
    ///     Main stays a thin dispatcher.
    /// </summary>
    internal static string BuildVersionOutput()
    {
        return
            $"lurp {VersionConstants.ToolVersion}\n" +
            $"  schema v{VersionConstants.DatabaseSchemaVersion}  extractor v{VersionConstants.ExtractorVersion}  cli/mcp contract v{VersionConstants.CliMcpContractVersion}  output schema v{VersionConstants.OutputSchemaVersion}";
    }

    public static async Task Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine(BuildVersionOutput());
            return;
        }

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("--mode=help") || args.Length == 0)
        {
            HelpText.PrintHelp();
            return;
        }

        var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.Ordinal));
        if (modeArg is null)
        {
            HelpText.PrintUnknownModeError();
            Environment.Exit(1);
            return;
        }

        var mode = modeArg["--mode=".Length..];
        var entry = FindMode(mode);
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
            catch (Exception ex)
            {
                // Last-resort crash path. Diagnosed failures throw CliExitException
                // above; anything reaching this catch is a bug or an unexpected
                // environment failure. Report one line and exit with the documented
                // general-failure code (1) instead of the runtime's unhandled-
                // exception dump, whose stack frames carry build-machine source
                // paths from the shipped PDB. The full trace is opt-in.
                // ReSharper disable once MethodHasAsyncOverload
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                if (args.Contains("--verbose") || Environment.GetEnvironmentVariable("LURP_DEBUG") == "1")
                {
                    // ReSharper disable once MethodHasAsyncOverload
                    Console.Error.WriteLine(ex.ToString());
                }

                Environment.Exit(1);
            }
        }
        else
        {
            HelpText.PrintUnknownModeError();
            Environment.Exit(1);
        }
    }

    /// <summary>
    ///     One CLI mode: name, one-line help, the flag records it reads, and its dispatch target.
    ///     <para>
    ///         <paramref name="Specs" /> is the mode's complete flag inventory excluding the globals
    ///         in <see cref="CliFlagValidation" />. A valued flag's <see cref="FlagSpec.Name" /> ends
    ///         with '=' (<c>"--content-budget="</c>) and a bare flag's does not (<c>"--quiet"</c>); the
    ///         validator matches on that shape, so the two forms are not interchangeable. Flags a handler
    ///         reads indirectly through a <see cref="Handlers.HandlerBootstrap" /> helper belong here too :
    ///         the helper call is invisible to the validator.
    ///     </para>
    /// </summary>
    internal sealed record ModeRegistryEntry(string Name, string HelpText, FlagSpec[] Specs, Func<string[], Task> Handler)
    {
        /// <summary>The flag names of <see cref="Specs" />, in the same order. Name-based readers use this.</summary>
        public string[] Flags { get; } = Specs.Select(static s => s.Name).ToArray();
    }
}