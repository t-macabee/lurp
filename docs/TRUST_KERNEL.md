# Trust Kernel: Implementation Status

**Status:** implementation-status reference. Compare with `CLAUDE.md` for
architecture, then inspect the relevant live code and tests. There is no live
task queue in this repository.
**Architecture:** `CLAUDE.md` (design rules, boundaries, layout).
**Historical records:** superseded by the "Change log" table below; each row
states the resulting current state.

---

## Purpose

This file records implementation status backed by git history and the
targeted tests cited below: not by any single audit run's scratch output.
Earlier revisions cited `task/findings.md` as an evidence source; that file
was a one-off audit artifact, was never durable, and does not exist in this
folder. Do not recreate it or treat any future per-run scratch file the same
way: evidence here cites git commits and named tests directly.

## Two numbering schemes in this folder: read this before touching either

- **T1–T12**: a flat, now-closed list from the original trust audit. All
  twelve items are implemented and validated: the evidence is inlined below.
  (An earlier per-iteration `task/task_list.txt` scratch file existed briefly
  and has been removed; it was never durable and its content is fully captured
  here.) This list is not extended going forward: there is no T13.
- **order 1–13**: a completed historical implementation sequence. Orders 1–2
  were the final loose ends of T11 (root-detection fix and baseline run: see
  T11). Order 3 is the Track A/B roadmap decision. Orders 4–13 implement
  `LURP_ARCHITECTURE.md` §23 Phases 4–13. Per-order planning artifacts are
  consolidated into this tracker; do not recreate a task queue from closed
  work.

## Where we are right now

| Order | Status | Evidence |
|---|---|---|
| 1: Fix benchmark root detection | Done | commit `452fdaa` |
| 2: Establish outcome-benchmark baseline | Done | commit `452fdaa`; `tests/benchmark-runs/baseline.json` |
| 3: Track A vs. Track B decision | Done | Track A chosen; completion is recorded here. |
| 4: Document/snapshot identity | Done | commit `f550fee` (`ux_snapshot_documents` unique index, `document_versions` immutability trigger, D4/D5 negative tests in `tests/D4D5SchemaHardeningTests.cs`) |
| 5: Declaration lookup and partial types | Done | commit `8199760`; `DeclarationLookup_ResolvesPartialType_AndPreservesBothDeclarations` against the real indexed fixture |
| 6: Fast-travel reads and lexical search | Done | `FastTravelQueries`, persisted-span navigation in `DeclarationMaintenanceStore`, `navigate` handler; `SearchSourceStore.SearchSource` lexical search hardened by commit `994429c` (per-snapshot document-version-scoped generated-code exclusion, empty-query/non-positive-limit guards, path dedup). Tests: `FastTravelQuery_NavigatesFromIndexedSpan`, `NavigateHandler_ReturnsSnapshotBoundTarget`, `SearchSource_SnapshotIsolation_UsesVersionBoundToRequestedSnapshot`, `SearchSource_EmptyQueryAndNonPositiveLimit_ReturnEmpty`, `SourceSearch_Returns_Bounded_Distinct_Snippets` |
| 7: Migrate dirty state, fingerprints, diagnostics, and facts out of JSON (removes dual JSON/SQLite authority) | Done | Already SQLite-only; the sole remaining `--output-json=`/`SnapshotManifest.Load` surface is a documented, tested, one-way export never consulted as an authority source. |
| 8: Facts-table decision | Done | Facts table rejected; attributes stay in `metadata_json` with identity/multiplicity preserved. |
| 9 | Not applicable | Conditional on order 8 approving the facts table; order 8 did not, so there is no implementation work |
| 10: Generated-code discovery | Done | Dual-path detection and provenance were traced; all workspace generated files are under `obj/` and therefore out of index scope. |
| 11: Generated-code provenance decision/implementation | Done | Existing plumbing was used; no `GeneratorDriver` or package detection. Encoding/short-file detection and structural-edge `IsCrossGenerated` propagation were fixed. `GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges` exercises a real injected `.g.cs` file end-to-end. |
| 12: Phase 9/10 gap audit | Done | Evidence-backed backlog of uncovered polymorphism/dispatch and structured-diff cases (see "Order 12 findings" below). Four characterization tests prove `SignatureFormat` string comparison already catches nullable annotations, ref/out/in modifiers, operator overloads, and conversion operator changes. |
| 13: Phase 9/10 implementation | Done | `CallsEdgeExtractor` now records compiler-resolved overloaded binary operators and user-defined cast conversions as canonical `Calls` edges. Tests: `Calls_OverloadedBinaryOperator_EmitsCallsEdge`, `Calls_UserDefinedConversion_EmitsCallsEdge`, `SemanticDiffer_EdgeAddedAndRemoved`, `IncrementalIndex_Matches_FullRebuild_AfterOperatorAndConversionCallEdit` |

## Architecture Phase completion status (§23 roadmap)

| Phase | Description | Status | Evidence |
|---|---|---|---|
| 1 | Product constitution and schema/version rules | ✅ Done | `VersionConstants`, `MigrationRunner` |
| 2 | Workspace, snapshot, document, and configuration identities | ✅ Done | Order 4 |
| 3 | SQLite storage boundary and migrations | ✅ Done | `SqliteIndexStore`, 24 migrations; `SchemaStabilityTests` binds the migration list to `VersionConstants`, upgrades the checked-in v22 fixture, proves unknown persisted symbol kinds survive as `Unknown` |
| 4 | Immutable document versions and source storage | ✅ Done | Order 4, T12 |
| 5 | Stable type/member identities and declaration spans | ✅ Done | Order 5 |
| 6 | Fast `get` and lexical `search` queries | ✅ Done | Order 6 |
| 7 | Migrate dirty state, fingerprints, diagnostics, and existing facts | ✅ Done | Order 7 |
| 8 | Typed member-level semantic edges | ✅ Done | Order 8 decision + Order 13 + G1–G7 |
| 9 | Polymorphism and dispatch candidates | ✅ Done | Order 12 audit + Order 13 + G3, G5, G6, G7 |
| 10 | Structured semantic snapshot diffs | ✅ Done | Order 12 + G1, G2 |
| 11 | Generated-code provenance | ✅ Done | Orders 10, 11 |
| 12 | ASP.NET, DI, MediatR, EF, serialization, and test adapters | ✅ Done | All 6 adapters exist; `TestedBy` granularity fix in `RelevantTestsTierBuilder` (commit `63cfaf4`); `FullIndex_OutcomeBenchmark_EmitsTestedByOnProductionType` and `RunBaseline_WritesOutcomeEvaluation` pass |
| 13 | Reflection evidence ladder | ✅ Done | See "Phase 13 verification" below |
| 14 | Evidence-backed impact paths | ✅ Done | `ImpactTraverser`, `ImpactHandler`, semantic_causes |
| 15 | Context capsules with source and token budgets | ✅ Done | See "Phase 15 verification" below |
| 16 | Rebase simulations and audits on the shared store | ✅ Done | All read/simulate/audit handlers run through the shared `HandlerBootstrap` (`src/Handlers/HandlerBootstrap.cs`): `GetArgValue`, `RequireArg`, `ResolveOutputDir`, `ResolveDbPath`, `OpenStore`, `ResolveSnapshotId`. Per-handler copies deleted as each converted: simulate family (`SimulateRenameHandler`/`SimulateMoveHandler`/`SimulateRemoveHandler`), `AuditHandler`, `ImpactHandler`, `DiffHandler`, `GetSymbolHandler`, `GetSourceHandler`, `SearchHandler`, `FindSymbolHandler`, `NavigateHandler`, `ContextHandler`, `AnnotationHandler`, `TimingsHandler`. `StatusHandler`/`IndexHandler` keep only their diverging control flow and share just `GetArgValue`/output-dir resolution. |
| 17 | Optimize incremental updates from measurements | ✅ Done | Per-extractor elapsed time and current-thread allocations emitted by `CompilationFactExtractor`. A matched self-host measurement identified `CallsEdgeExtractor`/`ReadsWritesEdgeExtractor` as dominant; single-pass call/operator/cast/indexer traversal plus cached method enumeration reduced `CallsEdgeExtractor` 2051→1944 ms (Lurp), 217→182 ms (Storage), 427→418 ms (tests). A broader node-cache candidate was measured and rejected. `B1MemberEdgeExtractorTests` 22/22 pass. |

### Phase 13 verification: Reflection Evidence Ladder

**Architecture §18 requirements:**

| Requirement | Edge Kind | Extractor | Tests |
|---|---|---|---|
| `typeof(T)` type reference | `ReflectionTypeRef` | `TypeOfReflectionExtractor` | `TypeOf_EmitsReflectionTypeRefEdge` |
| `nameof` member reference | `ReflectionMemberRef` | `NameOfReflectionExtractor` | `NameOf_EmitsReflectionMemberRefEdge` |
| String literal matching known name | `ReflectionNameCandidate` | `StringLiteralReflectionExtractor` | `StringLiteral_MatchingTypeName_EmitsNameCandidateEdge` |
| Runtime-unknown reflection target | `ReflectionTargetUnknown` | `UnknownPatternReflectionExtractor` | `TypeGetType_EmitsUnknownEdge`, `ActivatorCreateInstance_EmitsReflectionTargetUnknownEdge` |

- All extractors in `src/Workspace/`; registered in `ExtractorRegistry` as `"reflection-v1"`; 9 unit tests in `tests/B6ReflectionExtractorTests.cs` (`MigrationRunnerTests.B6ReflectionTests`); integrated with `UncertaintyDetector` for capsule uncertainty reporting.

### Phase 15 verification: Context Capsules

**Architecture §20 requirements:**

| Requirement | Implementation | Status |
|---|---|---|
| Anchor symbols | `CapsuleAnchor` includes symbol/FQN/kind/source plus scope, intent, hop limit, snapshot, affected projects, objective, provenance, extractor identity, and declaration locations | ✅ |
| Relevant contracts | `ContractsTierBuilder` | ✅ |
| Registered or possible runtime targets | `RegisteredImplementationsTierBuilder` | ✅ |
| Relevant tests | `RelevantTestsTierBuilder`; shared containing-type expansion in `TestSymbolDiscovery` | ✅ |
| Uncertainties and unchecked vectors | `UncertaintyDetector`, including reflection/generated exclusions and persisted binding incompleteness | ✅ |
| Incoming and outgoing paths | `ContextCapsule.IncomingPaths` / `OutgoingPaths`; `ImpactHop` carries the complete source span | ✅ |
| Suggested verification commands | `VerificationSuggestion.Command`; owning test project derived from the persisted test declaration path; multi-project changes escalate to a full-suite step | ✅ |
| Likely change sites | Ranked `LikelyChangeSite` entries for anchor, direct callers, and composition points | ✅ |
| Exact source spans | `DeclarationReadStore.GetDeclarationLocations`; every anchor and capsule item carries path/start/end coordinates | ✅ |
| Affected public surfaces | Derived from persisted declaration accessibility | ✅ |
| Reason every item was included | Per-group `InclusionReasons` plus per-item `InclusionReason`, authored explicitly by every tier builder | ✅ |

Budgeting follows §20.1 order. The partial-tier policy is explicitly
greedy-prefix: once a higher-priority item cannot fit, lower tiers are not
allowed to leapfrog it. Every omitted tier is still evaluated and recorded with
`budget_exhausted` or `empty`. `ContextCapsuleAcceptanceTests.SelfHost_EdgeLocationResolver_CapsuleSatisfiesPhase15Contract`
indexes `Lurp.slnx` and proves the eleven-item contract plus an executable test
command.

Closed capsule decisions:

- No occurrence multigraph: architecture §12/§13 defines one evidence-bearing
  relation edge, not exhaustive call-site storage. A future occurrence table
  would require an architecture amendment.
- No generated anchor narrative: architecture §24 keeps deterministic
  structured facts canonical. Consumer-authored prose is not stored as fact;
  source-authored XML documentation remains retrievable source evidence.
- Architectural constraints come from snapshot annotations and repeatable
  caller `--constraint` input. No second JSON authority was added.

### Phase 14 verification: Evidence-backed Impact Paths

**Architecture §19 completion condition:**

> An agent can see why a code location is considered affected and inspect the exact source at every hop.

| Requirement | Implementation | Status |
|---|---|---|
| Path representation | `ImpactPath` with `List<ImpactHop>` | ✅ |
| Hop details | `SourceSymbolId`, `TargetSymbolId`, `EdgeKind`, `Provenance`, `SourceDocument`, `SourceLine` | ✅ |
| Direction control | `ImpactDirection.Upstream/Downstream` | ✅ |
| Depth limiting | `maxDepth` parameter | ✅ |
| Edge filtering | `allowedEdgeKinds` parameter | ✅ |
| Cycle detection | `visited` set per path | ✅ |
| Truncation explanation | `Truncated`, `TruncationReason` | ✅ |
| Semantic causes | `SemanticCauses` attached via `ISemanticDiffStore` | ✅ |

Tests: `B7ImpactTraverserTests` (14 tests), `SemanticRenameIntegrationTests`.

Order 4's scope was narrowed by an audit before its D1–D5 sub-tasks were
written: immutable document versions already existed de facto (T12), and
"snapshot-scoped document identity" as literally worded was rejected as
inconsistent with `LURP_ARCHITECTURE.md` Stage A0 (decision D1). The remaining
work was schema-level immutability enforcement and a `snapshot_documents`
uniqueness constraint, both in commit `f550fee`.

### Architecture §26 definitive-version checklist

| Criterion | Evidence | Status |
|---|---|---|
| One SQLite database holds indexed workspace state | `SqliteIndexStore` and migrations 1–24 | ✅ |
| Source and facts share snapshot identity | `snapshot_documents`, `snapshot_symbols`, snapshot-scoped edges/diagnostics/completeness | ✅ |
| Symbols link to exact retrievable spans | `DeclarationReadStore.GetDeclarationLocations` and all-declaration source reads | ✅ |
| Ordinary reads avoid Roslyn reload | Storage-backed handlers and context tiers | ✅ |
| Changed documents update incrementally | Fixed-point document invalidation plus reverse project closure | ✅ |
| Member-level typed edges power queries | Existing edge extractors/handlers; P0-3 payload-aware diffs | ✅ |
| Polymorphism/framework indirection keeps evidence levels | Existing provenance ladder and capsule uncertainties | ✅ |
| Generated semantics participate without flooding context | Existing generated flags, `includeGenerated`, and declared completeness limit | ✅ within declared boundary |
| Semantic diffs explain changes | Metadata diffs, semantic causes, edge evidence/location changes | ✅ |
| Impact results are paths with reasons | `ImpactPath`, exact hop spans, semantic causes | ✅ |
| Capsules return bounded relevant code and surroundings | Eleven-item Phase 15 contract and reason-coded truncation | ✅ |
| Every fact states provenance and extractor version | Edge/declaration anchor contracts and binding-completeness scope | ✅ |
| Simulations/audits consume the common model | Phase 16 handlers | ✅ |
| Indexer never modifies source | Index and handler pipelines are read-only with respect to repository source | ✅ |

## T1–T12: closed, implemented, and validated

### T1: Schema-version verification is authoritative

`VersionConstants.DatabaseSchemaVersion` is the sole reference for
migration-version assertions. `MigrationRunnerTests`: 124 passed, 0 failed.

### T2: Failed snapshot cleanup is atomic

`SnapshotPruner.DeleteSnapshotData(string)` deletes inside a transaction and
rolls back/rethrows on failure, matching `PruneWorkspace`/
`DeleteIncompleteSnapshots`. A trigger-induced mid-delete failure left all
earlier-deleted rows intact in the regression test. Storage suite: 139/139.

### T3: Orphan edges are removed on both endpoints

`EdgeOperationsStore.DeleteOrphanEdges` removes an edge when either endpoint is absent
from the snapshot's `snapshot_symbols`. `FullIndex_Has_No_Orphan_Edge_Targets`
passes; the deletion path runs in every full-index pipeline invocation.

### T4: Cross-project edge-relation deduplication

`EdgeOperationsStore.SaveEdges` uses `INSERT OR IGNORE INTO edges`; `IndexRunner.RunFullIndexAsync`
accumulates edges across all projects, runs `EdgeDedup.Deduplicate`
(provenance priority: `compiler_proved` > `framework_derived` >
`global_implementation_relation` > `possible` > `convention` > `name_candidate`
> `runtime_unknown`), then calls `SaveEdges` once. The unique index
`ux_edges_relation` is unchanged: no relaxation, no migration-side dedup.
`SaveEdges_DeduplicatesSameTripleAcrossProjects` and
`EdgeDedup_KeepsHighestProvenance` both pass.

### T5–T8: Semantic-diff metadata has a producer/consumer contract

`BuildMetadataJson` writes `base_type` (null for interfaces/`System.Object`),
canonical callable-member `signature`, and sorted `attributes` (type presence
plus constructor/named-argument values: attributes stay in `metadata_json`,
not the unimplemented `facts` table from architecture §3.4). `CompareMetadata`
consumes all of it; a contract test binds every consumer key to producer
coverage. Combined targeted + semantic-diff suites pass (T5: 7, T6: 8, T7: 12,
T8: 8 tests).

### T9: Generated-output absence is declared and reported

`MSBuildWorkspace.OpenSolutionAsync` does not expose source-generator output;
snapshot completeness persists `generated_trees_included`, active TFMs,
skipped adapters, and extractor version, surfaced via `status --json`.
Generated semantics remain declared absent: no `GeneratorDriver` work was
added (that's architecture Phase 11 / orders 10–11).

### T10: Incremental-versus-full equivalence coverage expanded

Snapshot comparison includes FQNs, metadata JSON, declaration
paths/spans/flags, and exact source/symbol FTS records. Cases cover
signature, body-only, document-move, partial-class, base/interface, and
DI-registration edits. Full suite: 167/167 at time of record.

### T11: Outcome benchmark exists and its baseline is established

The benchmark (local-validation, handler/DTO, DI-replacement scenarios) has a
fixture, machine-readable scenario contract, runner, and baseline JSON, and
records the ten architecture §25 measures without fabricating the
post-capsule worker-token measure. Two blockers, both fixed (commit `452fdaa`):
`LocateRepositoryRoot()` searched for a nonexistent `task/task_list.txt`
sentinel (replaced with the solution-file marker); `SearchSymbolStore` FQN lookups
didn't account for Roslyn's `global::` prefix (now accepts either form).
`OutcomeBenchmarkTests.RunBaseline_WritesOutcomeEvaluation` passes and writes
`tests/benchmark-runs/baseline.json`. Fixture and scenario contract live in
`tests/fixtures/OutcomeBenchmark/` (load-bearing test infrastructure, so under
`tests/` rather than `task/`). One capability gap surfaced, tracked separately:
both scenarios reported `missingRelevantTests: true` — closed by the Phase 12
fix (commit `63cfaf4`).

### T12: Snapshot pruning reclaims document-version storage

Post-prune cleanup deletes unreferenced `document_versions`, deletes documents
with no retained versions, and repairs/clears dangling
`last_changed_snapshot_id` values inside the prune transaction (foreign-key
enforcement is not enabled on the store connection, so cleanup is explicit
rather than cascade-driven). Both GC tests pass.

## Order 12 findings: Phase 9/10 gap audit

### Phase 10 (structured semantic diff): gaps found

**Already covered by `SignatureFormat` string comparison** (with
`IncludeNullableReferenceTypeModifier`, `IncludeParamsRefOut`,
`IncludeExplicitInterface`, `IncludeTypeConstraints`) — previously untested,
now locked by characterization tests:

| ID | Case | Test |
|---|---|---|
| S1 | Nullable annotation change (`string` → `string?`) | `SemanticDiffer_NullableAnnotationChanged` |
| S2 | ref/out/in modifier change | `SemanticDiffer_RefParameterModifierChanged` |
| S5 | Operator overload return type change | `SemanticDiffer_OperatorOverloadSignatureChanged` |
| S6 | implicit↔explicit conversion operator change | `SemanticDiffer_ConversionOperatorSignatureChanged` |

**Remaining gaps, all since closed:**

| ID | Gap | Resolution |
|---|---|---|
| S8 | `interfaces` key not written to `metadata_json` / not compared | Done: G1 persists sorted interface FQNs; `CompareMetadata` emits `interfaces_changed` |
| S9 | `isRecord` written but never compared | Done: G1 emits `record_changed` |
| S10 | `returnType`, `isAbstract`, `isVirtual`, `isOverride`, `isStatic`, `isAsync`, `arity`, `isExtensionMethod`, `typeKind` persisted but never compared | Done: G1 — modifiers are independently semantic (`metadata_changed` with field); `returnType`/callable arity covered by the canonical signature; type arity changes symbol identity |
| S11 | `semantic_changes` not queried for invalidation explanation | Done: G2 attaches `semantic_causes` to impact paths |

### Phase 9 (polymorphism/dispatch): gaps found

| ID | Gap | Resolution |
|---|---|---|
| D1 | `new` keyword (member hiding) not modeled | Done: G3 `Hides` edge |
| D2 | Generic type construction tracking | Done: G7 `type_arguments_json` on `MayDispatchTo` |
| D3/D4 | Overloaded binary operators / user-defined cast conversions | Done: Order 13 (`Calls` edges) |
| D5 | Indexer access not distinguished from method calls | Done: G5 `Reads`/`Writes` edges |
| D6 | Extension method binding edges | Done: G6 `ExtensionReceiver` edge |

## Architecture alignment and recorded deviations

| Architecture reference | Status | Evidence-based reading |
|---|---|---|
| §3.4 logical `facts` table | Decided deviation | Attributes stored in `metadata_json`; the facts table is deliberately not built. The approved contract preserves attribute identity and multiplicity. |
| §5.6 schema stability | Aligned | `SchemaStabilityTests.MigrationListAndSchemaConstant_AreConsistent` binds the migration list to `VersionConstants.DatabaseSchemaVersion` (count + uniqueness); `CheckedInPriorV22Fixture_UpgradesToCurrentSchema` upgrades the checked-in v22 schema fixture; `UnknownPersistedSymbolKind_DeserializesAsUnknown`. Caveat: the prior-version fixture is a SQL recreation of the v22 schema, not a checked-in binary database image: it validates migration SQL, not real persisted bytes. |
| §6 Stage A0 identity | Aligned, confirmed | `document_id` is path-scoped per the architecture's own definition; order-4 decision D1 made this explicit rather than building snapshot-scoped identity. |
| §15 structured semantic diffs | Aligned | Base type, signature, attributes, interfaces, record status, type kind, and persisted declaration/binding modifiers have producer/consumer coverage. `ImpactHandler` passes `SemanticDiffStore` to `ImpactTraverser`, which attaches persisted changes as `semantic_causes` on every returned impact path. Order 13 verifies operator/conversion `Calls` edges surface via the existing edge diff and converge between incremental and full snapshots. |
| §16 generated semantics | Aligned, narrow scope | Existing detection/identity/`IsCrossGenerated` plumbing used (encoding, short-file, structural-edge provenance gaps fixed); `GeneratorDriver` generation and package-metadata detection remain postponed. `SnapshotCompleteness.GeneratedTreesIncluded` is `false` because all workspace generated files are under `obj/`, which `IsBuildOutputPath` excludes. A file injected outside `obj/` is proven end-to-end to be indexed correctly. |
| §22 atomic incremental operation | Aligned | Snapshot cleanup is atomic, equivalence comparisons are stronger, edge dedup is implemented. Full suite passed 167/167 at time of record. |
| §25 outcome validation | Done | Benchmark contract, runner, and baseline all exist and run clean; `missingRelevantTests: false` for all scenarios (Phase 12 fix, commit `63cfaf4`). |

### Order 13: approved dispatch/diff capability

Narrow scope D3/D4: capture compiler-resolved overloaded binary operators and
user-defined cast conversions in the existing `Calls` edge contract.
`CallsEdgeExtractor` keeps built-in operators out of the graph, deduplicates
multiple call sites with the source/target/kind key, and records the syntax
location through the existing edge-location path. Validation at three levels:
extractor contracts (`op_Addition`/`op_Explicit` edges), `SemanticDiffer_EdgeAddedAndRemoved`
(canonical edge diff), `IncrementalIndex_Matches_FullRebuild_AfterOperatorAndConversionCallEdit`
(equivalence across symbols, declarations, edges, diagnostics, annotations, FTS).

### Post-order gap register: G1–G7

| Gap | Implemented behavior | Status |
|---|---|---|
| G1 metadata producer/consumer contract | `BuildMetadataJson` persists sorted implemented-interface FQNs under `interfaces`; `CompareMetadata` emits `interfaces_changed`, `record_changed`, and `metadata_changed` (with the changed field) for `typeKind` + declaration/binding modifiers (`isAbstract`, `isVirtual`, `isOverride`, `isStatic`, `isAsync`, `isExtensionMethod`, `isReadOnly`, `isWriteOnly`, `isConst`, `isVolatile`). `returnType` and callable-member arity intentionally stay covered by the canonical signature; type arity changes symbol identity. | Done 2026-07-27 (`MetadataContractTests` 11/11, `SemanticDiffer` 14/14) |
| G2 semantic cause in impact explanations | `ISemanticDiffStore` supports snapshot-targeted reads; `ImpactHandler` supplies it to `ImpactTraverser`; each impact path carries `semantic_causes` (change type, originating snapshot, structured detail). | Done 2026-07-27 (`MigrationRunnerTests` 152/152 incl. `TraceImpact_SemanticChanges_ExplainCauseOfDownstreamImpact`) |
| G3 `new` member-hiding (`Hides` edge) | `OverridesEdgeExtractor` emits a distinct `Hides` edge (methods: parameter-compatible, non-override, non-constructor; properties: non-override) to the hidden member in the immediate base type. `Hides` in `EdgeKind`, `hides-v1` in `ExtractorConstants`, both in `ExtractorRegistry.All`. Overrides emit only `Overrides`; overloads with different parameter types emit none. | Done 2026-07-27 (4 extraction tests; `MigrationRunnerTests` 190/190) |
| G5 indexer Reads/Writes edges | `CallsEdgeExtractor` resolves `ElementAccessExpressionSyntax` to the indexer property and emits `Reads` (get contexts) or `Writes` (assignment LHS, pre/post increment/decrement, `ref`/`out` argument contexts) instead of flattening to `Calls`. Dedup by source/target/kind. | Done 2026-07-27 (3 extraction tests) |
| G6 extension-method receiver edge | `CallsEdgeExtractor.AddCallEdge` detects instance-style extension call syntax (`callee.ReducedFrom != null`) and emits `ExtensionReceiver` from the receiver type to the extension method alongside the compiler-proved `Calls` edge; static-call syntax emits none. `extension-receiver-v1` in `ExtractorConstants`. | Done 2026-07-27 (2 extraction tests; `MigrationRunnerTests` 195/195) |
| G7 generic type-argument evidence on dispatch edges | `InterfaceDispatchExtractor.GetTypeArgumentsJson` serializes concrete type arguments of constructed generic interfaces (`IRepository<Customer>` → `["Customer"]`, multi-param in order); persisted in the `type_arguments_json` column. `VirtualOverrideExtractor` passes none (virtual dispatch has no generic construction). | Done 2026-07-28 (5 extraction tests) |

## Source-generator support: reassessed 2026-07-28

Observed facts:

1. No source-generator packages exist in any `.csproj` in this workspace.
2. All `.g.cs` files are under `obj/` — `.GlobalUsings.g.cs` implicit-usings
   output from the compiler, not source-generator output.
3. `IsBuildOutputPath` filters every file under `obj/` (and `bin/`) before
   generated-code detection logic runs.
4. `GeneratedTreesIncluded` is always `false`, hardcoded in
   `SnapshotManifest.FromWorkspace` and `.FromStorageManifest`.
5. The plumbing is proven correct: `GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges`
   injects a `.g.cs` file outside `obj/` and proves end-to-end that
   declarations are marked generated and edges flagged cross-generated —
   but only via artificial injection, since no real generated file exists
   outside `obj/`.

Decision: `GeneratedTreesIncluded = false` is **accurate, not a gap**: it
honestly declares that no generated trees are included because there are none.
`GeneratorDriver`-based source-generator execution remains correctly postponed;
no changes to `SnapshotManifest` or `SnapshotCompleteness` are warranted.

Revisit conditions (unchanged from the original postponement): a NuGet
source-generator package is added to any project in the workspace, or a
generated file appears outside `obj/` carrying meaningful symbols not already
surfaced by the existing header-based detection.

## Open findings for follow-up

1. **`search` output cannot round-trip directly into `context`/`impact`/
   `simulate-*`.** `search` prints a fully-qualified name; those commands need
   the `docCommentId|assemblyIdentity` form only `find-symbol` returns. Every
   driving-session query needed a `search` → `find-symbol` → `context`
   round-trip; this mismatch was the structural cause of the `--mode=context`
   `FormatException` crash now guarded by `ContextHandler.ValidateSymbolIdFormat`
   (change log, 2026-08-04). Two shapes considered, neither implemented:
   `search` emitting the resolvable form directly, or a `search --resolve`
   variant that does the round trip server-side. Decide whether
   `context`/`impact`/`simulate-*` should accept a bare FQN or file+line and
   resolve the symbol ID internally.
2. **`--mode=impact --output=summary`'s per-group lines name caller/callee
    symbol IDs but not human-readable names.** Each row shows
    `first_hop_source_symbol_id → first_hop_target_symbol_id [edge_kind]` plus a
    count and provenance, so fan-out structure is visible (`ImpactHandler.WriteSummary`).
    What's missing is the human-readable fully-qualified name alongside the opaque
    symbol ID. Low severity; `--output=json` remains the richer view.
3. **`ContextBudgeter` tier costing is still source-only.**
   (Now `src/Workspace/ContextBudgeter.cs`, extracted from `ContextAssembler`.)
   `EstimateTokens(item.Source)` charges nothing for path-shaped tiers that
   carry no `Source`. Not observable in emitted capsules (`CapsuleBudgetEnforcer`
   re-measures the settled artifact and is the authority), but tier-level
   greedy-prefix decisions are made on an incomplete measure, so tier
   *selection* can be skewed. Low severity.
4. **Capsule budget defaults exhaust quickly on type-level anchors.** A
   mid-sized service type (12 methods) at `--budget=4000` zeroes out
   `directCallers`, `relevantTests`, and every path/topology section. Not
   incorrect (`budget_exhausted` is honestly reported), but `--tier=`
   follow-ups are the norm above method granularity. Decide whether the
   default budget should scale with anchor kind or type anchors should
   auto-tier.
5. **`estimatedTokens` vs `estimatedArtifactTokens` differ roughly 2×.** The
   number a caller budgets against isn't the number that governs what's
   actually loaded. Documented as a deliberate, evidenced design choice
   (change log, "Capsule contract honesty pass"); re-confirmed still true,
   not reopened.
6. **Temporary capsule-output artifacts are stale and pre-fix: do not treat
   them as current evidence.** Every capsule there came from a reference-less
   compilation the workspace gate now refuses. Regenerate the folder against
   restored solutions before using it to judge output quality.
7. **`EffectiveSymbolIds` design question.** Whether
   `ContextTierContext.EffectiveSymbolIds` should include dispatching
   interface members, or the builder should walk incoming `MayDispatchTo`
   edges — recorded, not acted on. (The dispatch-caller walk itself is
   implemented and tested: `CapsuleProvenanceCompositionTests`; see change
   log, "Capsule dispatch-provenance fix".)
8. **MusicLibrary dispatch reproduction is permanently deferred.** Externally
   blocked (no MusicLibrary checkout); recorded as deferred, not open work.

## Explicitly postponed

- Multi-TFM per-framework indexing.
- DI adapter matching by parameter type instead of containing-type name.
- Concurrency, daemon, or server architecture.
- Explicit source-generator execution with a `GeneratorDriver`.

## Declared boundaries registry (capsule audit Task 7)

**Source:** `src/Workspace/DeclaredBoundaries.cs`

This is the closed, single-source registry of construct classes Lurp
deliberately does not model. The `RegisteredImplementationsTierBuilder`
reports `"unmodeled_construct"` (not `"empty"`) when detected, and the
`UncertaintyDetector` derives its runtime-unknown uncertainty descriptions
from this registry.

**Policy:** encountering a new unmodeled construct in the wild → add a
registry entry here, not a new extractor. Extractor work requires an
explicit scope decision recorded in TRUST_KERNEL.md §Declared boundaries registry.

| Id | Construct Class | Description |
|---|---|---|
| `di_hosted_service` | `AddHostedService<T>` | Hosted-service lifecycle registration — concrete type resolved, activation semantics not captured |
| `di_options` | `Configure<T>` / `AddOptions<T>` | Options-pattern registration — options type resolved, configuration-binding semantics not captured |
| `di_external_extension` | External IServiceCollection extension methods | External DI extension method detected — source outside compilation, registration semantics unknown |
| `masstransit_consumer` | MassTransit consumer registration | No MassTransit adapter exists — consumer wiring edges never emitted |
| `ef_convention` | EF Core model conventions beyond query filters and indexes | Fluent API model building (IsRequired, HasMaxLength, HasDefaultSchema, etc.) not modeled |
| `shape_similarity` | Semantic sibling similarity | Similarity between implementations (shared collaborator sets, name patterns) has no compiler oracle and no verifiable completeness claim — deliberately not modeled (capsule audit Task 9, Option B) |

## Reclassified as done (2026-08-05)

- **Deterministic snapshot IDs.** `SnapshotIdentity.Create(workspaceInfo, skipAdapters)` is the production default in `IndexRunner.RunFullIndexAsync` (`src/Workspace/IndexRunner.cs:109`). `SnapshotId.New()` (random GUID) is used only in tests. The infrastructure is complete: `SnapshotIdentityInput`, `SnapshotId.CreateDeterministic`, `SnapshotIdentity.BuildPayload` (length-prefixed, ordinally sorted, every field hashed), and `DeterministicSnapshotTests`.
- **`SqliteIndexStore` decomposition.** `SqliteIndexStore` (277 lines) is now a connection/ownership facade. All real logic lives in decomposed stores: `SnapshotLifecycleStore`, `SnapshotDocumentStore`, `SnapshotSymbolStore`, `SnapshotPruner`, `SnapshotTimingStore`, `DeclarationWriteStore`, `DeclarationReadStore`, `DeclarationMaintenanceStore`, `EdgeOperationsStore`, `DiagnosticStore`, `AnnotationStore`, `ExtractorRegistryStore`, `SearchSourceStore`, `SearchSymbolStore`, `SearchIndexMaintenance`, `SemanticDiffStore`, `BindingIncompletenessStore`. The former `EdgeStore`/`SearchStore` middle facades were removed on 2026-08-05; `SqliteIndexStore` now forwards to the leaf stores directly.

## Reclassified as done (2026-08-05)

- **Deterministic snapshot IDs.** `SnapshotIdentity.Create(workspaceInfo, skipAdapters)` is the production default in `IndexRunner.RunFullIndexAsync` (`src/Workspace/IndexRunner.cs:109`). `SnapshotId.New()` (random GUID) is used only in tests. The infrastructure is complete: `SnapshotIdentityInput`, `SnapshotId.CreateDeterministic`, `SnapshotIdentity.BuildPayload` (length-prefixed, ordinally sorted, every field hashed), and `DeterministicSnapshotTests`.
- **`SqliteIndexStore` decomposition.** `SqliteIndexStore` (277 lines) is now a connection/ownership facade. All real logic lives in decomposed stores: `SnapshotLifecycleStore`, `SnapshotDocumentStore`, `SnapshotSymbolStore`, `SnapshotPruner`, `SnapshotTimingStore`, `DeclarationWriteStore`, `DeclarationReadStore`, `DeclarationMaintenanceStore`, `EdgeOperationsStore`, `DiagnosticStore`, `AnnotationStore`, `ExtractorRegistryStore`, `SearchSourceStore`, `SearchSymbolStore`, `SearchIndexMaintenance`, `SemanticDiffStore`, `BindingIncompletenessStore`. The former `EdgeStore`/`SearchStore` middle facades were removed on 2026-08-05; `SqliteIndexStore` now forwards to the leaf stores directly.


