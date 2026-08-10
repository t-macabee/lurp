// Purpose: CLI help and unknown-mode error text.
// Owns: the human-readable mode/option reference printed by --help.
// Must not contain: dispatch logic or handler calls.

using System.Linq;

namespace Lurp;

internal static class HelpText
{
    internal static void PrintUnknownModeError()
    {
        var modes = Program.ModeRegistry.Select(entry => $"--mode={entry.Name}").ToList();
        var modeList = string.Join(", ", modes.Take(modes.Count - 1)) + ", or " + modes[^1];
        Console.Error.WriteLine($"ERROR: Unknown mode. Use {modeList}.");
        Console.Error.WriteLine("  Note: For --mode=index, use --strategy=<incremental|full> (default: full on first run, incremental on subsequent runs).");
        Console.Error.WriteLine("    --strategy=full forces a complete reindex. Use it as a recovery mechanism if something looks wrong.");
    }

    // Single source of the long-form flag prose. The set of flags each mode
    // accepts is NOT declared here : it is read from Program.ModeRegistry[].Flags
    // by PrintHelp, so the help inventory can never drift from the validator.
    // A flag missing from this map is still listed (name only) by its mode.
    private static readonly IReadOnlyDictionary<string, string> FlagHelp = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Shared across modes
        ["--symbol="] = "Symbol ID: fully qualified name, doc-comment ID, or 'docCommentId|assemblyIdentity'.",
        ["--snapshot="] = "Snapshot to use (default: latest).",
        ["--include-generated"] = "Include source-generated symbols.",
        ["--cursor="] = "Continue from a previous page's nextCursor / truncated.cursor.",
        ["--json"] = "Emit structured JSON instead of plain text.",
        ["--file="] = "Source file path (paired with --line=) to anchor by location.",
        ["--line="] = "1-based line number, paired with --file=.",

        // Read-command output controls (search, find-symbol, impact, context)
        ["--output="] = "Payload rendering: summary | json | jsonl (default: json). 'jsonl' emits a {\"type\":\"meta\"} envelope then one compact object per result; it is rejected for a whole capsule.",
        ["--quiet"] = "Emit only the payload: suppress the freshness stderr line, and for --mode=context print just the written capsule path.",
        ["--freshness="] = "How hard to check the snapshot still matches the working tree: auto (stat only) | hash (re-hash suspect files) | off (default: auto).",
        ["--require-fresh"] = "Exit 2 when the snapshot is not fresh.",

        // index
        ["--solution="] = "Path to the .sln/.slnx to index (--mode=index); for --mode=status, the workspace to compare against the latest snapshot for freshness.",
        ["--strategy="] = "full | incremental. 'full' reindexes every document from scratch and is the DEFINITION OF CORRECTNESS (use it to recover a known-good index); 'incremental' re-indexes only changed documents. Default: 'full' on first run, 'incremental' afterward.",
        ["--output-json="] = "Also write the snapshot manifest as JSON (a one-way export; never consulted as authority).",
        ["--skip-adapter="] = "Skip a named framework adapter. Valid: ASP.NET Core, Dependency Injection, MediatR, EF Core, Serialization, Test.",
        ["--skip-diff"] = "Skip computing and persisting the semantic diff against the previous snapshot (--mode=diff still recomputes live).",
        ["--verbose"] = "Emit per-extractor [measure] timing lines to stderr.",
        ["--force"] = "Force a full reindex even when the deterministic snapshot id already exists as a completed snapshot. Does NOT bypass identity computation — an identical workspace will produce the same snapshot id.",

        // get-source
        ["--document="] = "Relative path of the document to retrieve.",

        // get-symbol
        ["--view="] = "What to return: metadata | signature | body | declaration | containing-type | surrounding.",
        ["--context-lines="] = "Extra source lines of context around the symbol.",

        // search
        ["--query="] = "Search term. Symbol search matches whole identifier tokens first; when no token matches, any substring of a symbol's fully qualified name matches (so \"Service\" finds \"CourseService\").",
        ["--type="] = "Search scope: all | source | symbol (default: all).",
        ["--kind="] = "Filter symbol results by Roslyn SymbolKind (e.g. Type, Method, Field, Property).",
        ["--limit="] = "Max results per scope (default: 20).",
        ["--snippet-tokens="] = "Token window for source snippets (default: 64).",

        // impact
        ["--direction="] = "Traversal direction: downstream | upstream (default: downstream).",
        ["--kinds="] = "Comma-separated edge kinds to follow.",
        ["--max-depth="] = "Maximum hops per path (default: 3).",
        ["--max-paths="] = "Paths per page (default: 50); when more exist, the response carries truncated.{reason,total,remaining,cursor}.",

        // diff
        ["--from-snapshot="] = "Baseline snapshot ID to diff from (required for --mode=diff).",
        ["--to-snapshot="] = "Target snapshot ID to diff to (required for --mode=diff).",

        // context
        ["--intent="] = "Assembly-priority hint: inspect | modify | diagnose (default: inspect).",
        ["--content-budget="] = "Token budget for capsule CONTENT (default: 8000). Over-budget capsules bound paths and item source, then drop the lowest-priority sections (surroundingSource first); as a last resort the anchor source is bounded, so estimatedTokens never exceeds the budget. Every truncated category gets one record in omittedTiers.",
        ["--max-hops="] = "Maximum graph hops to expand (default: 3).",
        ["--scope="] = "Logical scope label recorded on the anchor (default: the symbol ID).",
        ["--affected-project="] = "Repeatable. A project affected by the change.",
        ["--tier="] = "Fetch ONE tier on its own instead of a capsule, with no token budget applied. This is how a capsule's omittedTiers 'budget_exhausted' entry is acted on. Valid: "
            + string.Join(", ", Workspace.ContextAssembler.TierNames) + ".",
        ["--tier-limit="] = "Items per tier page (default: 25).",
        ["--completeness-detail"] = "Emit per-document binding_incompleteness rows (default: a reason/project rollup plus the total).",

        // status
        ["--detail="] = "Comma-separated JSON sections to expand: 'documents' (per-document version map), 'completeness' (per-document binding rows), or 'all'. Both are summarized by default.",

        // annotate
        ["--annotation-kind="] = "Annotation kind (required for --mode=annotate).",
        ["--value="]           = "Annotation value text (required for --mode=annotate).",
    };

    // Trailing behavioural notes that are NOT per-flag (so they cannot cause flag
    // drift). Keyed by mode name; printed after that mode's flag list.
    private static readonly IReadOnlyDictionary<string, string> ModeNotes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["impact"] = "Every response also carries `groups`: the paths grouped by first hop, computed over ALL paths before the page is cut, so the fan-out summary stays complete even when the path list is truncated.",
        ["context"] = "The capsule is always written to <output-dir>/capsule-<symbol>.json (long symbols are shortened with a stable hash suffix); the stdout copy is what --quiet and --output=summary replace.",
    };

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
            Console.WriteLine($"  --mode={entry.Name.PadRight(20)}{entry.HelpText}");
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
        Console.WriteLine("  Each indexing run (full or incremental) creates a NEW snapshot.");
        Console.WriteLine("  The last 3 snapshots are retained; older ones are pruned automatically.");
        Console.WriteLine("  Snapshots are never mutated : incremental creates a new snapshot,");
        Console.WriteLine("  it does NOT modify the previous one.");
        Console.WriteLine();
        Console.WriteLine("ENVIRONMENT VARIABLES");
        Console.WriteLine("  LURP_SOLUTION_PATH      Equivalent to --solution=.");
        Console.WriteLine("  LURP_OUTPUT_DIR         Equivalent to --output-dir=.");
        Console.WriteLine("  INDEXER_SOLUTION_PATH   Deprecated alias for LURP_SOLUTION_PATH.");
        Console.WriteLine("  INDEXER_OUTPUT_DIR      Deprecated alias for LURP_OUTPUT_DIR.");
    }

    private const int FlagColumn = 24;

    private static void WriteFlag(string flag, string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            Console.WriteLine($"    {flag}");
            return;
        }

        var lines = WrapWords(description, 52);
        Console.WriteLine($"    {flag.PadRight(FlagColumn)}{lines[0]}");
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
