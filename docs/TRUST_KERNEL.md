# Trust Kernel: Implementation Status

**Status:** implementation-status reference. Compare with `CLAUDE.md` for
architecture, then inspect the relevant live code. There is no live
task queue in this repository.
**Architecture:** `docs/LURP_ARCHITECTURE.md` (design rules, boundaries, layout).
**Historical records:** superseded work is recorded in git history and the
inline change-log notes; each row below states the resulting current state.

**Test-suite status:** the entire `tests/` tree was removed in the Aug 2026
cleanup (commit `f1254fc`) along with the audit/simulate withdrawal —
`tests/Lurp.Tests.csproj` and all fixtures (`fixtures/Sample/` Widget,
`fixtures/OutcomeBenchmark/`, `fixtures/schema/prior-v22.sql`,
`benchmark-runs/baseline.json`). It has since been rebuilt: `tests/Lurp.Tests.csproj`
and the test suite are present again and run. Citations below that name a
current test file are therefore runnable evidence; citations marked
"(deleted, `f1254fc`)" denote tests that were removed and have **not** been
re-added, so the verification they anchored is not currently reproducible.
The outcome-benchmark suite (`fixtures/OutcomeBenchmark/`,
`benchmark-runs/baseline.json`) remains postponed.

---

## Purpose

This file records implementation status backed by git history and the
targeted tests cited below. The `tests/` tree was removed in the Aug 2026
cleanup (commit `f1254fc`) and has since been rebuilt (see test-suite status
above): citations that name a current test file are runnable evidence, while
citations marked "(deleted, `f1254fc`)" denote tests that were removed and have
not been re-added. Not by any single audit run's scratch output.
Earlier revisions cited `task/findings.md` as an evidence source; that file
was a one-off audit artifact, was never durable, and does not exist in this
folder. Do not recreate it or treat any future per-run scratch file the same
way: evidence here cites git commits and named tests directly.

## Two numbering schemes in this folder: read this before touching either

- **T1–T12**: a flat, now-closed list from the original trust audit. All
  twelve items were implemented and validated at time of record: the evidence
  is inlined below. (An earlier per-iteration `task/task_list.txt` scratch file
  existed briefly and has been removed; it was never durable and its content is
  fully captured here.) This list is not extended going forward: there is no
  T13.
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
| 2: Establish outcome-benchmark baseline | Done | commit `452fdaa`; `tests/benchmark-runs/baseline.json` (deleted, `f1254fc`) |
| 3: Track A vs. Track B decision | Done | Track A chosen; completion is recorded here. |
| 4: Document/snapshot identity | Done | commit `f550fee` (`ux_snapshot_documents` unique index, `document_versions` immutability trigger, D4/D5 negative tests in `CapsuleCharacterizationTests.cs`: `Capsule_UnresolvedTier_WhenAnchorRegionHasLostBindings`, `Capsule_GapAnchor_MarksEveryTierUnresolved`) |
| 5: Declaration lookup and partial types | Done | commit `8199760`; partial-type declaration coverage in `IncrementalParityTests.cs` (`ScenarioI_PartialTypeSpanningFiles_EditOnePartKeepsOtherPartEdges`) and declaration source in `StoreReadPathTests.cs` (`GetSymbolSource_ReturnsDeclarationSpanText`) |
| 6: Fast-travel reads and lexical search | Done | `FastTravelQueries`, persisted-span navigation in `DeclarationMaintenanceStore`, `navigate` handler; `SearchSourceStore.SearchSource` lexical search hardened by commit `994429c` (per-snapshot document-version-scoped generated-code exclusion, empty-query/non-positive-limit guards, path dedup). Read-path coverage in `StoreReadPathTests.cs`: declaration source, incoming/outgoing edges, symbol search, and the blind-project incompleteness contract |
| 7: Migrate dirty state, fingerprints, diagnostics, and facts out of JSON (removes dual JSON/SQLite authority) | Done | Already SQLite-only; the sole remaining `--output-json=`/`SnapshotManifest.Load` surface is a documented, tested, one-way export never consulted as an authority source. |
| 8: Facts-table decision | Done | Facts table rejected; attributes stay in `metadata_json` with identity/multiplicity preserved. |
| 9 | Not applicable | Conditional on order 8 approving the facts table; order 8 did not, so there is no implementation work |
| 10: Generated-code discovery | Done | Dual-path detection and provenance were traced; all workspace generated files are under `obj/` and therefore out of index scope. |
| 11: Generated-code provenance decision/implementation | Done | Existing plumbing was used; no `GeneratorDriver` or package detection. Encoding/short-file detection and structural-edge `IsCrossGenerated` propagation were fixed. The end-to-end `.g.cs` injection proof (`GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges`) was deleted with the suite (`f1254fc`) and has not been re-added; that end-to-end proof is not currently reproducible. |
| 12: Phase 9/10 gap audit | Done | Evidence-backed backlog of uncovered polymorphism/dispatch and structured-diff cases (see "Order 12 findings" below). The four `SemanticDiffer` `SignatureFormat` characterization tests (nullable annotations, ref/out/in modifiers, operator overloads, conversion operators) were deleted with the suite (`f1254fc`) and have not been re-added; that coverage is not currently reproducible. |
| 13: Phase 9/10 implementation | Done | `CallsEdgeExtractor` now records compiler-resolved overloaded binary operators and user-defined cast conversions as canonical `Calls` edges. `Calls` edge extraction is covered by `GoldenEdgeTests.cs` (`CallsEdge_MethodCallsMethod`); incremental-versus-full parity is covered by `IncrementalParityTests.cs` and `CleanRebuildEquivalenceTest.cs`. The dedicated operator/conversion `Calls` tests (`Calls_OverloadedBinaryOperator_EmitsCallsEdge`, `Calls_UserDefinedConversion_EmitsCallsEdge`, `SemanticDiffer_EdgeAddedAndRemoved`, `IncrementalIndex_Matches_FullRebuild_AfterOperatorAndConversionCallEdit`) were deleted with the suite (`f1254fc`) and have not been re-added. |

## Architecture Phase completion status (§23 roadmap)

| Phase | Description | Status | Evidence |
|---|---|---|---|
| 1 | Product constitution and schema/version rules | ✅ Done | `VersionConstants`, `MigrationRunner` |
| 2 | Workspace, snapshot, document, and configuration identities | ✅ Done | Order 4 |
| 3 | SQLite storage boundary and migrations | ✅ Done | `SqliteIndexStore`, 27 migrations; `SchemaMigrationRoundTripTests.cs` binds the migration list to `VersionConstants` (`MigrationList_CountIs27`, `MigrationList_HighestVersionMatches_DatabaseSchemaVersion`, `MigrationList_AllVersionsAreUnique`), round-trips all migrations to the current schema (`RoundTrip_AllMigrations_ProducesCurrentSchema`), and forward-migrates a seeded v1 fixture (`ForwardMigration_FromV1Schema_PreservesSeededData`) |
| 4 | Immutable document versions and source storage | ✅ Done | Order 4, T12 |
| 5 | Stable type/member identities and declaration spans | ✅ Done | Order 5 |
| 6 | Fast `get` and lexical `search` queries | ✅ Done | Order 6 |
| 7 | Migrate dirty state, fingerprints, diagnostics, and existing facts | ✅ Done | Order 7 |
| 8 | Typed member-level semantic edges | ✅ Done | Order 8 decision + Order 13 + G1–G7 |
| 9 | Polymorphism and dispatch candidates | ✅ Done | Order 12 audit + Order 13 + G3, G5, G6, G7 |
| 10 | Structured semantic snapshot diffs | ✅ Done | Order 12 + G1, G2 |
| 11 | Generated-code provenance | ✅ Done | Orders 10, 11 |
| 12 | ASP.NET, DI, MediatR, EF, serialization, and test adapters | ✅ Done | All 6 adapters exist; `TestedBy` granularity fix in `RelevantTestsTierBuilder` (commit `63cfaf4`); `TestedBy` emission covered by `GoldenAdapterTests.cs` (`TestAdapter_TestProjectProducesTestedBy`). The outcome-benchmark tests (`FullIndex_OutcomeBenchmark_EmitsTestedByOnProductionType`, `RunBaseline_WritesOutcomeEvaluation`) were deleted with the suite (`f1254fc`) and have not been re-added. |
| 13 | Reflection evidence ladder | ✅ Done | See "Phase 13 verification" below |
| 14 | Evidence-backed impact paths | ✅ Done | `ImpactTraverser`, `ImpactHandler`, semantic_causes |
| 15 | Context capsules with source and token budgets | ✅ Done | See "Phase 15 verification" below |
| 16 | Rebase simulations and audits on the shared store | ✅ Done → Removed in Aug 2026 cleanup. All simulate/audit handlers and engines were deleted as a category. |
| 17 | Optimize incremental updates from measurements | ✅ Done | Per-extractor elapsed time and current-thread allocations emitted by `CompilationFactExtractor`. A matched self-host measurement identified `CallsEdgeExtractor`/`ReadsWritesEdgeExtractor` as dominant; single-pass call/operator/cast/indexer traversal plus cached method enumeration reduced `CallsEdgeExtractor` 2051→1944 ms (Lurp), 217→182 ms (Storage), 427→418 ms (tests) — measurements at time of record. A broader node-cache candidate was measured and rejected. Member edge extraction is covered by `GoldenEdgeTests.cs` (one test per compiler-proved edge kind). The dedicated `B1MemberEdgeExtractorTests` (22 tests) was deleted with the suite (`f1254fc`) and has not been re-added. |

### Phase 13 verification: Reflection Evidence Ladder

**Architecture §18 requirements:**

| Requirement | Edge Kind | Extractor | Tests |
|---|---|---|---|
| `typeof(T)` type reference | `ReflectionTypeRef` | `TypeOfReflectionExtractor` | `TypeOf_EmitsReflectionTypeRefEdge` |
| `nameof` member reference | `ReflectionMemberRef` | `NameOfReflectionExtractor` | `NameOf_EmitsReflectionMemberRefEdge` |
| String literal matching known name | `ReflectionNameCandidate` | `StringLiteralReflectionExtractor` | `StringLiteral_MatchingTypeName_EmitsNameCandidateEdge` |
| Runtime-unknown reflection target | `ReflectionTargetUnknown` | `UnknownPatternReflectionExtractor` | `TypeGetType_EmitsUnknownEdge`, `ActivatorCreateInstance_EmitsReflectionTargetUnknownEdge` |

- All extractors in `src/Workspace/`; registered in `ExtractorRegistry` as `"reflection-v1"`; covered by `GoldenReflectionTests.cs` (`ReflectionTypeRef_TypeofExpression`, `ReflectionMemberRef_NameofExpression`, `ReflectionNameCandidate_StringLiteralMatchingSymbolName`, `AllThreeReflectionKinds_FromOneSource`). The dedicated `tests/B6ReflectionExtractorTests.cs` (`MigrationRunnerTests.B6ReflectionTests`) was deleted with the suite (`f1254fc`) and has not been re-added. Integrated with `UncertaintyDetector` for capsule uncertainty reporting.

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
`budget_exhausted` or `empty`. Capsule behavior is covered by
`CapsuleCharacterizationTests.cs`: content-budget tiers (`Capsule_RespectsContentBudget_*`),
proved-absence empty tiers (`Capsule_EmptyTier_*`), unresolved tiers under lost bindings
(`Capsule_UnresolvedTier_*`), gap anchors (`Capsule_GapAnchor_MarksEveryTierUnresolved`),
and the budget-exhausted omitted-tier fetch path (`Capsule_OmittedTier_FetchCommandReturnsTheOmittedContent`).
The self-host `Lurp.slnx` contract proof (`ContextCapsuleAcceptanceTests.SelfHost_EdgeLocationResolver_CapsuleSatisfiesPhase15Contract`)
was deleted with the suite (`f1254fc`) and has not been re-added.

Closed capsule decisions:

- No occurrence multigraph: architecture §12/§13 defines one evidence-bearing
  relation edge, not exhaustive call-site storage. A future occurrence table
  would require an architecture amendment.
- No generated anchor narrative: architecture §24 keeps deterministic
  structured facts canonical. Consumer-authored prose is not stored as fact;
  source-authored XML documentation remains retrievable source evidence.
- Architectural constraints come from snapshot annotations. No second JSON
  authority was added.

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

Tests: `ImpactTraverserTests.cs` (cycle detection, maxDepth bounding, edge-kind filtering, direction control). `SemanticRenameIntegrationTests` was deleted with the suite (`f1254fc`) and has not been re-added.

Order 4's scope was narrowed by an audit before its D1–D5 sub-tasks were
written: immutable document versions already existed de facto (T12), and
"snapshot-scoped document identity" as literally worded was rejected as
inconsistent with `LURP_ARCHITECTURE.md` Stage A0 (decision D1). The remaining
work was schema-level immutability enforcement and a `snapshot_documents`
uniqueness constraint, both in commit `f550fee`.

### Architecture §26 definitive-version checklist

| Criterion | Evidence | Status |
|---|---|---|
| One SQLite database holds indexed workspace state | `SqliteIndexStore` and migrations 1–27 | ✅ |
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
| N/A (removed) | Removed: simulation/audit modes deleted Aug 2026 | — |
| Indexer never modifies source | Index and handler pipelines are read-only with respect to repository source | ✅ |

## T1–T12: closed, implemented, and validated

Each item below states the resulting current state. Citations that name a
current test file are runnable evidence; citations marked "(deleted,
`f1254fc`)" denote tests that were removed and have not been re-added, so the
verification they anchored is not currently reproducible.

### T1: Schema-version verification is authoritative

`VersionConstants.DatabaseSchemaVersion` is the sole reference for
migration-version assertions. Covered by `SchemaMigrationRoundTripTests.cs`
(`MigrationList_CountIs27`, `MigrationList_HighestVersionMatches_DatabaseSchemaVersion`,
`MigrationList_AllVersionsAreUnique`, `RoundTrip_AllMigrations_ProducesCurrentSchema`).
The dedicated `MigrationRunnerTests` (124 tests) was deleted with the suite (`f1254fc`)
and has not been re-added.

### T2: Failed snapshot cleanup is atomic

`SnapshotPruner.DeleteSnapshotData(string)` deletes inside a transaction and
rolls back/rethrows on failure, matching `PruneWorkspace`/
`DeleteIncompleteSnapshots`. The delete-and-retry path is covered by
`SnapshotReuseResolutionTests.cs` (`IncompleteExistingRow_ResolvesRetry_AndIsDeleted`).
The trigger-induced mid-delete rollback regression test was deleted with the
suite (`f1254fc`) and has not been re-added. The "Storage suite: 139/139" figure
was a time-of-record count from the deleted suite.

### T3: Orphan edges are removed on both endpoints

`EdgeOperationsStore.DeleteOrphanEdges` removes an edge when either endpoint is absent
from the snapshot's `snapshot_symbols`. The orphan-edge contract is covered by
`KnownCorrectnessSeamTests.cs` (`GenericBaseInstantiation_InheritsEdgeTargetsOriginalDefinition` —
the `OriginalDefinition` normalization keeps in-snapshot edges from being dropped).
The dedicated `FullIndex_Has_No_Orphan_Edge_Targets` was deleted with the suite (`f1254fc`)
and has not been re-added; the deletion path runs in every full-index pipeline invocation.

Orphan drops are classified into three buckets (returned as
`OrphanEdgeDropSummary`):

- **Compiler-synthesized**: the missing endpoint id contains `<` (closures,
  iterator state machines, backing fields — `<>c__DisplayClass...`,
  `<Foo>b__0`, `<Foo>d__3`, `k__BackingField`).
- **External**: the missing endpoint's assembly-identity suffix is not among
  the in-scope assemblies for this snapshot.
- **Other**: everything else — a real in-scope declaration that vanished. Only
  this bucket is actionable; a nonzero `other` count prints a warning.

The eNoteV2 case (2026-08-07, extractor 1.6.0): a clean full rebuild of the
7-project solution (402 documents, 3,656 declarations) produced 0 residual
`other` orphan drops after the `OriginalDefinition` normalization fix. 8,608
edges were dropped as out-of-scope (8,428 external, 180 compiler-synthesized).
10,544 edges retained in-snapshot across 23 edge kinds (90.2% compiler_proved,
8.9% framework_derived). Binding incompleteness: 26,727 records, of which
26,640 are `filtered_external` and 87 `unsupported_syntax`.

2026-08-07 (extractor 1.5.0): `SymbolIdFactory.Make` now normalizes symbols to
`OriginalDefinition` (and un-reduces extension methods via `ReducedFrom`) before
building the ID. Previously, edge endpoints carrying constructed-generic IDs
(`T:Base{System.Int32}`) or reduced extension-method IDs (receiver parameter
omitted) could never match a snapshot member, so `DeleteOrphanEdges` silently
removed real relationships — measured on a 7-project solution: `Inherits` to an
internal generic base and `Calls`/`ExtensionReceiver` to user-written extension
methods were absent from every snapshot. The normalization is covered by
`SymbolIdentityTests.cs` (symbol IDs are deterministic and stable across re-indexing
and workspace reloads). The dedicated `SymbolIdNormalizationTests` (4 tests) was deleted
with the suite (`f1254fc`) and has not been re-added. The audited re-index measured
internal non-synthesized orphan drops falling from 1,345 to 2, both legitimately outside
the declared universe. Instantiation detail, where a consumer needs it, remains
in `edges.type_arguments_json`.

### T4: Cross-project edge-relation deduplication and merge-on-write

`EdgeOperationsStore.SaveEdges` performs in-batch pre-merge via
`EdgeMerge.CollapseBatch` (provenance priority: `compiler_proved` >
`framework_derived` > `global_implementation_relation` > `possible` >
`convention` > `name_candidate` > `runtime_unknown`), then splits the batch by
type-argument presence:

- **Bulk path** (edges with null `type_arguments_json`, the ~99.95% majority):
  `INSERT … ON CONFLICT(snapshot_id, source_symbol_id, target_symbol_id, kind)
  DO UPDATE SET` covering all winner-takes-all columns (provenance, extractor
  version, document path, 4 position columns, is_cross_generated, receiver
  type constraints) — strictly `>` rank comparison, ties keep the persisted
  row. `type_arguments_json` is **not** in the bulk DO UPDATE SET; the
  persisted value is always preserved when the incoming edge carries null type
  args (non-destructive guarantee, pinned by
  `SaveEdges_IncomingNullTypeArgs_DoesNotOverwritePersistedTypeArgs`).

- **Split path** (edges carrying non-null `type_arguments_json`, ~0.05%):
  individual SELECT of the existing persisted type args, merging via
  `EdgeMerge.MergeTypeArguments` (dedup+resort), then UPDATE with merged type
  args and winner-takes-all columns.

The unique index `ux_edges_relation` is unchanged. The caller-side pre-collapse
`EdgeDedup.Deduplicate` remains at `IndexRunner.cs:264` as a redundant guard
that shares `EdgeMerge.CollapseBatch`; it reduces SQL round-trips on collision-
heavy full builds and the two-callers hazard is resolved by the shared
implementation.

Binding tests (2026-08-10, `EdgeWritePathTests.cs`):
`SaveEdges_SameTripleDifferentProvenance_KeepsHigherRank`,
`SaveEdges_SameTripleDifferentTypeArguments_MergesVariants`,
`SaveEdges_CalledTwiceAcrossBatches_MergesAgainstPersistedRow`,
`SaveEdges_UnknownProvenance_NeverBeatsCanonical`,
`SaveEdges_FlatTypeArgumentEncoding_NormalizesToNested`,
`SaveEdges_IncomingNullTypeArgs_DoesNotOverwritePersistedTypeArgs`,
`SaveEdges_CrossProjectReemit_MergesAgainstCopiedForwardRow`.

**Known narrow parity gap (equal-rank tie-break):** the strictly `>` rank
comparison means equal-rank collisions keep the persisted / copied-forward row,
which on a clean rebuild would be whatever allEdges accumulation order produced
first. Positions can differ in principle. This has not been observed on real
code; `CleanRebuildEquivalenceTest` is the backstop.

**Freshness-scope:** the hash-based freshness detector (`WorkspaceFreshness`) observes
file content, compilation inputs (package references, project references, define
constants, analyzer config), and extractor version. When all are unchanged against
the previous snapshot identity, the short-circuit avoids a full extraction.

### T5–T8: Semantic-diff metadata has a producer/consumer contract

`BuildMetadataJson` writes `base_type` (null for interfaces/`System.Object`),
canonical callable-member `signature`, and sorted `attributes` (type presence
plus constructor/named-argument values: attributes stay in `metadata_json`,
not the unimplemented `facts` table from architecture §3.4). `CompareMetadata`
consumes all of it. The producer/consumer contract test for metadata keys
(T5: 7, T6: 8, T7: 12, T8: 8 tests) was deleted with the suite (`f1254fc`) and
has not been re-added; that coverage is not currently reproducible.

### T9: Generated-output absence is declared and reported

`MSBuildWorkspace.OpenSolutionAsync` does not expose source-generator output;
snapshot completeness persists `generated_trees_included`, active TFMs,
skipped adapters, and extractor version, surfaced via `status --json`.
Generated semantics remain declared absent: no `GeneratorDriver` work was
added (that's architecture Phase 11 / orders 10–11).

### T10: Incremental-versus-full equivalence coverage expanded

Snapshot comparison includes FQNs, metadata JSON, declaration
paths/spans/flags, and exact source/symbol FTS records. Cases covered
signature, body-only, document-move, partial-class, base/interface,
DI-registration, and new-overload-in-new-file edits (R3). Covered by
`IncrementalParityTests.cs` (phase-2 parity
matrix across delete-symbol, delete-file, rename, signature, base-type,
interface, cross-project, partial-type, new-implementation, generic-dispatch,
and new-overload-in-new-file
scenarios), `CleanRebuildEquivalenceTest.cs`, and `MultiCycleConvergenceTests.cs`.
The 167/167 figure was a time-of-record count from the deleted full suite.

### T11: Outcome benchmark existed and its baseline was established

The benchmark (local-validation, handler/DTO, DI-replacement scenarios) had a
fixture, machine-readable scenario contract, runner, and baseline JSON, and
recorded the ten architecture §25 measures without fabricating the
post-capsule worker-token measure. Two blockers, both fixed (commit `452fdaa`):
`LocateRepositoryRoot()` searched for a nonexistent `task/task_list.txt`
sentinel (replaced with the solution-file marker); `SearchSymbolStore` FQN lookups
didn't account for Roslyn's `global::` prefix (now accepts either form).
`OutcomeBenchmarkTests.RunBaseline_WritesOutcomeEvaluation` passed at time of
record and wrote `tests/benchmark-runs/baseline.json`. Fixture and scenario
contract lived in `tests/fixtures/OutcomeBenchmark/` (load-bearing test
infrastructure, so under `tests/` rather than `task/`). All of the above —
test class, baseline JSON, fixture — was deleted with the suite (`f1254fc`);
the benchmark is not currently reproducible. One capability gap surfaced,
tracked separately: both scenarios reported `missingRelevantTests: true` —
closed by the Phase 12 fix (commit `63cfaf4`).

### T12: Snapshot pruning reclaims document-version storage

Post-prune cleanup deletes unreferenced `document_versions`, deletes documents
with no retained versions, and repairs/clears dangling
`last_changed_snapshot_id` values inside the prune transaction (foreign-key
enforcement is not enabled on the store connection, so cleanup is explicit
rather than cascade-driven). `PruneOldSnapshots` is exercised by
`CleanRebuildEquivalenceTest.cs` and `IncrementalParityTests.cs`. The two
dedicated GC tests were deleted with the suite (`f1254fc`) and have not been re-added.

## Order 12 findings: Phase 9/10 gap audit

### Phase 10 (structured semantic diff): gaps found

**Already covered by `SignatureFormat` string comparison** (with
`IncludeNullableReferenceTypeModifier`, `IncludeParamsRefOut`,
`IncludeExplicitInterface`, `IncludeTypeConstraints`) — previously untested,
locked by characterization tests that were deleted with the suite (`f1254fc`) and
have not been re-added (not currently reproducible):

| ID | Case | Test (deleted, `f1254fc`) |
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
| §5.6 schema stability | Aligned | `SchemaMigrationRoundTripTests.cs` binds the migration list to `VersionConstants.DatabaseSchemaVersion` (count + uniqueness) via `MigrationList_CountIs27`, `MigrationList_HighestVersionMatches_DatabaseSchemaVersion`, and `MigrationList_AllVersionsAreUnique`; `RoundTrip_AllMigrations_ProducesCurrentSchema` round-trips all migrations to the current schema; `ForwardMigration_FromV1Schema_PreservesSeededData` forward-migrates a seeded v1 fixture. |
| §6 Stage A0 identity | Aligned, confirmed | `document_id` is path-scoped per the architecture's own definition; order-4 decision D1 made this explicit rather than building snapshot-scoped identity. |
| §15 structured semantic diffs | Aligned | Base type, signature, attributes, interfaces, record status, type kind, and persisted declaration/binding modifiers have producer/consumer coverage. `ImpactHandler` passes `SemanticDiffStore` to `ImpactTraverser`, which attaches persisted changes as `semantic_causes` on every returned impact path. Order 13 verified that operator/conversion `Calls` edges surface via the existing edge diff and converge between incremental and full snapshots: `Calls` extraction covered by `GoldenEdgeTests.cs`, incremental/full convergence by `IncrementalParityTests.cs` and `CleanRebuildEquivalenceTest.cs`. |
| §16 generated semantics | Aligned, narrow scope | Existing detection/identity/`IsCrossGenerated` plumbing used (encoding, short-file, structural-edge provenance gaps fixed); `GeneratorDriver` generation and package-metadata detection remain postponed. `SnapshotCompleteness.GeneratedTreesIncluded` is `false` because all workspace generated files are under `obj/`, which `IsBuildOutputPath` excludes. The end-to-end injected-`.g.cs` proof was deleted with the suite (`f1254fc`) and has not been re-added; that end-to-end proof is not currently reproducible. |
| §22 atomic incremental operation | Aligned | Snapshot cleanup is atomic, equivalence comparisons are stronger, edge dedup is implemented. Incremental-versus-full equivalence covered by `IncrementalParityTests.cs`, `CleanRebuildEquivalenceTest.cs`, and `MultiCycleConvergenceTests.cs`. |
| §25 outcome validation | Done at time of record; not currently reproducible | Benchmark contract, runner, and baseline all existed and ran clean (all deleted, `f1254fc`); `missingRelevantTests: false` for all scenarios was established by the Phase 12 fix (commit `63cfaf4`). |

### Order 13: approved dispatch/diff capability

Narrow scope D3/D4: capture compiler-resolved overloaded binary operators and
user-defined cast conversions in the existing `Calls` edge contract.
`CallsEdgeExtractor` keeps built-in operators out of the graph, deduplicates
multiple call sites with the source/target/kind key, and records the syntax
location through the existing edge-location path. Validation: `Calls` edge
extraction covered by `GoldenEdgeTests.cs` (`CallsEdge_MethodCallsMethod`);
incremental/full equivalence covered by `IncrementalParityTests.cs` and
`CleanRebuildEquivalenceTest.cs`. The dedicated Order 13 tests
(`op_Addition`/`op_Explicit` extractor contracts, `SemanticDiffer_EdgeAddedAndRemoved`,
`IncrementalIndex_Matches_FullRebuild_AfterOperatorAndConversionCallEdit`) were deleted
with the suite (`f1254fc`) and have not been re-added.

### Post-order gap register: G1–G7

| Gap | Implemented behavior | Status |
|---|---|---|
| G1 metadata producer/consumer contract | `BuildMetadataJson` persists sorted implemented-interface FQNs under `interfaces`; `CompareMetadata` emits `interfaces_changed`, `record_changed`, and `metadata_changed` (with the changed field) for `typeKind` + declaration/binding modifiers (`isAbstract`, `isVirtual`, `isOverride`, `isStatic`, `isAsync`, `isExtensionMethod`, `isReadOnly`, `isWriteOnly`, `isConst`, `isVolatile`). `returnType` and callable-member arity intentionally stay covered by the canonical signature; type arity changes symbol identity. | Done 2026-07-27. The `MetadataContractTests` (11/11) and `SemanticDiffer` (14/14) counts were time-of-record figures from the deleted suite; those dedicated tests have not been re-added. |
| G2 semantic cause in impact explanations | `ISemanticDiffStore` supports snapshot-targeted reads; `ImpactHandler` supplies it to `ImpactTraverser`; each impact path carries `semantic_causes` (change type, originating snapshot, structured detail). Impact traversal covered by `ImpactTraverserTests.cs`. | Done 2026-07-27. The `MigrationRunnerTests` (152/152) count was a time-of-record figure from the deleted suite. |
| G3 `new` member-hiding (`Hides` edge) | `OverridesEdgeExtractor` emits a distinct `Hides` edge (methods: parameter-compatible, non-override, non-constructor; properties: non-override) to the hidden member in the immediate base type. `Hides` in `EdgeKind`, `hides-v1` in `ExtractorConstants`, both in `ExtractorRegistry.All`. Overrides emit only `Overrides`; overloads with different parameter types emit none. `Hides` extraction covered by `GoldenEdgeTests.cs` (`HidesEdge_MethodHidesBaseMember`). | Done 2026-07-27. The 4 extraction tests and `MigrationRunnerTests` (190/190) count were time-of-record figures from the deleted suite. |
| G5 indexer Reads/Writes edges | `CallsEdgeExtractor` resolves `ElementAccessExpressionSyntax` to the indexer property and emits `Reads` (get contexts) or `Writes` (assignment LHS, pre/post increment/decrement, `ref`/`out` argument contexts) instead of flattening to `Calls`. Dedup by source/target/kind. Reads/Writes extraction covered by `GoldenEdgeTests.cs` (`ReadsEdge_MethodReadsField`, `WritesEdge_MethodWritesField`). | Done 2026-07-27. The 3 extraction tests were deleted with the suite (`f1254fc`) and have not been re-added. |
| G6 extension-method receiver edge | `CallsEdgeExtractor.AddCallEdge` detects instance-style extension call syntax (`callee.ReducedFrom != null`) and emits `ExtensionReceiver` from the receiver type to the extension method alongside the compiler-proved `Calls` edge; static-call syntax emits none. `extension-receiver-v1` in `ExtractorConstants`. Extension-receiver extraction covered by `GoldenEdgeTests.cs` (`ExtensionReceiverEdge_ReceiverTypeBindsToExtensionMethod`). | Done 2026-07-27. The 2 extraction tests and `MigrationRunnerTests` (195/195) count were time-of-record figures from the deleted suite. |
| G7 generic type-argument evidence on dispatch edges | `InterfaceDispatchExtractor.GetTypeArgumentsJson` serializes concrete type arguments of constructed generic interfaces (`IRepository<Customer>` → `["Customer"]`, multi-param in order); persisted in the `type_arguments_json` column. `VirtualOverrideExtractor` passes none (virtual dispatch has no generic construction). Generic-dispatch type-argument merging covered by `IncrementalParityTests.cs` (`Parity_GenericBaseWithMultipleConcreteImplementations_MergesDispatchTypeArguments`). | Done 2026-07-28. The 5 extraction tests were deleted with the suite (`f1254fc`) and has not been re-added. |

## Source-generator support: reassessed 2026-07-28

Observed facts:

1. No source-generator packages exist in any `.csproj` in this workspace.
2. All `.g.cs` files are under `obj/` — `.GlobalUsings.g.cs` implicit-usings
   output from the compiler, not source-generator output.
3. `IsBuildOutputPath` filters every file under `obj/` (and `bin/`) before
   generated-code detection logic runs.
4. `GeneratedTreesIncluded` is always `false`, hardcoded in
   `SnapshotManifest.FromWorkspace` and `.FromStorageManifest`.
5. The plumbing was proven correct at time of record:
   `GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges`
   injected a `.g.cs` file outside `obj/` and proved end-to-end that
   declarations are marked generated and edges flagged cross-generated —
   but only via artificial injection, since no real generated file exists
   outside `obj/`. That test was deleted with the suite (`f1254fc`); the
   end-to-end proof is not currently reproducible.

Decision: `GeneratedTreesIncluded = false` is **accurate, not a gap**: it
honestly declares that no generated trees are included because there are none.
`GeneratorDriver`-based source-generator execution remains correctly postponed;
no changes to `SnapshotManifest` or `SnapshotCompleteness` are warranted.

Revisit conditions (unchanged from the original postponement): a NuGet
source-generator package is added to any project in the workspace, or a
generated file appears outside `obj/` carrying meaningful symbols not already
surfaced by the existing header-based detection.

Update 2026-08-07 (Gap #9): the cross-generated flag now also reaches the
polymorphism and reflection lineages. `ExtractionContextBase` owns an
`EdgeLocationResolver` and exposes `IsGenerated(path)`; `MayDispatchTo`,
`StaticallyCalls`, and all four reflection edge kinds set `IsCrossGenerated`
from it — parity with the member-edge/adapter lineages, which already did.
`CompilationFactExtractor` feeds one shared document-id projection into the
poly/reflection contexts and the adapter resolver.

The generated *set* is authoritative by path, not merely heuristic:
`DocumentId.ToString()` is the git-relative document path (`Identity.cs`), the
same form `EdgeLocationResolver.IsGenerated` normalizes and looks up, so a file
`WorkspaceInfo` flagged generated by header (`<auto-generated>`,
`[GeneratedCode(`) — even without a `.g.cs`/`.Designer.cs` name — is honored
across all three lineages. The suffix/`/obj/`/`/generated/` checks are a
fallback on top of that set, not the only signal.

## Open findings for follow-up

1. **`search` output cannot round-trip directly into `context`/
   `impact`.** `search` prints a fully-qualified name; those commands need
   the `docCommentId|assemblyIdentity` form only `find-symbol` returns. Every
   driving-session query needed a `search` → `find-symbol` → `context`
   round-trip; this mismatch was the structural cause of the `--mode=context`
   `FormatException` crash now guarded by `ContextHandler.ValidateSymbolIdFormat`
   (change log, 2026-08-04). Two shapes considered, neither implemented:
   `search` emitting the resolvable form directly, or a `search --resolve`
   variant that does the round trip server-side. Decide whether
   `context`/`impact` should accept a bare FQN or file+line and
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
   mid-sized service type (12 methods) at `--content-budget=4000` zeroes out
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
    edges — recorded, not acted on. (The dispatch-caller walk itself was
    implemented and tested at time of record:
    `CapsuleProvenanceCompositionTests`, deleted with the suite, `f1254fc`;
    see change log, "Capsule dispatch-provenance fix".)
8. **MusicLibrary dispatch reproduction is permanently deferred.** Externally
    blocked (no MusicLibrary checkout); recorded as deferred, not open work.
9. **`impact` default `--max-depth=10` causes unbounded BFS on dense graphs.**
    (Resolved 2026-08-07.) `ImpactHandler.Run`
    (`src/Handlers/ImpactHandler.cs:34`) defaulted to depth 10 with no
    timeout or progress signal. On a graph with 10K+ edges, the BFS
    enumerates millions of paths before the pagination step. Observed hang
    (5+ min) against eNoteV2 at default depth; depth 2 returned 82 paths
    instantly. The `groups` aggregation was already designed to answer
    fan-out structure from first hops, making deep exhaustions unnecessary
    for the primary use case. Default lowered to 3.
10. **Incremental `binding_incompleteness.occurrence_count` undercounts
    out-of-scope documents in affected projects (REAL parity bug, from the R1
    FIT-RS2-2026 run; see the R1 section above). CLOSED (2026-08-11).** Fix
    applied at both incremental save sites: `IncrementalIndexer.ExtractReplacementFacts`
    and `CrossDocumentEdgeRefresher.ProcessCompilationsAsync` now filter
    `result.BindingIncompleteness` to the same document-path set used for the
    delete before calling `SaveBindingIncompleteness`. Null/empty-path records
    (doc-less aggregates) are excluded from the incremental save and carry
    forward their copied-forward values; a `--strategy=full` rebuild is the
    reference for that bucket. (Predicate correction 2026-08-11: the exclude was
    initially inverted — `IsNullOrEmpty || contains` KEPT the null bucket; both
    sites now use `DocumentPath is { } p && contains(p)`. See the R6 section.)
    The lockstep invariant ("extraction and deletion
    narrow in lockstep") is now enforced for binding incompleteness as it was
    already for edges and annotations. Regression test
    `ScenarioR6_BindingIncompleteness_IncrementalCarryForward` in
    `IncrementalParityTests.cs` verifies parity on a synthetic fixture with
    external-assembly references. See the R6 section below for full details.

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

- **Deterministic snapshot IDs.** `SnapshotIdentity.Create(workspaceInfo, skipAdapters)` is the production default in `IndexRunner.RunFullIndexAsync` (`src/Workspace/IndexRunner.cs:109`). `SnapshotId.New()` (random GUID) is used only in tests. The infrastructure is complete: `SnapshotIdentityInput`, `SnapshotId.CreateDeterministic`, `SnapshotIdentity.BuildPayload` (length-prefixed, ordinally sorted, every field hashed). Snapshot-identity behavior is covered by `SnapshotIdentityCompletenessTests.cs` (package-reference, define-constants, project-reference, extractor-version, and `--force` rebuild scenarios). The dedicated `DeterministicSnapshotTests` was deleted with the suite (`f1254fc`) and has not been re-added.
- **`SqliteIndexStore` decomposition.** `SqliteIndexStore` (277 lines) is now a connection/ownership facade. All real logic lives in decomposed stores: `SnapshotLifecycleStore`, `SnapshotDocumentStore`, `SnapshotSymbolStore`, `SnapshotPruner`, `SnapshotTimingStore`, `DeclarationWriteStore`, `DeclarationReadStore`, `DeclarationMaintenanceStore`, `EdgeOperationsStore`, `DiagnosticStore`, `AnnotationStore`, `ExtractorRegistryStore`, `SearchSourceStore`, `SearchSymbolStore`, `SearchIndexMaintenance`, `SemanticDiffStore`, `BindingIncompletenessStore`. The former `EdgeStore`/`SearchStore` middle facades were removed on 2026-08-05; `SqliteIndexStore` now forwards to the leaf stores directly.

## Reclassified as done (2026-08-07)

- **`--solution=` accepted on every mode.** `CliFlagValidation.GlobalFlags`
  gained `"--solution="` (all modes accept it). `HandlerBootstrap.ResolveOutputDir`
  falls back to the solution's directory when no `--output-dir=`,
  `LURP_OUTPUT_DIR`, or `INDEXER_OUTPUT_DIR` is set — a user who only knows
  `--solution=` from `index` now gets a working DB path instead of a rejection.
   Also accepted via env vars `LURP_SOLUTION_PATH` and `INDEXER_SOLUTION_PATH`.
- **`impact` default `--max-depth` lowered from 10 to 3.** (See finding 9.)

## Reclassified as done (2026-08-06)

- **Incremental re-extraction is document-scoped.** `IncrementalIndexer` no longer re-extracts every document of every affected project. `ExtractReplacementFacts` receives the extraction scope (changed documents ∪ reverse-edge closure ∪ every document declaring a part of a touched type), converted to the absolute forward-slash form the extraction guards compare against, and `PrepareSnapshotData` deletes over exactly the same set — extraction and deletion narrow in lockstep, so no fact is deleted for a document that is not re-extracted. All six adapters honor `AdapterExtractionContext.ScopeDocuments`; their re-emitted edges are merged by `SaveEdges`' `ON CONFLICT DO UPDATE` + `EdgeMerge.MergeTypeArguments`, and re-emitted annotations are deduplicated by retiring the copied-forward rows first (`DeleteAnnotationsByDocumentPaths`).
- **The cross-document refresh (step 7) is load-bearing again.** It is seeded from the genuinely-changed documents rather than from the invalidation set that had already absorbed the closure — the seeding bug that made `FindAffectedDocPaths` return ∅ while still charging for the walk. `RefreshAsync` subtracts the documents step 6 already re-extracted, so the two phases never re-extract the same document twice in one snapshot.
- **The closure sees absences, not just edges.** `FindAffectedDocPaths` seeds its frontier from the previous snapshot's `binding_incompleteness` rows whose reason is in `UnobservableReasons`. A document whose reference did not bind produced no edge, so a pure edge BFS had no arc to follow when an edit made it bind.
- **Adapters honor the extraction scope.** All six now skip out-of-scope work: `DependencyInjectionAdapter` and `SerializationAdapter` guard their `compilation.SyntaxTrees` loop with `IsInScope`, `TestAdapter` guards each declaring syntax reference, and `AspNetCoreAdapter`, `MediatRAdapter`, and `EfCoreAdapter` guard by declaring type via `AdapterExtractionContext.IsSymbolInScope`. In every case the guarded unit is the one the emitted edge (or annotation) is anchored to, so extraction and deletion stay on the same set. `EfCoreAdapter` annotations carry the evidence document of the walk that produced them (migration 26, `annotations.document_path`), and `IIndexStore.DeleteAnnotationsByDocumentPaths` retires copied-forward rows over exactly the extraction scope in `IncrementalIndexer.PrepareSnapshotData` and in the cross-document refresh — the lockstep invariant holds for annotations too.
- **Binding incompleteness is attributable to a document.** Implicitly-declared symbols (default constructor, record synthesized member, auto-property accessor) have no syntax of their own, and previously landed in a document-less bucket that was a whole-compilation aggregate: no document-scoped delete could retire it and no document-scoped re-extraction could reproduce it. `BindingIncompletenessCollector.DeclaringSyntaxOrContainingType` falls back to the containing type's declaring syntax, the same resolution used for null-path edges in `CrossDocumentEdgeRefresher`.
- **CLI document-path arguments accept the host separator.** Document paths are
  persisted forward-slashed (`Identity`, `src/Workspace/Identity.cs:181,197`), but
  the read handlers passed `--document=` / `--file=` through verbatim, so a Windows
  caller pasting a native path missed every stored document: `get-source` reported
  "not found in snapshot" and `navigate` reported "no indexed declaration contains"
  — both indistinguishable from a genuine absence. `HandlerBootstrap.NormalizeDocumentPath`
  converts the CLI form to the stored form at all three argument sites
(`GetSourceHandler`, `NavigateHandler`, `ContextHandler`'s file+line anchor).
The dedicated `tests/HandlerDocumentPathNormalizationTests.cs` was deleted with the
suite (`f1254fc`) and has not been re-added; found by driving the CLI against an
out-of-repo solution (eNoteV2, 7 projects, 3,656 declarations) — a historical run record.

## Reclassified as done (2026-08-10): snapshot identity covers compilation inputs

- **The deterministic snapshot identity hashes NuGet references and
  compilation options.** `SnapshotIdentityInput` gained
  `MetadataReferenceIdentities` (per-project, deduplicated, ordinally sorted
  `AssemblyName.GetAssemblyName` full names, each folded with a SHA-256 of the
  reference file's bytes — see R2 below; file-name+length+mtime fallback
  when the assembly header is unreadable) and `CompilationOptionsFingerprints`
  (optimization, allow-unsafe, nullable context, platform, language version,
  sorted preprocessor symbols). A package version bump or a `<DefineConstants>`
  flip now changes `SnapshotId` and trips the full-rebuild gate
  (`CheckMetadataReferences`, `CheckCompilationOptions` in
  `WorkspaceFreshness`) instead of producing an identical id.
- **Recorded decision — pre-027 null semantics.** `projects.metadata_reference_identities`
  and `projects.compilation_options_fingerprint` are nullable (migration 27).
  A null value on a pre-027 snapshot means *unknown*, not *different*: the
  comparators only compare projects present in both the current workspace map
  and the stored manifest map, so pre-027 rows are skipped rather than
  reported as mismatches. Project add/remove is still reported by the TFM and
  project-graph comparators, so the skip does not mask a structural change.
- **`--force` re-extracts without bypassing identity.** When the deterministic
  snapshot id already exists as a complete snapshot, `--force` deletes it and
  re-extracts (full strategy); the id stays identical because the workspace is
  genuinely identical. It does NOT bypass identity computation.

## Verified against eNoteV2 (2026-08-07, extractor 1.6.0)

A clean full rebuild followed by targeted handler runs against the 7-project
eNoteV2 solution confirmed the following capabilities end-to-end:

| Mode | Result |
|---|---|
| `index --strategy=full` | 3,656 declarations, 10,544 edges, 128 diagnostics, 22.3s |
| `index` (incremental, no-change) | 2.6s — hash freshness short-circuit works |
| `status --output=json` | Fresh, schema v26, all 6 adapters active, completeness declared |
| `search --query=Order` | Symbol + source results with snapshot-bound scoping |
| `context --symbol=... --output=summary` | Token-budgeted capsule (5189/8000 content tokens), tiers omit honestly |
| `impact --symbol=... --output=summary` | 4 paths, 3 distinct first hops, `MayDispatchTo` edges with provenance |
| `timings` | Per-step breakdown (extraction_loop 87.9% of total) |

Edge-kind coverage observed: 23 kinds in-snapshot, including `MayDispatchTo`,
`StaticallyCalls`, `RoutesTo`, `Registers`, `MapsTo`, `Overrides`,
`ExtensionReceiver`, `ReflectionTypeRef`, `ReflectionMemberRef`,
`ReflectionNameCandidate`, `ReflectionTargetUnknown` — confirming the
polymorphism, framework-adapter, and reflection evidence ladders are live.

## R1 — 5-cycle incremental convergence on real solutions (2026-08-11, extractor 1.6.0)

Five consecutive incremental edits were applied to scratchpad copies of two real
multi-project solutions, followed by a fresh full rebuild of the final state.
The 5th incremental snapshot (B5) was compared field-by-field against the clean
rebuild (C) using the same normalization as `SnapshotAssertions.CompareSnapshotsAreEquivalent`
(all persisted edge value columns — every column except snapshot_id, the partition key — declarations, symbols, diagnostics, annotations, FTS,
binding incompleteness).

### FIT-RS2-2026 — 4 projects, `eCommerce.sln`

| Edits |
|---|
| 1. Doc comment added to `IBaseReadService.GetByIdAsync` |
| 2. Optional `CancellationToken` parameter added to `IBaseReadService.GetAllAsync` |
| 3. `CancelAsync` method added to `IOrderService` + `OrderService` |
| 4. `DeleteAsync` method added to `IBaseReadService` + `BaseReadService` |
| 5. `Class1.cs` renamed to `ServiceExtensions.cs` with new content |

| Check | Result |
|---|---|
| Snapshot ID (B5 ≡ C) | `0e4f03607a6f70d7d653e20e24f96e42` — match |
| Symbols | 0 diffs |
| Declarations | 0 diffs |
| Edges (3,862) | 0 diffs across all persisted value columns |
| Diagnostics (409) | 0 diffs |
| Annotations | 0 diffs |
| FTS (source + symbol) | 0 diffs |
| Binding incompleteness (167) | **2 records diverge** |

Divergence: two `filtered_external` records undercount `occurrence_count` in the
incremental snapshot — `eCommerce.Services/Database/eCommerceConfiguration.cs`
(73 vs 130, lost 57) and `eCommerce.Services/Migrations/…IncreasePaymentTransactionIdLength.cs`
(8 vs 16, lost 8).

**Edge-column tooling fix (2026-08-11):** `r1-compare/Program.cs` `EdgeDiff` and
`NormalizeEdges` previously omitted the `extractor_version` column (not compared,
not in the sort tiebreak). Both are now included, and the FIT-RS2-2026 procedure
was re-run with the corrected comparator. Edges still report 0 diffs (3,862 edges,
snapshot IDs `cb7add0815145b1f5a1e91b7033e9920` match). The only divergence
remains the known R6 binding-incompleteness undercount. All "13 edge columns"
wording in this section was replaced with the column-agnostic
"all persisted value columns."

Note on FIT-RS2-2026 snapshot IDs across runs: this section records three
different final-state IDs for FIT-RS2-2026 — `0e4f0360…` (original 5-edit R1
run), `a6ac1bc8…` (5-cycle triage reproduction), and `cb7add08…` (edge-column
re-verify). These differ because each run applied a different edit set, so the
final states differ, so the deterministic IDs differ — not a contradiction. The
guarantee each run records is B ≡ C *within that run* (same ID for the 5th
incremental and the fresh full rebuild), and the edge count (3,862) is stable
across them.

Housekeeping (2026-08-11): stale external-solution artifacts under `out/`
(eNoteV2/FIT-RS2 `index.db` snapshots and capsule outputs from these runs) were
removed so later indexing runs and models are not confused by pre-fix databases.
The TokenSave code graph under `.tokensave/` is unrelated and was left intact.

**Triage (2026-08-11): this is a REAL incremental/full parity bug in the
binding-incompleteness carry-forward, now reproduced and minimized.**

- **Reproduction:** the recorded script re-run on a fresh scratchpad copy
  reproduced the identical divergence: deterministic snapshot ids equal
  (`a6ac1bc8…` both), `r1-compare` reports a single diff ("Binding
  incompleteness mismatch") and exits 1, with the same counts (73/8 vs 130/16).
- **Minimal edit:** EDIT 1 alone — a semantics-preserving doc comment on
  `eCommerce.Services/IBaseReadService.cs` — reproduces it. One incremental
  cycle after the initial full index suffices (`/tmp/r1-minimal-repro.sh`,
  2026-08-11): snapshot A = 130/16 → B1 = 73/8; a fresh full rebuild of the
  same final state = 130/16; `r1-compare` B1-vs-C' fails identically.
- **Per-cycle trajectory:** the undercount appears at cycle 1 and is constant —
  B1 = B5 = 73/8. It is **not cumulative**; the earlier "cumulative
  degradation across 5 cycles" phrasing was an inference without per-cycle
  data and is superseded.
- **Both documents are outside the extraction scope** in every cycle: not
  changed, not partial parts of types declared in changed documents, and not in
  the reverse-edge closure (closure narrowed, no fallback).
- **Root cause:** the per-project `SaveBindingIncompleteness` upsert
  (`BindingIncompletenessStore`, `ON CONFLICT(snapshot_id, project_name,
  document_path, reason) DO UPDATE SET occurrence_count = excluded.occurrence_count`)
  persists ALL records the affected project's re-extraction collector produced
  — including partial `filtered_external` counts for documents outside the
  deletion scope, whose rows were copied forward with the full count and are
  silently overwritten. The deletion scope (`DeleteFactsByDocumentPaths` in
  `IncrementalIndexer.PrepareSnapshotData` and
  `DeleteBindingIncompletenessByDocumentPaths` in
  `CrossDocumentEdgeRefresher.ProcessCompilationsAsync`) is narrower than the
  save scope, violating the documented lockstep invariant ("extraction and
  deletion must always be scoped to exactly the same set; documents outside it
  keep their copied-forward facts intact") for binding incompleteness.
- **`occurrence_count` is compared and consumed:** both parity oracles
  (`SnapshotAssertions.CompareSnapshotsAreEquivalent`,
  `scripts/r1-compare/Program.cs`) compare the records, and consumers sum it —
  `SnapshotCompleteness.BindingIncompletenessTotal` (`status --json`) and
  `UncertaintyDetector` capsule-uncertainty descriptions. The divergence is a
  genuine parity failure of a persisted, consumer-read field, not a benign
  artifact.
- **Fix scope (proposed, NOT implemented):** filter the collector's records to
  the deletion scope before `SaveBindingIncompleteness` at both incremental
  save sites (producer-agnostic lockstep restoration). Tracked as open finding
  10 below; see that row for candidates.

### eNoteV2 — 7 projects, `eNote.sln`

| Edits |
|---|
| 1. Doc comment added to `IReferenceCrudService.GetByIdAsync` |
| 2. Optional `includeDeleted` parameter added to `IReferenceCrudService.GetPagedAsync` |
| 3. `ExistsAsync` method added to `IReferenceCrudService` + `ReferenceCrudService` |
| 4. `ArchiveAsync` method added to `IReferenceCrudService` + `ReferenceCrudService` |
| 5. `InstrumentTypeRequest.cs` renamed to `InstrumentTypeUpsertRequest.cs` (class name unchanged) |

| Check | Result |
|---|---|
| Snapshot ID (B5 ≡ C) | `138fe56d56b6d1e3927f8763afae38c9` — match |
| Symbols (3,662) | 0 diffs |
| Declarations | 0 diffs |
| Edges (10,771) | 0 diffs across all persisted value columns |
| Diagnostics (129) | 0 diffs |
| Annotations | 0 diffs |
| FTS (source + symbol) | 0 diffs |
| Binding incompleteness | 0 diffs |

**Zero divergence on all checks.** The binding-incompleteness undercount
observed on FIT-RS2-2026 did not reproduce here.

### Verdict

**Deterministic snapshot identity + byte-identical edges, declarations, symbols,
diagnostics, and FTS across 5 incremental cycles confirmed on both solutions.**
The binding-incompleteness undercount on FIT-RS2-2026 is a REAL, reproducible
parity bug (see the triage above): it appears at the first incremental cycle of
any edit in the affected project, holds constant through cycle 5, and fails the
repo's own parity oracles on a compared, consumer-read field. It did not
reproduce on eNoteV2 because that solution's affected projects contain no
out-of-scope documents that hit the affected producer path — pattern-dependent,
not sequence-dependent (the exact pattern was not isolated; see finding 10).
The core guarantee promoted from "fixture-only" to "real-code evidence on two
multi-project solutions" is scoped to edges, declarations, symbols, diagnostics,
and FTS. Binding-incompleteness parity was an open gap (finding 10) until the
R6 lockstep fix closed it (see R6 section below).

**Reproduction:** `scripts/r1-verify-convergence.sh` (FIT-RS2-2026),
`scripts/r1-verify-enote.sh` (eNoteV2), both using `scripts/r1-compare/` for
the field-by-field diff.

---

## R6 — Binding-incompleteness incremental carry-forward parity fix (2026-08-11)

**Origin:** R1 FIT-RS2-2026 run. Two `filtered_external` records undercounted
`occurrence_count` in the incremental snapshot (73/8 vs 130/16). Finding 10.

**Root cause:** the `SaveBindingIncompleteness` call at both incremental save
sites was unscoped — it persisted ALL collector records, including partial
counts for documents outside the deletion scope. The delete was scoped to the
extraction-scope documents, so copied-forward full counts for out-of-scope
documents survived the delete but were overwritten by the unscoped save. This
violated the lockstep invariant ("extraction and deletion narrow in lockstep")
which held for edges and annotations but not for binding incompleteness.

**Fix:** at both save sites, filter `result.BindingIncompleteness` to the same
document-path set used for the delete before calling `SaveBindingIncompleteness`.
Null/empty-path records (doc-less aggregates) are excluded from the incremental
save and carry forward unchanged.

| Site | File | Change |
|---|---|---|
| Step 6 (IncrementalIndexer) | `src/Workspace/IncrementalIndexer.cs` `ExtractReplacementFacts` | `ScopeBindingIncompleteness()` filters to `extractionScopePaths` |
| Step 7 (CrossDocumentEdgeRefresher) | `src/Workspace/CrossDocumentEdgeRefresher.cs` `ProcessCompilationsAsync` | Inline filter to `scopeRelativePaths` |

**Null-path policy:** EXCLUDE-AND-CARRY-FORWARD. The scoped save keeps only
records whose `DocumentPath` is in the deletion scope set; null/empty-path
doc-less aggregates (e.g. `extractor_failure`) are dropped, so their
copied-forward value is preserved. A `--strategy=full` rebuild is the reference
for that bucket — a document-scoped incremental pass cannot correctly refresh it
(no scoped delete retires it, no scoped re-extraction reproduces the
whole-compilation count).

**Null-path policy correction (2026-08-11, branch `r6-null-bucket-parity`):** the
initial R6 fix documented EXCLUDE-AND-CARRY-FORWARD but the predicate at both
save sites was `string.IsNullOrEmpty(r.DocumentPath) || scope.Contains(...)`,
which *kept* null/empty-path records and INCLUDED them in the scoped save — the
opposite of carry-forward. A partial doc-less aggregate re-emitted by a scoped
incremental pass would overwrite the copied-forward full count via the
`ON CONFLICT ... DO UPDATE SET occurrence_count` upsert (the same undercount
class as the original out-of-scope-document bug, for the null bucket). Both
predicates are now `r.DocumentPath is { } path && scope.Contains(path)`, dropping
the null/empty bucket as the policy states. (`IncrementalIndexer.ScopeBindingIncompleteness`
was also raised from `private` to `internal` so the predicate is unit-testable.)
The end-to-end null bucket cannot be reproduced deterministically from source —
it requires an extractor stage to throw (`CompilationFactExtractor.RunStage` →
`RecordExtractorFailure`, the only true null-path source; `filtered_external`
null paths were eliminated by `DeclaringSyntaxOrContainingType`) — so the
guarantee is pinned at the predicate instead.

**Regression tests:**
- `BindingIncompletenessScopingTests.ScopeBindingIncompleteness_ExcludesNullPathBucket_ForCarryForward`
  (`tests/BindingIncompletenessScopingTests.cs`, new) — pins that the scope
  filter drops null- and empty-`DocumentPath` records and keeps only the
  in-scope one. FAILED before the predicate correction (kept 3 of 4 records),
  PASSES after.
- `ScenarioR6_BindingIncompleteness_IncrementalCarryForward`
  (`tests/IncrementalParityTests.cs`) — end-to-end out-of-scope-document parity
  (in-scope Helper.cs + out-of-scope ExternalRef.cs using Newtonsoft.Json),
  asserts parity via `SnapshotAssertions.CompareSnapshotsAreEquivalent`.

All 12 `IncrementalParityTests` scenarios and `CleanRebuildEquivalenceTest`
continue to pass with the corrected predicate.

**Scope of impact:** incremental-only. Full-run behavior is unchanged (no scope
filtering needed when all documents are in scope). Affects any incremental index
where an affected project has out-of-scope documents with external-target
references.



## R3 — Overload-resolution BFS gap fix (2026-08-11)

**Origin:** REVIEW.md I3 — "still a narrow known gap": when a NEWLY ADDED
overload causes an UNEDITED caller to bind to it, the incremental
cross-document BFS follows the old arc and may not re-extract the caller, so
incremental can diverge from full rebuild for that caller.

**Reproduction (test only, red first):**
`ScenarioR3_NewOverloadInNewFile_RebindsUneditedCaller`
(`tests/IncrementalParityTests.cs`). Full-index V1 — Calc project declares
`M(long)` only; App project has `Caller.Use` calling `c.M(42)` (binds to
`M(long)` via implicit conversion). Add a NEW file (`Overloads.cs`) declaring
`M(int)` in the Calc project, incremental-index, fresh full rebuild of the same
state, assert parity via `SnapshotAssertions.CompareSnapshotsAreEquivalent`.
FAILED before the fix, exactly on the predicted divergence: incremental kept
`Caller.Use → M(System.Int64)` (stale) where the full rebuild has
`Caller.Use → M(System.Int32)`. A second, adjacent divergence on the same root
cause: the copied-forward `T:Calc.Calc` declaration row in `Provider.cs` kept
`is_partial = 0` (the type had one declaring syntax reference when snapshot A
was extracted) instead of `1` — the parity oracle trips on it before the edges.

**Root cause:** the reverse-edge BFS (`CrossDocumentEdgeRefresher.FindAffectedDocPaths`)
can only follow persisted arcs, and a newly added file has none — no document
version in the previous snapshot, so no symbols and no incoming edges. The BFS
seed was exactly the changed paths, so the unedited caller in the other project
was never reached, its copied-forward edge kept the old binding, and the
new file's project documents were never re-extracted (stale `is_partial`).

**Fix** (`src/Workspace/IncrementalIndexer.cs` `RunIncrementalAsync`, guarded to
added files): when `changedDocs` contains any `New` document, (1) widen the BFS
seed with every document of each added file's project — their declarations
exist in the previous snapshot, so the BFS follows the old arc to the callers
that must be rebound; (2) widen `invalidationPaths`/`closurePaths` with the same
project documents; (3) widen the extraction-scope seed to changed + new-file
project documents + the closure, so the affected caller lands in the
EXTRACTION scope (re-extraction, with its copied-forward facts deleted in
lockstep), not just invalidation (FTS refresh/diff). Edits with no added files
keep today's narrow seed unchanged — the widening is add-file-only.

**Measured:**
- `dotnet test --filter "FullyQualifiedName~ScenarioR3_NewOverloadInNewFile_RebindsUneditedCaller"` — FAILED before the fix (stale `M(System.Int64)` edge, `is_partial` 0 vs 1), PASSES after (1 passed, 7.8s).
- `dotnet test --filter "FullyQualifiedName~IncrementalParityTests"` — 13 passed (47.4s), no regression (incl. `ScenarioJ_AddFileWithNewInterfaceImplementation`, `ScenarioI_PartialTypeSpanningFiles_EditOnePartKeepsOtherPartEdges`, `ScenarioH_CrossProjectCallerEdit`).
- `dotnet test --filter "FullyQualifiedName~CleanRebuildEquivalenceTest"` — passed (5.6s).
- `dotnet test --filter "FullyQualifiedName~MultiCycleConvergenceTests"` — 2 passed (17.8s). Mutation 1 of `Incremental_FiveSequentialCycles_ConvergesWithCleanRebuild` is an add-file event (`ProductService.cs`), so the new-file widening fires at cycle 1; cycles 2–5 are plain edits and keep the narrow seed. Both tests pass, confirming multi-cycle convergence is unaffected.

**Scope of impact:** incremental-only. Full-run behavior is unchanged. Any
incremental index where a file is added to a project re-extracts that
project's documents plus the reverse-edge closure of their symbols for that
run (perf cost proportional to project + dependent size, on add-file events
only; plain edits keep the narrow closure).

Note: change is uncommitted on `main` (commit authorization pending).



## R2 — Assembly-identity granularity in freshness (2026-08-11, branch `r2-assembly-identity`)

**Was the single biggest remaining risk (audit §15).** A metadata reference's
identity was `AssemblyName.GetAssemblyName(path).FullName` alone
(`WorkspaceInfo.TryGetAssemblyIdentity`, happy path). The file-name+length+mtime
signal in `FallbackReferenceToken` only fired when the assembly header was
unreadable. Both identity (`SnapshotIdentity.BuildPayload`) and freshness
(`WorkspaceFreshness.CheckMetadataReferences`) consume those strings, so a NuGet
patch that changed a package's CONTENTS without bumping the assembly version was
invisible: same full name -> same identity string -> same `SnapshotId` and no
`MetadataReferencesChanged` mismatch. Lurp could silently serve a graph built
against different bytes.

**Fix (content hash, no version bump — approved).** `TryGetAssemblyIdentity` now
folds a SHA-256 of the reference file's bytes into the identity string:
`{FullName}|sha256={hex}` (`SHA256.HashData` over the file stream). A content
hash is content-derived and therefore deterministic, so it preserves the
"identical indexed state => identical id" invariant — unlike file mtime, which
a `restore` would touch without a byte change. One code site; the two consumers
are unchanged. No DB schema migration: identities are still stored as `string[]`
in `projects.metadata_reference_identities`, only the string content is richer.

**Blast radius (intended).** Every existing snapshot gets a new `SnapshotId`, so
the next index writes a fresh snapshot instead of reusing — a one-time full
rebuild per existing DB. Pre-fix snapshots (non-null old-format identities)
report `MetadataReferencesChanged` on first post-fix compare -> safe forced
rebuild; snapshots predating the field entirely (null JSON) keep the
"unknown, not different" rule (`SnapshotManifest.FromStorageManifest`), no false
alarm. Per-index cost: one read+hash per distinct reference DLL at
workspace-load (bounded, OS-cache-warm since Roslyn already reads them).

**Regression tests** (`tests/AssemblyIdentityGranularityTests.cs`, new — pure
Roslyn, no MSBuild). Both emit two `Dep.dll` builds with identical
`AssemblyName.FullName` (`Version=1.0.0.0`) but different IL:
- `SameAssemblyVersion_DifferentBytes_ProducesDifferentSnapshotId` — asserts
  `SnapshotIdentity.Create` now returns DIFFERENT ids. Asserted the opposite
  (same id) on the unfixed code; flipped to `NotEqual` with the fix.
- `SameAssemblyVersion_DifferentBytes_FreshnessReportsMismatch` — asserts
  `WorkspaceFreshness.GetFullRebuildMismatches` now reports
  `MetadataReferencesChanged`. Asserted `DoesNotContain` on the unfixed code;
  flipped to `Contains` with the fix.

Measured: `dotnet test --filter "FullyQualifiedName~AssemblyIdentityGranularityTests"`
-> 2 passed (2.2s). `--filter "FullyQualifiedName~SnapshotIdentityCompletenessTests"`
-> 6 passed (30.8s), no regression (identical-workspace determinism preserved).
