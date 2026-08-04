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
            PrintHelp();
            return;
        }

        var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode="));
        if (modeArg is null)
        {
            PrintUnknownModeError();
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
            PrintUnknownModeError();
            Environment.Exit(1);
        }
    }

    private static void PrintUnknownModeError()
    {
        Console.Error.WriteLine("ERROR: Unknown mode. Use --mode=index, --mode=get-source, --mode=get-symbol, --mode=search, --mode=find-symbol, --mode=navigate, --mode=diff, --mode=impact, --mode=context, --mode=status, --mode=timings, --mode=simulate-rename, --mode=simulate-move, --mode=simulate-remove, --mode=audit, --mode=annotate, or --mode=get-annotations.");
        Console.Error.WriteLine("  Note: For --mode=index, use --strategy=<incremental|full> (default: full on first run, incremental on subsequent runs).");
        Console.Error.WriteLine("    --strategy=full forces a complete reindex. Use it as a recovery mechanism if something looks wrong.");
        Console.Error.WriteLine("  Note: 'structure' is served by --mode=context --intent=inspect.");
        Console.Error.WriteLine("  Note: 'who-references' is served by --mode=impact --direction=upstream.");
        Console.Error.WriteLine("  Note: 'discover' is served by --mode=search --type=symbol --kind=<SymbolKind>.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("lurp : Roslyn-based code indexer");
        Console.WriteLine();
        Console.WriteLine("MODES");
        Console.WriteLine("  --mode=index              Index a solution and store facts in the database.");
        Console.WriteLine("  --mode=get-source          Retrieve source for a symbol by ID.");
        Console.WriteLine("  --mode=get-symbol          Look up symbol metadata.");
        Console.WriteLine("  --mode=search              Full-text search over source and symbols.");
        Console.WriteLine("  --mode=find-symbol         Resolve a symbol by FQN.");
        Console.WriteLine("  --mode=navigate            Resolve an indexed declaration by file and line.");
        Console.WriteLine("  --mode=diff                Show semantic changes between two snapshots.");
        Console.WriteLine("  --mode=impact              Trace the impact path of a changed symbol.");
        Console.WriteLine("  --mode=context             Assemble a context capsule for a symbol.");
        Console.WriteLine("  --mode=status              Show the current database status.");
        Console.WriteLine("  --mode=timings             Show step-by-step timing data for a snapshot.");
        Console.WriteLine("  --mode=simulate-rename     Simulate renaming a symbol and show affected references.");
        Console.WriteLine("  --mode=simulate-move       Simulate moving a symbol to a new namespace.");
        Console.WriteLine("  --mode=simulate-remove     Simulate removing a symbol and show cascading impact.");
        Console.WriteLine("  --mode=audit               Run static analysis checks on the index.");
        Console.WriteLine("  --mode=annotate            Attach a user-authored annotation to a symbol.");
        Console.WriteLine("  --mode=get-annotations     Retrieve annotations for a symbol.");
        Console.WriteLine();
        Console.WriteLine("SEARCH (--mode=search)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --query=<term>       Search term. Symbol search matches whole");
        Console.WriteLine("                         identifier tokens first; when no token matches,");
        Console.WriteLine("                         any substring of a symbol's fully qualified name");
        Console.WriteLine("                         matches (so \"Service\" finds \"CourseService\").");
        Console.WriteLine("    --output-dir=<path>  Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --type=<all|source|symbol>");
        Console.WriteLine("                         Search scope (default: all).");
        Console.WriteLine("    --kind=<SymbolKind>  Filter symbol results by Roslyn SymbolKind");
        Console.WriteLine("                         (e.g. \"Type\", \"Method\", \"Field\", \"Property\").");
        Console.WriteLine("    --limit=<n>          Max results per scope (default: 20).");
        Console.WriteLine("    --snippet-tokens=<n> Token window for source snippets (default: 64).");
        Console.WriteLine("    --snapshot=<id>      Snapshot to search (default: latest).");
        Console.WriteLine("    --include-generated  Include source-generated symbols.");
        Console.WriteLine("    --cursor=<token>     Continue from a previous page's nextCursor");
        Console.WriteLine("                         (--type=symbol only).");
        Console.WriteLine();
        Console.WriteLine("IMPACT (--mode=impact)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --symbol=<id>         The symbol ID to trace from.");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --direction=<downstream|upstream>");
        Console.WriteLine("                          Traversal direction (default: downstream).");
        Console.WriteLine("    --max-depth=<n>       Maximum hops per path (default: 10).");
        Console.WriteLine("    --kinds=<list>        Comma-separated edge kinds to follow.");
        Console.WriteLine("    --max-paths=<n>       Paths per page (default: 50). When more exist, the");
        Console.WriteLine("                          response carries truncated.{reason,total,remaining,cursor}.");
        Console.WriteLine("    --cursor=<token>      Continue from a previous page's truncated.cursor.");
        Console.WriteLine("    --snapshot=<id>       Snapshot to use (default: latest).");
        Console.WriteLine("  Every response also carries `groups`: the paths grouped by first hop,");
        Console.WriteLine("  computed over ALL paths before the page is cut, so the fan-out summary");
        Console.WriteLine("  stays complete even when the path list is truncated.");
        Console.WriteLine();
        Console.WriteLine("INDEXING (--mode=index)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --solution=<path>     Path to the .sln or .slnx file.");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine();
        Console.WriteLine("  Optional:");
        Console.WriteLine("    --strategy=<full|incremental>");
        Console.WriteLine("        full:        Index every document from scratch.");
        Console.WriteLine("                     This is the DEFINITION OF CORRECTNESS for the index.");
        Console.WriteLine("                     Use it as the recovery mechanism when something looks");
        Console.WriteLine("                     wrong: run '--strategy=full' to reset the index to a");
        Console.WriteLine("                     known-good state.");
        Console.WriteLine("        incremental: Only re-index changed documents; reuses facts for");
        Console.WriteLine("                     unchanged documents from the previous snapshot.");
        Console.WriteLine("                     Default on subsequent runs (after an initial full index).");
        Console.WriteLine("        Default: 'full' on first run (no snapshot exists),");
        Console.WriteLine("                 'incremental' on subsequent runs.");
        Console.WriteLine();
        Console.WriteLine("    --output-json=<path>  Also write the snapshot manifest as JSON.");
        Console.WriteLine("    --verbose             Emit per-extractor [measure] timing lines to stderr.");
        Console.WriteLine("    --skip-adapter=<name> Skip a named framework adapter.");
        Console.WriteLine("                          Valid: ASP.NET Core, Dependency Injection,");
        Console.WriteLine("                                 MediatR, EF Core, Serialization, Test.");
        Console.WriteLine();
        Console.WriteLine("CONTEXT (--mode=context)");
        Console.WriteLine("  Required (one of):");
        Console.WriteLine("    --symbol=<id>         The symbol ID to anchor on.");
        Console.WriteLine("    --file=<path> --line=<n>  Anchor by source location.");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --intent=<inspect|modify|diagnose>");
        Console.WriteLine("                         Intent hint for assembly priority (default: inspect).");
        Console.WriteLine("    --budget=<n>          Token budget for capsule CONTENT (default: 8000).");
        Console.WriteLine("                         Reported as estimatedTokens: item and anchor source plus");
        Console.WriteLine("                         the serialized weight of the substantive non-source");
        Console.WriteLine("                         sections (paths, topology, completeness, uncertainties,");
        Console.WriteLine("                         verification, ...). Per-item identity/provenance framing");
        Console.WriteLine("                         is uncounted navigation metadata, so the emitted file is");
        Console.WriteLine("                         larger; size a context window from estimatedArtifactTokens");
        Console.WriteLine("                         instead. Over-budget capsules bound paths and item source,");
        Console.WriteLine("                         then drop the lowest-priority sections; every truncated");
        Console.WriteLine("                         category gets one terminal record in omittedTiers.");
        Console.WriteLine("    --max-hops=<n>        Maximum graph hops to expand (default: 3).");
        Console.WriteLine("    --snapshot=<id>       Snapshot to use (default: latest).");
        Console.WriteLine("    --include-generated   Include source-generated symbols.");
        Console.WriteLine("    --completeness-detail Emit per-document binding_incompleteness rows.");
        Console.WriteLine("                         Without it, completeness carries a reason/project");
        Console.WriteLine("                         rollup and the total.");
        Console.WriteLine("    --tier=<name>         Fetch ONE tier on its own instead of a capsule, with");
        Console.WriteLine("                          no token budget applied. This is how a capsule's");
        Console.WriteLine("                          omittedTiers 'budget_exhausted' entry is acted on.");
        Console.WriteLine("                          Valid: contracts, directCallees, directCallers,");
        Console.WriteLine("                          registeredImplementations, relevantTests,");
        Console.WriteLine("                          secondDegreeContext, surroundingSource.");
        Console.WriteLine("    --tier-limit=<n>      Items per tier page (default: 25).");
        Console.WriteLine("    --cursor=<token>      Continue a tier from its next_cursor (--tier only).");
        Console.WriteLine("  The capsule is always written to <output-dir>/capsule-<symbol>.json (long");
        Console.WriteLine("  symbols are shortened with a stable hash suffix); the");
        Console.WriteLine("  stdout copy is what --quiet and --output=summary replace.");
        Console.WriteLine();
        Console.WriteLine("SIMULATION (--mode=simulate-*)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --symbol=<id>         The symbol ID to simulate.");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --new-name=<name>     New simple name (simulate-rename only).");
        Console.WriteLine("    --new-namespace=<ns>  Target namespace (simulate-move only).");
        Console.WriteLine("    --snapshot=<id>       Snapshot to use (default: latest).");
        Console.WriteLine();
        Console.WriteLine("AUDIT (--mode=audit)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --checks=<list>       Comma-separated checks: dead-symbol, untested-surface,");
        Console.WriteLine("                          unregistered-impl, high-fan-out (default: all).");
        Console.WriteLine("    --fan-out-threshold=N Call-count threshold for high-fan-out (default: 20).");
        Console.WriteLine("    --snapshot=<id>       Snapshot to use (default: latest).");
        Console.WriteLine();
        Console.WriteLine("ANNOTATIONS (--mode=annotate / --mode=get-annotations)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --symbol=<id>         The symbol ID to annotate or query.");
        Console.WriteLine("    --annotation-kind=<kind>  Annotation kind (annotate only, required).");
        Console.WriteLine("    --value=<text>        Annotation value (annotate only, required).");
        Console.WriteLine("    --snapshot=<id>       Snapshot to use (default: latest).");
        Console.WriteLine();
        Console.WriteLine("STATUS (--mode=status)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --solution=<path>     If provided, compares the current workspace against");
        Console.WriteLine("                          the latest snapshot and reports freshness mismatches.");
        Console.WriteLine("                          Without it, only schema version and latest snapshot");
        Console.WriteLine("                          ID are reported.");
        Console.WriteLine("    --json                Emit structured JSON instead of plain text.");
        Console.WriteLine("    --detail=<list>       Comma-separated sections to expand in --json output.");
        Console.WriteLine("                          'documents' restores the per-document version map;");
        Console.WriteLine("                          'completeness' restores per-document binding rows.");
        Console.WriteLine("                          Both are summarized by default.");
        Console.WriteLine("                          'all' expands every section.");
        Console.WriteLine();
        Console.WriteLine("TIMINGS (--mode=timings)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --snapshot=<id>       Show timings for a specific snapshot (default: latest).");
        Console.WriteLine("    --json                Emit structured JSON instead of plain text.");
        Console.WriteLine();
        Console.WriteLine("SNAPSHOT LIFECYCLE");
        Console.WriteLine("  Each indexing run (full or incremental) creates a NEW snapshot.");
        Console.WriteLine("  The last 3 snapshots are retained; older ones are pruned automatically.");
        Console.WriteLine("  Snapshots are never mutated : incremental creates a new snapshot,");
        Console.WriteLine("  it does NOT modify the previous one.");
        Console.WriteLine();
        Console.WriteLine("READ-COMMAND OPTIONS (search, find-symbol, impact, context)");
        Console.WriteLine("  --output=<summary|json|jsonl>");
        Console.WriteLine("                          Payload rendering (default: json). 'summary' is a");
        Console.WriteLine("                          digest; 'jsonl' emits a {\"type\":\"meta\"} envelope then");
        Console.WriteLine("                          one compact object per result, so a consumer can");
        Console.WriteLine("                          stream and stop early. 'jsonl' is rejected for a");
        Console.WriteLine("                          whole capsule, whose payload is a single document.");
        Console.WriteLine("  --quiet                 Emit only the payload: suppresses the freshness");
        Console.WriteLine("                          stderr line, and for --mode=context prints just the");
        Console.WriteLine("                          written capsule path instead of the capsule itself.");
        Console.WriteLine("  --freshness=<auto|hash|off>");
        Console.WriteLine("                          How hard to check that the snapshot still matches the");
        Console.WriteLine("                          working tree (default: auto : stat only; 'hash'");
        Console.WriteLine("                          re-hashes suspect files, 'off' skips the check).");
        Console.WriteLine("  --require-fresh         Exit 2 when the snapshot is not fresh.");
        Console.WriteLine();
        Console.WriteLine("ENVIRONMENT VARIABLES");
        Console.WriteLine("  INDEXER_SOLUTION_PATH   Equivalent to --solution=.");
        Console.WriteLine("  INDEXER_OUTPUT_DIR      Equivalent to --output-dir=.");
    }
}
