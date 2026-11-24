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
///     means the same in every mode that takes it, one field serves all of them.
/// </summary>
internal static class FlagCatalog
{
    public static readonly FlagSpec Symbol = new("--symbol=", $"The symbol to look up. {HandlerBootstrap.AcceptedSymbolFormsText} More than one match is an error.", "id");

    public static readonly FlagSpec Snapshot = new("--snapshot=", "Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same.", "id");

    public static readonly FlagSpec IncludePublic = new("--include-public", "Include public/protected symbols with no internal LIVE incoming edge as `uncertain_dead` (reason: `public_surface`). Default excludes them from `proved_dead`.");

    public static readonly FlagSpec IncludeTests = new("--include-tests", $"Include test-project symbols (a project that references a test framework: {TestFrameworks.FrameworkAssembliesText}) with no LIVE incoming edge as `uncertain` (reason: `test_harness`). Default excludes them. Without it, test helpers reached only by a test framework would all appear dead.");

    public static readonly FlagSpec Cursor = new("--cursor=", "Continue from a previous page's `next_cursor`.", "token");

    public static readonly FlagSpec Json = new("--json", "Emit structured JSON instead of plain text.");

    public static readonly FlagSpec File = new("--file=", "Source file path relative to the solution root.", "path");

    public static readonly FlagSpec Line = new("--line=", "1-based line number in the source file. Every emitted line number is 1-based too, so a reported `start_line` feeds straight back into `--line=`.", "n");

    // Read-command output controls
    public static readonly FlagSpec Output = new("--output=", "Payload rendering (default: `json`). `summary` is a digest; `jsonl` emits nested records `{\"type\":\"meta\",\"meta\":{...}}` followed by one `{\"type\":\"<t>\",\"<t>\":{...}}` record per item (see the JSONL record-types table in docs/CLI_REFERENCE.md). `jsonl` is rejected for a whole capsule, whose payload is a single document.", AllowedValues: ["summary", "json", "jsonl"]);
    public static readonly FlagSpec Quiet = new("--quiet", "Emit only the payload: suppresses the freshness stderr line, and for `--mode=context` prints just the written capsule path instead of the capsule itself.");
    public static readonly FlagSpec Freshness = new("--freshness=", "How hard to check that the snapshot still matches the working tree (default: `auto`). `auto` compares file times with the time of the index run that last built or reused the snapshot; `auto` and `hash` also list a new `.cs` file under the git root (outside `bin`, `obj`, `.git`) that is not in the snapshot and is newer than that run, or that sits in a directory changed after it; `hash` also re-hashes every tracked document; `off` skips the check. An unknown value is an error.", AllowedValues: ["auto", "hash", "off"]);
    public static readonly FlagSpec RequireFresh = new("--require-fresh", "Exit `2` when the snapshot is not fresh.");

    public static readonly FlagSpec IncludeGenerated = new("--include-generated", "Include source-generated symbols.");

    public static readonly FlagSpec Solution = new("--solution=", "Path to the `.sln` or `.slnx` file.", "path");
    public static readonly FlagSpec Strategy = new("--strategy=", "`full`: index every document from scratch. `incremental`: re-index changed documents, their reverse-edge closure and type parts; when a document is added, deleted or edited outside member bodies, re-index its project and dependent projects. Default: `full` on first run, `incremental` on subsequent runs. `full` reuses a snapshot with the same id only when a full run built it; `full` is the definition of correctness: use it to recover a known-good index.", AllowedValues: IndexRunner.Strategies);
    public static readonly FlagSpec OutputJson = new("--output-json=", "Also write the snapshot manifest as JSON (a one-way export; never read back as authority).", "path");
    public static readonly FlagSpec SkipAdapter = new("--skip-adapter=", "Skip a named framework adapter.", "name", AllowedValues: AdapterRegistry.Names, Repeatable: true);
    public static readonly FlagSpec SkipDiff = new("--skip-diff", "Skip computing and persisting the semantic diff against the previous snapshot. `--mode=diff` still computes the diff live.");
    public static readonly FlagSpec Verbose = new("--verbose", "Emit per-extractor timing lines to stderr.");
    public static readonly FlagSpec Force = new("--force", "Re-extract even when the deterministic snapshot id already exists as a completed snapshot: the run deletes the existing complete snapshot and builds it again (with `--strategy=incremental`, from the previous snapshot; a no-op when no document changed). A pinned snapshot is not rebuilt and the run stops with an error. Does not bypass identity computation: an identical workspace produces the same snapshot id.");

    public static readonly FlagSpec Document = new("--document=", "Relative path of the document within the solution.", "relative-path");
    public static readonly FlagSpec StartLine = new("--start-line=", "1-based start line for line window (inclusive).", "n");
    public static readonly FlagSpec EndLine = new("--end-line=", "1-based end line for line window (inclusive).", "n");
    public static readonly FlagSpec ContextLines = new("--context-lines=", "Lines of context (symmetric expansion). Requires `--start-line=` or `--end-line=`.", "n");

    public static readonly FlagSpec View = new("--view=", "What to return.", AllowedValues: GetSymbolHandler.ViewNames);

    public static readonly FlagSpec Query = new("--query=", "Search term. Symbol search lists whole-token matches of the fully qualified name first (by rank), then case-insensitive substring matches of the fully qualified name (camel-case segments and prefixes count), for plain identifier queries. `--limit` cuts this one list; `--cursor` continues it.", "term");
    public static readonly FlagSpec Type = new("--type=", "Search scope (default: `all`). An unknown value is an error.", AllowedValues: ["all", "source", "symbol"]);
    public static readonly FlagSpec Kind = new("--kind=", "Filter symbol results by kind (case-insensitive). An unknown value is an error.", AllowedValues: DeadCandidateLiveness.CandidateKinds);
    public static readonly FlagSpec SnippetTokens = new("--snippet-tokens=", $"Tokens in the window around the first match for source snippets (default: {SearchHandler.DefaultSnippetTokens}). The window always holds the whole match.", "n");

    public static readonly FlagSpec Limit = new("--limit=", $"Max declarations per page (default: {OutlineHandler.DefaultLimit}).", "n");

    public static readonly FlagSpec IgnoreCase = new("--ignore-case", "Make the search case-insensitive (default: case-sensitive).");

    public static readonly FlagSpec Direction = new("--direction=", "Traversal direction (default: `downstream`). Both directions exclude `Declares`, `Contains` and `TestedBy` by default. Use `--kinds=` to supply a different set.", AllowedValues: ["downstream", "upstream"]);
    public static readonly FlagSpec Kinds = new("--kinds=", "Comma-separated edge kinds to follow, matched case-insensitively. An unknown kind is rejected with an error. An empty value or an empty item (`Calls,`) is an error. Replaces the default set for the direction (see `--direction=`).", "list", AllowedValues: Enum.GetNames<EdgeKind>());
    public static readonly FlagSpec Provenance = new("--provenance=", "Comma-separated provenance values to follow (e.g. `compiler_proved,framework_derived`). Pass `compiler_proved` to follow only compiler-verified edges: direct interface implementations, virtual/override `MayDispatchTo`, `Calls`, `MethodGroupRef`, `Constructs`, `Implements`, `Inherits`, `Overrides`. Excludes framework-derived DI (`Registers`), string-reflection candidates (`Reflection*`), and inherited-only dispatch edges. Values are matched case-insensitively; an unknown value is rejected with an error. An empty value or an empty item is an error.", "list", AllowedValues: Lurp.Storage.Provenance.All);
    public static readonly FlagSpec MaxDepth = new("--max-depth=", $"Maximum traversal depth (default: {ImpactRequest.DefaultMaxDepth}).", "n");

    public static readonly FlagSpec FromSnapshot = new("--from-snapshot=", "Base snapshot ID.", "id");
    public static readonly FlagSpec ToSnapshot = new("--to-snapshot=", "Target snapshot ID.", "id");

    public static readonly FlagSpec Intent = new("--intent=", "Intent hint for assembly (default: `inspect`).", AllowedValues: ["inspect", "modify", "diagnose"]);
    public static readonly FlagSpec ContentBudget = new("--content-budget=",
        $"Token budget for capsule content (default: {ContextRequest.DefaultBudget}, or {ContextRequest.DefaultTypeAnchorBudget} when the anchor resolves to a type (from `--symbol=` or `--file=`/`--line=`) and `--content-budget=` is omitted: a type's callee/caller tiers scale with member fan-out, so the default is kind-aware. An explicit `--content-budget=` is always honored as-is). Even at {ContextRequest.DefaultTypeAnchorBudget}, a large type anchor can still exhaust the budget before its lowest-priority tiers are reached (typically `relevant_tests` and `second_degree_context`). That is not a failure to budget away: refetch those tiers on their own with `--tier=`, e.g. `lurp --mode=context --symbol=<symbol-id> --tier=relevant_tests` (see `--tier=` below). Reported as `estimated_tokens`: anchor and item source plus the serialized weight of the substantive non-source sections (paths, semantic causes, constraints, topology, completeness, verification, likely change sites, affected public surfaces, inclusion reasons). `uncertainties` is appended after this pass, so it is not counted and is never trimmed. Per-item identity/provenance framing is navigation metadata and is not counted, so the emitted file is larger than `estimated_tokens`: size a context window from `estimated_artifact_tokens`. Over-budget capsules first keep only the first whole paths and bound item source (recorded as `summarized`), then clear the lowest-priority sections greedily (`budget_exhausted`); `suggested_verification` is measured and can be cleared this way, while `uncertainties` is not. Every truncated category is declared in `omitted_tiers`. The anchor is never dropped.",
        "n");
    public static readonly FlagSpec MaxHops = new("--max-hops=", $"Maximum graph hops to expand (default: {ContextRequest.DefaultMaxHops}).", "n");
    public static readonly FlagSpec Scope = new("--scope=", "Logical scope label recorded on the anchor (default: the symbol ID).", "label");
    public static readonly FlagSpec AffectedProject = new("--affected-project=", "Repeatable. A project affected by the change.", "name", Repeatable: true);
    public static readonly FlagSpec Tier = new("--tier=", "Fetch ONE tier on its own instead of a capsule, with no token budget applied: this is how a capsule's `omitted_tiers` `budget_exhausted` entry is acted on.", AllowedValues: ContextAssembler.TierNames);
    public static readonly FlagSpec TierLimit = new("--tier-limit=", $"Items per tier page (default: {ContextRequest.DefaultTierLimit}).", "n");
    public static readonly FlagSpec CompletenessDetail = new("--completeness-detail", "Emit per-document `binding_incompleteness` rows. Without it, completeness carries a deterministic reason/project rollup (`binding_incompleteness_summary`) plus the total.");

    public static readonly FlagSpec Detail = new("--detail=", "Comma-separated sections to expand in `--json` output: `documents` restores the per-document version map, `references` the full metadata reference identities, `completeness` the per-document binding-incompleteness rows. Each is summarized by default; `all` expands every section. Unknown values are an error.", "list", AllowedValues: ["documents", "references", "completeness", "all"]);
    public static readonly FlagSpec MaxMismatches = new("--max-mismatches=", $"Cap on the `mismatches` list in `--json` output (default: {StatusHandler.DefaultMaxMismatches}; positive integer).", "n");

    public static readonly FlagSpec Project = new("--project=", "Filter to diagnostics in this project.", "name");

    public static readonly FlagSpec AnnotationKind = new("--annotation-kind=", "Annotation kind.", "kind", Requirement: FlagRequirement.Required);
    public static readonly FlagSpec Value = new("--value=", "Annotation value.", "text", Requirement: FlagRequirement.Required);
    public static readonly FlagSpec AnnotationId = new("--annotation-id=", "Surrogate PK from `get-annotations` annotation_id (positive integer).", "n", Requirement: FlagRequirement.Required);

    public static readonly FlagSpec Clear = new("--clear", "Remove every pin in the database; reads will again default to the snapshot that the last successful index run built or reused (`ORDER BY selected_at_utc DESC`).");

    public static readonly FlagSpec Severity = new("--severity=", "Filter by severity: Roslyn `DiagnosticSeverity` names, matched case-insensitively, or `all` for every severity including `Hidden`. When omitted, `Hidden` diagnostics are excluded. Unknown severity values are rejected with an error instead of returning an empty result.", "level", AllowedValues: DiagnosticSeverityFilters.Values);
    public static readonly FlagSpec Id = new("--id=", "Filter by diagnostic ID (e.g. `CS8933`).", "code");

    public static readonly FlagSpec EnableWriteTools = new("--enable-write-tools", "Also register the write tools `lurp_index` and `lurp_retract_annotation`. Off by default, so an agent that follows prompt-injected text cannot start an index run or delete annotations.");
}
