# Lurp CLI reference

This is the operational reference for Lurp's command-line interface. For the
product overview, see the [root README](../README.md). For implementation
status and design context, see the [docs](../docs/).

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
`--mode=context`, etc.) — you cannot index a subset up front.

## Read-command options

Accepted by every read command: `--mode=search`, `--mode=find-symbol`, `--mode=impact`, and `--mode=context`.

| Argument | Required | Description |
|---|---|---|
| `--output=<summary\|json\|jsonl>` | No | Payload rendering (default: `json`). `summary` is a digest; `jsonl` emits a `{"type":"meta"}` envelope followed by one compact object per result, so a consumer can stream and stop early. `jsonl` is rejected for a whole capsule, whose payload is a single document. |
| `--quiet` | No | Emit only the payload: suppresses the freshness stderr line, and for `--mode=context` prints just the written capsule path instead of the capsule itself. |
| `--freshness=<auto\|hash\|off>` | No | How hard to check that the snapshot still matches the working tree (default: `auto`: stat only). `hash` re-hashes files whose stat differs; `off` skips the check. |
| `--require-fresh` | No | Exit `2` when the snapshot is not fresh. |

Every read response carries a `freshness` block reporting `state` (`fresh`, `stale`, `unknown`) and the `method` used to determine it, so a stale read is never presented as a current one.

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
--mode=get-source --document=<relative-path> --output-dir=<path> [--snapshot=<id>]
```

| Argument | Required | Description |
|---|---|---|
| `--document=<relative-path>` | Yes | Relative path of the document within the solution. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to read from (default: latest). |

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

Also accepts the shared [read-command options](#read-command-options).

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

Returns the symbol ID, fully-qualified name, kind, and exact source span of the declaration at that location.

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
| `--content-budget=<n>` | No | Token budget for capsule **content** (default: 8000, or 16000 when `--symbol=` is a type anchor and `--content-budget=` is omitted: a type's callee/caller tiers scale with member fan-out, so the default is kind-aware. An explicit `--content-budget=` is always honored as-is). Even at 16000, a large type anchor can still exhaust the budget before its lowest-priority tiers are reached — typically `relevantTests` and `secondDegreeContext`. That is not a failure to budget away: refetch those tiers on their own with `--tier=`, e.g. `lurp --mode=context --symbol=<symbol-id> --tier=relevantTests` (see `--tier=` below). Reported as `estimatedTokens`: anchor and item source plus the serialized weight of the substantive non-source sections (paths, topology, completeness, uncertainties, verification, likely change sites, affected public surfaces, inclusion reasons). Per-item identity/provenance framing is navigation metadata and is not counted, so the emitted file is larger than `estimatedTokens`: size a context window from `estimatedArtifactTokens` (see [Capsule token estimates](#capsule-token-estimates)). Over-budget capsules first bound paths and item source (recorded as `summarized`), then clear the lowest-priority sections greedily (`budget_exhausted`); every truncated category is declared in `omittedTiers`. The anchor is never dropped. |
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
| `--json` | No | Emit structured JSON instead of plain text. |
| `--detail=<list>` | No | Comma-separated sections to expand in `--json` output. `documents` restores the per-document version map; `completeness` restores per-document binding-incompleteness rows. Both are summarized by default; `all` expands every section. |

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

Retrieve annotations for a symbol.

```
--mode=get-annotations --symbol=<id> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | No | The symbol ID to query. Omit to return all annotations. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

---

## Snapshot Lifecycle

Each indexing run (full or incremental) creates a **new** snapshot. The last 3 snapshots are retained; older ones are pruned automatically. Snapshots are never mutated: incremental indexing creates a new snapshot, it does not modify the previous one.

## Environment Variables

| Variable | Purpose |
|---|---|
| `LURP_SOLUTION_PATH` | Equivalent to `--solution=<path>`. |
| `LURP_OUTPUT_DIR` | Equivalent to `--output-dir=<path>`. |

## License

MIT
