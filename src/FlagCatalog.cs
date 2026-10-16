// Purpose: one FlagSpec per CLI flag name, the single source of flag descriptions.
// Owns: the description, value hint, closed value list and requirement of each flag.
// Must not contain: mode membership (Program.ModeRegistry lists which flags a mode takes).

using Lurp.Adapters;
using Lurp.Handlers;
using Lurp.Storage;
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
    public static readonly FlagSpec Symbol = new("--symbol=", "The symbol ID to look up. Accepts the full `docCommentId|assemblyIdentity` form, a bare doc-comment ID (e.g. `T:Some.Type`), or a fully-qualified name.", "id");

    // Modes that accept --snapshot=: get-source, outline, get-symbol, search, find-symbol, navigate,
    // impact, context, timings, pin-snapshot, annotate, retract-annotation, get-annotations,
    // diagnostics, grep, and dead-candidates.
    public static readonly FlagSpec Snapshot = new("--snapshot=", "Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same.", "id");

    // Modes that accept --include-public: dead-candidates.
    public static readonly FlagSpec IncludePublic = new("--include-public", "Include public/protected symbols with no internal LIVE incoming edge as `uncertain_dead` (reason: `public_surface`). Default excludes them from `proved_dead`.");

    // Modes that accept --include-tests: dead-candidates.
    public static readonly FlagSpec IncludeTests = new("--include-tests", "Include test-project symbols (a project that references a test framework: `Microsoft.VisualStudio.TestPlatform.ObjectModel`, `Microsoft.Testing.Platform`, `xunit.core`, `xunit.v3.core`, `nunit.framework`, `Microsoft.VisualStudio.TestPlatform.TestFramework`) with no LIVE incoming edge as `uncertain` (reason: `test_harness`). Default excludes them. Without it, test helpers reached only through xUnit reflection discovery would all appear dead.");

    // Modes that accept --cursor=: outline, search, impact, context, get-annotations, diagnostics, grep, and dead-candidates.
    public static readonly FlagSpec Cursor = new("--cursor=", "Continue from a previous page's `next_cursor`.", "token");

    // Modes that accept --json: status, timings, and pin-snapshot.
    public static readonly FlagSpec Json = new("--json", "Emit structured JSON instead of plain text.");

    // Modes that accept --file=: navigate and context.
    public static readonly FlagSpec File = new("--file=", "Source file path relative to the solution root.", "path");

    // Modes that accept --line=: navigate and context.
    public static readonly FlagSpec Line = new("--line=", "1-based line number in the source file. Every emitted line number is 1-based too, so a reported `start_line` feeds straight back into `--line=`.", "n");

    // Read-command output controls (search, find-symbol, impact, context)
    public static readonly FlagSpec Output = new("--output=", "Payload rendering (default: `json`). `summary` is a digest; `jsonl` emits a `{\"type\":\"meta\"}` envelope followed by one compact object per result, so a consumer can stream and stop early. `jsonl` is rejected for a whole capsule, whose payload is a single document.", AllowedValues: ["summary", "json", "jsonl"]);
    public static readonly FlagSpec Quiet = new("--quiet", "Emit only the payload: suppresses the freshness stderr line, and for `--mode=context` prints just the written capsule path instead of the capsule itself.");
    public static readonly FlagSpec Freshness = new("--freshness=", "How hard to check that the snapshot still matches the working tree (default: `auto`: stat only). `hash` re-hashes every document; `off` skips the check. An unknown value is an error.", AllowedValues: ["auto", "hash", "off"]);
    public static readonly FlagSpec RequireFresh = new("--require-fresh", "Exit `2` when the snapshot is not fresh.");

    // Modes that accept --include-generated: outline, get-symbol, search, find-symbol,
    // navigate, context, diagnostics, grep, and dead-candidates (not index).
    public static readonly FlagSpec IncludeGenerated = new("--include-generated", "Include source-generated symbols.");

    // index
    public static readonly FlagSpec Solution = new("--solution=", "Path to the `.sln` or `.slnx` file.", "path");
    public static readonly FlagSpec Strategy = new("--strategy=", "`full`: index every document from scratch. `incremental`: only re-index changed documents. Default: `full` on first run, `incremental` on subsequent runs. `full` is the definition of correctness: use it to recover a known-good index.", AllowedValues: IndexRunner.Strategies);
    public static readonly FlagSpec OutputJson = new("--output-json=", "Also write the snapshot manifest as JSON (a one-way export; never read back as authority).", "path");
    public static readonly FlagSpec SkipAdapter = new("--skip-adapter=", "Skip a named framework adapter.", "name", AllowedValues: AdapterRegistry.Names, Repeatable: true);
    public static readonly FlagSpec SkipDiff = new("--skip-diff", "Skip computing and persisting the semantic diff against the previous snapshot. `--mode=diff` still computes the diff live.");
    public static readonly FlagSpec Verbose = new("--verbose", "Emit per-extractor timing lines to stderr.");
    public static readonly FlagSpec Force = new("--force", "Re-extract even when the deterministic snapshot id already exists as a completed snapshot; a full reindex needs `--strategy=full --force`, which deletes the identical snapshot and re-extracts it. With the incremental strategy it re-runs the incremental re-extraction in place (a no-op when no document changed). Does not bypass identity computation: an identical workspace produces the same snapshot id.");

    // get-source
    public static readonly FlagSpec Document = new("--document=", "Relative path of the document within the solution.", "relative-path");
    public static readonly FlagSpec StartLine = new("--start-line=", "1-based start line for line window (inclusive).", "n");
    public static readonly FlagSpec EndLine = new("--end-line=", "1-based end line for line window (inclusive).", "n");
    public static readonly FlagSpec ContextLines = new("--context-lines=", "Lines of context (symmetric expansion). Requires `--start-line=` or `--end-line=`.", "n");

    // get-symbol
    public static readonly FlagSpec View = new("--view=", "What to return.", AllowedValues: GetSymbolHandler.ViewNames);

    // search
    public static readonly FlagSpec Query = new("--query=", "Search term. Symbol search matches whole identifier tokens first; when no token matches, it falls back to case-insensitive substring matches over fully qualified symbol names (camel-case segments and prefixes count).", "term");
    public static readonly FlagSpec Type = new("--type=", "Search scope (default: `all`). An unknown value is an error.", AllowedValues: ["all", "source", "symbol"]);
    public static readonly FlagSpec Kind = new("--kind=", "Filter symbol results by Roslyn SymbolKind (e.g. `Type`, `Method`, `Field`, `Property`).", "SymbolKind");
    public static readonly FlagSpec SnippetTokens = new("--snippet-tokens=", $"Token window for source snippets (default: {SearchHandler.DefaultSnippetTokens}).", "n");

    // Modes that accept --limit=: outline, search, impact, get-annotations, diagnostics, grep, and dead-candidates.
    public static readonly FlagSpec Limit = new("--limit=", $"Max declarations per page (default: {OutlineHandler.DefaultLimit}).", "n");

    // grep
    public static readonly FlagSpec IgnoreCase = new("--ignore-case", "Make the search case-insensitive (default: case-sensitive).");

    // impact
    public static readonly FlagSpec Direction = new("--direction=", "Traversal direction (default: `downstream`). Use `upstream` to find all references to a symbol.", AllowedValues: ["downstream", "upstream"]);
    public static readonly FlagSpec Kinds = new("--kinds=", "Comma-separated edge kinds to follow, matched case-insensitively. An unknown kind is rejected with an error.", "list", AllowedValues: Enum.GetNames<EdgeKind>());
    public static readonly FlagSpec Provenance = new("--provenance=", "Comma-separated provenance values to follow (e.g. `compiler_proved,framework_derived`). Pass `compiler_proved` to follow only compiler-verified edges: direct interface implementations, virtual/override `MayDispatchTo`, `Calls`, `MethodGroupRef`, `Constructs`, `Implements`, `Inherits`, `Overrides`. Excludes framework-derived DI (`Registers`), string-reflection candidates (`Reflection*`), and inherited-only dispatch edges. Values are matched case-insensitively; an unknown value is rejected with an error.", "list", AllowedValues: Lurp.Storage.Provenance.All);
    public static readonly FlagSpec MaxDepth = new("--max-depth=", $"Maximum traversal depth (default: {ImpactHandler.DefaultMaxDepth}).", "n");

    // diff
    public static readonly FlagSpec FromSnapshot = new("--from-snapshot=", "Base snapshot ID.", "id");
    public static readonly FlagSpec ToSnapshot = new("--to-snapshot=", "Target snapshot ID.", "id");

    // context
    public static readonly FlagSpec Intent = new("--intent=", "Intent hint for assembly (default: `inspect`).", AllowedValues: ["inspect", "modify", "diagnose"]);
    public static readonly FlagSpec ContentBudget = new("--content-budget=",
        $"Token budget for capsule content (default: {ContextHandler.DefaultBudget}, or {ContextHandler.DefaultTypeAnchorBudget} when `--symbol=` is a type anchor and `--content-budget=` is omitted: a type's callee/caller tiers scale with member fan-out, so the default is kind-aware. An explicit `--content-budget=` is always honored as-is). Even at {ContextHandler.DefaultTypeAnchorBudget}, a large type anchor can still exhaust the budget before its lowest-priority tiers are reached (typically `relevant_tests` and `second_degree_context`). That is not a failure to budget away: refetch those tiers on their own with `--tier=`, e.g. `lurp --mode=context --symbol=<symbol-id> --tier=relevant_tests` (see `--tier=` below). Reported as `estimated_tokens`: anchor and item source plus the serialized weight of the substantive non-source sections (paths, topology, completeness, verification, likely change sites, affected public surfaces, inclusion reasons). `uncertainties` is appended after this pass, so it is not counted and is never trimmed. Per-item identity/provenance framing is navigation metadata and is not counted, so the emitted file is larger than `estimated_tokens`: size a context window from `estimated_artifact_tokens`. Over-budget capsules first bound paths and item source (recorded as `summarized`), then clear the lowest-priority sections greedily (`budget_exhausted`); `suggested_verification` is measured and can be cleared this way, while `uncertainties` is not. Every truncated category is declared in `omitted_tiers`. The anchor is never dropped.",
        "n");
    public static readonly FlagSpec MaxHops = new("--max-hops=", $"Maximum graph hops to expand (default: {ContextHandler.DefaultMaxHops}).", "n");
    public static readonly FlagSpec Scope = new("--scope=", "Logical scope label recorded on the anchor (default: the symbol ID).", "label");
    public static readonly FlagSpec AffectedProject = new("--affected-project=", "Repeatable. A project affected by the change.", "name", Repeatable: true);
    public static readonly FlagSpec Tier = new("--tier=", "Fetch ONE tier on its own instead of a capsule, with no token budget applied: this is how a capsule's `omitted_tiers` `budget_exhausted` entry is acted on.", AllowedValues: ContextAssembler.TierNames);
    public static readonly FlagSpec TierLimit = new("--tier-limit=", $"Items per tier page (default: {ContextHandler.DefaultTierLimit}).", "n");
    public static readonly FlagSpec CompletenessDetail = new("--completeness-detail", "Emit per-document `binding_incompleteness` rows. Without it, completeness carries a deterministic reason/project rollup (`binding_incompleteness_summary`) plus the total.");

    // status
    public static readonly FlagSpec Detail = new("--detail=", "Comma-separated sections to expand in `--json` output: `documents` restores the per-document version map, `references` the full metadata reference identities, `completeness` the per-document binding-incompleteness rows. Each is summarized by default; `all` expands every section. Unknown values are an error.", "list", AllowedValues: ["documents", "references", "completeness", "all"]);
    public static readonly FlagSpec MaxMismatches = new("--max-mismatches=", $"Cap on the `mismatches` list in `--json` output (default: {StatusHandler.DefaultMaxMismatches}; positive integer).", "n");

    // Modes that accept --project=: diagnostics and dead-candidates.
    public static readonly FlagSpec Project = new("--project=", "Filter to diagnostics in this project.", "name");

    // annotate
    public static readonly FlagSpec AnnotationKind = new("--annotation-kind=", "Annotation kind.", "kind", Requirement: FlagRequirement.Required);
    public static readonly FlagSpec Value = new("--value=", "Annotation value.", "text", Requirement: FlagRequirement.Required);
    public static readonly FlagSpec AnnotationId = new("--annotation-id=", "Surrogate PK from `get-annotations` annotation_id (positive integer).", "n", Requirement: FlagRequirement.Required);

    // pin-snapshot
    public static readonly FlagSpec Clear = new("--clear", "Clear the pin; reads will again default to the most recently built snapshot (`ORDER BY built_at_utc DESC`).");

    // diagnostics: no description in the former help map; the text comes from the CLI reference.
    public static readonly FlagSpec Severity = new("--severity=", "Filter by severity: Roslyn `DiagnosticSeverity` names, matched case-insensitively, or `all` for every severity including `Hidden`. When omitted, `Hidden` diagnostics are excluded. Unknown severity values are rejected with an error instead of returning an empty result.", "level", AllowedValues: DiagnosticSeverityFilters.Values);
    public static readonly FlagSpec Id = new("--id=", "Filter by diagnostic ID (e.g. `CS8933`).", "code");

    // serve
    public static readonly FlagSpec EnableWriteTools = new("--enable-write-tools", "Also register the write tools `lurp_index` and `lurp_retract_annotation`. Off by default, so an agent that follows prompt-injected text cannot start an index run or delete annotations.");
}
