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
| 3 | SQLite storage boundary and migrations | ✅ Done | `SqliteIndexStore`, 24 migrations; `SchemaStabilityTests` binds the migration list to `VersionConstants`, upgrades the checked-in v22 fixture, and proves unknown persisted symbol kinds survive as `Unknown` |
| 4 | Immutable document versions and source storage | ✅ Done | Order 4, T12 |
| 5 | Stable type/member identities and declaration spans | ✅ Done | Order 5 |
| 6 | Fast `get` and lexical `search` queries | ✅ Done | Order 6 |
| 7 | Migrate dirty state, fingerprints, diagnostics, and existing facts | ✅ Done | Order 7 |
| 8 | Typed member-level semantic edges | ✅ Done | Order 8 decision + Order 13 + G1-G7 |
| 9 | Polymorphism and dispatch candidates | ✅ Done | Order 12 audit + Order 13 + G3, G5, G6, G7 |
| 10 | Structured semantic snapshot diffs | ✅ Done | Order 12 + G1, G2 |
| 11 | Generated-code provenance | ✅ Done | Order 10, 11 |
| 12 | ASP.NET, DI, MediatR, EF, serialization, and test adapters | ✅ Done | All 6 adapters exist; `TestedBy` granularity fix in `RelevantTestsTierBuilder` (commit `63cfaf4`); `FullIndex_OutcomeBenchmark_EmitsTestedByOnProductionType` and `RunBaseline_WritesOutcomeEvaluation` pass |
| 13 | Reflection evidence ladder | ✅ Done | See §Phase 13 verification below |
| 14 | Evidence-backed impact paths | ✅ Done | `ImpactTraverser`, `ImpactHandler`, semantic_causes |
| 15 | Context capsules with source and token budgets | ✅ Done | See §Phase 15 verification below |
| 16 | Rebase simulations and audits on the shared store | ✅ Done | All read/simulate/audit handlers now run through the shared `HandlerBootstrap` (`src/Handlers/HandlerBootstrap.cs`): `GetArgValue`, `RequireArg`, `ResolveOutputDir`, `ResolveDbPath`, `OpenStore`, `ResolveSnapshotId`. Each handler's private copies were deleted as it converted — simulate family (`SimulateRenameHandler`/`SimulateMoveHandler`/`SimulateRemoveHandler`), `AuditHandler`, then `ImpactHandler`, `DiffHandler`, `GetSymbolHandler`, `GetSourceHandler`, `SearchHandler`, `FindSymbolHandler`, `NavigateHandler`, `ContextHandler`, `AnnotationHandler`, `TimingsHandler`. `StatusHandler`/`IndexHandler` keep only their diverging control flow (async, pre-store-open branches, Roslyn use) and share just `GetArgValue`/output-dir resolution. Validation: `dotnet build src/Lurp.csproj` clean (0 warnings, 0 errors); full suite 237/237 pass, including handler-driving integration tests `Status_AfterFreshIndex_ReportsUpToDate` and `NavigateHandler_ReturnsSnapshotBoundTarget`. |
| 17 | Optimize incremental updates from measurements | ✅ Done | Per-extractor elapsed time and current-thread allocations are emitted by `CompilationFactExtractor`. A matched self-host measurement identified `CallsEdgeExtractor`/`ReadsWritesEdgeExtractor` as the dominant traversals; single-pass call/operator/cast/indexer traversal plus cached method enumeration reduced `CallsEdgeExtractor` from 2051→1944 ms (Lurp), 217→182 ms (Storage), and 427→418 ms (tests). A broader node-cache candidate was measured and rejected because it did not produce a reliable improvement. `B1MemberEdgeExtractorTests` 22/22 pass after the retained optimization. |

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
| Anchor symbols | `CapsuleAnchor` includes symbol/FQN/kind/source plus scope, intent, hop limit, snapshot, affected projects, objective, provenance, extractor identity, and declaration locations | ✅ |
| Relevant contracts | `ContractsTierBuilder` | ✅ |
| Registered or possible runtime targets | `RegisteredImplementationsTierBuilder` | ✅ |
| Relevant tests | `RelevantTestsTierBuilder`; shared containing-type expansion in `TestSymbolDiscovery` | ✅ |
| Uncertainties and unchecked vectors | `UncertaintyDetector`, including reflection/generated exclusions and persisted binding incompleteness | ✅ |
| Incoming and outgoing paths | `ContextCapsule.IncomingPaths` / `OutgoingPaths`; `ImpactHop` carries the complete source span | ✅ |
| Suggested verification commands | `VerificationSuggestion.Command`; owning test project is derived from the persisted test declaration path; multi-project changes escalate to a full-suite step | ✅ |
| Likely change sites | Ranked `LikelyChangeSite` entries for anchor, direct callers, and composition points | ✅ |
| Exact source spans | `DeclarationReadStore.GetDeclarationLocations`; every anchor and capsule item carries path/start/end coordinates | ✅ |
| Affected public surfaces | Derived from persisted declaration accessibility | ✅ |
| Reason every item was included | Per-group `InclusionReasons` plus per-item `InclusionReason`, authored explicitly by every tier builder | ✅ |

Budgeting follows §20.1 order. The partial-tier policy is explicitly greedy-prefix;
once a higher-priority item cannot fit, lower tiers are not allowed to leapfrog it.
Every omitted tier is still evaluated and recorded with `budget_exhausted` or `empty`.
`ContextCapsuleAcceptanceTests.SelfHost_EdgeLocationResolver_CapsuleSatisfiesPhase15Contract`
indexes `Lurp.slnx` and proves the eleven-item contract plus an executable test command.

Closed capsule decisions:

- No occurrence multigraph: architecture §12/§13 defines one evidence-bearing relation edge, not exhaustive call-site storage. A future occurrence table would require an architecture amendment.
- No generated anchor narrative: architecture §24 keeps deterministic structured facts canonical. Consumer-authored prose is not stored as fact; source-authored XML documentation remains retrievable source evidence.
- Architectural constraints come from snapshot annotations and repeatable caller `--constraint` input. No second JSON authority was added.

### Rework completion — incremental closure, honesty, and operations (2026-08-01)

- Reverse project invalidation uses `Solution.GetProjectDependencyGraph()` to compute the transitive dependent closure. Edge-sourced document invalidation iterates to a fixed point before extraction.
- Every affected project is re-extracted without document scoping. Copied edges and incompleteness rows for all affected documents are replaced; declarations and compiler diagnostics are recomputed. `IncrementalIndex_ThreeProjectReverseClosure_MatchesFullIncludingDiagnostics` proves a change in A converges through B and C to the same canonical facts as a clean rebuild, including diagnostics.
- A source/binary breaking-change classifier remains deferred: it is a separate capability, not required for dependency closure.
- Matching edge triples now emit `edge_evidence_changed` for provenance/type-arguments/generated-evidence changes and `edge_location_changed` for source moves. Relation identity remains `(source, target, kind)` and `EdgeDedup` still retains the strongest same-snapshot evidence.
- `binding_incompleteness` persists reason-coded counts per snapshot/project/document (`ambiguous_overload`, `compiler_error`, `unresolved_metadata`, `unsupported_syntax`, `filtered_external`, `extractor_failure`) and surfaces them through status JSON and capsule completeness. Cross-document refresh deletes stale rows using git-relative document paths, matching the form `BindingIncompletenessCollector` writes; `CrossDocumentRefresh_DeletesStaleBindingIncompletenessByRelativePath` drives `CrossDocumentEdgeRefresher.RefreshAsync` directly, because the full incremental pipeline masks the delete (`PrepareSnapshotData` already clears the invalidation scope) and a store-level test passes either way.
- Failed full or incremental snapshots are marked `failed` immediately with a stable reason code and message; complete-snapshot reader gating is unchanged.
- Partial declarations are read deterministically as all declaration views. Source content, spans, and line starts now always come from the same persisted document version.
- The §25 benchmark was re-baselined after capsule expansion. All three scenarios still report exact starting-symbol resolution, no missing contracts/tests, no irrelevant capsule content, and incremental/full equivalence; only timestamps and generated snapshot IDs changed.
- Post-audit validation (2026-08-01): full suite 254/254 pass, 0 skipped, 421s. The audit that produced this state also verified the two persistence fixes are falsifiable — reverting the relative-path conversion fails `CrossDocumentRefresh_DeletesStaleBindingIncompletenessByRelativePath` on the surviving stale row.

### Architecture §26 definitive-version checklist (2026-08-01)

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

### Phase 12 — Production-to-test traversal (Done, commit `63cfaf4`)

`TestAdapter` emits `TestedBy` edges with the containing **type** as source (never the method), so `RelevantTestsTierBuilder` found nothing when querying a method-level anchor. Fixed in `RelevantTestsTierBuilder.AddTestsFor`: after querying the anchor's direct edges, `DeriveContainingTypeId` strips the member segment from the doc-comment ID (`M:A.B.C.Method` → `T:A.B.C`) and queries `TestedBy` on the type ID too. No store API addition was needed.

Validation: `FullIndex_OutcomeBenchmark_EmitsTestedByOnProductionType` (edges non-empty, all sources start with `T:`) and `OutcomeBenchmarkTests.RunBaseline_WritesOutcomeEvaluation` (`missingRelevantTests: false` for all scenarios) both pass.

### CLI fix — `--mode=index` failed on a fresh nested `--output-dir` (2026-08-01)

External test against `FIT-RS2-2026/eCommerce` (33 declarations, 4 projects)
found `--mode=index` throwing `SQLite Error 14: unable to open database file`
whenever `--output-dir` pointed at a directory that did not yet exist (e.g. a
nested path with no existing parent). `IndexHandler.Run` computed `dbPath`
and called `HandlerBootstrap.OpenStore` without ever creating `outputDir`;
every other mode is read-only and correctly fails via `ResolveDbPath`'s
`File.Exists` check, but indexing is the one mode that must create the
directory. Fixed in `IndexHandler.cs` by calling
`Directory.CreateDirectory(outputDir)` before computing `dbPath`. Verified
manually end-to-end: full index of `FIT-RS2-2026/eCommerce` into a
previously-nonexistent nested output dir now completes (1654 declarations,
6453 edges, 1240 diagnostics), and `status`/`search`/`find-symbol` against
the resulting database all resolve correctly.

Same test run also confirmed `search --kind=` filters on Roslyn's coarse
`SymbolKind` (`"Type"`, `"Method"`, `"Field"`, ...), not the finer
`TypeKind` (`"Class"`, `"Interface"`, `"Struct"`, ...) that only lives in
`metadata_json`. `--kind` for `--mode=search` isn't in `--help` output at
all (only the unrelated `--mode=annotate --kind=` is documented), so
`--kind=Class` finding nothing isn't a regression — it's an undocumented
filter behaving as coded. Left as-is; not a correctness bug.

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

Tests: `B7ImpactTraverserTests` (14 tests), `SemanticRenameIntegrationTests` integration

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
| §5.6 schema stability | Aligned | `SchemaStabilityTests.MigrationListAndSchemaConstant_AreConsistent` binds the migration list to `VersionConstants.DatabaseSchemaVersion` (count + uniqueness); `CheckedInPriorV22Fixture_UpgradesToCurrentSchema` upgrades the checked-in v22 schema fixture and asserts the failure-state columns exist; `UnknownPersistedSymbolKind_DeserializesAsUnknown` proves unknown persisted enum values survive as `Unknown`. Open caveat carried from the rework report: the prior-version fixture is a SQL recreation of the v22 schema, not a checked-in binary database image — it validates migration SQL, not real persisted bytes. |
| §6 Stage A0 identity | Aligned, confirmed | `document_id` is path-scoped per the architecture's own definition; order-4's D1 decision made this explicit rather than building snapshot-scoped identity. |
| §15 structured semantic diffs | Aligned | Base type, signature, attributes, interfaces, record status, type kind, and persisted declaration/binding modifiers have producer/consumer coverage. `ImpactHandler` now passes `SemanticDiffStore` to `ImpactTraverser`, which attaches persisted changes for the queried root symbol and target snapshot as `semantic_causes` on every returned impact path. Order 13 also verifies that newly extracted operator/conversion `Calls` edges are surfaced by the existing edge diff and converge between incremental and full snapshots. |
| §16 generated semantics | Aligned, narrow scope | Existing detection/identity/`IsCrossGenerated` plumbing was used, fixing encoding, short-file, and structural-edge provenance gaps; `GeneratorDriver` generation and package-metadata detection remain postponed. `SnapshotCompleteness.GeneratedTreesIncluded` is `false` because all workspace generated files are under `obj/`, which `IsBuildOutputPath` excludes. A file injected outside `obj/` is proven end-to-end to be indexed correctly. |
| §22 atomic incremental operation | Aligned | Snapshot cleanup is atomic, equivalence comparisons are stronger, edge dedup is implemented. Full suite passes (167/167). |
| §25 outcome validation | Done | Benchmark contract, runner, and baseline all exist and run clean; `missingRelevantTests: false` for all scenarios (Phase 12 fix, commit `63cfaf4`). |

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
`GetSymbolSource` (`src/Storage/DeclarationReadStore.cs`) selected a
declaration span with `LIMIT 1` and no document filter. For a partial class
declared across two files (the `Widget`/`Widget.Extra.cs` test fixture),
that could nondeterministically return either file's declaration. Resolved by
P1-5: `GetSymbolSpanContents` now returns **all** declaration spans ordered by
document path, and `GetSymbolSource` joins every view slice (declaration /
signature / body / name), so content and line starts always come from the same
persisted document version. `GetDeclarationLocations` additionally exposes
exact path/start/end line-and-column coordinates per declaration. The interim
`GetLabel` workaround in `tests/SourceEncodingIntegrationTests.cs` was
reverted; all three tests now assert against the partial type (`Widget`)
declaration directly, and the `LIMIT 1` ambiguity no longer exists.

## Explicitly postponed

- Multi-TFM per-framework indexing.
- Deterministic snapshot IDs.
- DI adapter matching by parameter type instead of containing-type name.
- Concurrency, daemon, or server architecture.
- `SqliteIndexStore` decomposition.
- Explicit source-generator execution with a `GeneratorDriver`.
