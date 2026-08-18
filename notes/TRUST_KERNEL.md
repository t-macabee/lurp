# Trust Kernel: Implementation Status

**Status:** implementation-status reference. The architecture is documented in
`docs/ARCHITECTURE.md` and `AGENTS.md`; this file records the
evidence-backed status of implementation, verification, and known deviations.
There is no live task queue in this repository.

**Test-suite status:** the `tests/` tree was rebuilt after the Aug 2026
cleanup (commit `f1254fc`). All citations below name live tests or dated
historical figures.

---

## Purpose

Records implementation status backed by git history and the targeted tests
cited below. Evidence cites git commits and named tests directly.

## Two numbering schemes in this folder: read this before touching either

- **T1–T12**: a flat, now-closed list from the original trust audit. All
  twelve items were implemented and validated at time of record: the evidence
  is inlined below. This list is not extended going forward: there is no T13.
- **order 1–13**: a completed historical implementation sequence. Orders 1–2
  were the final loose ends of T11 (root-detection fix and baseline run: see
  T11). Order 3 is the storage strategy decision. Orders 4–13 implement the
  architecture phases. Per-order planning artifacts are consolidated into this
  tracker; do not recreate a task queue from closed work.

## Where we are right now

| Order | Status | Evidence |
|---|---|---|
| 1: Fix benchmark root detection | Done | commit `452fdaa` |
| 2: Establish outcome-benchmark baseline | Done | commit `452fdaa`; baseline ran clean at time of record (removed with audit/simulate infra) |
| 3: Track A vs. Track B decision | Done | Track A chosen |
| 4: Document/snapshot identity | Done | commit `f550fee` (`ux_snapshot_documents` unique index, `document_versions` immutability trigger, D4/D5 negative tests) |
| 5: Declaration lookup and partial types | Done | commit `8199760`; partial-type + declaration-source coverage |
| 6: Fast-travel reads and lexical search | Done | `FastTravelQueries`, persisted-span navigation, `SearchSourceStore.SearchSource` hardened by `994429c` |
| 7: Migrate dirty state, fingerprints, diagnostics, facts out of JSON | Done | SQLite-only; sole remaining `--output-json=` is a documented one-way export |
| 8: Facts-table decision | Done | Facts table rejected; attributes stay in `metadata_json` |
| 9 | Not applicable | Conditional on order 8; not approved |
| 10: Generated-code discovery | Done | All generated files under `obj/`, out of index scope |
| 11: Generated-code provenance | Done | Used existing plumbing; end-to-end `.g.cs` proof live in `GeneratedCodeProvenanceTests` |
| 12: Phase 9/10 gap audit | Done | Backlog in "Order 12 findings" below; four `SemanticDiffer` `SignatureFormat` characterization tests live |
| 13: Phase 9/10 implementation | Done | `CallsEdgeExtractor` records resolved operators/casts as `Calls`; covered by `GoldenEdgeTests` + parity tests |

## MCP Server Phase completion status

| MCP Phase | Description | Status | Evidence |
|---|---|---|---|
| 1 | Read surface (`lurp_context` via MCP) | ✅ COMPLETE | `McpServeHandler`, `ContextTool`, `tests/Mcp/McpContextTests.cs` |
| 2 | Full read parity (`search`, `find_symbol`, `get_source`, etc.) | ✅ COMPLETE | 13 MCP tools via `tools/list`: `lurp_find_symbol, lurp_diff, lurp_get_symbol, lurp_index, lurp_get_annotations, lurp_status, lurp_get_source, lurp_context, lurp_refresh, lurp_navigate, lurp_search, lurp_timings, lurp_impact` (13th `lurp_timings` added 2026-08-17, no `lurp_annotate` by design). `SearchTool`, `FindSymbolTool`, `GetSourceTool`, `GetSymbolTool`, `NavigateTool`, `ImpactTool`, `DiffTool`, `AnnotationsTool`, `TimingsTool` (`lurp_timings` parity with `--mode=timings --output=json`, `tests/Mcp/McpTimingsTests.cs`, `McpParityTests.Timings_Parity_WithCliJson`). MCP session is read-only, `PRAGMA query_only=ON` (`src/Storage/SqliteIndexStore.cs:74`, `src/Mcp/McpSessionContext.cs:Create` → `EnableQueryOnly()`); annotation writes remain CLI-only (`tests/Mcp/McpAnnotationsTests.Annotate_Gated_ReadOnly`). Stdio purity: `McpServeHandler` `ConsoleLoggerOptions.LogToStandardErrorThreshold = LogLevel.Trace` + `IOutputSink` plumbing, enforced by `tests/Mcp/McpStdioPurityTests.cs` (report `.tmp_test/MCP_LIVE_TEST_REPORT_MCP_SURFACE_2026-08-17_P2.md` §J: 158/129 stdout lines, 0 leaks) |
| 3 | Freshness contract (`lurp_status`, `lurp_refresh`, pin hardening) | ✅ COMPLETE | Commit `dfa3b6f`; `StatusTool`, `RefreshTool`, `McpSessionContext` pin logic, `tests/Mcp/McpStatusTests.cs`, `McpRefreshTests.cs`, `McpPinningTests.cs`. `--mode=serve` requires existing snapshot at startup, `McpSessionContext.Create` (`src/Mcp/McpSessionContext.cs:47`) throws `ERROR: No snapshots found in the database` if `GetLatestSnapshotId()` is null; it does not bootstrap a fresh index |
| 4 | Push-button index (`lurp_index` with progress/cancel/refresh hookup) | ✅ COMPLETE | `McpIndexSessionState` (`src/Mcp/McpIndexSessionState.cs`), `IndexTool` (`src/Mcp/Tools/IndexTool.cs`) Option B (in-process `IndexRunner.RunAsync` + `IOutputSink` + `CancellationToken`), `McpServeHandler` wiring, `McpErrorMapper` `workspace_unreadable`/`restore_required` structured data, `tests/Mcp/McpIndexTests.cs` (5 cases), manual validation per §4.11 of commit `175e52d` |

## Architecture Phase completion status

| Phase | Description | Status | Evidence |
|---|---|---|---|
| 1 | Product constitution and schema/version rules | ✅ Done | `VersionConstants`, `VersionConstants.DatabaseSchemaVersion`, `MigrationRunner` |
| 2 | Workspace, snapshot, document, configuration identities | ✅ Done | Order 4 |
| 3 | SQLite storage boundary and migrations | ✅ Done | `SqliteIndexStore`, 27 migrations; `SchemaMigrationRoundTripTests.cs` (`MigrationList_CountIs27`, `MigrationList_HighestVersionMatches_DatabaseSchemaVersion`, `MigrationList_AllVersionsAreUnique`, `RoundTrip_AllMigrations_ProducesCurrentSchema`, `ForwardMigration_FromV1Schema_PreservesSeededData`) |
| 4 | Immutable document versions and source storage | ✅ Done | Order 4, T12 |
| 5 | Stable type/member identities and declaration spans | ✅ Done | Order 5 |
| 6 | Fast `get` and lexical `search` queries | ✅ Done | Order 6; live-verified (§C) `CourseService.CreateAsync` no longer throws `fts5: syntax error` (phrase-literal quoting), punctuation `"` `*` `:()` `<>` `.` all zero `SqliteException`, `Service` substring fallback 20, `Migrations` 17→21/35→46 with `include-generated` |
| 7 | Migrate dirty state, fingerprints, diagnostics, facts | ✅ Done | Order 7 |
| 8 | Typed member-level semantic edges | ✅ Done | Order 8 + Order 13 + G1–G7 |
| 9 | Polymorphism and dispatch candidates | ✅ Done | Order 12 + Order 13 + G3, G5, G6, G7 |
| 10 | Structured semantic snapshot diffs | ✅ Done | Order 12 + G1, G2; live-verified (§F) scratch `InstrumentTypeService.LurpDiffTestMethod` → `symbol_added` + `edge_added Declares` (8 changes, 0 skipped, no misclassify) |
| 11 | Generated-code provenance | ✅ Done | Orders 10, 11 |
| 12 | ASP.NET, DI, MediatR, EF, serialization, test adapters | ✅ Done | All 6 adapters; `TestedBy` granularity fix `63cfaf4`; `GoldenAdapterTests.TestAdapter_TestProjectProducesTestedBy`, live-verified §G: neither solution uses MediatR, 0 MediatR edges, no null warnings, framework_derived 1466/89 |
| 13 | Reflection evidence ladder | ✅ Done | See below |
| 14 | Evidence-backed impact paths | ✅ Done | `ImpactTraverser`, `ImpactHandler`, `semantic_causes`, live-verified §D & §H (see Phase 14) |
| 15 | Context capsules with source and token budgets | ✅ Done | See below, live-verified §E (RelevantTestsTierBuilder fix) |
| 16 | Rebase simulations and audits | ✅ Done → Removed in Aug 2026 cleanup |
| 17 | Optimize incremental updates from measurements | ✅ Done | `CallsEdgeExtractor` 2051→1944 ms (Lurp) at time of record; member-edge coverage in `GoldenEdgeTests` |

### Phase 13 verification: Reflection Evidence Ladder

| Requirement | Edge Kind | Extractor | Tests |
|---|---|---|---|
| `typeof(T)` | `ReflectionTypeRef` | `TypeOfReflectionExtractor` | `TypeOf_EmitsReflectionTypeRefEdge` |
| `nameof` | `ReflectionMemberRef` | `NameOfReflectionExtractor` | `NameOf_EmitsReflectionMemberRefEdge` |
| String literal matching known name | `ReflectionNameCandidate` | `StringLiteralReflectionExtractor` | `StringLiteral_MatchingTypeName_EmitsNameCandidateEdge` |
| Runtime-unknown reflection | `ReflectionTargetUnknown` | `UnknownPatternReflectionExtractor` | `TypeGetType_EmitsUnknownEdge`, `ActivatorCreateInstance_EmitsReflectionTargetUnknownEdge` |

All in `src/Workspace/`, registered as `"reflection-v1"`, covered by `GoldenReflectionTests.cs`; integrated with `UncertaintyDetector`.

### Phase 15 verification: Context Capsules

All §6.2 requirements implemented: anchor (`CapsuleAnchor` with full scope/intent/snapshot metadata), `ContractsTierBuilder`, `RegisteredImplementationsTierBuilder`, `RelevantTestsTierBuilder` (shared containing-type expansion in `TestSymbolDiscovery`, was build-breaking positional-arg bug, now fixed), `UncertaintyDetector` (incl. reflection/generated exclusions + binding incompleteness), `IncomingPaths`/`OutgoingPaths` with complete spans, `VerificationSuggestion.Command`, `LikelyChangeSite` ranking, exact spans via `DeclarationReadStore.GetDeclarationLocations`, affected public surfaces, per-item `InclusionReason`. Budgeting is greedy-prefix (higher-priority items cannot be leapfrogged; every omitted tier still evaluated and recorded `budget_exhausted`/`empty`). Covered by `CapsuleCharacterizationTests.cs` (content-budget, empty-tier, unresolved-tier, gap-anchor, budget-exhausted fetch) and `ContextCapsuleAcceptanceTests.SelfHost_EdgeLocationResolver_CapsuleSatisfiesPhase15Contract`. Live-verified on both solutions (§E): 3–4 real anchors (controller action, service method, interface method with 2+ impls) all exit 0 with `relevant_tests` tier present, `estimatedTokens` ≤ `content-budget` (eNoteV2 571/283/388 at 1000, 8000 at default), `max-hops 1→3` changes expansion. `RelevantTestsTierBuilder` no longer crashes and tier actually produces results.

Closed capsule decisions: no occurrence multigraph (one evidence-bearing relation edge, not exhaustive call-site storage); no generated anchor narrative; architectural constraints come from snapshot annotations (no second JSON authority).

### Phase 14 verification: Evidence-backed Impact Paths

| Requirement | Status |
|---|---|
| `ImpactPath` with `List<ImpactHop>` | ✅ |
| Hop details (`SourceSymbolId`, `TargetSymbolId`, `EdgeKind`, `Provenance`, `SourceDocument`, `SourceLine`) | ✅ |
| Direction control (`Upstream`/`Downstream`) | ✅ verified live: `upstream 50 vs downstream 6` on `ENoteContext.SaveChangesAsync`, `IAuthenticatedUserAccessor.GetUserId` 2/50 truncated groups correct |
| Depth limiting (`maxDepth`) | ✅ verified live: `1→5, 2→6, 3→6` (eNoteV2) and `1→1,2→2,3→2` (eCommerce) |
| Edge filtering (`allowedEdgeKinds`) | ✅ verified live: `--kinds=Calls` 8 vs `Calls,MayDispatchTo` 14 |
| Provenance filtering (`allowedProvenance`, `--provenance=`, `e5bbaf0`) | ✅ verified live: eNoteV2 `IEntity.Id` pure inherited `all:1, compiler_proved:0, possible:1` vs direct `ICurrentUserService.UserId` `9/9/0`; eCommerce `IBaseCRUDService` mixed 242→186→1 is not a bug (79 compiler_proved+5 possible) |
| Cycle detection (`visited` set) | ✅ |
| Truncation explanation (`Truncated`, `TruncationReason`) | ✅ verified live: `max-paths=2` → `truncated:{reason:max_paths,total:6,remaining:4,cursor:...}` with page2 cursor |
| Semantic causes (`SemanticCauses`) | ✅ verified live: impact `semantic_causes` populated (edge_added `MayDispatchTo`); was `near "=": syntax error` from `SemanticChangesSelect` missing separator, fixed pre-run, re-run shows 0 WARNING lines |

Tests: `ImpactTraverserTests.cs`.

### Architecture §10 definitive-version checklist

All criteria ✅ except the removed simulation/audit modes (n/a). One SQLite DB (migrations 1–27); source/facts share snapshot identity; symbols link to exact spans; reads avoid Roslyn reload; incremental updates changed docs (dedup when unchanged: `No changes detected. Skipping incremental index.` counts identical); member-level typed edges; polymorphism/framework indirection keeps evidence levels; generated semantics participate without flooding; semantic diffs explain changes; impact = paths with reasons; capsules bounded; every fact states provenance + extractor version; indexer never modifies source.

---

## T1–T12: closed, implemented, and validated

Each item states the resulting current state. Citations name live tests or dated historical figures.

- **T1: Schema-version verification authoritative.** `VersionConstants.DatabaseSchemaVersion` sole reference; covered by `SchemaMigrationRoundTripTests.cs`.
- **T2: Failed snapshot cleanup atomic.** `SnapshotPruner.DeleteSnapshotData` deletes inside a transaction; covered by `SnapshotReuseResolutionTests.IncompleteExistingRow_ResolvesRetry_AndIsDeleted`.
- **T3: Orphan edges removed on both endpoints.** `EdgeOperationsStore.DeleteOrphanEdges` removes edges when either endpoint absent from `snapshot_symbols`; `OriginalDefinition` normalization keeps in-snapshot edges. Orphan drops bucketed: compiler-synthesized (`<` in id), external (out-of-scope assembly), other (actionable warning). eNoteV2 clean rebuild (402 docs, 3,656 decls, extractor 1.6.0): 0 residual `other` drops; 10,544 edges retained (90.2% `compiler_proved`, 8.9% `framework_derived`). `SymbolIdFactory.Make` normalizes to `OriginalDefinition` + un-reduces `ReducedFrom` (extractor 1.5.0); internal non-synthesized orphan drops fell 1,345→2.
- **T4: Cross-project edge dedup + merge-on-write.** `EdgeOperationsStore.SaveEdges` pre-merges via `EdgeMerge.CollapseBatch` (provenance priority `compiler_proved` > `framework_derived` > `global_implementation_relation` > `possible` > `convention` > `name_candidate` > `runtime_unknown`), then bulk (`type_arguments_json` null, ~99.95%) vs split paths. `ux_edges_relation` unchanged; `EdgeDedup.Deduplicate` at `IndexRunner.cs:264` a redundant guard. Binding tests in `EdgeWritePathTests.cs`. Known narrow gap: equal-rank tie-break keeps persisted row (not observed on real code; `CleanRebuildEquivalenceTest` backstop). Freshness (`WorkspaceFreshness`) observes file content, compilation inputs, extractor version.
- **T5–T8: Semantic-diff metadata producer/consumer contract.** `BuildMetadataJson` writes `base_type`, canonical `signature`, sorted `attributes`; `CompareMetadata` consumes all. Attributes stay in `metadata_json` (not the unimplemented `facts` table).
- **T9: Generated-output absence declared.** `MSBuildWorkspace` does not expose source-generator output; completeness persists `generated_trees_included`, TFMs, skipped adapters, extractor version, surfaced via `status --json`.
- **T10: Incremental-vs-full equivalence coverage.** Snapshot comparison covers FQNs, metadata JSON, declaration paths/spans/flags, FTS records; cases: signature, body-only, document-move, partial-class, base/interface, DI, new-overload-in-new-file. Covered by `IncrementalParityTests.cs`, `CleanRebuildEquivalenceTest.cs`, `MultiCycleConvergenceTests.cs`.
- **T11: Outcome benchmark existed, baseline established, then removed** with audit/simulate infra (`f1254fc`); not currently reproducible. `missingRelevantTests` gap closed by Phase 12 fix `63cfaf4`.
- **T12: Snapshot pruning reclaims document-version storage.** Post-prune cleanup deletes unreferenced `document_versions`/documents and repairs `last_changed_snapshot_id`; exercised by `CleanRebuildEquivalenceTest.cs` and `IncrementalParityTests.cs`.

---

## Order 12 findings: Phase 9/10 gap audit

### Phase 10 (structured semantic diff)

Already covered by `SignatureFormat` (`IncludeNullableReferenceTypeModifier`, `IncludeParamsRefOut`, `IncludeExplicitInterface`, `IncludeTypeConstraints`), locked by `SemanticDifferTests.cs`: S1 `S1_NullableAnnotationChanged`, S2 `S2_RefParameterModifierChanged`, S5 `S5_OperatorOverloadSignatureChanged`, S6 `S6_ConversionOperatorSignatureChanged`.

Remaining gaps, all closed: S8 interfaces key (`interfaces_changed`); S9 `isRecord` (`record_changed`); S10 persisted-but-uncompared modifiers (`metadata_changed` + field); S11 `semantic_changes` invalidation (`semantic_causes` on impact paths); S12 declaration move to another file (`symbol_relocated`, covered by `SemanticDifferTests.cs` relocation tests).

### Phase 9 (polymorphism/dispatch)

| ID | Gap | Resolution |
|---|---|---|
| D1 | `new` member hiding | G3 `Hides` edge |
| D2 | Generic type construction tracking | G7 `type_arguments_json` on `MayDispatchTo` |
| D3/D4 | Overloaded binary operators / user-defined cast conversions | Order 13 (`Calls` edges) |
| D5 | Indexer access vs method calls | G5 `Reads`/`Writes` edges |
| D6 | Extension method binding | G6 `ExtensionReceiver` edge |

---

## Architecture alignment and recorded deviations

| Architecture reference | Status | Reading |
|---|---|---|
| §3.4 logical `facts` table | Decided deviation | Attributes in `metadata_json`; facts table deliberately not built |
| §7.6 schema stability | Aligned | `SchemaMigrationRoundTripTests.cs` binds migration list to `VersionConstants` |
| §3.3 document identity | Aligned | `document_id` path-scoped per architecture; order-4 D1 made explicit |
| §4.1 structured semantic diffs | Aligned | Base type, signature, attributes, interfaces, record status, type kind, modifiers covered; `ImpactHandler` attaches `semantic_causes` |
| §4.4 generated semantics | Aligned, narrow | Existing plumbing used; `GeneratedTreesIncluded=false` because all generated files under `obj/`; `.g.cs` proof live |
| §4.2 atomic incremental operation | Aligned | Atomic cleanup, equivalence comparisons, edge dedup; parity tests cover |
| §9 outcome validation | Done at time of record; not reproducible | Removed with audit/simulate infra; `missingRelevantTests:false` per Phase 12 fix |

### Post-order gap register: G1–G7

| Gap | Implemented behavior | Status |
|---|---|---|
| G1 metadata producer/consumer | `BuildMetadataJson` persists sorted `interfaces`; `CompareMetadata` emits `interfaces_changed`, `record_changed`, `metadata_changed` (typeKind + modifiers) | Done 2026-07-27 |
| G2 semantic cause in impact | `ISemanticDiffStore` + `ImpactHandler` → `semantic_causes` per path; `ImpactTraverserTests.cs` | Done 2026-07-27 |
| G3 `Hides` edge | `OverridesEdgeExtractor` emits distinct `Hides`; `GoldenEdgeTests.HidesEdge_MethodHidesBaseMember` | Done 2026-07-27 |
| G5 indexer Reads/Writes | `CallsEdgeExtractor` resolves `ElementAccessExpressionSyntax` to indexer; `GoldenEdgeTests.ReadsEdge_*`/`WritesEdge_*` | Done 2026-07-27 |
| G6 extension receiver | `AddCallEdge` emits `ExtensionReceiver` for instance-style call; `GoldenEdgeTests.ExtensionReceiverEdge_*` | Done 2026-07-27 |
| G7 generic dispatch type args | `InterfaceDispatchExtractor.GetTypeArgumentsJson` → `type_arguments_json`; merge covered by `IncrementalParityTests.Parity_GenericBaseWithMultipleConcreteImplementations_MergesDispatchTypeArguments` | Done 2026-07-28 |

### Order 13: approved dispatch/diff capability

Narrow D3/D4: capture compiler-resolved overloaded operators and user-defined casts in `Calls` contract. `CallsEdgeExtractor` keeps built-in operators out, dedups by source/target/kind. Covered by `GoldenEdgeTests.CallsEdge_MethodCallsMethod` + parity tests.

---

## Source-generator support: reassessed 2026-07-28

Observed: (1) no source-generator packages in any `.csproj`; (2) all `.g.cs` under `obj/` (compiler implicit-usings, not SG output); (3) `IsBuildOutputPath` filters `obj/`+`bin/`; (4) `GeneratedTreesIncluded` hardcoded `false`; (5) plumbing proven correct via injected-`.g.cs` test `GeneratedCodeProvenanceTests.GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges`. Decision: `GeneratedTreesIncluded=false` is accurate, not a gap. `GeneratorDriver` execution correctly postponed. Revisit if a NuGet SG package is added or a generated file appears outside `obj/` with meaningful symbols.

Update 2026-08-07 (Gap #9): `IsCrossGenerated` now reaches polymorphism and reflection lineages (`MayDispatchTo`, `StaticallyCalls`, all four reflection kinds), parity with member-edge/adapter lineages. The generated *set* is authoritative by path (`DocumentId.ToString()` = git-relative path, same form `EdgeLocationResolver.IsGenerated` normalizes).

---

## Fix log (Aug 2026; full narratives in commit history / `LIVE_TEST_FIXES.md`)

| Date | Area | Fix | Evidence |
|---|---|---|---|
| 08-05 | Deterministic snapshot IDs | `SnapshotIdentity.Create` production default; `SnapshotId.New()` tests-only | `SnapshotIdentityCompletenessTests.cs` |
| 08-05 | `SqliteIndexStore` decomposition | Facade forwards to leaf stores; `EdgeStore`/`SearchStore` removed | n/a |
| 08-06 | Incremental doc-scoped re-extraction | `ExtractReplacementFacts` + `PrepareSnapshotData` narrow in lockstep; cross-doc refresh load-bearing; closures seed from binding incompleteness; adapters honor scope; doc-less binding falls back to containing type; CLI doc paths normalized to stored form | historical eNoteV2 run |
| 08-07 | `--solution=` on every mode | `CliFlagValidation.GlobalFlags`; `ResolveOutputDir` fallback; `LURP_SOLUTION_PATH` env | n/a |
| 08-07 | `impact` default `--max-depth` 10→3 | `ImpactHandler.Run` | finding 9 |
| 08-07 | `context`/`impact` accept bare FQN/doc-comment ID | `HandlerBootstrap.ResolveSymbolArg` | finding 1, `d63d251` |
| 08-10 | Snapshot identity covers compilation inputs | `MetadataReferenceIdentities` (SHA-256 of assembly bytes) + `CompilationOptionsFingerprints`; migration 27 nullable | R2 (`AssemblyIdentityGranularityTests.cs`) |
| 08-11 | R1: 5-cycle incremental↔full convergence | Verified B≡C on eNoteV2 + FIT-RS2-2026 (post R6 fix); scripts `r1-verify-*.sh` + `r1-compare/` | historical |
| 08-11 | R6: binding-incompleteness carry-forward parity | Scope save to deletion-set; null-path EXCLUDE-AND-CARRY-FORWARD | `BindingIncompletenessScopingTests`, `ScenarioR6_*`; post-fix R1 PASS |
| 08-11 | R3: overload-resolution BFS gap | Widen BFS seed + extraction scope on added-file events | `ScenarioR3_NewOverloadInNewFile_RebindsUneditedCaller`; `5923efe` |
| 08-12 | R4: cross-compiler symbol-identity stability | Characterized; full-rebuild gate on `CompilerChanged` is the mitigation | historical |
| 08-12 | R5: non-conventional DI/MediatR coverage | Pinned: `AddHostedService`→`RuntimeUnknown`; stream handlers silently skipped | `GoldenAdapterTests` R5 suite |
| 08-13 | R7: cross-doc dispatch type-arg aggregation lockstep | `UnionWith` changed docs into step-7 refresh scope | `ScenarioG_ChangeImplementedInterface_*`, post-fix R1 PASS |
| 08-13 | T1: read-mode freshness signal | `get-source`/`get-symbol`/`navigate` wired to freshness; `--require-fresh` exit 2 | `ReadModeFreshnessTests.cs` |
| 08-13 | T4: 1-based line numbers on emit | `LineNumbers.ToOneBased` single choke point; storage stays 0-based | `LineNumberBaseTests.cs` |
| 08-13 | T3: lightweight "where is X defined?" | `find-symbol`/`get-symbol --view=metadata` gain `locations[]`; `navigate` gains 1-based lines | `SymbolLocationRetrievalTests.cs` |
| 08-14 | R8 (3 live-test defects) | (1) full-index diff after orphan cleanup; (2) `--snapshot=latest` resolved; (3) punctuation-only search returns 0 | `LIVE_TEST_FIXES.md` Tasks 1–3; `SemanticDifferTests`, `CliExitSmokeTests.ResolveSnapshotId_Latest_*`, `StoreReadPathTests`/`CapsuleCharacterizationTests` |
| 08-17 | MCP stdout-purity + `lurp_status` freshness + `lurp_timings` (13th tool) | Stdout leak: `ConsoleLoggerOptions.LogToStandardErrorThreshold = LogLevel.Trace` (`src/Mcp/McpServeHandler.cs`) + `IOutputSink` plumbing for `Console.*` in `src/Workspace/`, every stdout line now JSON (`tests/Mcp/McpStdioPurityTests.cs`). `lurp_status` full-method freshness: pinned snapshot now loaded with documents (was metadata-only, mis-reported 397/402 stale). `lurp_timings` 13th MCP tool added. | `McpStdioPurityTests` Passed 1/1; report `.tmp_test/MCP_LIVE_TEST_REPORT_MCP_SURFACE_2026-08-17_P2.md` §H (fresh 0) + §J (0 leaks /158 + /129 lines) + §Tools present (13 tools) |

---

## Open findings for follow-up

1. **`search` → `context`/`impact` round-trip.** Resolved 2026-08-07 (`d63d251`): bare FQN/doc-comment ID accepted; `FormatException` guarded by `ContextHandler.ValidateSymbolIdFormat`.
2. **`impact --output=summary` names symbol IDs, not human-readable names.** Low severity; `--output=json` richer.
3. **`ContextBudgeter` tier costing source-only.** Low severity; `CapsuleBudgetEnforcer` re-measures and is authoritative.
4. **Capsule budget defaults exhaust quickly on type-level anchors.** `--content-budget=4000` zeroes `directCallers`/`relevantTests` on a 12-method service. Decide whether default should scale with anchor kind.
5. **`estimatedTokens` vs `estimatedArtifactTokens` differ ~2×.** Documented design choice; not reopened.
6. **Temporary capsule-output artifacts stale/pre-fix.** Regenerate before judging output quality.
7. **`EffectiveSymbolIds` design question.** Whether dispatch-interface members should be included; recorded, not acted on.
8. **MusicLibrary dispatch reproduction deferred.** Externally blocked; recorded.
9. **`impact` default `--max-depth=10` unbounded BFS.** Resolved 2026-08-07 (default→3).
10. **Incremental `binding_incompleteness.occurrence_count` undercount (REAL parity bug).** CLOSED 2026-08-11 (R6); regression `ScenarioR6_BindingIncompleteness_IncrementalCarryForward`.
11. **`--mode=timings` does not honor `--snapshot=latest`.** `TimingsHandler.Run` bypasses `ResolveSnapshotId`; deferred by explicit decision to keep the fix single-site.

---

## Explicitly postponed

- Multi-TFM per-framework indexing.
- DI adapter matching by parameter type instead of containing-type name.
- Concurrency, daemon, or server architecture.
- Explicit source-generator execution with a `GeneratorDriver`.

## Declared boundaries registry (capsule audit Task 7)

**Source:** `src/Workspace/DeclaredBoundaries.cs`. Single-source registry of construct classes Lurp does not model. `RegisteredImplementationsTierBuilder` reports `"unmodeled_construct"` (not `"empty"`); `UncertaintyDetector` derives descriptions from it. Policy: new unmodeled construct → add a registry entry, not a new extractor.

| Id | Construct Class | Description |
|---|---|---|
| `di_hosted_service` | `AddHostedService<T>` | Concrete type resolved, activation semantics not captured |
| `di_options` | `Configure<T>` / `AddOptions<T>` | Options type resolved, binding semantics not captured |
| `di_external_extension` | External IServiceCollection extensions | Source outside compilation, semantics unknown |
| `masstransit_consumer` | MassTransit consumer registration | No adapter; wiring edges never emitted |
| `ef_convention` | EF Core model conventions beyond query filters/indexes | Fluent API model building not modeled |
| `shape_similarity` | Semantic sibling similarity | No compiler oracle; deliberately not modeled |

## Reclassified as done

- **Deterministic snapshot IDs** (`SnapshotIdentity.Create` default; `SnapshotId.New()` tests-only), `SnapshotIdentityCompletenessTests.cs`.
- **`SqliteIndexStore` decomposition** into leaf stores (08-05).
- **Incremental re-extraction document-scoped** with lockstep delete/extract (08-06).
- **Snapshot identity covers compilation inputs** (NuGet refs + compilation options hashed; migration 27) (08-10).
- **`--force` re-extracts without bypassing identity** (deletes complete snapshot, id stays identical).
