# lurp

Roslyn-based semantic indexer for .NET solutions. Indexes a solution into a SQLite database (`index.db`) and provides query, diff, impact-analysis, simulation, and audit commands over the indexed facts.

## Quick Start

```
lurp --mode=index --solution=MySolution.sln --output-dir=./out
```

This creates `./out/index.db` containing all indexed symbols, edges, and source facts.

## Modes

### `--mode=index`

Index a solution and store facts in the database.

```
--mode=index --solution=<path> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--solution=<path>` | Yes | Path to the `.sln` or `.slnx` file. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--strategy=<full\|incremental>` | No | `full`: index every document from scratch. `incremental`: only re-index changed documents. Default: `full` on first run, `incremental` on subsequent runs. |
| `--output-json=<path>` | No | Also write the snapshot manifest as JSON. |
| `--skip-adapter=<name>` | No | Skip a named framework adapter. Valid: `ASP.NET Core`, `Dependency Injection`, `MediatR`, `EF Core`, `Serialization`, `Test`. |

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
| `--symbol=<id>` | Yes | The symbol ID to look up. |
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

---

### `--mode=find-symbol`

Resolve a symbol by fully qualified name.

```
--mode=find-symbol --fqn=<name> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--fqn=<name>` | Yes | Fully qualified name to resolve. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to search (default: latest). |
| `--include-generated` | No | Include source-generated symbols. |

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
| `--symbol=<id>` | Yes | The symbol ID to trace from. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--direction=<downstream\|upstream>` | No | Traversal direction (default: `downstream`). Use `upstream` to find all references to a symbol. |
| `--max-depth=<n>` | No | Maximum traversal depth (default: 10). |
| `--kinds=<list>` | No | Comma-separated edge kinds to follow. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

---

### `--mode=context`

Assemble a context capsule for a symbol or source location.

```
--mode=context (--symbol=<id> | --file=<path> --line=<n>) --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes* | Symbol ID to anchor on. |
| `--file=<path>` | Yes* | Source file path (use with `--line`). |
| `--line=<n>` | Yes* | Line number in the source file. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--intent=<inspect\|modify\|diagnose>` | No | Intent hint for assembly (default: `inspect`). |
| `--budget=<n>` | No | Token budget for the emitted capsule (default: 8000). The estimate counts content: anchor and item source plus the serialized weight of the substantive non-source sections (paths, topology, completeness, uncertainties, verification, likely change sites, affected public surfaces, inclusion reasons). Per-item identity/provenance framing is navigation metadata and is not counted. Over-budget capsules first bound paths and item source (recorded as `summarized`), then clear the lowest-priority sections greedily (`budget_exhausted`); every truncated category is declared in `omittedTiers`. The anchor is never dropped. |
| `--max-hops=<n>` | No | Maximum graph hops to expand (default: 3). |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |
| `--include-generated` | No | Include source-generated symbols. |
| `--completeness-detail` | No | Emit per-document `binding_incompleteness` rows. Without it, completeness carries a deterministic reason/project rollup (`binding_incompleteness_summary`) plus the total. |

\* Either `--symbol` or both `--file` and `--line` must be provided.

The capsule is written to `<output-dir>/capsule-<sanitized-id>.json` and also printed to stdout.

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

### `--mode=simulate-rename`

Simulate renaming a symbol and show affected references.

```
--mode=simulate-rename --symbol=<id> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol ID to simulate. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--new-name=<name>` | No | New simple name for the symbol. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

---

### `--mode=simulate-move`

Simulate moving a symbol to a new namespace.

```
--mode=simulate-move --symbol=<id> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol ID to simulate. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--new-namespace=<ns>` | No | Target namespace. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

---

### `--mode=simulate-remove`

Simulate removing a symbol and show cascading impact.

```
--mode=simulate-remove --symbol=<id> --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol ID to simulate. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

---

### `--mode=audit`

Run static analysis checks on the index.

```
--mode=audit --output-dir=<path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--checks=<list>` | No | Comma-separated checks: `dead-symbol`, `untested-surface`, `unregistered-impl`, `high-fan-out` (default: all). |
| `--fan-out-threshold=<n>` | No | Call-count threshold for `high-fan-out` (default: 20). |
| `--snapshot=<id>` | No | Snapshot to audit (default: latest). |

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
| `--symbol=<id>` | Yes | The symbol ID to query. |
| `--output-dir=<path>` | Yes | Directory where `index.db` is stored. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest). |

---

## Snapshot Lifecycle

Each indexing run (full or incremental) creates a **new** snapshot. The last 3 snapshots are retained; older ones are pruned automatically. Snapshots are never mutated — incremental indexing creates a new snapshot, it does not modify the previous one.

## Environment Variables

| Variable | Purpose |
|---|---|
| `INDEXER_SOLUTION_PATH` | Equivalent to `--solution=<path>`. |
| `INDEXER_OUTPUT_DIR` | Equivalent to `--output-dir=<path>`. |

## Migration from Legacy Modes

The following modes from earlier versions have been replaced:

| Old mode | Replacement |
|---|---|
| `--mode=discover` | `--mode=search --type=symbol --kind=<SymbolKind>` |
| `--mode=structure` | `--mode=context --intent=inspect` |
| `--mode=fingerprint` | No direct replacement; use `--mode=get-symbol --view=metadata`. |
| `--mode=who-references` | `--mode=impact --direction=upstream` |
| `--mode=recompute-all` | Removed. Re-index with `--strategy=full`. |
| `--mode=mark-dirty` | Removed. Incremental indexing detects changes automatically. |
| `--mode=sweep` | Removed. No longer needed. |
| `--mode=lint` | Removed. Use `--mode=audit` for index validation. |

The `.codeaudit/` output directory and `dirty-files.json` manifest are no longer produced. All data is stored in `index.db`.

## License

MIT
