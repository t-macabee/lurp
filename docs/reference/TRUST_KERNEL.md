# Trust Kernel — Implementation Status

**Status:** verified implementation-status reference (read this first)
**Next-step method:** compare this record with `LURP_ARCHITECTURE.md`, then
inspect the relevant live code and tests. There is no live task queue in this
repository.
**Architecture reference:** `LURP_ARCHITECTURE.md`

---

## Purpose

This file records implementation status backed by git history and the
targeted tests cited below — not by any single audit run's scratch output.
Earlier revisions of this file cited `task/findings.md` as an evidence
source; that file was a one-off audit artifact from a single iteration, was
never durable, and does not exist in this folder. Do not recreate it or
treat any future per-run scratch file the same way — evidence here should
cite git commits and named tests directly.

## Two numbering schemes in this folder — read this before touching either

- **T1–T12**: a flat, now-closed list from the original trust audit. All
  twelve items are implemented and validated — the evidence is inlined below
  in this file, not in a separate `task_list.txt`. (An earlier, per-iteration
  `task/task_list.txt` scratch file existed briefly and has been removed;
  it was never a durable artifact and its content is fully captured here.)
  This list is not extended going forward — there is no T13.
- **order 1–13**: a completed historical implementation sequence.
  Orders 1–2 were the final loose ends of T11 (root-detection fix and
  baseline run — see T11 note below). Order 3 is the Track A/B roadmap
  decision. Orders 4–13 implement `LURP_ARCHITECTURE.md` §23 Phases 4–13.
  Completed per-order planning and decision artifacts have been consolidated
  into this tracker; do not recreate a task queue from closed work.

## Where we are right now

| Order | Status | Evidence |
|---|---|---|
| 1 — Fix benchmark root detection | Done | commit `452fdaa` |
| 2 — Establish outcome-benchmark baseline | Done | commit `452fdaa`, `tests/benchmark-runs/baseline.json` |
| 3 — Track A vs. Track B decision | Done | Track A chosen; completion is recorded here. |
| 4 — Document/snapshot identity | Done | commit `f550fee` (`ux_snapshot_documents` unique index, `document_versions` immutability trigger, D4/D5 negative tests in `tests/D4D5SchemaHardeningTests.cs`) |
| 5 — Declaration lookup and partial types | Done | commit `8199760` (`DeclarationLookup_ResolvesPartialType_AndPreservesBothDeclarations` against the real indexed fixture) |
| 6 — Fast-travel reads and lexical search | Done | `FastTravelQueries`, persisted-span navigation in `DeclarationMaintenanceStore`, `navigate` handler, indexed-snapshot tests `FastTravelQuery_NavigatesFromIndexedSpan` / `NavigateHandler_ReturnsSnapshotBoundTarget`; `SearchStore.SearchSource` lexical search, hardened by commit `994429c` (per-snapshot document-version-scoped generated-code exclusion, empty-query/non-positive-limit guards, path dedup) — `SearchSource_SnapshotIsolation_UsesVersionBoundToRequestedSnapshot`, `SearchSource_EmptyQueryAndNonPositiveLimit_ReturnEmpty`, `SourceSearch_Returns_Bounded_Distinct_Snippets` |
| 7 — Migrate dirty state, fingerprints, diagnostics, and facts out of JSON (removes dual JSON/SQLite authority) | Done | Already SQLite-only; the sole remaining `--output-json=`/`SnapshotManifest.Load` surface is a documented, tested, one-way export never consulted as an authority source. |
| 8 — Facts-table decision | Done | Facts table rejected; attributes stay in `metadata_json` with identity/multiplicity preserved. |
| 9 | Not applicable | Conditional on order 8 approving the facts table; order 8 did not, so there is no implementation work |
| 10 — Generated-code discovery | Done | Dual-path detection and provenance were traced; all workspace generated files are under `obj/` and therefore out of index scope. |
| 11 — Generated-code provenance decision/implementation | Done | Existing plumbing was used; no `GeneratorDriver` or package detection. Encoding/short-file detection and structural-edge `IsCrossGenerated` propagation were fixed. `GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges` exercises a real injected `.g.cs` file end-to-end. Full suite: 175/175 pass. |
| 12 — Phase 9/10 gap audit | Done | Evidence-backed backlog of uncovered polymorphism/dispatch and structured-diff cases (see §Order 12 findings below). Four characterization tests added proving `SignatureFormat` string comparison already catches nullable annotations, ref/out/in modifiers, operator overloads, and conversion operator changes. Full suite: 179/179 pass. |
| 13 — Phase 9/10 implementation | Done | `CallsEdgeExtractor` now records compiler-resolved overloaded binary operators and user-defined cast conversions as canonical `Calls` edges; contracts `Calls_OverloadedBinaryOperator_EmitsCallsEdge` / `Calls_UserDefinedConversion_EmitsCallsEdge`, semantic edge-diff regression `SemanticDiffer_EdgeAddedAndRemoved`, and convergence test `IncrementalIndex_Matches_FullRebuild_AfterOperatorAndConversionCallEdit` pass. |

## Architecture Phase completion status (§23 roadmap)

| Phase | Description | Status | Evidence |
|---|---|---|---|
| 1 | Product constitution and schema/version rules | ✅ Done | `VersionConstants`, `MigrationRunner` |
| 2 | Workspace, snapshot, document, and configuration identities | ✅ Done | Order 4 |
| 3 | SQLite storage boundary and migrations | ✅ Done | `SqliteIndexStore`, 21 migrations |
| 4 | Immutable document versions and source storage | ✅ Done | Order 4, T12 |
| 5 | Stable type/member identities and declaration spans | ✅ Done | Order 5 |
| 6 | Fast `get` and lexical `search` queries | ✅ Done | Order 6 |
| 7 | Migrate dirty state, fingerprints, diagnostics, and existing facts | ✅ Done | Order 7 |
| 8 | Typed member-level semantic edges | ✅ Done | Order 8 decision + Order 13 + G1-G7 |
| 9 | Polymorphism and dispatch candidates | ✅ Done | Order 12 audit + Order 13 + G3, G5, G6, G7 |
| 10 | Structured semantic snapshot diffs | ✅ Done | Order 12 + G1, G2 |
| 11 | Generated-code provenance | ✅ Done | Order 10, 11 |
| 12 | ASP.NET, DI, MediatR, EF, serialization, and test adapters | ⚠️ Partial | All 6 adapters exist; production-to-test traversal gap (see below) |
| 13 | Reflection evidence ladder | ✅ Done | See §Phase 13 verification below |
| 14 | Evidence-backed impact paths | ✅ Done | `ImpactTraverser`, `ImpactHandler`, semantic_causes |
| 15 | Context capsules with source and token budgets | ✅ Done | See §Phase 15 verification below |
| 16 | Rebase simulations and audits on the shared store | ❌ Not done | |
| 17 | Optimize incremental updates from measurements | ❌ Not done | |

### Phase 13 verification — Reflection Evidence Ladder

**Architecture §18 requirements:**

| Requirement | Edge Kind | Extractor | Tests |
|---|---|---|---|
| `typeof(T)` type reference | `ReflectionTypeRef` | `TypeOfReflectionExtractor` | `TypeOf_EmitsReflectionTypeRefEdge` |
| `nameof` member reference | `ReflectionMemberRef` | `NameOfReflectionExtractor` | `NameOf_EmitsReflectionMemberRefEdge` |
| String literal matching known name | `ReflectionNameCandidate` | `StringLiteralReflectionExtractor` | `StringLiteral_MatchingTypeName_EmitsNameCandidateEdge` |
| Runtime-unknown reflection target | `ReflectionTargetUnknown` | `UnknownPatternReflectionExtractor` | `TypeGetType_EmitsUnknownEdge`, `ActivatorCreateInstance_EmitsReflectionTargetUnknownEdge` |

- All extractors in `src/Workspace/`
- Registered in `ExtractorRegistry` as `"reflection-v1"`
- 9 unit tests in `UnitTest1.cs` lines 4580-4748
- Integrated with `UncertaintyDetector` for capsule uncertainty reporting

### Phase 15 verification — Context Capsules

**Architecture §20 requirements:**

| Requirement | Implementation | Status |
|---|---|---|
| CLI entry point | `ContextHandler.cs` | ✅ |
| Tier-based assembly | `ContextAssembler.cs` with `IContextTierBuilder` | ✅ |
| Token budgeting | `Budget` parameter, `EstimateTokens`, truncation tracking | ✅ |
| Intent-based ordering | `ContextIntent.Inspect/Modify/Diagnose` | ✅ |
| Uncertainty detection | `UncertaintyDetector.cs` consumes reflection edges | ✅ |
| Suggested verification | `VerificationSuggestion` from `TestedBy` edges | ✅ |

### Phase 12 gap — Production-to-test traversal

The benchmark (T11) reported `missingRelevantTests: true` for all scenarios. Root cause:

1. `TestAdapter` only indexes test projects (filtered by assembly name suffix and test framework references)
2. Production projects are indexed separately with no knowledge of their tests
3. The `TestedBy` edge direction is `production → test`, but the edge is emitted from the **test project's compilation**, not the production project's

**Gap:** A production symbol `P` in a production project has no outgoing `TestedBy` edge during its own project's indexing because test code is not visible in that compilation. The edge exists in the test project's index, but with `P` as source, which requires cross-project edge accumulation to be meaningful.

**Required fix:** During cross-project edge accumulation in `IndexRunner.RunFullIndexAsync`, `TestedBy` edges from test projects should be merged into the production symbol's edge set so that `RelevantTestsTierBuilder` can find them.

### Phase 14 verification — Evidence-backed Impact Paths

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

Tests: `B7ImpactTraverserTests` (13 tests), `SemanticRenameIntegrationTests` integration

Order 4's scope was narrowed by an audit before its D1–D5 sub-tasks were
written: immutable document versions already existed de facto (T12 below),
and "snapshot-scoped document identity" as literally worded in `task.txt`
order 4 was rejected as inconsistent with `LURP_ARCHITECTURE.md` Stage A0
(see the architecture doc's annotation next to the identity table, and
decision D1). The remaining work was schema-level immutability enforcement
and a `snapshot_documents` uniqueness constraint, not a new identity model —
both landed in commit `f550fee`.

## T1–T12 — closed, implemented, and validated

### T1 — Schema-version verification is authoritative

`VersionConstants.DatabaseSchemaVersion` is now the sole reference for
migration-version assertions. `MigrationRunnerTests`: 124 passed, 0 failed.

### T2 — Failed snapshot cleanup is atomic

`SnapshotPruner.DeleteSnapshotData(string)` deletes inside a transaction and
rolls back/rethrows on failure, matching `PruneWorkspace`/
`DeleteIncompleteSnapshots`. A trigger-induced mid-delete failure left all
earlier-deleted rows intact in the regression test. Storage suite: 139
passed, 0 failed.

### T3 — Orphan edges are removed on both endpoints

`EdgeStore.DeleteOrphanEdges` removes an edge when either endpoint is absent
from the snapshot's `snapshot_symbols`. `FullIndex_Has_No_Orphan_Edge_Targets`
passes against the real-fixture integration test; the deletion path runs in
every full-index pipeline invocation.

### T4 — Cross-project edge-relation deduplication

`EdgeStore.SaveEdges` uses `INSERT OR IGNORE INTO edges`, and
`IndexRunner.RunFullIndexAsync` accumulates edges across all projects, runs
`EdgeDedup.Deduplicate` (provenance priority: `compiler_proved` >
`framework_derived` > `possible` > `convention` > `name_candidate` >
`runtime_unknown`), then calls `SaveEdges` once. The unique index
`ux_edges_relation` is unchanged — no relaxation, no migration-side dedup.
`SaveEdges_DeduplicatesSameTripleAcrossProjects` and
`EdgeDedup_KeepsHighestProvenance` both pass.

### T5–T8 — Semantic-diff metadata has a producer/consumer contract

`BuildMetadataJson` writes `base_type` (null for interfaces/`System.Object`),
canonical callable-member `signature`, and sorted `attributes` (type presence
plus constructor/named-argument values — attributes stay in `metadata_json`,
not the unimplemented `facts` table from architecture §3.4). `CompareMetadata`
consumes all of it; a contract test binds every consumer key to producer
coverage. Combined targeted + semantic-diff suites pass (T5: 7, T6: 8, T7: 12,
T8: 8 tests).

### T9 — Generated-output absence is declared and reported

`MSBuildWorkspace.OpenSolutionAsync` does not expose source-generator output;
snapshot completeness now persists `generated_trees_included`, active TFMs,
skipped adapters, and extractor version, surfaced via `status --json`.
Generated semantics remain declared absent — no `GeneratorDriver` work was
added (that's architecture Phase 11 / task.txt order 10–11).

### T10 — Incremental-versus-full equivalence coverage expanded

Snapshot comparison now includes FQNs, metadata JSON, declaration
paths/spans/flags, and exact source/symbol FTS records. Cases cover
signature, body-only, document-move, partial-class, base/interface, and
DI-registration edits. Full suite: 167/167 pass.

### T11 — Outcome benchmark exists and its baseline is established

The benchmark (local-validation, handler/DTO, DI-replacement scenarios) has a
fixture, machine-readable scenario contract, runner, and baseline JSON, and
records the ten architecture §25 measures without fabricating the
post-capsule worker-token measure. It was blocked by two issues, both now
resolved (commit `452fdaa`):
- `LocateRepositoryRoot()` searched for a `task/task_list.txt` sentinel that
  didn't exist — replaced with a real repository marker (the solution file).
- `SearchStore` symbol lookups compared caller-supplied FQNs against stored
  FQNs without accounting for Roslyn's `global::` prefix — fixed to accept
  either form.

`OutcomeBenchmarkTests.RunBaseline_WritesOutcomeEvaluation` now passes and
writes `tests/benchmark-runs/baseline.json`. The benchmark's fixture solution
and scenario contract live in `tests/fixtures/OutcomeBenchmark/` — load-bearing
test infrastructure, not reference material, so it lives under `tests/`
rather than `task/`. One capability gap surfaced by
the rerun, tracked separately (not on the order-1..13 sequence): both
scenarios report `missingRelevantTests: true` — there's no edge/traversal
connecting a production symbol to the tests covering it.

### T12 — Snapshot pruning reclaims document-version storage

Post-prune cleanup deletes unreferenced `document_versions`, deletes
documents with no retained versions, and repairs/clears dangling
`last_changed_snapshot_id` values inside the prune transaction (foreign-key
enforcement is not enabled on the store connection, so this cleanup is
explicit rather than cascade-driven). Both GC tests pass.

## Order 12 findings — Phase 9/10 gap audit

### Phase 10 (structured semantic diff) — gaps found

**Already covered by `SignatureFormat` string comparison (proven by characterization tests):**

| ID | Case | Test | Status |
|---|---|---|---|
| S1 | Nullable annotation change (`string` → `string?`) | `SemanticDiffer_NullableAnnotationChanged` | ✅ Pass |
| S2 | ref/out/in modifier change | `SemanticDiffer_RefParameterModifierChanged` | ✅ Pass |
| S5 | Operator overload return type change | `SemanticDiffer_OperatorOverloadSignatureChanged` | ✅ Pass |
| S6 | implicit↔explicit conversion operator change | `SemanticDiffer_ConversionOperatorSignatureChanged` | ✅ Pass |

These were previously untested but the `SignatureFormat` (with
`IncludeNullableReferenceTypeModifier`, `IncludeParamsRefOut`,
`IncludeExplicitInterface`, `IncludeTypeConstraints`) already produces
distinct strings for each case. The tests are characterization tests
locking in that behavior.

**Remaining gaps requiring implementation:**

| ID | Gap | What's needed |
|---|---|---|
| S8 | `interfaces` key not written to `metadata_json` | `BuildMetadataJson` writes `base_type` but not implemented interfaces; `CompareMetadata` has no interfaces comparison. Paired producer+consumer addition. |
| S9 | `isRecord` written but never compared | `BuildMetadataJson` persists `isRecord`; no consumer checks it. Low priority — record vs class is visible in `base_type` and signature. |
| S10 | Many metadata fields persisted but never compared | `returnType`, `isAbstract`, `isVirtual`, `isOverride`, `isStatic`, `isAsync`, `arity`, `isExtensionMethod`, `typeKind` are all written by `BuildMetadataJson` but `CompareMetadata` reads only 4 keys. Each needs an explicit decision: is this a semantic change worth surfacing, or is it already captured by signature/base_type? |
| S11 | `semantic_changes` table not queried for invalidation explanation | Diff results are persisted but not used to explain *why* downstream symbols were invalidated. Architecture §15 completion condition requires sweep/impact to explain semantic cause. |

### Phase 9 (polymorphism/dispatch) — gaps found

| ID | Gap | What's needed |
|---|---|---|
| D1 | `new` keyword (member hiding) not modeled | Done — see G3 below. |
| D2 | Generic type construction tracking | Done — see G7 below. |
| D5 | Indexer access not distinguished from method calls | Indexer `get_Item`/`set_Item` are emitted as regular `Calls` edges; no distinction from explicit method invocations. |
| D6 | Extension method binding edges | Extension method calls resolve to the static method, not the instance-style dispatch. No edge from the extended type to the extension method. |

### Priority recommendation for order 13

1. **S8** (interfaces metadata) — smallest self-contained producer+consumer pair; no existing test covers it.
2. **S10 triage** — decide per-field whether each is a semantic change worth surfacing; most are likely already captured by signature comparison.

## Architecture alignment and recorded deviations

| Architecture reference | Status | Evidence-based reading |
|---|---|---|
| §3.4 logical `facts` table | Decided deviation | Attributes are stored in `metadata_json`; the facts table is deliberately not built. The approved contract preserves attribute identity and multiplicity. |
| §5.6 schema stability | Partially aligned | Version assertions follow the schema constant; no separate migration/constant consistency test exists yet. |
| §6 Stage A0 identity | Aligned, confirmed | `document_id` is path-scoped per the architecture's own definition; order-4's D1 decision made this explicit rather than building snapshot-scoped identity. |
| §15 structured semantic diffs | Aligned | Base type, signature, attributes, interfaces, record status, type kind, and persisted declaration/binding modifiers have producer/consumer coverage. `ImpactHandler` now passes `SemanticDiffStore` to `ImpactTraverser`, which attaches persisted changes for the queried root symbol and target snapshot as `semantic_causes` on every returned impact path. Order 13 also verifies that newly extracted operator/conversion `Calls` edges are surfaced by the existing edge diff and converge between incremental and full snapshots. |
| §16 generated semantics | Aligned, narrow scope | Existing detection/identity/`IsCrossGenerated` plumbing was used, fixing encoding, short-file, and structural-edge provenance gaps; `GeneratorDriver` generation and package-metadata detection remain postponed. `SnapshotCompleteness.GeneratedTreesIncluded` is `false` because all workspace generated files are under `obj/`, which `IsBuildOutputPath` excludes. A file injected outside `obj/` is proven end-to-end to be indexed correctly. |
| §22 atomic incremental operation | Aligned | Snapshot cleanup is atomic, equivalence comparisons are stronger, edge dedup is implemented. Full suite passes (167/167). |
| §25 outcome validation | Baseline established | Benchmark contract, runner, and baseline all exist and run clean; `missingRelevantTests` gap tracked separately. |

### Order 13 — approved dispatch/diff capability

The approved narrow scope was D3/D4: capture compiler-resolved overloaded
binary operators and user-defined cast conversions in the existing `Calls`
edge contract. `CallsEdgeExtractor` keeps built-in operators out of the graph,
deduplicates multiple call sites using the existing source/target/kind key, and
records the syntax location through the existing edge-location path.

Validation is bound at three levels:

- extractor contracts prove `op_Addition` and `op_Explicit` edges are emitted;
- `SemanticDiffer_EdgeAddedAndRemoved` proves the existing canonical edge diff
  reports relation changes;
- `IncrementalIndex_Matches_FullRebuild_AfterOperatorAndConversionCallEdit`
  proves incremental and full rebuild snapshots are equivalent across symbols,
  declarations, edges, diagnostics, annotations, and FTS records.

### Post-order gap register — G1 metadata producer/consumer contract

**Done (2026-07-27).** `BuildMetadataJson` now persists sorted direct
implemented-interface FQNs under `interfaces`; `SemanticDiffer.CompareMetadata`
emits `interfaces_changed` when that list changes and `record_changed` when
`isRecord` changes. `typeKind` and persisted declaration/binding modifiers
(`isAbstract`, `isVirtual`, `isOverride`, `isStatic`, `isAsync`,
`isExtensionMethod`, `isReadOnly`, `isWriteOnly`, `isConst`, `isVolatile`)
are independently semantic and emit `metadata_changed` with the changed field.
`returnType` and callable-member arity remain intentionally covered by the
canonical signature; type arity changes symbol identity. Focused validation:
`MetadataContractTests` 11/11 and `SemanticDiffer` 14/14 passed; `git diff
--check` passed.

### Post-order gap register — G2 semantic cause in impact explanations

**Done (2026-07-27).** `ISemanticDiffStore` now supports snapshot-targeted
semantic-change reads; `ImpactHandler` supplies that store to
`ImpactTraverser`. Each returned impact path includes `semantic_causes` for
the queried root symbol, preserving the change type, originating snapshot,
and structured detail so downstream consumers can explain why the traversal
was invalidated. Focused validation: `MigrationRunnerTests` 152/152 passed
(including `TraceImpact_SemanticChanges_ExplainCauseOfDownstreamImpact`);
`git diff --check` passed.

### Post-order gap register — G3 `new` member-hiding (Hides edge)

**Done (2026-07-27).** `OverridesEdgeExtractor` now detects `new`
member-hiding for methods (parameter-compatible, non-override, non-constructor)
and properties (non-override) and emits a distinct `Hides` edge from the hiding
member to the hidden member in the immediate base type. Added `Hides` to
`EdgeKind`, `hides-v1` to `ExtractorConstants`, and registered both in
`ExtractorRegistry.All`. Override members emit only `Overrides`, not also
`Hides`; overloads with different parameter types do not emit `Hides`.
Focused validation: 4 focused extraction tests pass (`Hides_DerivedHidesBaseMethod_EmitsHidesEdge`,
`Hides_DerivedHidesBaseProperty_EmitsHidesEdge`,
`Hides_OverloadWithDifferentParams_DoesNotEmitHidesEdge`,
`Hides_OverrideDoesNotAlsoEmitHidesEdge`); `MigrationRunnerTests` 190/190 pass
(including pre-existing `test-v1` → `test-v3` mismatch fixed in
`UpsertExtractors_AllVersionsMatchRegistryConstants`).

### Post-order gap register — G5 indexer Reads/Writes edges

**Done (2026-07-27).** `CallsEdgeExtractor` now resolves indexer access
(`ElementAccessExpressionSyntax`) to the indexer property and emits `Reads`
(get contexts) or `Writes` (assignment left-hand-side, pre/post increment/
decrement, `ref`/`out` argument contexts) edges instead of flattening
indexer access into ordinary `Calls` edges. Multiple access sites in the same
method are deduplicated by source/target/kind. Focused validation: 3 extraction
contract tests pass (`Calls_IndexerGetter_EmitsReadsEdge`,
`Calls_IndexerSetter_EmitsWritesEdge`,
`Calls_IndexerMultipleAccessSites_DeduplicatesByRelation`).

### Post-order gap register — G6 extension-method receiver edge

**Done (2026-07-27).** `CallsEdgeExtractor.AddCallEdge` now detects extension-method
call syntax (`callee.ReducedFrom != null`) and emits a distinct `ExtensionReceiver`
edge from the receiver type to the extension method alongside the existing
compiler-proved `Calls` edge. Added `ExtensionReceiver` to `EdgeKind`,
`extension-receiver-v1` to `ExtractorConstants`, and registered both in
`ExtractorRegistry.All`. Static-call syntax (`Extensions.Bar(this)`) does not
emit `ExtensionReceiver` — only instance-style syntax triggers the new edge.
Focused validation: 2 extraction contract tests pass
(`Calls_ExtensionMethod_EmitsExtensionReceiverEdge`,
`Calls_ExtensionMethod_StaticCall_NoExtensionReceiverEdge`);
`MigrationRunnerTests` 195/195 pass.

### Post-order gap register — G7 generic type-argument evidence on dispatch edges

**Done (2026-07-28).** `InterfaceDispatchExtractor.GetTypeArgumentsJson` serialises
the concrete type arguments of constructed generic interfaces (e.g.
`IRepository<Customer>` → `["Customer"]`) into a JSON array; multi-parameter
generics (e.g. `IMapper<TSource, TDest>`) capture all type arguments in order.
`PolymorphismExtractionContext.MakeMayDispatchEdge` accepts the optional
`typeArgumentsJson` parameter and sets it on the `EdgeRecord.TypeArgumentsJson`
property. `EdgeStore.SaveEdges` persists the value in the `type_arguments_json`
column. `VirtualOverrideExtractor` does not pass type arguments (correct — virtual
dispatch does not involve generic type construction). Focused validation: 5
extraction contract tests pass (`InterfaceDispatch_ClassImplementsInterface_EmitsMayDispatchTo`,
`InterfaceDispatch_GenericInterface_CapturesTypeArguments`,
`InterfaceDispatch_GenericInterface_DifferentTypeArgsProduceDistinctEdges`,
`InterfaceDispatch_NonGenericInterface_TypeArgumentsJsonIsNull`,
`InterfaceDispatch_MultiTypeParamGeneric_CapturesAllTypeArguments`);
`VirtualOverride_DerivedOverridesVirtual_EmitsMayDispatchTo` also passes.

## Source-generator support — reassessed 2026-07-28

The explicitly postponed item "explicit source-generator execution with a
`GeneratorDriver`" was reassessed against the current workspace to determine
whether it's actually needed before touching `SnapshotManifest`/
`SnapshotCompleteness`.

### Observed facts

1. **No source generator packages** exist in any `.csproj` in this workspace
   (`src/Lurp.csproj`, test project, fixture projects). `rg` for "Generator"
   in `.csproj` files returned empty.

2. **All `.g.cs` files are under `obj/`** and are `.GlobalUsings.g.cs` —
   implicit-usings output from the C# compiler, not source-generator output.
   `find . -name "*.g.cs" -not -path "*/obj/*"` returned **zero** results.

3. **`IsBuildOutputPath`** filters every file under `obj/` (and `bin/`) before
   generated-code detection logic runs. No generated file survives to reach
   the detection pipeline.

4. **`GeneratedTreesIncluded` is always `false`**, hardcoded in both
   `SnapshotManifest.FromWorkspace` and `SnapshotManifest.FromStorageManifest`.

5. **The plumbing is proven correct** — `GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges`
   injects a `.g.cs` file outside `obj/` and proves end-to-end that
   declarations are marked generated and edges flagged cross-generated. But
   this tests the code path, not the workspace reality — it requires
   artificial injection because no real generated file exists outside `obj/`.

### Inference and decision

`GeneratedTreesIncluded = false` is **accurate, not a gap** for the current
workspace. It honestly declares that no generated trees are included because
there are none to include. `GeneratorDriver`-based source-generator execution
remains correctly postponed — no changes to `SnapshotManifest` or
`SnapshotCompleteness` are warranted.

Revisit conditions (unchanged from original postponement):
- A NuGet source-generator package is added to any project in the workspace.
- A generated file appears outside `obj/` that carries meaningful symbols
  not already surfaced by the existing header-based detection.

### Source encoding normalization (2026-07-30)

`WorkspaceInfo.NormalizeSourceBytes` (`src/Workspace/WorkspaceInfo.cs`) now
strips UTF-8 BOM and transcodes UTF-16 LE/BE source to canonical BOM-free
UTF-8 at ingestion, replacing the old `DetectEncoding`/`ResolveEncoding`
pair; `document_versions.encoding` is now always `"utf-8"`. Covered by
`tests/SourceEncodingIntegrationTests.cs` (3 tests, full-index integration,
BOM/UTF-16 LE/UTF-16 BE fixtures).

While debugging an initial failure in the BOM test, a red herring surfaced:
`GetSymbolSource` (`src/Storage/DeclarationReadStore.cs`,
`GetSymbolSpanContent`) resolves a symbol's declaration span with `LIMIT 1`
and no document filter. For a partial class declared across two files
(the `Widget`/`Widget.Extra.cs` test fixture), this can nondeterministically
return either file's declaration — that, not encoding, was the actual test
failure. Fixed by scoping the affected assertions to `GetLabel`, a method
unique to `Widget.cs`, in all three tests. The underlying `LIMIT 1`
ambiguity in `GetSymbolSpanContent` itself is unfixed and out of scope here.

## Explicitly postponed

- Multi-TFM per-framework indexing.
- Deterministic snapshot IDs.
- DI adapter matching by parameter type instead of containing-type name.
- Concurrency, daemon, or server architecture.
- `SqliteIndexStore` decomposition.
- Explicit source-generator execution with a `GeneratorDriver`.
- Production-symbol-to-test traversal (surfaced by T11's benchmark rerun;
  not on the order-1..13 sequence — pick this up only if a future order
  explicitly adds it).
