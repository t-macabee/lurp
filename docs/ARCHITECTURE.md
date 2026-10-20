# Lurp Architecture

**Status:** Design reference. The architecture described here is fully implemented.
See `notes/TRUST_KERNEL.md` for verification evidence and known deviations.
**Current:** <!-- generated:versions -->tool 3.5.2, schema v37, extractor 1.11.0, CLI/MCP contract v6, output schema v6<!-- /generated:versions -->
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
inputs are unchanged, a re-index reuses the existing snapshot. Source content,
compilation inputs and the version of each extractor and adapter make up the
identity, so a version bump also gives a new snapshot id.

Logical boundaries:

1. **Repository source files**: authoritative content.
2. **Roslyn compilation**: authoritative static semantics for a build config.
3. **`index.db`**: persistent materialized snapshot.
4. **Context capsules**: task-specific projections of stored facts + source.
5. **Annotations**: human/agent interpretation, kept separate from compiler facts.

Store complete document contents once per content hash; never make separately
copied method bodies the canonical representation. Schema migrations are
sequential and managed by `MigrationRunner`.

Per-snapshot index cost is recorded in `snapshot_timings` (step → elapsed ms)
and `snapshot_metrics` (metric name → integer value). `peak_working_set_mb`
covers the Lurp process only; MSBuild BuildHost child processes are not counted.

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

Declarations are per document: a `partial` type declared in two documents
yields two declaration records with one symbol id, and the snapshot's symbol
set holds that symbol once. `declarations_extracted_this_run` therefore counts
declaration records while `symbols_in_snapshot` counts symbols.

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
| `global_implementation_relation` | Capsule-only grade: an implementation of a called interface or virtual member, projected to a call site. The relation is real; that the call reaches it is not established. Not stored on edges. |
| `possible` | Possible target (e.g. `MayDispatchTo` inherited-only) |
| `convention` | Convention-based inference |
| `name_candidate` | Name argument of a runtime name-binding API (reflection lookup, `PropertyChanged`/`[CallerMemberName]`, EF Core string APIs, binding attributes, MVC action names), resolved semantically |
| `runtime_unknown` | Runtime target not observable statically |

## 5. Read Path (Handlers)

Handlers consume persisted facts through `SqliteIndexStore` and related store
interfaces. They do not re-run Roslyn analysis, except `--mode=status
--solution=` (CLI) and MCP `lurp_status` with `full:true`, which load the
workspace through MSBuild to compare it against the stored snapshot.

Seventeen handlers cover: index, search, grep, find-symbol, get-symbol, get-source,
navigate, outline, diagnostics, diff, impact, context, status, timings,
pin-snapshot, annotations (attach/retrieve/retract), and dead-candidates. MCP surface (`--mode=serve`): 16 read-only tools over stdio
(`lurp_context`, `lurp_get_source`, `lurp_outline`, `lurp_navigate`,
`lurp_find_symbol`, `lurp_search`, `lurp_grep`, `lurp_impact`, `lurp_diff`,
`lurp_get_symbol`, `lurp_get_annotations`, `lurp_diagnostics`, `lurp_status`,
`lurp_timings`, `lurp_refresh`, `lurp_dead_candidates`); `--enable-write-tools` additionally registers `lurp_index` and `lurp_retract_annotation` (write),
which start a background (re-)index or a retraction through a separate writer connection (see
[CLI_REFERENCE.md](CLI_REFERENCE.md#mcp-server---modeserve)).

Read commands never migrate `index.db`. They refuse a database whose schema
version differs from `VersionConstants.DatabaseSchemaVersion` and name the fix
(`--mode=index`, or a newer Lurp). The exception is `status`, which reports
both versions and exits 0. Only `index`, `pin-snapshot` and `lurp_index`
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
and enforced by `CapsuleBudgetEnforcer`. Default priority order: contracts,
direct callees, direct callers, registered implementations, relevant tests,
second-degree context, surrounding source. The intent (`modify`, `diagnose`)
reorders it, and a type anchor puts surrounding source before relevant tests.

Budgeting: `--content-budget` bounds content tokens; tiers are included in
priority order (greedy-prefix) until the budget is exhausted; omitted tiers
are reported with reason (`budget_exhausted`, `empty`, `unresolved`,
`summarized`, `unmodeled_construct`). `unmodeled_construct` is used by the
registered-implementations tier when an unmodeled registration form exists.
Individual tiers can be refetched with `--tier=<name>`.

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

<!-- generated:declared-boundaries -->
| Id | Description |
|---|---|
| `di_hosted_service` | Hosted-service registration form AddHostedService<T> is not fully modeled: the concrete type is resolved but the runtime activation semantics of the hosted-service lifecycle are not captured. |
| `di_options` | Options-pattern registration Configure<T>/AddOptions<T> is not fully modeled: the options type is resolved but the configuration-binding semantics are not captured. |
| `di_external_extension` | An external IServiceCollection extension method was detected but could not be analyzed: the method lives outside the compilation so its registration semantics are unknown. |
| `masstransit_consumer` | MassTransit consumer registration is not modeled: no adapter exists to emit consumer-wiring edges for AddConsumer or endpoint configuration. |
| `ef_convention` | EF Core model conventions beyond query filters and indexes (e.g. IsRequired, HasMaxLength, HasDefaultSchema) are not modeled. |
| `shape_similarity` | Semantic sibling similarity is not modeled: consistency audits requiring comparison against 'similar' implementations are unsupported. Use the proved neighborhood instead — implementations of a shared interface, shared base types and overrides, callers/callees, and containing-declaration siblings. |
| `mediatr_stream_handler` | MediatR stream handler pattern (IStreamRequestHandler or IAsyncStreamHandler) is not modeled: the implementing type was detected but no Handles edge was emitted. |
| `mediatr_pipeline_behavior` | MediatR pipeline behavior (IPipelineBehavior) is not modeled: the implementing type was detected but no edge was emitted. Pipeline behaviors affect all requests passing through the pipeline. |
| `mediatr_exception_handler` | MediatR exception handler (IRequestExceptionHandler) is not modeled: the implementing type was detected but no edge was emitted. |
| `mediatr_pre_post_processor` | MediatR pre/post processor (IRequestPreProcessor or IRequestPostProcessor) is not modeled: the implementing type was detected but no edge was emitted. |
| `source_generators` | Source-generated code is not executed or indexed (generated trees are excluded and obj/ output is path-filtered), so symbols, calls, and registrations declared only by a source generator are absent from the graph. |
| `razor_blazor` | Razor (.cshtml) and Blazor components are not parsed: only C# documents in the solution's compilations are indexed, so component event handlers and render-tree calls are absent from the graph. |
| `minimal_api_endpoints` | Minimal API endpoints and non-controller ASP.NET Core surfaces (MapGet/MapPost, endpoint filters, SignalR hubs, gRPC services) are not modeled: the ASP.NET Core adapter recognizes only Controller-derived types, so endpoint-to-handler routes are absent from the graph. |
| `multi_target_union` | Multi-target projects are indexed as one union snapshot over every declared target framework: the declared TFM list is recorded per project and the per-symbol TFM set is recorded per snapshot, but reads do not surface the per-symbol set, so a construct present in only one target framework cannot be told apart by a consumer from one present in all of them. |
| `non_csharp_projects` | F# and VB projects are out of scope: MSBuildWorkspace skips non-C# languages and the skip is reported as a warning, so symbols and relationships declared in those projects are absent from the graph. |
| `intermediate_redirect_not_applied` | A project's build files set the intermediate output path after Lurp's redirect (or assign CustomBeforeMicrosoftCommonTargets in the project file), so its design-time build files are written under its own obj/ directory in the target tree. The project's own build hooks do take effect; the indexed compilation is not affected. |
| `static_abstract_dispatch` | Static-abstract interface dispatch is not modeled as a direct edge to the implementation: a generic method constrained to an interface with a static abstract member is extracted as a Calls edge to the interface member, but the implementing type selected at dispatch time is not emitted as a relation. |
| `collection_expression_implicit_calls` | A collection expression whose target type has Add lowers to the constructor plus one Add call per element, and a spread element is enumerated; Roslyn exposes the construct method only, so Add calls and spread GetEnumerator produce no edge. |
<!-- /generated:declared-boundaries -->

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

1. **Read-only relationship to source**: may read, index, report consequences; must not apply fixes. Lurp loads projects through MSBuild design-time builds, but redirects their intermediates into `%LOCALAPPDATA%\lurp\<hash>\obj\<project>\<tfm>\` and their `OutputPath` into `...\obj\<project>\<tfm>\bin\` (through the `CustomAfterDirectoryBuildProps` global property on `MSBuildWorkspace`; the solution's own `CustomBeforeMicrosoftCommonTargets` hook still runs), so an index run does not create or change `bin/` or `obj/` files in the target tree; restore assets are still read from the real `obj/`. A project that moves its own intermediate path or assigns that hook in its project file is reported with a warning (declared boundary `intermediate_redirect_not_applied`) and keeps its files in its own `obj/`. A solution that sets `CustomAfterDirectoryBuildProps` itself loses that value while Lurp loads it; Lurp does not detect this. The only locations Lurp writes are its own cache/output directory, the lock directory under `%TEMP%\lurp-locks`, and any explicit `--output-dir=`/`--output-json=` path the caller names.
2. **C#/.NET specialization**: remain Roslyn-native; do not weaken the semantic model for multiple languages.
3. **Fact before interpretation**: record `implements MediatR.IRequest<T>`, not "query" as compiler-proved business intent.
4. **Unknown is a valid result**: make each blind spot the strongest honest form available.
5. **Schema stability**: every field is a contract; renaming/removing requires a major revision.
6. **Roslyn boundary**: Roslyn analysis belongs in `src/Workspace/`, `src/Adapters/`, `src/Shared/` and `src/Helpers/`, plus the MSBuild workspace load in `status`. Never in `src/Storage/`.

## 9. What Not to Build

Not a UI, not multi-language, not autonomous editing, not LLM summaries, not
architecture scoring, not change simulation. The indexer is the map and
retrieval system, not the hand changing the code.
