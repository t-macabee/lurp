# Lurp Architecture

**Status:** Design reference. The architecture described here is fully implemented
— see `TRUST_KERNEL.md` for verification evidence and known deviations.
**Current:** schema v27, extractor 1.6.0, tool 1.4.0
**Scope:** C#/.NET through Roslyn; local, compiler-grounded, read-only analysis

---

## 1. Purpose of This Document

This document describes Lurp's **design**: the product model, the data model,
the non-negotiable rules, and what the system explicitly does not do. It is a
*what-and-why* reference, not a build roadmap or implementation status tracker.

For implementation status, verification evidence, gap audits, and recorded
deviations, see `TRUST_KERNEL.md`.

### 1.1 Current state

Lurp today:

> **A persistent, incremental semantic workspace mirror that combines
> compiler-derived relationships with immediate, snapshot-consistent source
> retrieval for software agents.**

It loads a .NET solution through Roslyn, discovers types and dependencies,
tracks stale semantic data, and assembles bounded context capsules for agent
tasks. All capabilities share one document identity, one symbol identity, one
workspace snapshot, one freshness model, one persistent database, and one query
interface.

It remains an indexer and context provider (see §8 for what it does not become).

## 2. The Product Model

Four responsibilities:

| Responsibility | Question answered |
|---|---|
| **Catalog** | What code entities exist? |
| **Map** | How are those entities related? |
| **Fast travel** | What source belongs to this entity, right now? |
| **Context assembly** | What is the smallest sufficient neighborhood for this task? |

### 2.1 The central data model

```text
Snapshot
  ├─ immutable document contents
  ├─ semantic entities linked to exact source spans
  ├─ typed relationships between entities
  ├─ evidence and provenance for every relationship
  └─ structured changes from the previous snapshot
```

A semantic fact has this shape: `subject + relationship + object + source
location + provenance + snapshot identity + extractor version`. Example:

```text
UpdateCustomerHandler.Handle
    calls
ICustomerRepository.EmailExistsAsync

Evidence: compiler_proved
Location: UpdateCustomerHandler.cs, invocation span
Snapshot: workspace-0184
Extractor: calls-v1
```

Source retrieval uses the same identity: `symbol ID → document version →
exact source span`. There is no translation layer between a symbol ID and its
source.

## 3. Storage

### 3.1 One SQLite database

One primary SQLite database (`index.db` in the output directory) is the
canonical persisted state. JSON is appropriate for CLI response output and
one-way export — it is never a parallel authority. Snapshot identity is
content-derived and content-addressed: when source content and compilation
inputs are unchanged, a re-index reuses the existing snapshot rather than
writing a duplicate row.

The database is a single source of truth for the **indexed snapshot**, not a
replacement for the repository or compiler. Logical boundaries:

1. **Repository source files** — authoritative content.
2. **Roslyn compilation** — authoritative static semantics for a build config.
3. **`index.db`** — persistent materialized snapshot.
4. **Context capsules** — task-specific projections of stored facts + source.
5. **Annotations** — human/agent interpretation, kept separate from compiler facts.

### 3.2 Why SQLite

The indexer is a local single-binary CLI that needs many small indexed
lookups, atomic related-record updates, safe concurrent readers, no database
server, and explicit schema migrations. SQLite provides indexed lookup,
transactions, foreign keys, full-text search, and reliable atomic commits
without external infrastructure.

### 3.3 Source storage model

Store complete document contents once per content hash; never make separately
copied method bodies the canonical representation:

```text
document → immutable document version → complete source text
symbol declaration → span inside that document version
```

### 3.4 Logical storage organization

The `Lurp.Storage` library decomposes persistence behind `IIndexStore` into
focused stores:

| Store | Responsibility |
|---|---|
| `SnapshotLifecycleStore` | Snapshot creation, identity resolution, status |
| `SnapshotDocumentStore` | Document + document-version persistence |
| `SnapshotSymbolStore` | Symbol identity and metadata |
| `SnapshotPruner` | Old-snapshot cleanup (retains last 3) |
| `SnapshotTimingStore` | Per-step timing records |
| `DeclarationWriteStore` / `DeclarationReadStore` | Declaration persistence and retrieval |
| `DeclarationMaintenanceStore` | Incremental declaration updates |
| `EdgeOperationsStore` | Typed relationship persistence (merge-on-write) |
| `DiagnosticStore` | Snapshot-bound compiler diagnostics |
| `AnnotationStore` | User/agent annotations |
| `ExtractorRegistryStore` | Extractor identity and version |
| `SearchSourceStore` / `SearchSymbolStore` | Full-text search over source and symbols |
| `SemanticDiffStore` | Structured semantic changes between snapshots |
| `BindingIncompletenessStore` | Binding-failure records per document |

Schema migrations: 27 sequential migrations managed by `MigrationRunner`,
bound to `VersionConstants.DatabaseSchemaVersion`.

## 4. Indexing Pipeline

### 4.1 Full indexing

```
WorkspaceLoader.LoadAsync
  → WorkspaceInfo (solution identity, compilation inputs)
  → SnapshotIdentity.Create (deterministic, content-derived)
  → IndexRunner.RunFullIndexAsync
      → CompilationFactExtractor (per-document extraction)
          → SymbolExtractor / SymbolDeclarationExtractor
          → SymbolStructuralEdgeExtractor / MemberEdgeExtractor
          → CallsEdgeExtractor / ReadsWritesEdgeExtractor / OverridesEdgeExtractor
          → InterfaceDispatchExtractor / VirtualOverrideExtractor
          → ReflectionExtractor (TypeOf, NameOf, StringLiteral, UnknownPattern)
          → Framework adapters (AspNetCore, DI, MediatR, EF Core, Serialization, Test)
      → EdgeOperationsStore.SaveEdges (merge-on-write, provenance-ranked)
      → SemanticDiffStep (compare against previous snapshot)
      → SnapshotLifecycleStore.Commit
```

### 4.2 Incremental indexing

`IncrementalIndexer.RunIncrementalAsync` re-extracts only the scoped document set and creates a new snapshot when content changes; it does not mutate an existing snapshot. When the whole workspace is byte-identical to the previous snapshot (no document, compilation-input, or extractor-version change), no new snapshot is written — the existing one is reused via content-addressed dedup (observed live on both solutions, §B: `Hashing documents and detecting changes... done (0 changed, 402 unchanged).` / `No changes detected. Skipping incremental index.` — snapshot `f3bff523...` / `0a256115...` reused, edge/symbol/doc counts identical before/after):

1. Detect changed documents (hash-based `WorkspaceFreshness`).
2. Compute reverse-edge closure + new-file widening.
3. Re-extract only the scoped document set.
4. Delete copied-forward facts in lockstep with extraction (edges, annotations,
   binding incompleteness).
5. Refresh cross-document edges (`CrossDocumentEdgeRefresher`).
6. Commit new snapshot.

### 4.3 Framework adapters

Lurp models framework-mediated relationships through six adapters that emit
ordinary typed facts into the shared model:

| Adapter | Facts emitted |
|---|---|
| ASP.NET Core | `RoutesTo` (route → controller action) |
| Dependency Injection | `Registers` (service → implementation), `RuntimeUnknown` for non-conventional forms |
| MediatR | `Handles` (request/notification → handler) |
| EF Core | `MapsTo` (entity, `DbSet`, configuration) |
| Serialization | DTO/property contract participation |
| Test | `TestedBy` (production symbol → covering test) |

Each fact retains its evidence level: `compiler_proved`, `framework_derived`,
`global_implementation_relation`, `possible`, `convention`, `name_candidate`,
or `runtime_unknown`.

### 4.4 Reflection evidence ladder

Four extractors cover reflection-based relationships:

| Extractor | Edge kind | Coverage |
|---|---|---|
| `TypeOfReflectionExtractor` | `ReflectionTypeRef` | `typeof(T)` |
| `NameOfReflectionExtractor` | `ReflectionMemberRef` | `nameof(M)` |
| `StringLiteralReflectionExtractor` | `ReflectionNameCandidate` | string literals matching known names |
| `UnknownPatternReflectionExtractor` | `ReflectionTargetUnknown` | `Type.GetType()`, `Activator.CreateInstance()` |

## 5. Read Path (Handlers)

Handlers consume persisted facts through `IIndexStore` and related store
interfaces. They do not re-run Roslyn analysis (except `--mode=status
--solution=`, which performs a storage-backed freshness check).

| Handler | Responsibility |
|---|---|
| `IndexHandler` | Orchestrates `IndexRunner` / `IncrementalIndexer` |
| `SearchHandler` | Full-text search over source + symbols (`SearchSourceStore`, `SearchSymbolStore`) |
| `FindSymbolHandler` | FQN resolution to symbol ID |
| `GetSymbolHandler` | Symbol metadata / signature / body / declaration / containing-type / surrounding |
| `GetSourceHandler` | Full source text for a document |
| `NavigateHandler` | Declaration lookup by file + line |
| `DiffHandler` | Semantic diff between two snapshots |
| `ImpactHandler` | Impact path traversal (`ImpactTraverser`) with semantic causes |
| `ContextHandler` | Context capsule assembly (`ContextAssembler`, tier builders, `CapsuleBudgetEnforcer`) |
| `StatusHandler` | Snapshot freshness, schema version, completeness |
| `TimingsHandler` | Per-step performance data |
| `AnnotationHandler` | Write annotations (`annotate`) and read them (`get-annotations`) |

MCP surface (`--mode=serve`, `src/Mcp/McpServeHandler.cs`): 13 tools via
`tools/list` (`lurp_find_symbol, lurp_diff, lurp_get_symbol, lurp_index,
lurp_get_annotations, lurp_status, lurp_get_source, lurp_context,
lurp_refresh, lurp_navigate, lurp_search, lurp_timings, lurp_impact` —
13th `lurp_timings` added 2026-08-17, verified in
`.tmp_test/MCP_LIVE_TEST_REPORT_MCP_SURFACE_2026-08-17_P2.md` §Tools present;
no `lurp_annotate` by design). MCP session is read-only
(`PRAGMA query_only=ON`, `src/Storage/SqliteIndexStore.cs:74` /
`src/Mcp/McpSessionContext.cs:Create` → `EnableQueryOnly()`) and stdio-pure
(`ConsoleLoggerOptions.LogToStandardErrorThreshold = LogLevel.Trace` +
`IOutputSink` plumbing, `tests/Mcp/McpStdioPurityTests.cs`; report §J: 0 leaks
over 158/129 stdout lines). `--mode=serve` requires an existing snapshot at
startup (`src/Mcp/McpSessionContext.cs:47` throws `ERROR: No snapshots found
in the database` if `GetLatestSnapshotId()` is null) and pins that snapshot
until `lurp_refresh` advances it.

## 6. Context Capsules

A context capsule is a bounded, evidence-backed package of relevant code
assembled for an agent task. Built by `ContextAssembler` with tier builders
and enforced by `CapsuleBudgetEnforcer`.

### 6.1 Tier builders (priority order)

| Tier builder | Produces | Tier name |
|---|---|---|
| `ContractsTierBuilder` | Relevant contracts (interfaces, base types) | `contracts` |
| `DirectCalleesTierBuilder` | Direct outgoing calls | `direct_callees` |
| `DirectCallersTierBuilder` | Direct incoming calls | `direct_callers` |
| `RegisteredImplementationsTierBuilder` | Registered or possible runtime targets | `registered_implementations` |
| `RelevantTestsTierBuilder` | Tests covering the anchor | `relevant_tests` |
| `SurroundingSiblingsTierBuilder` | Surrounding type members | `surrounding_source` |
| `SecondDegreeContextTierBuilder` | Second-degree relationships | `second_degree_context` |

### 6.2 Capsule contract

- Anchor symbol / source location
- Relevant contracts and implementations
- Relevant tests
- Uncertainties and unchecked vectors (via `UncertaintyDetector`)
- Incoming and outgoing paths (with exact source spans)
- Suggested verification commands
- Likely change sites
- Affected public surfaces
- Per-item inclusion reasons

Budgeting: `--content-budget` bounds content tokens; tiers are included in
priority order (greedy-prefix) until the budget is exhausted; omitted tiers
are reported with reason (`budget_exhausted`, `empty`, `unresolved`,
`summarized`). Individual tiers can be refetched with `--tier=<name>`.

## 7. Non-Negotiable Design Rules

### 7.1 Product boundary

A capability belongs in the core only if it materially improves: code
localization; exact source retrieval; semantic context selection; consequence
tracing; freshness; verification selection; representation of uncertainty.

### 7.2 Read-only relationship to source

May: read source; index source; report consequences. Must not: apply fixes;
compile hypothetical or in-memory modified solutions; rewrite the working tree;
decide architectural intent; call an LLM to make changes; become the worker
agent.

### 7.3 C#/.NET specialization

Remain Roslyn-native; do not weaken the semantic model for generic syntax
trees or multiple languages.

### 7.4 Fact before interpretation

The core records `implements MediatR.IRequest<CustomerDto>`; it does not claim
"query" as compiler-proved business intent. Project conventions may derive
such classifications with distinct provenance.

### 7.5 Unknown is a valid result

Do not eliminate `not_determinable` by guessing; make each blind spot the
strongest honest form available: compiler-proved relationship;
framework-derived relationship; possible target set; string/name candidate;
explicitly unknown runtime target.

### 7.6 Schema stability

Every field is a contract with future consumers. Never silently change a
field's meaning. Add compatible optional fields only in minor schema
revisions. Rename, removal, retyping, or semantic change requires a major
revision. Treat unknown enum values as survivable input.

### 7.7 Roslyn boundary

Roslyn analysis belongs in `src/Workspace/` and `src/Adapters/` only. Never
put Roslyn semantic analysis into `src/Storage/` — the boundary is enforced
at the project reference. Do not write raw SQL from analysis or handlers;
persist through store interfaces.

## 8. What Not to Build

Explicitly outside the core:

- **UI, dashboard, or visualization** — a separate consumer may render
  database queries; the indexer optimizes for correct facts and stable
  machine-readable responses.
- **Multi-language genericization** — Roslyn compiler semantics are the
  project's advantage; do not replace them with a lowest-common-denominator
  syntax model.
- **Autonomous editing** — the indexer is the map and retrieval system, not
  the hand changing the code.
- **LLM-authored canonical summaries** — interpretive summaries go stale and
  cannot be reproduced deterministically; store them as annotations with
  their own provenance if useful, never as compiler facts.
- **General-purpose architecture scoring** — complexity, redundancy, and
  dead-code audits are not part of the product; the indexer optimizes for
  correct facts and stable machine-readable retrieval.
- **Change simulation / preflight** — withdrawn from the product. Pre-computing
  the consequences of hypothetical source edits is outside core scope.
- **Premature daemon/server architecture** — the core remains a local CLI
  and persistent SQLite store; the MCP stdio server (`--mode=serve`,
  `src/Mcp/McpServeHandler.cs`) is a thin, pinned-snapshot, read-only
  transport over that store, not a background daemon or autonomous editor.

## 9. Validation Strategy

Judge by agent outcomes, not database size or indexed edge counts. The
intended benchmark set covers: vague cleanup request; local validation change;
handler/DTO modification; cross-project interface change; apparently unused
type deletion; entity change affecting EF or serialization; DI implementation
replacement; route contract change.

The outcome-benchmark suite (`tests/fixtures/OutcomeBenchmark/`) was built and
its baseline established at time of record, then deleted with the test-suite
cleanup (`f1254fc`); it is not currently reproducible. See `TRUST_KERNEL.md`
§T11.

Measure: correct starting symbol found? actual source retrieved without
project exploration? all constraining contracts included? affected callers and
implementations represented? relevant tests included? unknown runtime vectors
declared? how much irrelevant source entered the capsule? how many tokens the
worker required after receiving it? did the resulting patch avoid unrelated
changes? did incremental and clean indexing agree?

## 10. Definition of the Definitive Version

The tool has reached its definitive conceptual form when:

- one SQLite database holds the current indexed workspace state;
- source documents and semantic facts share snapshot identity;
- symbols link directly to exact, retrievable code spans;
- ordinary reads do not require Roslyn reload;
- changed documents update the index incrementally;
- member-level typed edges power relationship queries;
- polymorphism and framework indirection retain honest evidence levels;
- generated semantics participate without flooding context;
- semantic diffs explain what changed;
- impact results are paths with reasons, not opaque scores;
- context capsules return bounded relevant code plus its surroundings;
- every fact states its provenance and extractor version;
- the indexer never becomes the actor modifying source.
