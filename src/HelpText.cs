// Purpose: CLI help and unknown-mode error text.
// Owns: the human-readable mode/option reference printed by --help.
// Must not contain: dispatch logic or handler calls.

using Lurp.Workspace;

namespace Lurp;

internal static class HelpText
{
    private const int FlagColumn = 24;

    // Single source of the long-form flag prose. The set of flags each mode
    // accepts is NOT declared here : it is read from Program.ModeRegistry[].Flags
    // by PrintHelp, so the help inventory can never drift from the validator.
    // A flag missing from this map is still listed (name only) by its mode.
    private static readonly IReadOnlyDictionary<string, string> FlagHelp = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Shared across modes
        ["--symbol="] = "Symbol ID: fully qualified name, doc-comment ID, or 'docCommentId|assemblyIdentity'.",
        ["--snapshot="] = "Snapshot to use (default: latest; the literal value 'latest' is also accepted explicitly). Not honored by --mode=timings, which resolves its own snapshot independently of this flag.",
        ["--include-generated"] = "Include source-generated symbols. Live-observed (both drives, §C): eNoteV2 Migrations 17→21, eCommerce 35→46 with --include-generated; Service 20→20 shows the flag is not a no-op, just query-dependent (EF migrations have no Service symbols).",
        ["--include-public"] = "Include public/protected symbols with no internal LIVE incoming edge as uncertain_dead (reason: public_surface). Default excludes them from proved_dead; without this flag a public helper with no callers is not flagged.",
        ["--include-tests"] = "Include test-project symbols (project name %.Tests) with no LIVE incoming edge as uncertain (reason: test_harness). Default excludes them; test helpers reached only via xUnit reflection discovery would otherwise all appear dead.",
        ["--cursor="] = "Continue from a previous page's nextCursor / truncated.cursor.",
        ["--json"] = "Emit structured JSON instead of plain text.",
        ["--file="] = "Source file path (paired with --line=) to anchor by location.",
        ["--line="] = "1-based line number, paired with --file=. Every emitted line number (edge source_line, declaration locations) is 1-based too, so a reported start_line feeds straight back into --line=.",

        // Read-command output controls (search, find-symbol, impact, context)
        ["--output="] = "Payload rendering: summary | json | jsonl (default: json). 'jsonl' emits a {\"type\":\"meta\"} envelope then one compact object per result; it is rejected for a whole capsule.",
        ["--quiet"] = "Emit only the payload: suppress the freshness stderr line, and for --mode=context print just the written capsule path.",
        ["--freshness="] = "How hard to check the snapshot still matches the working tree: auto (stat only) | hash (re-hash suspect files) | off (default: auto).",
        ["--require-fresh"] = "Exit 2 when the snapshot is not fresh.",

        // index
        ["--solution="] = "Path to the .sln/.slnx to index (--mode=index); for --mode=status, the workspace to compare against the latest snapshot for freshness.",
        ["--strategy="] =
            "full | incremental. 'full' reindexes every document from scratch and is the DEFINITION OF CORRECTNESS (use it to recover a known-good index); 'incremental' re-indexes only changed documents. Default: 'full' on first run, 'incremental' afterward.",
        ["--output-json="] = "Also write the snapshot manifest as JSON (a one-way export; never consulted as authority).",
        ["--skip-adapter="] = "Skip a named framework adapter. Valid: ASP.NET Core, Dependency Injection, MediatR, EF Core, Serialization, Test.",
        ["--skip-diff"] = "Skip computing and persisting the semantic diff against the previous snapshot (--mode=diff still recomputes live).",
        ["--verbose"] = "Emit per-extractor [measure] timing lines to stderr.",
        ["--force"] = "Force a full reindex even when the deterministic snapshot id already exists as a completed snapshot. Does NOT bypass identity computation — an identical workspace will produce the same snapshot id.",

        // get-source
        ["--document="] = "Relative path of the document to retrieve.",
        ["--start-line="] = "1-based first line to return (inclusive, requires --document=).",
        ["--end-line="] = "1-based last line to return (inclusive, requires --document=).",
        ["--context-lines="] = "Extra lines of context around the --start-line/--end-line window (symmetric, 1-based).",

        // get-symbol
        ["--view="] = "What to return: metadata | signature | body | declaration | containing-type | surrounding.",
        ["--context-lines="] = "Extra source lines of context around the symbol.",

        // search
        ["--query="] = "Search term. Symbol search matches whole identifier tokens first; when no token matches, any substring of a symbol's fully qualified name matches (so \"Service\" finds \"CourseService\"). For --mode=grep the query is a literal exact substring (byte-exact, including punctuation); use --ignore-case for case-insensitive.",
        ["--type="] = "Search scope: all | source | symbol (default: all).",
        ["--kind="] = "Filter symbol results by Roslyn SymbolKind (e.g. Type, Method, Field, Property).",
        ["--limit="] = "Max results per scope (default: 20; for --mode=grep default is 50).",
        ["--snippet-tokens="] = "Token window for source snippets (default: 64).",
        ["--ignore-case"] = "For --mode=grep, make the literal search case-insensitive (default: case-sensitive).",

        // impact
        ["--direction="] = "Traversal direction: downstream | upstream (default: downstream).",
        ["--kinds="] = "Comma-separated edge kinds to follow.",
        ["--provenance="] = "Comma-separated provenance values to follow (e.g. compiler_proved,framework_derived). "
                            + "Pass compiler_proved to follow only compiler-verified edges. This keeps edges such as Calls, "
                            + "Constructs, Implements, Inherits, Overrides, and compiler-verified dispatch (direct, non-inherited "
                            + "interface implementations and all virtual/override MayDispatchTo edges). It excludes "
                            + "framework-derived convention DI (Registers), string-reflection candidates (Reflection*), and "
                            + "interface-dispatch edges whose implementation is only inherited (MayDispatchTo provenance=possible). "
                            + "Live-observed (eNoteV2, IEntity.Id pure inherited-only via BaseEntity): all:1, compiler_proved:0, possible:1 vs direct ICurrentUserService.UserId 9/9/0 — confirms filter; eCommerce IBaseCRUDService is mixed (has direct impls) so 242→186→1 is not a filter bug.",
        ["--max-depth="] = "Maximum hops per path (default: 3).",
        ["--max-paths="] = "Paths per page (default: 50); when more exist, the response carries truncated.{reason,total,remaining,cursor}.",

        // diff
        ["--from-snapshot="] = "Baseline snapshot ID to diff from (required for --mode=diff).",
        ["--to-snapshot="] = "Target snapshot ID to diff to (required for --mode=diff).",

        // context
        ["--intent="] = "Assembly-priority hint: inspect | modify | diagnose (default: inspect).",
        ["--content-budget="] =
            "Token budget for capsule CONTENT (default: 8000, or 16000 when --symbol= is a type anchor and --content-budget= is omitted: a type's callee/caller tiers scale with member fan-out, so the default is kind-aware. An explicit --content-budget= is always honored as-is). Over-budget capsules bound paths and item source, then drop the lowest-priority sections (surroundingSource first); as a last resort the anchor source is bounded, so estimatedTokens never exceeds the budget. Every truncated category gets one record in omittedTiers.",
        ["--max-hops="] = "Maximum graph hops to expand (default: 3).",
        ["--scope="] = "Logical scope label recorded on the anchor (default: the symbol ID).",
        ["--affected-project="] = "Repeatable. A project affected by the change.",
        ["--tier="] = "Fetch ONE tier on its own instead of a capsule, with no token budget applied. This is how a capsule's omittedTiers 'budget_exhausted' entry is acted on. Valid: "
                      + string.Join(", ", ContextAssembler.TierNames) + ".",
        ["--tier-limit="] = "Items per tier page (default: 25).",
        ["--completeness-detail"] = "Emit per-document binding_incompleteness rows (default: a reason/project rollup plus the total).",

        // status
        ["--detail="] = "Comma-separated JSON sections to expand: 'documents' (per-document version map), 'completeness' (per-document binding rows), or 'all'. Both are summarized by default.",

        // dead-candidates
        ["--project="] = "Filter to this project (assembly name, e.g. eNote.API). For --mode=dead-candidates the candidate's owning project is its assembly identity's simple name.",

        // annotate
        ["--annotation-kind="] = "Annotation kind (required for --mode=annotate).",
        ["--value="] = "Annotation value text (required for --mode=annotate)."
    };

    // Trailing behavioural notes that are NOT per-flag (so they cannot cause flag
    // drift). Keyed by mode name; printed after that mode's flag list.
    private static readonly Dictionary<string, string> ModeNotes = new(StringComparer.Ordinal)
    {
        ["impact"] = "Every response also carries `groups`: the paths grouped by first hop, computed over ALL paths before the page is cut, so the fan-out summary stays complete even when the path list is truncated. "
                     + "Note: a static call site emits both a `Calls` edge and a `StaticallyCalls` edge; count distinct call sites (first_hop_source_symbol_id) rather than total edge rows when measuring fan-in.",
        ["context"] = "The capsule is always written to <output-dir>/capsule-<symbol>.json (long symbols are shortened with a stable hash suffix); the stdout copy is what --quiet and --output=summary replace."
    };

    internal static void PrintUnknownModeError()
    {
        var modes = Program.ModeRegistry.Select(entry => $"--mode={entry.Name}").ToList();
        var modeList = string.Join(", ", modes.Take(modes.Count - 1)) + ", or " + modes[^1];
        Console.Error.WriteLine($"ERROR: Unknown mode. Use {modeList}.");
        Console.Error.WriteLine("  Note: For --mode=index, use --strategy=<incremental|full> (default: full on first run, incremental on subsequent runs).");
        Console.Error.WriteLine("    --strategy=full forces a complete reindex. Use it as a recovery mechanism if something looks wrong.");
    }

    internal static void PrintHelp()
    {
        Console.WriteLine("lurp : Roslyn-native semantic context engine for C#");
        Console.WriteLine();
        Console.WriteLine("USAGE");
        Console.WriteLine("  lurp --mode=<mode> --output-dir=<path> [options]");
        Console.WriteLine();
        Console.WriteLine("  --output-dir=<path>  Directory where index.db is stored. --solution= may");
        Console.WriteLine("                       stand in for it when it points at a solution directory.");
        Console.WriteLine("                       (LURP_OUTPUT_DIR / LURP_SOLUTION_PATH set these too.)");
        Console.WriteLine();
        Console.WriteLine("MODES");
        foreach (var entry in Program.ModeRegistry)
            Console.WriteLine($"  --mode={entry.Name,-20}{entry.HelpText}");
        Console.WriteLine();
        Console.WriteLine("OPTIONS BY MODE");
        Console.WriteLine("  (flags below are exactly the ones each mode accepts, from the mode registry)");
        foreach (var entry in Program.ModeRegistry)
        {
            Console.WriteLine();
            Console.WriteLine($"  --mode={entry.Name}");
            if (entry.Flags.Length == 0)
                Console.WriteLine("      (no flags beyond --output-dir=)");
            foreach (var flag in entry.Flags)
                WriteFlag(flag, FlagHelp.GetValueOrDefault(flag));
            if (ModeNotes.TryGetValue(entry.Name, out var note))
                foreach (var line in WrapWords(note, 72))
                    Console.WriteLine($"      {line}");
        }

        Console.WriteLine();
        Console.WriteLine("SNAPSHOT LIFECYCLE");
        Console.WriteLine("  Each indexing run (full or incremental) creates a NEW snapshot,");
        Console.WriteLine("  unless source content and compilation inputs are unchanged, in which");
        Console.WriteLine("  case the existing snapshot is reused (content-addressed dedup).");
        Console.WriteLine("  The last 3 snapshots are retained; older ones are pruned automatically.");
        Console.WriteLine("  Snapshots are never mutated : a new snapshot is created when content");
        Console.WriteLine("  changes, and it does NOT modify the previous one.");
        Console.WriteLine();
        Console.WriteLine("ENVIRONMENT VARIABLES");
        Console.WriteLine("  LURP_SOLUTION_PATH      Equivalent to --solution=.");
        Console.WriteLine("  LURP_OUTPUT_DIR         Equivalent to --output-dir=.");
    }

    private static void WriteFlag(string flag, string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            Console.WriteLine($"    {flag}");
            return;
        }

        var lines = WrapWords(description, 52);
        Console.WriteLine($"    {flag,-FlagColumn}{lines[0]}");
        var continuationIndent = new string(' ', 4 + FlagColumn);
        for (var i = 1; i < lines.Count; i++)
            Console.WriteLine($"{continuationIndent}{lines[i]}");
    }

    private static List<string> WrapWords(string text, int width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line);
                line = "";
            }

            line = line.Length == 0 ? word : line + " " + word;
        }

        if (line.Length > 0)
            lines.Add(line);
        return lines;
    }
}