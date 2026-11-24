# Lurp Architecture

**Status:** Design reference. The architecture described here is fully implemented.
See `notes/TRUST_KERNEL.md` for verification evidence and known deviations.
**Current:** <!-- generated:versions -->tool 4.1.0, schema v50, extractor 1.14.0, CLI/MCP contract v8, output schema v7<!-- /generated:versions -->
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
tasks. A project that evaluated correctly is never changed; language-version
recovery runs only after a failed evaluation and uses the explicit
`LangVersion`, else the target-framework default (from the project file or
`Directory.Build.props`), else `LatestMajor`. All capabilities share one
document identity, one symbol identity, one workspace snapshot, one freshness
model, one persistent database, and one query interface.

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
content-derived and content-addressed (`SnapshotIdentity`): when every identity
input is unchanged, a re-index reuses the existing snapshot, except
`--strategy=full` reuses a snapshot only when a full run built it. The identity
hashes the workspace id (git root and solution path, so a clone at another path
gets other snapshot ids), the .NET SDK and Roslyn compiler versions,
`ExtractorVersion` and each extractor and adapter version, the per-project
target frameworks, assembly names, project file paths, project references, metadata reference
identities, compilation-options fingerprints (including the language version and
preprocessor symbols), per-project `InternalsVisibleTo` targets and project document lists, each document's path and
content hash, and the skipped adapters. A change to any of those inputs gives a
new snapshot id. The latest snapshot of a workspace is the one that the most
recent successful index run built or reused.

Logical boundaries:

1. **Repository source files**: authoritative content.
2. **Roslyn compilation**: authoritative static semantics for a build config.
3. **`index.db`**: persistent materialized snapshot.
4. **Context capsules**: task-specific projections of stored facts + source.
5. **Annotations**: human/agent interpretation, kept separate from compiler facts.

Store complete document contents once per content hash; never make separately
copied method bodies the canonical representation. Declarations are snapshot
facts; each snapshot links to its own rows. Schema migrations are
sequential and managed by `MigrationRunner`.

Per-snapshot index cost is recorded in `snapshot_timings` (step → elapsed ms)
and `snapshot_metrics` (metric name → integer value). `peak_working_set_bytes`
is the exact peak working set of the Lurp process; `peak_working_set_mb` is the same
value rounded up to a whole MiB for display. Both cover the Lurp process only; MSBuild
BuildHost child processes are not counted.

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

Only the declaration stage of `CompilationFactExtractor.ExtractAll` is required.
When it fails, or when the compilation is unreadable, the project is recorded as
unreadable (`project_unreadable`). Every other stage (structural edges, member
edges, polymorphism, reflection, each framework adapter) degrades: the facts of
the stages that succeeded are saved, each document of the project gets an
`extractor_failure` binding-incompleteness row, and the run prints a warning. The
full and incremental pipelines follow the same rule.

Incremental indexing (`IncrementalIndexer.RunIncrementalAsync`) re-extracts the
changed documents, every part of the types that they declare, and the documents
that hold an edge into them (the reverse-edge closure); when a changed document
changes anything outside member bodies (Roslyn top-level equivalence), or a
document is added or deleted, the run re-extracts every document of that
project and of every project that depends on it, and creates a new snapshot when
content changes; it never mutates an existing snapshot. When nothing changed, the
existing snapshot is reused via content-addressed dedup and becomes the latest
snapshot of the workspace.

Declarations are per document: a `partial` type declared in two documents
yields two declaration records of a snapshot with one symbol id, and the
snapshot's symbol set holds that symbol once. `declarations_extracted_this_run`
therefore counts declaration records while `symbols_in_snapshot` counts symbols.
An edge shows every site of its highest provenance in source order. Impact hops and
capsule path hops list up to 20 sites with `site_count`.

### 4.1 Framework adapters

| Adapter | Facts emitted |
|---|---|
| ASP.NET Core | `RoutesTo` from built-in and custom HTTP route attributes to controller actions. `UseMiddleware` emits `Constructs` to the middleware type and `Calls` to its entry points and explicit public constructors. |
| Dependency Injection | `Registers` for every MS.DI registration form (`Add*`, `TryAdd*`, keyed, `ServiceDescriptor`): service → implementation and registering member → implementation. A factory, an instance of an interface or abstract type and a `Type` value that is not a `typeof` give the runtime-unknown node. Hosted services, options and external single-type extensions also claim the named type with `runtime_unknown`. `Configure<T>` and `AddOptions<T>` add `options_binding` annotations. The supported Scrutor scan links interfaces to classes with `convention`. Unmodeled scans with a statically identifiable assembly emit a `convention:assembly_scan:<assembly>` placeholder. |
| MediatR | `Handles` (request, stream request or notification → the method that implements the handler interface's `Handle`, including explicit, inherited and overloaded implementations) |
| EF Core | `MapsTo` from `DbSet` properties, concrete contexts and configurations. Targets include fluent entity, owned and complex mappings and types reachable through navigation properties. `HasOne` and `HasMany` also emit `References`. Query filters and unique indexes add constraint annotations. |
| Serialization | `serialization_contract` annotation per member (`name=<wire name>`, `ignored`, `included`) from System.Text.Json, Newtonsoft.Json and DataContract attributes, writing a value only when the library reads the attribute (`DataMember` on a `[DataContract]` type; STJ name or condition on a non-public member only with `JsonInclude`); `JsonSerializable` context → type `References`. JSON constructors and serialization callbacks receive `included` annotations. |
| Test | `TestedBy` from solution production types to test methods. Follows test bodies, helpers, data sources, fixture initialization and setup methods. Reflected setup also contributes `References` from the test to its dependencies so edits refresh coverage facts. |

### 4.2 Evidence ladder

Stored edges carry one of six provenance levels, ranked in this order:

| Level | Meaning |
|---|---|
| `compiler_proved` | The compiler resolves the relationship. Examples include calls, inheritance and field reads. |
| `framework_derived` | A framework adapter records a relationship. The adapters record DI `Registers`, ASP.NET Core `RoutesTo`, MediatR `Handles`, EF Core `MapsTo` and navigation `References`, test `TestedBy`, and serialization context `References`. |
| `possible` | `MayDispatchTo` marks a possible dispatch target when a type implements an interface member through a member of its base class. The base class declares the implementation. |
| `convention` | Lurp infers the relationship from a convention such as an assembly scan. |
| `name_candidate` | Lurp uses the semantics of a runtime name-binding API to resolve a name argument. Examples include reflection lookup, `PropertyChanged` with `[CallerMemberName]`, EF Core string APIs, binding attributes and MVC action names. |
| `runtime_unknown` | The target is only known at run time. Lurp records that it cannot resolve the target. |

`global_implementation_relation` is a capsule grade, never an edge: context assembly assigns it to an implementation of a called interface or virtual member projected through `MayDispatchTo` into `direct_callees`. The global relation is real; that the call reaches it at run time is not established.

### 4.3 Edge kinds

An edge is one fact: a source symbol, a target symbol and a kind. A fact from a
body has the member that runs the code as its source (the owner rule). An
accessor owns its body, also the expression body of a property or an indexer. A
field, property, event or enum-member initializer belongs to that member; each
variable of a field declaration owns its own initializer. A local function, a
lambda and a query clause belong to the member that contains them. A parameter
default belongs to its method (an indexer parameter: the indexer; a delegate
parameter: the delegate type). The arguments of a primary-constructor base list
belong to the primary constructor. An attribute argument belongs to the
attributed member (a type attribute: the type; a parameter or return-value
attribute: its method). The top-level statements belong to the entry point. All
extractors read one list of these roots (`ExecutableRoots`). No extractor drops an
edge from a member to itself; the readers (dead candidates, capsules) ignore such
an edge. The `mapping` list of
[call-shapes.golden.json](../tests/Fixtures/GroundTruth/CallShapes/call-shapes.golden.json)
is the executable form of these rules for the kinds that the golden compares.

| Kind | Meaning | Source → target | Provenance |
|---|---|---|---|
| `Inherits` | A type names its base class. A class whose base is `object` and a struct give no edge. | type → base type | `compiler_proved` |
| `Implements` | A type names an interface in its base list (an interface: its base interfaces). | type → interface | `compiler_proved` |
| `References` | A type is named in a signature (a return, parameter, property, field or event type, with its type arguments, array elements and function-pointer types), as a type argument in a base list, in a written constraint, or in a body (a local declaration, a written or inferred lambda type, a pattern, a cast, `default`, `sizeof`, an array creation, a `catch` type, a method type argument, the receiver type of a static member access); an EF Core navigation (`HasOne`/`HasMany`); or a `[JsonSerializable]` context. | member, type, context or configuration → type | `compiler_proved`; `framework_derived` for EF Core navigations and `[JsonSerializable]` |
| `Contains` | A type declares a nested type. | type → nested type | `compiler_proved` |
| `Overrides` | A method, property or event overrides a base member. | override → overridden member | `compiler_proved` |
| `Hides` | A member hides an inherited member by the C# hiding rules. | member → hidden member | `compiler_proved` |
| `ExtensionReceiver` | A reduced extension call (`x.M()`) binds its receiver type to the extension method. | receiver type → extension method | `compiler_proved` |
| `Calls` | Code calls a method: an invocation, the implicit calls of the C# rules (`foreach`, `using`, `await`, deconstruction, collection initializers and expressions, user-defined operators and conversions, interpolated-string handlers, `with`), a constructor initializer, and a source-declared constructor of an object creation or an attribute. | owner → method | `compiler_proved` |
| `MethodGroupRef` | A method group converts to a delegate, also the handler of an event `+=` or `-=`. | owner → method | `compiler_proved` |
| `Constructs` | An object creation, an attribute application or an interpolated-string handler creation. | owner → created type | `compiler_proved` |
| `Reads` | Code reads a field, property, indexer or event. | owner → member | `compiler_proved` |
| `Writes` | Code writes a field, property, indexer or event. A compound assignment, `??=`, `++`, `--` and a `ref` use are a read and a write. | owner → member | `compiler_proved` |
| `Returns` | A method returns a source type. A delegate type owns the return type of its `Invoke`. | method or delegate type → type | `compiler_proved` |
| `Throws` | A `throw` names an exception type. | owner → exception type | `compiler_proved` |
| `Declares` | A type declares a member, also a compiler-made member that Lurp stores (the synthesized members of a record, the members of a delegate). | type → member | `compiler_proved` |
| `MayDispatchTo` | A call on a virtual, abstract or interface member can run this implementation at run time. Every member of an override chain points to every override below it. | dispatch member → implementation | `compiler_proved`; `possible` when only a derived type maps the member to an inherited implementation |
| `StaticallyCalls` | An access whose target can dispatch at run time. It sits next to the `Calls`, `Reads` or `Writes` edge of the same access. A `base.` access and a method group get none. | owner → dispatch member | `compiler_proved` |
| `RoutesTo` | An ASP.NET Core route (`route://<METHOD> /<template>`, tokens and overrides resolved, `ANY` without a method constraint) reaches a controller action. | route node → action method | `framework_derived` |
| `Registers` | A dependency-injection registration binds a service to an implementation. A registration without an implementation registers the service type itself. | registering member or service type → implementation type; interface → class for a Scrutor scan; or the runtime-unknown node or an assembly-scan placeholder | `framework_derived`; `convention` for a Scrutor scan; `runtime_unknown` for a registration that Lurp cannot read |
| `Handles` | A MediatR handler handles a request or a notification; target = the method that implements the interface's `Handle` (explicit, inherited and overloaded implementations included). | request type → handler method | `framework_derived` |
| `MapsTo` | EF Core maps an entity type through a declared or inherited `DbSet`, a configuration or a model builder call. | context, property or configuration → entity type | `framework_derived` |
| `TestedBy` | A test method covers a production type; the test reaches the type through its body, the helpers of the test assembly, its class constructor, initializers and setup methods. | production type → test method | `framework_derived` |
| `ReflectionTypeRef` | A `typeof(T)` names a type. | owner → type | `compiler_proved` |
| `ReflectionMemberRef` | A `nameof` names a member, and each member of its receiver chain. | owner → member | `compiler_proved` |
| `ReflectionNameCandidate` | A string literal is the name argument of a runtime or framework name-binding API, and it equals a declared name of the member kind of that API in its scope: the known type and its base types (see [TRUST_KERNEL.md](../notes/TRUST_KERNEL.md)). | owner → candidate member or type | `name_candidate` |
| `ReflectionTargetUnknown` | Code calls a runtime name-binding API, so the target is not known statically. | owner → owner | `runtime_unknown` |

## 5. Read Path (Handlers)

Handlers consume persisted facts through `SqliteIndexStore` and related store
interfaces. They do not re-run Roslyn analysis, except `--mode=status
--solution=` (CLI) and MCP `lurp_status` with `full:true`, which load the
workspace through MSBuild to compare it against the stored snapshot.

Each CLI mode has its handler in `src/Handlers`, except `serve` (`McpServeHandler` in
`src/Mcp`). `Program.ModeRegistry` maps each mode to its handler, and
[CLI_REFERENCE.md](CLI_REFERENCE.md#modes) lists the modes. MCP surface (`--mode=serve`):
<!-- generated:mcp-tools:read -->16 read tools (`lurp_context`, `lurp_get_source`, `lurp_outline`, `lurp_navigate`, `lurp_find_symbol`, `lurp_search`, `lurp_impact`, `lurp_diff`, `lurp_get_symbol`, `lurp_get_annotations`, `lurp_diagnostics`, `lurp_grep`, `lurp_status`, `lurp_timings`, `lurp_refresh`, `lurp_dead_candidates`)<!-- /generated:mcp-tools:read --> over stdio;
`--enable-write-tools` additionally registers
<!-- generated:mcp-tools:write -->`lurp_index` and `lurp_retract_annotation`<!-- /generated:mcp-tools:write --> (write),
which start a background (re-)index or a retraction through a separate writer connection (see
[CLI_REFERENCE.md](CLI_REFERENCE.md#mcp-server---modeserve)).

Read commands never migrate `index.db`. They refuse a database whose schema
version differs from `MigrationRunner.LatestVersion` (the highest migration number) and name the fix
(`--mode=index`, or a newer Lurp). The exception is `status`, which reports
both versions and exits 0. Only `index`, `pin-snapshot` and `lurp_index`
migrate.

### 5.1 Impact reachability

`ImpactReachability` answers "what can this change reach?". It runs a
level-by-level BFS from the anchor with one shared visited set, so memory grows
with symbols and edges, not with paths. For each reached symbol it records the
depth, one witness path, a shortest-path count (the number of shortest symbol routes;
parallel edges between two symbols count once, saturating at `long.MaxValue`)
and a frontier flag. Edges are sorted by neighbor ID, kind,
provenance and source location before expansion, and each level is expanded in
symbol-ID order, so the witness paths and the output are the same on every run.
`--mode=impact`, `lurp_impact` and the capsule paths use this trace.

## 6. Context Capsules

A context capsule is a bounded, evidence-backed package of relevant code
assembled for an agent task. Built by `ContextAssembler` with tier builders
and enforced by `CapsuleBudgetEnforcer`. Default priority order: contracts,
direct callees, direct callers, registered implementations, relevant tests,
second-degree context, surrounding source. The intent (`modify`, `diagnose`)
reorders it, and a type anchor swaps `relevant_tests` and `surrounding_source`.

| Intent | Tier priority order (highest to lowest) |
|---|---|
| `inspect` (default) | `contracts`, `direct_callees`, `direct_callers`, `registered_implementations`, `relevant_tests`, `second_degree_context`, `surrounding_source` |
| `modify` | `contracts`, `direct_callers`, `registered_implementations`, `relevant_tests`, `direct_callees`, `second_degree_context`, `surrounding_source` |
| `diagnose` | `direct_callers`, `registered_implementations`, `contracts`, `direct_callees`, `relevant_tests`, `second_degree_context`, `surrounding_source` |

For a type anchor, `relevant_tests` and `surrounding_source` swap positions wherever the intent placed them.

Trim order (first dropped to last dropped; `uncertainties` is appended after the budget pass and is never counted or trimmed): `surrounding_source`, `inclusion_reasons` (except `omitted_tiers_*`), `affected_public_surfaces`, `likely_change_sites`, `completeness` (detail first as `summarized`, then whole section as `budget_exhausted`), `constraints`, `topology`, `semantic_causes`, `outgoing_paths`, `incoming_paths`, `suggested_verification`, then the remaining tiers from lowest priority up, then `anchor` (source bounded to fit, or `budget_exhausted` when residual content still exceeds the budget).

Incoming capsule paths and default upstream impact use the same edge filter.
Outgoing capsule paths follow all edge kinds.
The caller and test tiers share a walk from the anchor and its effective members.
Callers at depth one appear in `direct_callers`. Callers from depth two through
the hop limit appear in `second_degree_context`. Reads and writes count at
each hop. A dispatch link consumes one hop. A type capsule omits callers
inside the type and its nested types. Tests use the minimum depth of the
symbols they cover. These tiers sort by depth, then by symbol ID.

When a tier is empty, contracts, direct callees and surrounding source check
for binding loss in the anchor documents. Callers, tests, registrations and
affected public surfaces check the anchor project and its transitive dependents.
A missing binding reader leaves both scopes unobservable.
A project-wide loss counts for the anchor documents when Lurp cannot recover
the documents of that project. Assembly names match without regard to case.

The contracts tier shows proved interface members implemented by the anchor
or its effective members. The registration tier uses the other runtime evidence.

Affected public surfaces and the dead-candidate ladder use the same access
rules. Types, ordinary methods, events and event accessors use effective
access. Instance constructors, properties, fields and property accessors use
declared access. Internal and private protected symbols need a friend grant
to an assembly outside the snapshot.

Constraints apply to the anchor, its containing types, emitted items and
the endpoints of emitted hops. The budget pass removes constraints when it
removes their last in-scope occurrence. MediatR warnings use the final
scope. A wrapper pattern also qualifies if a `Handles` edge touches the
effective anchor scope or a surviving item carries that edge kind.

Budgeting: `--content-budget` bounds content tokens; over the budget, the capsule
keeps the first three incoming or outgoing paths whole, in witness-path order,
and drops the rest (`summarized`), never cutting a path; tiers are included in
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
| `ef_convention` | The index does not model all EF Core conventions. EF Core can use fields, properties and property accessors of an EF-mapped entity or a base class of one by reflection. Instance constructors must belong to the entity type itself. |
| `shape_similarity` | Semantic sibling similarity is not modeled: consistency audits requiring comparison against 'similar' implementations are unsupported. Use the proved neighborhood instead — implementations of a shared interface, shared base types and overrides, callers/callees, and containing-declaration siblings. |
| `mediatr_stream_handler` | Legacy MediatR stream handler annotation: the implementing type was detected but no Handles edge was emitted. |
| `mediatr_pipeline_behavior` | MediatR pipeline behavior (IPipelineBehavior) is not modeled: the implementing type was detected but no edge was emitted. Pipeline behaviors affect all requests passing through the pipeline. |
| `mediatr_exception_handler` | MediatR exception handler (IRequestExceptionHandler) is not modeled: the implementing type was detected but no edge was emitted. |
| `mediatr_pre_post_processor` | MediatR pre/post processor (IRequestPreProcessor or IRequestPostProcessor) is not modeled: the implementing type was detected but no edge was emitted. |
| `source_generators` | Source-generated code is not executed or indexed (generated trees are excluded and obj/ output is path-filtered), so symbols, calls, and registrations declared only by a source generator are absent from the graph. |
| `razor_blazor` | Razor (.cshtml) and Blazor components are not parsed: only C# documents in the solution's compilations are indexed, so component event handlers and render-tree calls are absent from the graph. |
| `minimal_api_endpoints` | Minimal API endpoints, non-controller ASP.NET Core surfaces (MapGet/MapPost, endpoint filters, SignalR hubs, gRPC services) and conventional routes (MapControllerRoute) are not modeled: the ASP.NET Core adapter recognizes only types that ASP.NET Core's ControllerFeatureProvider accepts, so endpoint-to-handler and conventional routes are absent from the graph. |
| `multi_target_union` | Multi-target projects are indexed as one union snapshot over every declared target framework: the declared TFM list is recorded per project and the per-symbol TFM set is recorded per snapshot, but reads do not surface the per-symbol set, so a construct present in only one target framework cannot be told apart by a consumer from one present in all of them. |
| `non_csharp_projects` | F# and VB projects are out of scope: MSBuildWorkspace skips non-C# languages and the skip is reported as a warning, so symbols and relationships declared in those projects are absent from the graph. |
| `intermediate_redirect_not_applied` | A project's build files set the intermediate output path after Lurp's redirect (or assign CustomBeforeMicrosoftCommonTargets in the project file), so its design-time build files are written under its own obj/ directory in the target tree. The project's own build hooks do take effect; the indexed compilation is not affected. |
| `static_abstract_dispatch` | Static-abstract interface dispatch is not modeled as a direct edge to the implementation: a generic method constrained to an interface with a static abstract member is extracted as a Calls edge to the interface member, but the implementing type selected at dispatch time is not emitted as a relation. |
<!-- /generated:declared-boundaries -->

## 7. Glossary

| Term | Meaning |
|---|---|
| **snapshot** | One immutable view of a solution; content-addressed |
| **capsule** | Token-bounded neighborhood assembled for an agent task |
| **tier** | A category within a capsule; may be `budget_exhausted` |
| **evidence / provenance** | The 7-level ladder (`compiler_proved` → `runtime_unknown`) |
| **adapter** | Framework-mediated edge emitter (ASP.NET, DI, MediatR, …) |
| **freshness** | Snapshot state (`fresh`/`stale`/`unknown`); `status --json` also reports a `completeness` block with `binding_incompleteness` / `binding_incompleteness_summary` / `binding_incompleteness_total`; the `occurrence_count` of a row is the number of distinct binding sites (document, node span, reason) |

## 8. Non-Negotiable Design Rules

1. **Read-only relationship to source**: may read, index, report consequences; must not apply fixes. Lurp loads projects through MSBuild design-time builds, but redirects their intermediates into `%LOCALAPPDATA%\lurp\<hash>\obj\<project>\<tfm>\` (on Linux `~/.local/share/lurp/<hash>/obj/<project>/<tfm>/`, under `$XDG_DATA_HOME` when it is set) and their `OutputPath` into `...\obj\<project>\<tfm>\bin\` (through the `CustomAfterDirectoryBuildProps` global property on `MSBuildWorkspace`; the solution's own `CustomBeforeMicrosoftCommonTargets` hook still runs), so an index run does not create or change `bin/` or `obj/` files in the target tree; restore assets are still read from the real `obj/`. A project that moves its own intermediate path or assigns that hook in its project file is reported with a warning (declared boundary `intermediate_redirect_not_applied`) and keeps its files in its own `obj/`. A solution that sets `CustomAfterDirectoryBuildProps` itself loses that value while Lurp loads it; Lurp does not detect this. The only locations Lurp writes are its own cache/output directory, the lock directory (`%TEMP%\lurp-locks` on Windows, `$TMPDIR/lurp-locks` on Linux, usually `/tmp/lurp-locks`; the lock files stay after a run), and any explicit `--output-dir=`/`--output-json=` path the caller names.
2. **C#/.NET specialization**: remain Roslyn-native; do not weaken the semantic model for multiple languages.
3. **Fact before interpretation**: record `implements MediatR.IRequest<T>`, not "query" as compiler-proved business intent.
4. **Unknown is a valid result**: make each blind spot the strongest honest form available.
5. **Schema stability**: every field is a contract. Renaming or removing a field of the CLI or MCP output requires a `CliMcpContractVersion` bump, and a capsule field also an `OutputSchemaVersion` bump ([VERSIONING.md](../VERSIONING.md)). A change of a stored shape requires a schema migration.
6. **Roslyn boundary**: Roslyn analysis belongs in `src/Workspace/`, `src/Adapters/`, `src/Shared/` and `src/Helpers/`, plus the MSBuild workspace load in `status`. Never in `src/Storage/`.

## 9. What Not to Build

Not a UI, not multi-language, not autonomous editing, not LLM summaries, not
architecture scoring, not change simulation. The indexer is the map and
retrieval system, not the hand changing the code.
