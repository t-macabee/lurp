# Lurp Architecture

**Status:** Design reference. The architecture described here is fully implemented.
See `notes/TRUST_KERNEL.md` for verification evidence and known deviations.
**Current:** schema v30, extractor 1.6.0, CLI/MCP contract v2, output schema v5, tool 2.0.0
**Scope:** C#/.NET through Roslyn; local, compiler-grounded, read-only analysis

---

## 1. Purpose

This document describes Lurp's **design**: the product model, the data model,
the non-negotiable rules, and what the system explicitly does not do. It is a
*what-and-why* reference, not a build roadmap.

> **A persistent, incremental semantic workspace mirror that combines
> compiler-derived relationships with immediate, snapshot-consistent source
> retrieval for software agents.**

It loads a .NET solution through Roslyn, discovers types and dependencies,
tracks stale semantic data, and assembles bounded context capsules for agent
tasks. All capabilities share one document identity, one symbol identity, one
workspace snapshot, one freshness model, one persistent database, and one query
interface.

## 2. The Product Model

Four responsibilities:

| Responsibility | Question answered |
|---|---|
| **Catalog** | What code entities exist? |
| **Map** | How are those entities related? |
| **Fast travel** | What source belongs to this entity, right now? |
| **Context assembly** | What is the smallest sufficient neighborhood for this task? |

A semantic fact has this shape: `subject + relationship + object + source
location + provenance + snapshot identity + extractor version`. Source retrieval
uses the same identity: `symbol ID → document version → exact source span`.
There is no translation layer between a symbol ID and its source.

## 3. Storage

One primary SQLite database (`index.db` in the output directory) is the
canonical persisted state. JSON is appropriate for CLI response output and
one-way export, but it is never a parallel authority. Snapshot identity is
content-derived and content-addressed: when source content and compilation
inputs are unchanged, a re-index reuses the existing snapshot.

Logical boundaries:

1. **Repository source files**: authoritative content.
2. **Roslyn compilation**: authoritative static semantics for a build config.
3. **`index.db`**: persistent materialized snapshot.
4. **Context capsules**: task-specific projections of stored facts + source.
5. **Annotations**: human/agent interpretation, kept separate from compiler facts.

Store complete document contents once per content hash; never make separately
copied method bodies the canonical representation. Schema migrations: 30
sequential migrations managed by `MigrationRunner`.

## 4. Indexing Pipeline

```text
WorkspaceLoader.LoadAsync
  → WorkspaceInfo (solution identity, compilation inputs)
  → SnapshotIdentity.Create (deterministic, content-derived)
  → IndexRunner.RunFullIndexAsync
      → CompilationFactExtractor (per-document extraction)
          → symbol/declaration/edge/extractors + framework adapters
      → EdgeOperationsStore.SaveEdges (merge-on-write, provenance-ranked)
      → SemanticDiffStep (compare against previous snapshot)
      → SnapshotLifecycleStore.Commit
```

Incremental indexing (`IncrementalIndexer.RunIncrementalAsync`) re-extracts only
changed documents and creates a new snapshot when content changes; it never
mutates an existing snapshot. When nothing changed, the existing snapshot is
reused via content-addressed dedup.

### 4.1 Framework adapters

| Adapter | Facts emitted |
|---|---|
| ASP.NET Core | `RoutesTo` (route → controller action) |
| Dependency Injection | `Registers` (service → implementation), `RuntimeUnknown` for non-conventional forms |
| MediatR | `Handles` (request/notification → handler) |
| EF Core | `MapsTo` (entity, `DbSet`, configuration) |
| Serialization | DTO/property contract participation |
| Test | `TestedBy` (production symbol → covering test) |

### 4.2 Evidence ladder

| Level | Meaning |
|---|---|
| `compiler_proved` | Directly verified by the compiler |
| `framework_derived` | Derived from framework conventions |
| `global_implementation_relation` | Implementation relationship across the graph |
| `possible` | Possible target (e.g. `MayDispatchTo` inherited-only) |
| `convention` | Convention-based inference |
| `name_candidate` | Name argument of a runtime name-binding API (reflection lookup, `PropertyChanged`/`[CallerMemberName]`, EF Core string APIs, binding attributes, MVC action names), resolved semantically |
| `runtime_unknown` | Runtime target not observable statically |

## 5. Read Path (Handlers)

Handlers consume persisted facts through `IIndexStore` and related store
interfaces. They do not re-run Roslyn analysis (except `--mode=status
--solution=`, which performs a storage-backed freshness check).

Seventeen handlers cover: index, search, grep, find-symbol, get-symbol, get-source,
navigate, outline, diagnostics, diff, impact, context, status, timings,
pin-snapshot, annotations (attach/retrieve/retract), and dead-candidates. MCP surface (`--mode=serve`): 18 tools over stdio
(`lurp_context`, `lurp_get_source`, `lurp_outline`, `lurp_navigate`,
`lurp_find_symbol`, `lurp_search`, `lurp_grep`, `lurp_impact`, `lurp_diff`,
`lurp_get_symbol`, `lurp_get_annotations`, `lurp_retract_annotation`, `lurp_diagnostics`, `lurp_status`,
`lurp_timings`, `lurp_refresh`, `lurp_index`, `lurp_dead_candidates`) — 16 read-only plus `lurp_index` and `lurp_retract_annotation` (write),
which starts a background (re-)index through a separate writer connection (see
[CLI_REFERENCE.md](CLI_REFERENCE.md#mcp-server-mode-serve)).

Read commands never migrate `index.db`. They refuse a database whose schema
version differs from `VersionConstants.DatabaseSchemaVersion` and name the fix
(`--mode=index`, or a newer Lurp). Only `index`, `pin-snapshot` and `lurp_index`
migrate.

### 5.1 Impact reachability

`ImpactReachability` answers "what can this change reach?". It runs a
level-by-level BFS from the anchor with one shared visited set, so memory grows
with symbols and edges, not with paths. For each reached symbol it records the
depth, one witness path, a shortest-path count (edge rows, saturating at
`long.MaxValue`) and a frontier flag. Edges are sorted by neighbor ID, kind,
provenance and source location before expansion, and each level is expanded in
symbol-ID order, so the witness paths and the output are the same on every run.
`--mode=impact`, `lurp_impact`, the capsule paths and the `direct_callers`,
`second_degree_context` and `relevant_tests` tiers all use it.

## 6. Context Capsules

A context capsule is a bounded, evidence-backed package of relevant code
assembled for an agent task. Built by `ContextAssembler` with tier builders
(priority order: contracts → direct callees → direct callers → registered
implementations → relevant tests → surrounding source → second-degree context)
and enforced by `CapsuleBudgetEnforcer`.

Budgeting: `--content-budget` bounds content tokens; tiers are included in
priority order (greedy-prefix) until the budget is exhausted; omitted tiers
are reported with reason (`budget_exhausted`, `empty`, `unresolved`,
`summarized`). Individual tiers can be refetched with `--tier=<name>`.

Every capsule records its origin at the top level (`snapshot_id`,
`tool_version`, `output_schema_version`) and is written atomically, so a
capsule found on disk can be checked against `status` and never shows a
partial write. `UncertaintyDetector` fills `suggested_verification` before the
budget pass, so it is measured and droppable; `uncertainties` is appended
after it and is never trimmed; the artifact estimate is stamped last over the
emitted file.

### 6.1 Declared boundaries

Construct classes Lurp does not model are registered in
`src/Workspace/DeclaredBoundaries.cs`. New unmodeled construct → add a registry
entry, not a new extractor.

| Id | Construct Class | Description |
|---|---|---|
| `di_hosted_service` | `AddHostedService<T>` | Concrete type resolved, activation semantics not captured |
| `di_options` | `Configure<T>` / `AddOptions<T>` | Options type resolved, binding semantics not captured |
| `di_external_extension` | External IServiceCollection extensions | Source outside compilation, semantics unknown |
| `masstransit_consumer` | MassTransit consumer registration | No adapter; wiring edges never emitted |
| `ef_convention` | EF Core model conventions beyond query filters/indexes | Fluent API model building not modeled |
| `shape_similarity` | Semantic sibling similarity | No compiler oracle; deliberately not modeled |

## 7. Glossary

| Term | Meaning |
|---|---|
| **snapshot** | One immutable view of a solution; content-addressed |
| **capsule** | Token-bounded neighborhood assembled for an agent task |
| **tier** | A category within a capsule; may be `budget_exhausted` |
| **evidence / provenance** | The 7-level ladder (`compiler_proved` → `runtime_unknown`) |
| **adapter** | Framework-mediated edge emitter (ASP.NET, DI, MediatR, …) |
| **freshness** | Snapshot state (`fresh`/`stale`/`unknown`); `status --json` also reports a `completeness` block with `binding_incompleteness` / `binding_incompleteness_summary` / `binding_incompleteness_total` |

## 8. Non-Negotiable Design Rules

1. **Read-only relationship to source**: may read, index, report consequences; must not apply fixes. Lurp loads projects through MSBuild design-time builds. These create `bin/` and `obj/` output folders and regenerate `obj/` intermediates, as an IDE does. Lurp itself writes only to `--output-dir`.
2. **C#/.NET specialization**: remain Roslyn-native; do not weaken the semantic model for multiple languages.
3. **Fact before interpretation**: record `implements MediatR.IRequest<T>`, not "query" as compiler-proved business intent.
4. **Unknown is a valid result**: make each blind spot the strongest honest form available.
5. **Schema stability**: every field is a contract; renaming/removing requires a major revision.
6. **Roslyn boundary**: Roslyn analysis belongs in `src/Workspace/` and `src/Adapters/` only; never in `src/Storage/`.

## 9. What Not to Build

Not a UI, not multi-language, not autonomous editing, not LLM summaries, not
architecture scoring, not change simulation. The indexer is the map and
retrieval system, not the hand changing the code.
