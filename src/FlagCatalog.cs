// Purpose: one FlagSpec per CLI flag name, the single source of flag descriptions.
// Owns: the description, value hint, closed value list and requirement of each flag.
// Must not contain: mode membership (Program.ModeRegistry lists which flags a mode takes).

using Lurp.Workspace;

namespace Lurp;

/// <summary>
///     The flag records. A mode lists these in <see cref="Program.ModeRegistry" />. Where a flag
///     means the same in every mode that takes it, one field serves all of them. The field
///     comments name the modes that accept each flag.
/// </summary>
internal static class FlagCatalog
{
    // Modes that accept --symbol=: get-symbol, find-symbol, impact, context, annotate, and get-annotations.
    public static readonly FlagSpec Symbol = new("--symbol=", "Symbol ID: fully qualified name, doc-comment ID, or 'docCommentId|assemblyIdentity'.", "id");

    // Modes that accept --snapshot=: get-source, outline, get-symbol, search, find-symbol, navigate,
    // impact, context, timings, pin-snapshot, annotate, retract-annotation, get-annotations,
    // diagnostics, grep, and dead-candidates.
    public static readonly FlagSpec Snapshot = new("--snapshot=", "Snapshot to use (default: latest; the literal value 'latest' is also accepted explicitly).", "id");

    // Modes that accept --include-public: dead-candidates.
    public static readonly FlagSpec IncludePublic = new("--include-public", "Include public/protected symbols with no internal LIVE incoming edge as uncertain_dead (reason: public_surface). Default excludes them from proved_dead; without this flag a public helper with no callers is not flagged.");

    // Modes that accept --include-tests: dead-candidates.
    public static readonly FlagSpec IncludeTests = new("--include-tests", "Include test-project symbols (a project that references a test framework) with no LIVE incoming edge as uncertain (reason: test_harness). Default excludes them; test helpers reached only via xUnit reflection discovery would otherwise all appear dead.");

    // Modes that accept --cursor=: outline, search, impact, context, get-annotations, diagnostics, grep, and dead-candidates.
    public static readonly FlagSpec Cursor = new("--cursor=", "Continue from a previous page's nextCursor / truncated.cursor.", "token");

    // Modes that accept --json: status, timings, and pin-snapshot.
    public static readonly FlagSpec Json = new("--json", "Emit structured JSON instead of plain text.");

    // Modes that accept --file=: navigate and context.
    public static readonly FlagSpec File = new("--file=", "Source file path (paired with --line=) to anchor by location.", "path");

    // Modes that accept --line=: navigate and context.
    public static readonly FlagSpec Line = new("--line=", "1-based line number, paired with --file=. Every emitted line number (edge source_line, declaration locations) is 1-based too, so a reported start_line feeds straight back into --line=.", "n");

    // Read-command output controls (search, find-symbol, impact, context)
    public static readonly FlagSpec Output = new("--output=", "Payload rendering: summary | json | jsonl (default: json). 'jsonl' emits a {\"type\":\"meta\"} envelope then one compact object per result; it is rejected for a whole capsule.", AllowedValues: ["summary", "json", "jsonl"]);
    public static readonly FlagSpec Quiet = new("--quiet", "Emit only the payload: suppress the freshness stderr line, and for --mode=context print just the written capsule path.");
    public static readonly FlagSpec Freshness = new("--freshness=", "How hard to check the snapshot still matches the working tree: auto (stat only) | hash (re-hash suspect files) | off (default: auto). An unknown value is an error.", AllowedValues: ["auto", "hash", "off"]);
    public static readonly FlagSpec RequireFresh = new("--require-fresh", "Exit 2 when the snapshot is not fresh.");

    // Modes that accept --include-generated: outline, get-symbol, search, find-symbol,
    // navigate, context, diagnostics, grep, and dead-candidates (not index).
    public static readonly FlagSpec IncludeGenerated = new("--include-generated", "Include source-generated code (obj/**, *.g.cs, *.generated.cs, *.Designer.cs, *ModelSnapshot.cs); default excludes it. See CLI_REFERENCE.md's read-command options for live-observed evidence this isn't a no-op.");

    // index
    public static readonly FlagSpec Solution = new("--solution=", "Path to the .sln/.slnx to index (--mode=index); for --mode=status, the workspace to compare against the latest snapshot for freshness.", "path");
    public static readonly FlagSpec Strategy = new("--strategy=", "full | incremental. 'full' reindexes every document from scratch and is the DEFINITION OF CORRECTNESS (use it to recover a known-good index); 'incremental' re-indexes only changed documents. Default: 'full' on first run, 'incremental' afterward.", AllowedValues: ["full", "incremental"]);
    public static readonly FlagSpec OutputJson = new("--output-json=", "Also write the snapshot manifest as JSON (a one-way export; never consulted as authority).", "path");
    public static readonly FlagSpec SkipAdapter = new("--skip-adapter=", "Skip a named framework adapter. Valid: ASP.NET Core, Dependency Injection, MediatR, EF Core, Serialization, Test.", "name", Repeatable: true);
    public static readonly FlagSpec SkipDiff = new("--skip-diff", "Skip computing and persisting the semantic diff against the previous snapshot (--mode=diff still recomputes live).");
    public static readonly FlagSpec Verbose = new("--verbose", "Emit per-extractor [measure] timing lines to stderr.");
    public static readonly FlagSpec Force = new("--force", "Re-extract even when the deterministic snapshot id already exists as a completed snapshot. With the default incremental strategy this re-runs the incremental re-extraction in place (with no changed documents it is a no-op); a full reindex needs --strategy=full --force, which deletes the identical snapshot and reindexes everything. Does NOT bypass identity computation — an identical workspace will produce the same snapshot id.");

    // get-source
    public static readonly FlagSpec Document = new("--document=", "Relative path of the document to retrieve.", "relative-path");
    public static readonly FlagSpec StartLine = new("--start-line=", "1-based first line to return (inclusive, requires --document=).", "n");
    public static readonly FlagSpec EndLine = new("--end-line=", "1-based last line to return (inclusive, requires --document=).", "n");
    public static readonly FlagSpec ContextLines = new("--context-lines=", "Extra source lines of context: for get-source, expands the --start-line/--end-line window symmetrically (requires one of those, 1-based); for get-symbol's surrounding view, lines around the symbol (default 3).", "n");

    // get-symbol
    public static readonly FlagSpec View = new("--view=", "What to return: metadata | signature | body | declaration | containing-type | surrounding.", AllowedValues: ["metadata", "signature", "body", "declaration", "containing-type", "surrounding"]);

    // search
    public static readonly FlagSpec Query = new("--query=", "Search term. Symbol search matches whole identifier tokens first; when no token matches, any substring of a symbol's fully qualified name matches (so \"Service\" finds \"CourseService\"). For --mode=grep the query is a literal exact substring (byte-exact, including punctuation); use --ignore-case for case-insensitive.", "term");
    public static readonly FlagSpec Type = new("--type=", "Search scope: all | source | symbol (default: all). An unknown value is an error.", AllowedValues: ["all", "source", "symbol"]);
    public static readonly FlagSpec Kind = new("--kind=", "Filter symbol results by Roslyn SymbolKind (e.g. Type, Method, Field, Property).", "SymbolKind");
    public static readonly FlagSpec Limit = new("--limit=", "Max results per scope (default: 20; for --mode=grep and --mode=impact default is 50).", "n");
    public static readonly FlagSpec SnippetTokens = new("--snippet-tokens=", "Token window for source snippets (default: 64).", "n");
    public static readonly FlagSpec IgnoreCase = new("--ignore-case", "For --mode=grep, make the literal search case-insensitive (default: case-sensitive).");

    // impact
    public static readonly FlagSpec Direction = new("--direction=", "Traversal direction: downstream | upstream (default: downstream).", AllowedValues: ["downstream", "upstream"]);
    public static readonly FlagSpec Kinds = new("--kinds=", "Comma-separated edge kinds to follow.", "list");
    public static readonly FlagSpec Provenance = new("--provenance=", "Comma-separated provenance values to follow (e.g. compiler_proved,framework_derived). "
                                                                      + "Pass compiler_proved to follow only compiler-verified edges. This keeps edges such as Calls, "
                                                                      + "MethodGroupRef, Constructs, Implements, Inherits, Overrides, and compiler-verified dispatch (direct, non-inherited "
                                                                      + "interface implementations and all virtual/override MayDispatchTo edges). It excludes "
                                                                      + "framework-derived convention DI (Registers), string-reflection candidates (Reflection*), and "
                                                                      + "interface-dispatch edges whose implementation is only inherited (MayDispatchTo provenance=possible). "
                                                                      + "See CLI_REFERENCE.md's impact section for live-observed evidence this filters correctly.", "list");
    public static readonly FlagSpec MaxDepth = new("--max-depth=", "Maximum traversal depth in hops (default: 3).", "n");

    // diff
    public static readonly FlagSpec FromSnapshot = new("--from-snapshot=", "Baseline snapshot ID to diff from (required for --mode=diff).", "id");
    public static readonly FlagSpec ToSnapshot = new("--to-snapshot=", "Target snapshot ID to diff to (required for --mode=diff).", "id");

    // context
    public static readonly FlagSpec Intent = new("--intent=", "Assembly-priority hint: inspect | modify | diagnose (default: inspect).", AllowedValues: ["inspect", "modify", "diagnose"]);
    public static readonly FlagSpec ContentBudget = new("--content-budget=",
        "Token budget for capsule CONTENT (default: 8000, or 16000 when --symbol= is a type anchor and --content-budget= is omitted: a type's callee/caller tiers scale with member fan-out, so the default is kind-aware. An explicit --content-budget= is always honored as-is). Over-budget capsules bound paths and item source, then drop the lowest-priority sections (surrounding_source first); as a last resort the anchor source is bounded, so estimated_tokens never exceeds the budget. Every truncated category gets one record in omitted_tiers.",
        "n");
    public static readonly FlagSpec MaxHops = new("--max-hops=", "Maximum graph hops to expand (default: 3).", "n");
    public static readonly FlagSpec Scope = new("--scope=", "Logical scope label recorded on the anchor (default: the symbol ID).", "label");
    public static readonly FlagSpec AffectedProject = new("--affected-project=", "Repeatable. A project affected by the change.", "name", Repeatable: true);
    public static readonly FlagSpec Tier = new("--tier=", "Fetch ONE tier on its own instead of a capsule, with no token budget applied. This is how a capsule's omitted_tiers 'budget_exhausted' entry is acted on. Valid: "
                                                          + string.Join(", ", ContextAssembler.TierNames) + ".", AllowedValues: ContextAssembler.TierNames);
    public static readonly FlagSpec TierLimit = new("--tier-limit=", "Items per tier page (default: 25).", "n");
    public static readonly FlagSpec CompletenessDetail = new("--completeness-detail", "Emit per-document binding_incompleteness rows (default: a reason/project rollup plus the total).");

    // status
    public static readonly FlagSpec Detail = new("--detail=", "Comma-separated JSON sections to expand: 'documents' (per-document version map), 'completeness' (per-document binding rows), or 'all'. Both are summarized by default.", "list");
    public static readonly FlagSpec MaxMismatches = new("--max-mismatches=", "Cap on the mismatch list in --json output (default: 50; positive integer).", "n");

    // dead-candidates
    public static readonly FlagSpec Project = new("--project=", "Filter to this project (assembly name, e.g. eNote.API). For --mode=dead-candidates the candidate's owning project is its assembly identity's simple name.", "name");

    // annotate
    public static readonly FlagSpec AnnotationKind = new("--annotation-kind=", "Annotation kind (required for --mode=annotate).", "kind");
    public static readonly FlagSpec Value = new("--value=", "Annotation value text (required for --mode=annotate).", "text");
    public static readonly FlagSpec AnnotationId = new("--annotation-id=", "Surrogate PK of the annotation to retract (from get-annotations' annotation_id).", "n");

    // pin-snapshot
    public static readonly FlagSpec Clear = new("--clear", "Clear the pinned snapshot override; reads will again default to the most recently built snapshot.");

    // diagnostics: no description in the former help map; the text comes from the CLI reference.
    public static readonly FlagSpec Severity = new("--severity=", "Filter by severity (Roslyn `DiagnosticSeverity` names: `Hidden`, `Info`, `Warning`, `Error`; matched case-insensitively, or `all` for every severity including `Hidden`). When omitted, `Hidden` diagnostics are excluded. Unknown severity values are rejected with an error instead of returning an empty result.", "level");
    public static readonly FlagSpec Id = new("--id=", "Filter by diagnostic ID (e.g. `CS8933`).", "code");

    // serve
    public static readonly FlagSpec EnableWriteTools = new("--enable-write-tools", "Also register the write tools `lurp_index` and `lurp_retract_annotation`. Off by default, so an agent that follows prompt-injected text cannot start an index run or delete annotations.");
}
