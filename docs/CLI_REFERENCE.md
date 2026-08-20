# Lurp CLI reference

This is the operational reference for Lurp's command-line interface. For the
product overview, see the [root README](../README.md). For the architecture, see
[ARCHITECTURE.md](ARCHITECTURE.md).

Lurp loads .NET solutions through Roslyn, stores snapshot-bound symbols,
relationships, and source spans in SQLite (`index.db`), and exposes commands
for retrieval, semantic diffing, impact analysis, context capsules,
and annotations.

## Quick Start

```
lurp --mode=index --solution=MySolution.sln --output-dir=./out
```

This creates `./out/index.db` containing all indexed symbols, edges, and source facts.

`--mode=index` always indexes the entire solution named by `--solution=`; there is
no per-project or per-directory scoping flag. To point Lurp at one part of a larger
codebase, index the whole `.sln`/`.slnx` once and then query narrowly (`--mode=search`,
`--mode=context`, etc.). You cannot index a subset up front.

**MCP callers, note:** CLI validation errors name flags (e.g. `ERROR: --document is required.`), while MCP errors name plain properties (e.g. `document is required.`) without the `--` prefix — the two surfaces share the same validation logic but render the argument name in the form that matches the caller.

## Read-command options

Accepted by the read commands: `--mode=search`, `--mode=grep`, `--mode=find-symbol`,
`--mode=impact`, `--mode=context`, `--mode=get-source`, `--mode=get-symbol`,
`--mode=navigate`, `--mode=outline`, `--mode=diagnostics`,
`--mode=get-annotations`, and `--mode=dead-candidates`.

| Argument | Required | Description |
|---|---|---|
| `--output=<summary\|json\|jsonl>` | No | Payload rendering (default: `json`). `summary` is a digest; `jsonl` emits a `{"type":"meta"}` envelope followed by one compact object per result, so a consumer can stream and stop early. `jsonl` is rejected for a whole capsule, whose payload is a single document. |
| `--quiet` | No | Emit only the payload: suppresses the freshness stderr line, and for `--mode=context` prints just the written capsule path instead of the capsule itself. |
| `--freshness=<auto\|hash\|off>` | No | How hard to check that the snapshot still matches the working tree (default: `auto`: stat only). `hash` re-hashes files whose stat differs; `off` skips the check. |
| `--require-fresh` | No | Exit `2` when the snapshot is not fresh. |

`--output=` applies to modes whose payload is a JSON sequence (`search`, `grep`,
`find-symbol`, `impact`, `context`, `outline`, `diagnostics`, `get-annotations`, `dead-candidates`).
`get-source`, `get-symbol`, and `navigate` emit a single fixed shape — raw source
bytes or one JSON document — so they accept `--freshness=`, `--require-fresh`, and
`--quiet` but reject `--output=`.

Freshness is delivered in two tiers, because two payload shapes exist:

- **Every read mode emits a freshness signal**: a stderr line reporting `state` (`fresh`, `stale`, `unknown`) and the `method` used to determine it, plus an exit code. `--require-fresh` exits `2` when the snapshot is not fresh, so a stale read is never presented as a current one.
- **Modes whose payload is JSON additionally embed a `freshness` block** in the payload: `search`, `grep`, `find-symbol`, `impact`, `context`, `navigate`, `get-symbol --view=metadata`, `outline`, `diagnostics`, `get-annotations`, and `dead-candidates`.
- **Raw-source modes cannot carry a block**: `get-source`, and the `signature`/`body`/`declaration`/`containing-type`/`surrounding` views of `get-symbol`, write source bytes to stdout verbatim by contract (consumers pipe them to files or compilers). Their signal is the stderr line plus the exit code; `--quiet` suppresses the line, never the exit code.

**Line-number base:** every emitted line number is **1-based**, matching the `--line=<n>` input convention. This covers edge locations (`impact` hops' `source_line`/`source_end_line`, `diff` `edge_location_changed` details) and declaration locations (`context` capsule `locations`, tier-page `path:start_line`). A reported `start_line` can be passed verbatim to `--line=` (for example to `navigate`) and resolves to the same symbol.

**Column-number base:** unlike line numbers, column numbers are **not** converted to 1-based. `diagnostics`' `start_column`/`end_column` are the raw Roslyn `LinePosition.Character` values and are **0-based**. There is no CLI or MCP input that takes a column, so this only affects how you read the output.

**Document-path base:** every `--document=<relative-path>` argument (and the matching MCP `document` property) is relative to the directory containing the `--solution=` file, **not** the repository/git root. When the solution lives in a subdirectory of the repo (e.g. `--solution=eNote/eNote.sln`), a git-root-relative path like `eNote/eNote.Tests/Foo.cs` will not resolve — pass `eNote.Tests/Foo.cs` instead. Confirmed 2026-08-20 against a live target: `get-source` fails fast with `ERROR: Document '...' not found in snapshot.` on a mismatched path; `dead-candidates --document=` does not error on a mismatch — it's a filter, not a required lookup, so a wrong prefix silently returns `candidate_count: 0` instead of surfacing the mistake.

## Modes

### `--mode=index`

Index a solution and store facts in the database.

```
--mode=index --solution=<path> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--solution=<path>` | Yes | Path to the `.sln` or `.slnx` file. |
| `--output-dir=<path>` | No | Directory where `index.db` is stored. Defaults to the solution's directory. Also accepted via `LURP_OUTPUT_DIR`. |
| `--strategy=<full\|incremental>` | No | `full`: index every document from scratch. `incremental`: only re-index changed documents. Default: `full` on first run, `incremental` on subsequent runs. |
| `--output-json=<path>` | No | Also write the snapshot manifest as JSON. |
| `--skip-adapter=<name>` | No | Skip a named framework adapter. Valid: `ASP.NET Core`, `Dependency Injection`, `MediatR`, `EF Core`, `Serialization`, `Test`. |
| `--verbose` | No | Emit per-extractor timing lines to stderr. |
| `--skip-diff` | No | Skip computing and persisting the semantic diff against the previous snapshot. |

`--strategy=full` is the definition of correctness for the index. Use it as the recovery mechanism when something looks wrong.

---

### `--mode=get-source`

Retrieve source text for a document.

```
--mode=get-source --document=<relative-path> --output-dir=<path> [--start-line=<n>] [--end-line=<n>] [--context-lines=<n>] [--snapshot=<id>]
```

| Argument | Required | Description |
|---|---|---|
| `--document=<relative-path>` | Yes | Relative path of the document within the solution. |
| `--start-line=<n>` | No | 1-based start line for line window. |
| `--end-line=<n>` | No | 1-based end line for line window. |
| `--context-lines=<n>` | No | Lines of context (symmetric expansion). Requires `--start-line=` or `--end-line=`. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to read from (default: latest). |

Accepts the shared [read-command options](#read-command-options) except `--output=` (the source is written to stdout verbatim, so freshness is signaled on stderr and via `--require-fresh`'s exit code, never as a JSON wrapper). There is no CLI `--outline` flag for this mode; the MCP tool `lurp_get_source` offers an `outline` parameter instead.

---

### `--mode=outline`

List declarations in a document with line spans.

```
--mode=outline --document=<relative-path> --output-dir=<path> [--include-generated] [--limit=<n>] [--cursor=<token>] [--snapshot=<id>]
```

| Argument | Required | Description |
|---|---|---|
| `--document=<relative-path>` | Yes | Relative path of the document within the solution. |
| `--include-generated` | No | Include source-generated declarations. |
| `--limit=<n>` | No | Max declarations per page (default: 100). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

Also accepts the shared [read-command options](#read-command-options). Output includes `symbol_id`, `kind`, `fully_qualified_name`, `start_line`, `end_line`, `signature_start_line`, `name_start_line`, `is_partial`, `is_generated`, `declaration_count`, and `next_cursor` for pagination. `declaration_count` is the **total** number of declarations matching the filter across every page, not the number of items on the current page — do not use it to drive a pagination loop; use `next_cursor` (a `null`/absent `next_cursor` marks the last page).

---

### `--mode=diagnostics`

List diagnostics captured at index time for the snapshot: compiler diagnostics plus any diagnostics from analyzers referenced by the target project (`Project.AnalyzerReferences`, e.g. built-in SDK analyzers like CA1822). IDE-only code-style rules (e.g. IDE0005) are not included even when the target project's own build enables them (e.g. `EnforceCodeStyleInBuild` plus an `.editorconfig` severity) — confirmed 2026-08-20 against a live target: Roslyn's build-time compilation additionally gates `IDE0005` behind `GenerateDocumentationFile=true`, independent of anything Lurp does. **For unused-using-directive detection, query the compiler diagnostic instead: `--severity=hidden --id=CS8019`** ("Unnecessary using directive") — Lurp always captures this regardless of the target's analyzer/code-style configuration.

```
--mode=diagnostics --output-dir=<path> [--document=] [--project=] [--severity=] [--id=] [--limit=<n>] [--cursor=<token>] [--snapshot=<id>]
```

| Argument | Required | Description |
|---|---|---|
| `--document=<relative-path>` | No | Filter to diagnostics in this document. |
| `--project=<name>` | No | Filter to diagnostics in this project. |
| `--severity=<level>` | No | Filter by severity (Roslyn `DiagnosticSeverity` names: `Hidden`, `Info`, `Warning`, `Error`; matched case-insensitively, or `all` for every severity including `Hidden`). When omitted, `Hidden` diagnostics are excluded. Unknown severity values are rejected with an error instead of returning an empty result. |
| `--id=<code>` | No | Filter by diagnostic ID (e.g. `CS8933`). |
| `--limit=<n>` | No | Max diagnostics per page (default: 100). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

Also accepts the shared [read-command options](#read-command-options). The response includes `snapshot_id`, `document`, `project`, `severity`, `id`, `diagnostics` (array with `project_name`, `document_path`, `in_snapshot`, `severity`, `id`, `message`, `start_line`, `start_column`, `end_line`, `end_column`), `diagnostic_count`, and `next_cursor` for pagination. `diagnostic_count` is the **total** number of diagnostics matching the filter across every page, not the number of items on the current page — do not use it to drive a pagination loop; use `next_cursor` (a `null`/absent `next_cursor` marks the last page). Diagnostics reflect compiler and analyzer state at index time, not re-evaluated diagnostics. See the column-number-base note above: `start_column`/`end_column` are 0-based.

---

### `--mode=get-symbol`

Look up symbol metadata or source by view kind.

```
--mode=get-symbol --symbol=<id> --view=<kind> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol ID to look up. Accepts the full `docCommentId|assemblyIdentity` form, a bare doc-comment ID (e.g. `T:Some.Type`), or a fully-qualified name. |
| `--view=<kind>` | Yes | View kind: `metadata`, `signature`, `body`, `declaration`, `containing-type`, `surrounding`. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to read from (default: latest). |
| `--context-lines=<n>` | No | Lines of context for `--view=surrounding` (default: 3). |
| `--include-generated` | No | Include source-generated symbols. |

Accepts the shared [read-command options](#read-command-options) except `--output=`. `--view=metadata` embeds the `freshness` block in its JSON payload and a `locations` array (`{ document_path, start_line, end_line, is_generated }` per declaration, lines 1-based); the five source views write source bytes verbatim, so their freshness signal is the stderr line plus `--require-fresh`'s exit code.

---

### `--mode=search`

Full-text search over source text and symbols.

```
--mode=search --query=<term> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--query=<term>` | Yes | Search term. Symbol search matches whole identifier tokens first; when no token matches, it falls back to case-insensitive substring matches over fully qualified symbol names (camel-case segments and prefixes count). |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--type=<all\|source\|symbol>` | No | Search scope (default: `all`). |
| `--kind=<SymbolKind>` | No | Filter symbol results by Roslyn SymbolKind (e.g. `Type`, `Method`, `Field`, `Property`). |
| `--limit=<n>` | No | Max results per scope (default: 20). |
| `--snippet-tokens=<n>` | No | Token window for source snippets (default: 64). |
| `--snapshot=<id>` | No | Snapshot to search (default: latest). |
| `--include-generated` | No | Include source-generated symbols. |
| `--cursor=<token>` | No | Continue from a previous page's `nextCursor` (`--type=symbol` only). |

Punctuation in queries is quoted as an FTS5 phrase literal (`SearchUtils.ToFtsPhrase`), so dotted names like `CourseService.CreateAsync` and queries containing `"`, `*`, `:`, `()`, `<>` no longer throw `fts5: syntax error`.

Also accepts the shared [read-command options](#read-command-options).

---

### `--mode=grep`

Literal/exact-text search over source content with per-occurrence line numbers.

```
--mode=grep --query=<term> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--query=<term>` | Yes | Exact substring to find. The match is byte-exact, including punctuation and spacing. Case-sensitive by default; pass `--ignore-case` for case-insensitive. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--limit=<n>` | No | Max matches per page (default: 50). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--ignore-case` | No | Make the search case-insensitive (default: case-sensitive). |
| `--include-generated` | No | Include source-generated documents. |
| `--snapshot=<id>` | No | Snapshot to search (default: latest). |

Also accepts the shared [read-command options](#read-command-options).

Output is JSON with `snapshot_id`, `query`, `ignore_case`, `results` (array of `{ document_path, start_line, start_column, end_line, end_column, line_text }` — lines 1-based, columns 0-based), `match_count` (total matches across every page, not the page size), and `next_cursor`. `match_count` is the total, not the page length — do not use it to drive a pagination loop; use `next_cursor` (a `null`/absent `next_cursor` marks the last page). Each result is one exact occurrence, ordered by `document_path` then `start_line`/`start_column`, so the same `start_line` can be passed to `get-source --start-line=` or `navigate --file= --line=` to re-resolve the location.

---

### `--mode=find-symbol`

Resolve a symbol by fully qualified name.

```
--mode=find-symbol --symbol=<name> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<name>` | Yes | Fully qualified name to resolve. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to search (default: latest). |
| `--include-generated` | No | Include source-generated symbols. |

Also accepts the shared [read-command options](#read-command-options).

`declarationCount` is scoped to the requested snapshot, so a non-partial type reports `1` however many historical declarations retention still holds; a partial type reports its true multiplicity in that snapshot.

The payload carries a `locations` array: one entry per declaration in the snapshot, each `{ document_path, start_line, end_line, is_generated }` (lines 1-based). This answers "where is X defined?" in one call, without a capsule. A partial type reports one entry per file, matching `declaration_count`; `--include-generated` controls whether generated declarations appear. In `--output=summary`, the first location prints as `path:start_line`.

---

### `--mode=navigate`

Resolve an indexed declaration by file and line.

```
--mode=navigate --file=<path> --line=<n> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--file=<path>` | Yes | Source file path relative to the solution root. |
| `--line=<n>` | Yes | 1-based line number in the source file. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |
| `--include-generated` | No | Include source-generated declarations. |

Accepts the shared [read-command options](#read-command-options) except `--output=`; the JSON payload embeds the `freshness` block.

Returns the symbol ID, fully-qualified name, kind, and exact source span of the declaration at that location. The `target` carries both the character offsets (`full_start`/`full_end`/`name_start`/`name_end`, the exact-span contract) and 1-based `start_line`/`end_line`; the reported `start_line` round-trips, so passing it back as `--line=` resolves to the same symbol.

---

### `--mode=diff`

Show semantic changes between two snapshots.

```
--mode=diff --from-snapshot=<id> --to-snapshot=<id> --output-dir=<path>
```

| Argument | Required | Description |
|---|---|---|
| `--from-snapshot=<id>` | Yes | Base snapshot ID. |
| `--to-snapshot=<id>` | Yes | Target snapshot ID. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |

Output is JSON with change records (added, removed, modified symbols and their detail).

---

### `--mode=impact`

Trace the impact path of a changed symbol.

```
--mode=impact --symbol=<id> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol ID to trace from. Accepts the full `docCommentId|assemblyIdentity` form, a bare doc-comment ID (e.g. `T:Some.Type`), or a fully-qualified name. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--direction=<downstream\|upstream>` | No | Traversal direction (default: `downstream`). Use `upstream` to find all references to a symbol. |
| `--max-depth=<n>` | No | Maximum traversal depth (default: 3). |
| `--kinds=<list>` | No | Comma-separated edge kinds to follow. |
| `--provenance=<list>` | No | Comma-separated provenance values to follow (e.g. `compiler_proved,framework_derived`). Pass `compiler_proved` to follow only compiler-verified edges: direct interface implementations, virtual/override `MayDispatchTo`, `Calls`, `Constructs`, `Implements`, `Inherits`, `Overrides`. Excludes framework-derived DI (`Registers`), string-reflection candidates (`Reflection*`), and inherited-only dispatch edges. |
| `--max-paths=<n>` | No | Paths per page (default: 50). When more exist, the response carries `truncated.{reason,total,remaining,cursor}`. |
| `--cursor=<token>` | No | Continue from a previous page's `truncated.cursor`. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

Also accepts the shared [read-command options](#read-command-options).

Every response carries `groups`: the paths grouped by first hop, computed over *all* paths before the page is cut, so the fan-out summary stays complete even when the path list is truncated.

---

### `--mode=context`

Assemble a context capsule for a symbol or source location.

```
--mode=context (--symbol=<id> | --file=<path> --line=<n>) --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes* | Symbol ID to anchor on. Accepts the full `docCommentId|assemblyIdentity` form, a bare doc-comment ID (e.g. `T:Some.Type`), or a fully-qualified name. |
| `--file=<path>` | Yes* | Source file path (use with `--line`). |
| `--line=<n>` | Yes* | Line number in the source file. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--intent=<inspect\|modify\|diagnose>` | No | Intent hint for assembly (default: `inspect`). |
| `--content-budget=<n>` | No | Token budget for capsule **content** (default: 8000, or 16000 when `--symbol=` is a type anchor and `--content-budget=` is omitted: a type's callee/caller tiers scale with member fan-out, so the default is kind-aware. An explicit `--content-budget=` is always honored as-is). Even at 16000, a large type anchor can still exhaust the budget before its lowest-priority tiers are reached (typically `relevantTests` and `secondDegreeContext`). That is not a failure to budget away: refetch those tiers on their own with `--tier=`, e.g. `lurp --mode=context --symbol=<symbol-id> --tier=relevantTests` (see `--tier=` below). Reported as `estimatedTokens`: anchor and item source plus the serialized weight of the substantive non-source sections (paths, topology, completeness, uncertainties, verification, likely change sites, affected public surfaces, inclusion reasons). Per-item identity/provenance framing is navigation metadata and is not counted, so the emitted file is larger than `estimatedTokens`: size a context window from `estimatedArtifactTokens` (see [Capsule token estimates](#capsule-token-estimates)). Over-budget capsules first bound paths and item source (recorded as `summarized`), then clear the lowest-priority sections greedily (`budget_exhausted`); every truncated category is declared in `omittedTiers`. The anchor is never dropped. |
| `--max-hops=<n>` | No | Maximum graph hops to expand (default: 3). |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |
| `--include-generated` | No | Include source-generated symbols. |
| `--completeness-detail` | No | Emit per-document `binding_incompleteness` rows. Without it, completeness carries a deterministic reason/project rollup (`binding_incompleteness_summary`) plus the total. |
| `--tier=<name>` | No | Fetch ONE tier on its own instead of a capsule, with no token budget applied: this is how a capsule's `omittedTiers` `budget_exhausted` entry is acted on. Valid: `contracts`, `direct_callees`, `direct_callers`, `registered_implementations`, `relevant_tests`, `second_degree_context`, `surrounding_source`. |
| `--tier-limit=<n>` | No | Items per tier page (default: 25). |
| `--cursor=<token>` | No | Continue a tier from its `next_cursor` (`--tier` only). |

\* Either `--symbol` or both `--file` and `--line` must be provided.

Also accepts the shared [read-command options](#read-command-options).

The capsule is always written to `<output-dir>/capsule-<sanitized-id>.json` and also printed to stdout. Long symbol IDs are shortened with a stable hash suffix so the path remains valid on Windows; `--quiet` and `--output=summary` replace the stdout copy, never the file.

#### Capsule token estimates

A capsule reports two different numbers, and they are not interchangeable:

| Field | What it measures | Use it for |
|---|---|---|
| `estimatedTokens` | **Content only**: anchor and item source plus the serialized weight of the substantive non-source sections. Per-item identity/provenance framing (symbol IDs, fully-qualified names, edge kinds, provenance, coordinates) is navigation metadata and is not counted. | Understanding what `--content-budget` bounded. This is the budget basis. |
| `estimatedArtifactTokens` | The **whole emitted file** (serialized length ÷ 4), framing included. | Sizing a context window. |

`estimatedArtifactTokens` is always the larger of the two, typically by a wide
margin on capsules with many small items. It is reported, never budgeted
against: budgeting on the whole serialization was measured to force dropping
whole tiers (`directCallees`, `registeredImplementations`, `surroundingSource`)
at realistic budgets, which is a worse capsule for the same context cost.

#### Reading `omittedTiers`

`omittedTiers` carries **exactly one terminal record per category**, describing
the emitted capsule rather than the history of how it settled. Reasons:

| Reason | Meaning |
|---|---|
| `empty` | Proved absence. The relation was observable and there is none. Safe to act on. |
| `unresolved` | Unobservable. Bindings were lost over the anchor's region, or no anchor resolved at all (gap capsules mark every tier this way). **Not** evidence that the relation does not exist. |
| `summarized` | Present but bounded: paths clipped, or item source truncated at the per-item cap. |
| `budget_exhausted` | Bounded by budget. **With items still present in the section**, the included items are a complete greedy prefix of the tier in its relevance order. **With no items**, the tier was fully omitted. |

Both `budget_exhausted` shapes are recovered the same way: refetch that one tier
unbudgeted with `--tier=<category>` (see `inclusionReasons["omittedTiers.budget_exhausted"]`,
which is retained in the capsule even when budget pressure clears every other
inclusion reason).

A missing section is not the same as an empty one. When the enforcer drops
`topology` or `completeness` they are omitted from the JSON entirely rather than
serialized with zeroed counts, because zeroed counts read as a positive claim
("no incoming references") that the capsule has not established. The
corresponding `omittedTiers` record is the authority.

#### Regeneration is bounded by snapshot retention

A capsule is byte-for-byte regenerable from the snapshot it was built against,
but only while that snapshot is retained: `--mode=index` prunes to the most
recent 3 snapshots. Once the source snapshot is pruned, the artifact can no
longer be reproduced or re-verified against the store.

---

### `--mode=status`

Show the current database status.

```
--mode=status --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--solution=<path>` | No | If provided, compares the current workspace against the latest snapshot and reports freshness mismatches. |
| `--json` | No | Emit structured JSON instead of plain text. Back-compat alias for `--output=json`. |
| `--output=<summary\|json>` | No | Payload rendering (default: `summary` when neither flag is given). `jsonl` is rejected — the payload is a single document. |
| `--detail=<list>` | No | Comma-separated sections to expand in `--json` output: `documents` restores the per-document version map, `references` the full metadata reference identities, `completeness` the per-document binding-incompleteness rows. Each is summarized by default; `all` expands every section. |
| `--max-documents=<n>` | No | Accepted and validated (default: 50; positive integer), but **inert in the CLI**: the status JSON reports `mismatches`, not a changed-documents sample. The MCP `lurp_status` `max_documents` parameter is the effective cap. |
| `--max-mismatches=<n>` | No | Cap on the `mismatches` list in `--json` output (default: 50; positive integer). |

The manifest always carries a completeness block (`binding_incompleteness_summary`
plus `binding_incompleteness_total`); `--detail=completeness` (or `all`) adds the
per-document `binding_incompleteness` rows. This matches the MCP `lurp_status`
`sections=completeness` behavior.

Batch document freshness — checking a specific list of documents against the
snapshot, the MCP `lurp_status` `documents` parameter — has **no CLI
equivalent**: `--documents=` is not a flag of `--mode=status` (and would be
rejected as unknown). To check one document from the CLI, read its freshness via
a read mode's stderr line / `--require-fresh` exit code instead.

---

### `--mode=timings`

Show step-by-step timing data for a snapshot.

```
--mode=timings --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to inspect (default: latest). |
| `--json` | No | Emit structured JSON instead of plain text. |

---

### `--mode=annotate`

Attach a user-authored annotation to a symbol.

```
--mode=annotate --symbol=<id> --annotation-kind=<kind> --value=<text> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol ID to annotate. |
| `--annotation-kind=<kind>` | Yes | Annotation kind. |
| `--value=<text>` | Yes | Annotation value. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

---

### `--mode=get-annotations`

Retrieve annotations by symbol, document, or snapshot (with kind filtering and keyset pagination).

```
--mode=get-annotations (--symbol=<id> | --document=<path>) --output-dir=<path> [--kind=<kind>] [--limit=<n>] [--cursor=<token>] [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | No | The symbol ID to query. Accepts the full `docCommentId|assemblyIdentity` form, a bare doc-comment ID, or a fully-qualified name. Mutually exclusive with `--document=`. |
| `--document=<relative-path>` | No | Relative path of the document within the solution. Mutually exclusive with `--symbol=`. |
| `--kind=<kind>` | No | Filter to one annotation kind. |
| `--limit=<n>` | No | Max annotations per page (default: 100; positive integer). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

Accepts the shared [read-command options](#read-command-options). `--symbol=` and
`--document=` are enforced as mutually exclusive. With neither, all annotations in
the snapshot are returned.

The JSON payload is `{ snapshot_id, symbol_id, document, kind, annotations,
annotation_count, next_cursor, freshness }`; each annotation is `{ annotation_id, symbol_id,
kind, value, document_path }`. `annotation_count` is the **total** number of
annotations matching the filter across every page, not the number of items on the
current page — do not use it to drive a pagination loop; use `next_cursor` (a
`null`/absent `next_cursor` marks the last page). When more results remain than
fit on the page, `next_cursor` is set and `--output=summary` prints
`-- <shown>/<total> annotation(s); more available (--cursor)`. `--cursor` tokens
are snapshot-scoped continuation tokens, not raw offsets. Each annotation now carries `annotation_id` (surrogate PK) so a caller can address one row with `--mode=retract-annotation`.

---

### `--mode=retract-annotation`

Retract one user-authored annotation by surrogate id, scoped to exactly one snapshot. This is the supported way to correct or remove a mistaken `--mode=annotate` entry without manual SQL.

```
--mode=retract-annotation --annotation-id=<n> --output-dir=<path> [--snapshot=<id>]
```

| Argument | Required | Description |
|---|---|---|
| `--annotation-id=<n>` | Yes | Surrogate PK from `get-annotations` annotation_id (positive integer). |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to act on (default: latest). The DELETE is `WHERE snapshot_id=@snapshot AND annotation_id=@id` — one row only, no cross-snapshot effect. Copy-forward clones (`CopyAnnotationsToSnapshot`) allocate fresh ids, so retracting in one snapshot does not delete its clone in another. |

JSON output is `{ status:"ok", snapshot_id, annotation_id, retracted:true }`. On no such row in that snapshot the mode exits 1 with `ERROR: annotation_id <n> not found in snapshot <id>.` Use `--snapshot=` explicitly when you have more than one snapshot pinned.

---

### `--mode=dead-candidates`

List dead-code candidates: symbols with no incoming LIVE edge after suppression-ladder filtering.

```
--mode=dead-candidates --output-dir=<path> [--project=<name>] [--document=<path>] [--kind=<Type|Method|Property|Field|Event>] [--limit=<n>] [--cursor=<token>] [--snapshot=<id>] [--include-public] [--include-generated] [--include-tests]
```

| Argument | Required | Description |
|---|---|---|
| `--project=<name>` | No | Filter to this project (assembly simple name, e.g. `eNote.API`). |
| `--document=<relative-path>` | No | Filter to symbols declared in this document (forward-slash, relative to the solution root — see [Document-path base](#read-command-options) above, not the repo root). |
| `--kind=<kind>` | No | Filter by symbol kind: `Type`, `Method`, `Property`, `Field`, `Event` (case-insensitive). |
| `--limit=<n>` | No | Max dead candidates per page (default: 50, max: 200). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |
| `--include-public` | No | Include public/protected symbols with no internal LIVE incoming edge as `uncertain_dead` (reason: `public_surface`). Default excludes them from `proved_dead`. |
| `--include-generated` | No | Include source-generated symbols (default excludes `is_generated=1`). Surfaced as `uncertain` reason `generated_excluded` when no other signal. |
| `--include-tests` | No | Include test-project symbols (project `%.Tests`) with no LIVE incoming edge as `uncertain` (reason: `test_harness`). Default excludes them. |

Also accepts the shared [read-command options](#read-command-options). Output is JSON with `snapshot_id`, `filters` (echo of project/document/kind/include_*), `incoming_edge_kinds_checked[14]` (frozen canonical LIVE ordering: `Calls, Constructs, Reads, Writes, Handles, RoutesTo, Registers, MapsTo, MayDispatchTo, StaticallyCalls, TestedBy, ReflectionTypeRef, ReflectionMemberRef, ReflectionNameCandidate`), `live_provenance_rank[3]` (`compiler_proved, framework_derived, global_implementation_relation`), `uncertain_provenance[4]` (`possible, convention, name_candidate, runtime_unknown`), `candidate_count` (total filtered universe before LIVE/suppression, not page length), `dead_count`/`uncertain_count`/`unresolved_count` (totals across all pages, not page length), `candidates[]` page window (ordered by `symbol_id ASC`, `limit` items, default 50) with `symbol_id, fqn, kind, accessibility, document_path, locations[], project_name, declaration_count, is_generated, status (proved_dead/uncertain_dead/unresolved/uncertain), reason (handler-enum: no_incoming_live_edges, binding_incompleteness, public_surface, ef_convention, serialization_convention, framework_convention, possible_dispatch, name_candidate, runtime_unknown, generated_excluded, test_harness), uncertainties[] (verbatim `UncertaintyDetector`/`DeclaredBoundaries` wording), incoming_edge_summary {live_strong, live_weak, provenance_breakdown, kind_breakdown}, and `next_cursor` for pagination; `freshness` block is embedded. `candidate_count`/`dead_count`/`uncertain_count`/`unresolved_count` are totals, not page size — do not use them to drive a loop; use `next_cursor` (`null`/absent marks last page). `next_cursor` drives pagination (keyset over `symbol_id`). The handler uses batched `WHERE target_symbol_id IN (...)` per page, never per-candidate `GetIncomingEdges`. Structural kinds (`References, Inherits, Implements, Contains, Declares, Overrides, Hides, Returns, Throws, ExtensionReceiver`) are explicitly excluded from LIVE. Suppression ladder (strongest → weakest): strong LIVE edge -> alive (not dead); `MayDispatchTo` `possible` -> uncertain `possible_dispatch`; `convention`/`name_candidate`/`runtime_unknown` edges -> uncertain; binding_incompleteness `UnobservableReasons` overlap (`unsupported_syntax`, `compiler_error`, `unresolved_metadata`, `ambiguous_overload`, `extractor_failure`, `project_unreadable`, `convention_scan`) -> unresolved `binding_incompleteness` (excludes `filtered_external`); public/protected without `include_public` -> excluded from proved_dead; attribute-free `Public`/`Internal` property in `System.Text.Json`-referencing project with no `JsonPropertyName`/`JsonProperty`/`DataMember`/`JsonIgnore`/`IgnoreDataMember` attribute -> uncertain `serialization_convention`; private `set`/`get`/field/ctor of a `MapsTo` entity (21 eNoteV2 entities) -> uncertain `ef_convention`; otherwise `no_incoming_live_edges` -> `proved_dead`. Every `reason` is a frozen handler-enum; changing a code is a contract break. CLI `--output=summary` emits one line per candidate (`kind accessibility fqn document_path:start_line reason status`) plus `-- shown/total proved/uncertain/unresolved; more available (--cursor)`. CLI `--output=jsonl` emits `{"type":"meta", meta}` then `{"type":"candidate", candidate}` per entry. MCP tool `lurp_dead_candidates` mirrors CLI shape: `limit, cursor, snapshot_id, project, document, kind, include_public, include_generated, include_tests` plain properties; `Description` text matches this section verbatim (item #6 docs/tool mismatch not repeated).

---

## Snapshot Lifecycle

Snapshots are immutable: an existing snapshot is never mutated. Each indexing run *normally* creates a new snapshot when content changes; incremental indexing creates a new snapshot when content changes and does not modify the previous one. When source content and compilation inputs (and extractor version) are unchanged, the run reuses the existing snapshot via content-addressed dedup instead of writing a duplicate row.

```
Hashing documents and detecting changes... done (0 changed, 402 unchanged).
No changes detected. Skipping incremental index.
Incremental index complete. Snapshot: f3bff523b103462be239655c9b753be3
  Previous snapshot: f3bff523b103462be239655c9b753be3
```

(402 docs; eCommerce likewise 162 unchanged, snapshot reused). The last 3 snapshots are retained; older ones are pruned automatically.

## Environment Variables

| Variable | Purpose |
|---|---|
| `LURP_SOLUTION_PATH` | Equivalent to `--solution=<path>`. |
| `LURP_OUTPUT_DIR` | Equivalent to `--output-dir=<path>`. |

## MCP server (`--mode=serve`)

Lurp runs as an MCP server over stdio, exposing 18 tools via `tools/list`:
`lurp_context, lurp_get_source, lurp_outline, lurp_navigate, lurp_find_symbol,
lurp_search, lurp_grep, lurp_impact, lurp_diff, lurp_get_symbol, lurp_get_annotations,
lurp_retract_annotation, lurp_diagnostics, lurp_status, lurp_timings, lurp_refresh, lurp_index,
lurp_dead_candidates`. All are read-only except `lurp_index` and `lurp_retract_annotation`. There
is no `lurp_annotate` tool by design. The MCP session's SQLite connection opens with
`PRAGMA query_only=ON`, so read tools never mutate the pinned snapshot;
`lurp_index` writes through a separate writer connection while reads keep
answering from the old pin, and `lurp_retract_annotation` opens its own short-lived
writable connection the same way, scoped to a single `DELETE` (see
[`--mode=retract-annotation`](#--mode=retract-annotation)). Annotation *creation*
remains CLI-only (`--mode=annotate`); *retraction* is available from both
surfaces (`--mode=retract-annotation` and `lurp_retract_annotation`).

Stdio transport is JSON-RPC pure: every non-empty stdout line parses as JSON
and framework logs are routed to stderr.

`--mode=serve` requires an existing indexed snapshot at startup. It throws
`ERROR: No snapshots found in the database` if no snapshot exists. Index first
via CLI (`--mode=index`) or MCP `lurp_index`, then serve:

```bash
# 1. Index once (creates index.db with a snapshot)
lurp --mode=index --solution=path/to/Your.sln --output-dir=./out

# 2. Serve from that output dir (pins that snapshot)
lurp --mode=serve --solution=path/to/Your.sln --output-dir=./out
```

Example MCP client config (stdio, global tool):

```json
{
  "mcpServers": {
    "lurp": {
      "command": "lurp",
      "args": ["--mode=serve", "--solution=path/to/Your.sln", "--output-dir=./out"]
    }
  }
}
```

Pin semantics: every read tool serves the snapshot pinned at startup, not
`latest`. After any `lurp_index` completion, reads keep returning the old
`snapshot_id` until `lurp_refresh {}` (and if `changed:true`, `lurp_refresh
{"ack": new_id}`).

## License

MIT
