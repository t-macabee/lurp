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

### Compiler-language-version fidelity — restore the evaluated language version (2026-08-01)

Closes task #1 of `docs/reference/CONTEXT_CAPSULE_COMPLETENESS_AUDIT.md`.
`IndexRunner` opened every solution with `MSBuildWorkspace.Create()` and took the
parse options straight from `OpenSolutionAsync`. When MSBuild evaluation of a
project fails (observed on the external `eNote` solution, whose SDK-style
projects declare no `<TargetFramework>` and no `<LangVersion>`, so even
`dotnet build` fails with "Invalid framework identifier ''"), MSBuildWorkspace
silently falls back to **C# 7.3** parse options — not the project's evaluated
language version. The eNote snapshot therefore compiled C# 10+ source as C# 7.3:
914 CS8370 diagnostics, `CourseService.cs` at `compiler_error: 190`, and
semantic edges that depend on modern syntax binding were suppressed.

`LanguageVersionRecovery.Apply` (`src/Workspace/LanguageVersionRecovery.cs`) now
runs immediately after `OpenSolutionAsync` and derives each affected project's
effective language version from its own inputs instead of the fallback: an
explicit `<LangVersion>` property is authoritative (parsed from the project
file), an SDK-style project with no explicit `LangVersion` uses the SDK default
(`latest` → `LanguageVersion.LatestMajor`), and non-SDK projects are left at
their correct C# 7.3 default. Projects whose parse options are already correct
are untouched, so healthy solutions are unaffected. The corrected `Solution` is
used by both the full and incremental index paths (and therefore by
`CrossDocumentEdgeRefresher`).

Regression fixture `tests/fixtures/LanguageVersionFallback/` reproduces the
mismatch: `Modern/` is an SDK-style project with no `TargetFramework` (MSBuild
evaluation fails → fallback) and modern C# source; `ExplicitLang/` pins
`LangVersion=9.0` to prove explicit versions are honored, never clobbered.
`LanguageVersionFallback_ModernSyntax_NoCs8370_And_ControllerCallsBind` fails
without the fix (CS8370 present) and passes with it: the snapshot is `complete`,
**0 CS8370**, and `InstructorCourseController.GetMyCourses/GetById →
ICourseService.GetPagedForInstructorAsync/GetByIdForInstructorAsync` persist as
`compiler_proved` `Calls` edges.

Re-indexing `eNote.sln` (snapshot `ae1b3254...`): all 7 projects recovered to
C# 14, **CS8370 914 → 0**, `CourseService.cs` `compiler_error` 190 → 1, and the
controller constructors now bind a `References` edge to `ICourseService`. The
controller→interface *method* calls still do not bind for eNote — that is the
separate, genuinely-broken cross-project reference resolution (no TFM →
`ResolvePackageAssets` fails → no metadata references), honestly surfaced via
`binding_incompleteness` (`unresolved_metadata`/`filtered_external`), not a
language-version defect; the fixture proves the calls-persist path in a
workspace whose references are resolvable.

Validation: `dotnet build Lurp.slnx` clean (0 warnings, 0 errors);
`LanguageVersionRegressionTests` 1/1, `RealSolutionIntegrationTests` +
`T9CompletenessTests` + `OutcomeBenchmarkTests` 16/16, `CleanRebuildEquivalenceTest`
+ `CliDispatchTests` 7/7.

### Capsule budget truthfulness and enforceability (2026-08-01)

Closes task #3 of `docs/reference/CONTEXT_CAPSULE_COMPLETENESS_AUDIT.md`.
Previously `estimatedTokens` counted only anchor + tier-item source, and
`incomingPaths`/`outgoingPaths`/`topology`/`completeness.binding_incompleteness`
were appended after the budget was applied, untoken-counted and untruncated: an
eNote CourseService capsule serialized to ~1 MB while reporting
`estimatedTokens = 1466` under `--budget=4000`.

- `CapsuleBudgetEnforcer` (`src/Workspace/CapsuleBudgetEnforcer.cs`) runs after
  every section is populated and applies the architecture's greedy priority
  policy. The budget measures **content**: the source text of the anchor and
  every capsule item plus the serialized weight of the substantive non-source
  sections (paths, topology, constraints, completeness, uncertainties,
  verification, likely change sites, affected public surfaces, inclusion
  reasons). Per-item identity/provenance framing (symbol ids, fully-qualified
  names, edge kinds, provenance, coordinates) is navigation metadata and is not
  counted — this keeps a 4,000-token budget able to hold a type anchor's
  member-level tiers, which the whole-JSON measure could not (it forced the
  enforcer to drop `directCallees`, `registeredImplementations`,
  `surroundingSource`, and `uncertainties` outright). Over-budget capsules
  first bound the path sections (a `summarized` entry), then bound tier-item
  source text to a per-item cap (also `summarized`), then clear the
  lowest-priority sections greedily; every omitted/summarized category is
  recorded in `omittedTiers` and `truncatedCategories`. The anchor is never
  dropped; if it alone overflows the budget, that overflow is declared with
  `budget_exhausted`.
- `estimatedTokens` is the settled content measure of the exact capsule the
  handler writes (`ContextCapsuleJson.Serialize`), so it always describes the
  emitted artifact's content within the requested budget.
- `CapsuleTopology.Current` is a reference summary (direction, path and hop
  counts, `"see incomingPaths"`/`"see outgoingPaths"`) instead of a full copy of
  the path collections; the paths are serialized once under
  `incomingPaths`/`outgoingPaths`.
- `SnapshotCompleteness` carries `binding_incompleteness_summary` (a
  deterministic reason/project rollup) and `binding_incompleteness_total` by
  default; per-document rows are emitted only behind `--completeness-detail`.
- CLI: `--completeness-detail` documented in `--help` and `src/README.md`.

Completion criterion verified against the eNote snapshot (`870ebdf6...`, the
post-language-version re-index): `--budget=4000` emits a 34,095-byte capsule
with `estimatedTokens = 3549 <= 4000`, `truncated = true`, and — from a type
anchor — `contracts=1` (ICourseService, `Implements/compiler_proved`),
`directCallees=6` (Calls + `MayDispatchTo/compiler_proved` dispatch targets),
`registeredImplementations=7` (all graded `MayDispatchTo/compiler_proved`),
`surroundingSource=6` (bounded member source), and 4 reason-distinguished
`binding_incompleteness` uncertainties. Every omitted/summarized category is
reason-coded (`empty` for genuinely empty tiers, `summarized` for bounded
content, `budget_exhausted` for cleared sections).

Validation: full suite 275/275 pass. `CapsuleBudgetEnforcerTests` 7/7,
`ContextBudgeterTests` 2/2, `ContextCapsuleAcceptanceTests` 1/1,
`OutcomeBenchmarkTests` 1/1 (baseline regenerated with truthful estimates).

### Unreadable-workspace gate and the empty/unresolved distinction (2026-08-01)

Prompted by an inspection of the six capsule sets under `test-output/`. The
language-version work above (§"Compiler-language-version fidelity") had already
*diagnosed* the underlying condition for eNote — no TFM → `ResolvePackageAssets`
fails → no metadata references — but nothing **enforced** it. Lurp continued to
index reference-less compilations, mark the snapshot `complete`, and serve
capsules from the resulting near-empty graph.

Measured state of the shipped artifacts before this change:

| Run | Symbols | `Calls` edges | Error diagnostics | Top error |
|---|---|---|---|---|
| `music-library-review` | 913 | 85 | 3,177 | CS0518 × 2,293 |
| `enote-architecture-check` | 2,800 | 387 | 17,010 | CS0518 × 11,104 |
| `enote-remediation-review` / `enote-validated` | 3,146 | 389 | 16,407 | CS0518 × 11,632 |
| `fit-rs2-2026` / `-retry` | 1,641 | 52 | 716 | CS0246 × 355 |

CS0518 (`predefined type 'System.Object' is not defined`) is never a source
defect — it means the compilation had **no corlib**, i.e. MSBuild never handed
Roslyn a reference set. All six snapshots were nevertheless marked `complete`.
The concrete harm is the reason code, not the missing edges: `TokenService` in
`music-library-review` had zero incoming `Calls` edges and its capsule reported
`directCallers: []` with `omittedTiers` reason **`empty`** — a proved-absence
claim — when the truth was that no binding over that region was observable.

**Implemented.**

- `WorkspaceLoadGate` (`src/Workspace/WorkspaceLoadGate.cs`) classifies each
  compilation before extraction. `GetSpecialType(SpecialType.System_Object)
  .TypeKind == TypeKind.Error` ⇒ `CompilationReadability.Blind`. Blind projects
  are skipped, and every one of their documents is recorded as
  `binding_incompleteness` reason `project_unreadable`.
- `IndexRunner` no longer throws `AggregateException` when any project fails.
  A failing project is isolated and recorded; its siblings' facts are retained.
  The hard stop fires **only** when `extractedProjects == 0` — no readable
  project anywhere — raising `WorkspaceUnreadableException` and marking the
  snapshot `failed` with reason code `workspace_unreadable`. Previously one bad
  project in an *n*-project solution discarded all *n*.
- `BindingIncompletenessReason.UnobservableReasons` names the reasons under
  which a missing relation proves nothing: `ambiguous_overload`,
  `compiler_error`, `unresolved_metadata`, `unsupported_syntax`,
  `extractor_failure`, `project_unreadable`. `filtered_external` is deliberately
  **excluded** — there the target was resolved and is knowably outside the
  snapshot, an explained absence rather than an unknown one.
- `ContextBudgeter.Apply` takes `anchorBindingIsIncomplete` (defaulted, so
  existing callers and tests are unaffected) and emits `unresolved` instead of
  `empty` for empty tiers when
  `ContextAssembler.AnchorRegionHasLostBindings` matches the anchor's documents
  against the unobservable-reason set. The same distinction is applied to
  `affectedPublicSurfaces`, and an `inclusionReasons["omittedTiers.unresolved"]`
  entry states in-band that `unresolved` is not evidence of absence.
- `Program.Main` catches `WorkspaceUnreadableException` and exits `2` with the
  remediation text only — a diagnosed refusal, not a stack trace.

**Verification (MusicLibrary, the worst of the six).** Pre-restore the gate
refuses: `[API] UNREADABLE: no metadata references resolved; skipped.`, snapshot
`failed (workspace_unreadable)`, exit 2. After `dotnet restore` on the *same*
solution with the *same* command:

| | before | after |
|---|---|---|
| Edges | 1,844 | **4,622** |
| `Calls` edges | 85 | **113** |
| Error diagnostics | 3,177 | **0** |
| Unobservable bindings | 2,056 | **0** (4,189 `filtered_external` only) |

`AccountController.Login` and `.Register` now resolve as callers of
`ITokenService.CreateToken`. The healthy run carries 4,189 `filtered_external`
records and still correctly reports `empty`, not `unresolved` — the
reason-partition is load-bearing and is exercised by that path.

Validation: `dotnet build Lurp.slnx` clean (0 warnings, 0 errors);
`WorkspaceLoadGateTests` 9/9 and `ContextBudgeterTests` 2/2 pass. Full suite not
re-run — do that before merging.

#### Open findings for follow-up

1. **Capsules do not resolve callers through `MayDispatchTo` (open).** With a
   correct graph, a capsule anchored on a *concrete* implementation reports zero
   callers. Callers bind to the interface member, and the concrete symbol is one
   `MayDispatchTo` hop away that `DirectCallersTierBuilder` does not walk.
   Reproduction on the restored MusicLibrary index:
   `--mode=context --symbol='M:API.Interfaces.ITokenService.CreateToken(API.Entities.AppUser)|…'`
   returns `directCallers = [AccountController.Register, AccountController.Login]`;
   `--symbol='M:API.Services.TokenService.CreateToken(API.Entities.AppUser)|…'`
   returns `[]` with reason `empty`; the type anchor `T:API.Services.TokenService`
   likewise returns `[]`. The concrete symbol is what callers most often anchor
   on, so this is the same class of wrong claim as the one closed above, one
   layer up. Investigate whether `ContextTierContext.EffectiveSymbolIds` is
   meant to include dispatching interface members, or have the builder walk
   incoming `MayDispatchTo` edges before tracing `Calls` upstream. **Provenance
   must not launder**: a caller reached via dispatch is weaker evidence than a
   direct compiler-resolved call and the capsule item must say so. The
   laundering clause is now closed (`docs/reference/CAPSULE_PROVENANCE_FIX.md`):
   `DirectCallersTierBuilder`/`SecondDegreeContextTierBuilder` walk incoming
   `MayDispatchTo` edges before tracing `Calls` upstream, and dispatch-mediated
   items are classified `indirect_dispatch_candidate` / `direct: false` with
   `possible` claim provenance (or `framework_derived` only when a framework
   edge participates in the path). The callee mirror is closed in the same
   fix: `DirectCalleesTierBuilder` projects dispatch targets under
   `global_implementation_relation` / `indirect_dispatch_candidate` /
   `direct: false` while direct callees stay `compiler_proved`. The
   zero-callers reproduction above and the `EffectiveSymbolIds` question
   remain open.
2. **`test-output/` artifacts are stale and pre-fix — do not treat them as
   current evidence.** Every capsule there was produced from a reference-less
   compilation that the gate now refuses outright. In particular the 973 KB
   `enote-architecture-check` CourseService capsule reporting
   `estimatedTokens = 1466, truncated = false` **predates**
   `CapsuleBudgetEnforcer` (commit `19aefc8`); the post-fix capsule for the same
   symbol is `enote-validated` at 34,095 bytes with `estimatedTokens = 3549` and
   `truncated = true`, exactly as §"Capsule budget truthfulness" records. The
   budget defect is closed; do not reopen it from these files. Regenerate the
   folder against restored solutions before using it to judge output quality.
3. **`ContextBudgeter` tier costing is still source-only.** `EstimateTokens(
   item.Source)` charges nothing for path-shaped tiers that carry no `Source`.
   This is no longer observable in emitted capsules because
   `CapsuleBudgetEnforcer` re-measures the settled artifact afterward and is the
   authority, but the tier-level greedy-prefix decisions are made on an
   incomplete measure, so tier *selection* can still be skewed. Low severity,
   worth tidying when that area is next touched.

### Convention-based DI and helper-mediated test evidence — framework-evidence contract (2026-08-01)

Closes task #5 of `docs/reference/CONTEXT_CAPSULE_COMPLETENESS_AUDIT.md`.
`DependencyInjectionAdapter` (since commit `a8acd7a`) already carries a
convention path for Scrutor-style registration: `ProcessConventionCandidate`
handles `Scan`/`AddClasses`/`AsImplementedInterfaces`/`AsMatchingInterface`/
`UsingRegistrationStrategy`/`AddAssemblyTypes` and resolves the scanned
assembly from the `FromAssembliesOf`/`FromAssemblyOf` argument (generic or
`typeof(T)` form). It emits a `Registers` edge with **`convention`**
provenance (never `compiler_proved`) targeting `convention:assembly_scan:<assembly>`
with `TargetNodeKind = Convention`. The audit's snapshot predated this path
(0 `Registers` edges), which is why the gap was confirmed. The path is now
locked by focused adapter contract tests:

- `DI_ScrutorScan_FromAssembliesOf_EmitsConventionRegistersEdge` — generic
  marker form; asserts `Registers` + `Provenance.Convention` +
  `convention:assembly_scan:TestAssembly` + `GraphNodeKind.Convention`.
- `DI_ScrutorScan_FromAssembliesOfTypeOf_EmitsConventionRegistersEdge` — the
  `FromAssembliesOf(typeof(T))` form used by eNote.
- `DI_ExplicitGeneric_NotCompilerProved` — `AddScoped<,>` emits
  `framework_derived` (never `compiler_proved`, never a convention node),
  guarding the provenance distinction the audit required.
- `ScrutorStubs` minimal fixture (`tests/UnitTest1.cs`) models the selector
  chain (`Scan` → `FromAssembliesOf` → `AddClasses` → `AsImplementedInterfaces`).

Test-evidence behavior for helper-mediated construction is now **explicitly
defined** rather than inferred from an empty tier. `TestAdapter` scans
test-method bodies only; that scope is the product boundary, not a gap:

- `TestAdapter_HelperConstructedService_InvokedInTestBody_EmitsTestedByEdge` —
  a test that obtains the service from a local non-test helper and invokes a
  member in the test body **does** produce a `TestedBy` edge (the invocation
  binds the production type). This is the eNote `CourseEnrollmentServiceTests`
  pattern; its missing edge in the audit snapshot was caused by the degraded
  compilation, not by the helper.
- `TestAdapter_HelperConstructedService_OnlyConstructedInHelper_EmitsNoTestedByEdge` —
  a test body that only calls the helper and never references the service type
  emits no edge. This is a declared boundary: construction inside a non-test
  helper is not evidence the test exercises the service, and `CourseService`
  itself genuinely has no tests (0 references across all test documents).

No `TestAdapter` extension was made; extending to follow helper bodies would
over-claim coverage that the audit explicitly ruled out for `CourseService`.

Validation: `dotnet build Lurp.slnx` clean (0 warnings, 0 errors);
`B5AdapterTests` 20/20, `TestedByContractTests` + `GraphNodeMembershipTests`
16/16.

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
`metadata_json`. The `--mode=search --kind=<SymbolKind>` flag is now
documented in both `--help` (SEARCH section) and `src/README.md`, and the
annotate flag was renamed to `--annotation-kind` to remove the semantic
collision (task-list #31/#32/#44).

### Audit remediation — build fix and duplicate-helper consolidation (2026-08-01)

Closes the remaining actionable items from `docs/reference/AUDIT_FINDINGS.md`
(#3, #13, #18, #19, #23, #25 partially scoped out, #31/#32/#34/#44 — see
above). `FluentMigrator.Runner` and `System.Security.Cryptography.Xml` were
removed from all three `.csproj` files (unused, source of the MSB3270
warning); `SqliteIndexStore.Open(string)`'s dead parameter was dropped in
favor of `Open()`; `Program.Main` is now `async Task Main` awaiting the
handler delegates instead of blocking with `.GetAwaiter().GetResult()`; the
`[measure]` extractor timing lines are now gated behind `--verbose`.

Duplicate-helper consolidation (#18) was left mid-refactor with a build
break: `PolymorphismExtractionContext.GetLocationInfo` was defined twice
(CS0111) after `ExtractionContextBase` was introduced to hold the shared
`GetOrCreateSemanticModel`/`GetLocationInfo`/`GetNamespaceTypeMembers`
logic — both copies in `PolymorphismExtractionContext` were dead weight
since the base class already provides it; removed. The last outstanding
duplicate, `IsWriteContext` (identical bodies in `ReadsWritesEdgeExtractor`
and `CallsEdgeExtractor`), was extracted to
`SyntaxNodeExtensions.IsWriteContext(this SyntaxNode)` in
`src/Workspace/SyntaxNodeExtensions.cs`. `dotnet build Lurp.slnx` now
reports 0 errors, 0 warnings.

`GitIgnoreMatcher`↔`WorkspaceInfo` and the
`CompilationFactExtractor`/`CrossDocumentEdgeRefresher`/`IncrementalIndexer`/
`IndexRunner` "cycle" flagged by `tokensave_circular` (#17) were checked
against actual source references: in both cases the dependency is
one-directional (`WorkspaceInfo` → `GitIgnoreMatcher`;
`CrossDocumentEdgeRefresher`/`IncrementalIndexer`/`IndexRunner` →
`CompilationFactExtractor`, never the reverse). No real reference cycle
exists — `tokensave_circular`'s file-level detector is a false positive
here (confirmed by grep, not by trusting the tool's own output alone), so
no refactor was made.

`repomix-output.xml`, `test-output/`, and the one-off
`docs/reference/AUDIT_FINDINGS.md` report itself were added to `.gitignore`
(source-bearing/non-durable artifacts, audit #9).

### Audit remediation — closing #25 and #45 (2026-08-01)

**#25 (per-row insert perf):** `EdgeOperationsStore.SaveEdges`'s actual
defect wasn't the per-row loop (SQLite handles that fine inside one
transaction) — it was that `command.CommandText` was reassigned every
iteration despite being identical text every time, forcing SQLite to
re-prepare the same statement for all 20,057 edges on the self-index. Fixed
by preparing the statement and its parameter objects once outside the loop
(matching the pattern the same method already used for `nodeCmd`/`memberCmd`)
and only mutating `.Value` per row inside the loop. `SnapshotLifecycleStore`'s
insert loops (`InsertDocumentsAndBindings`, `InsertProjectGraph`) were
checked and already reuse a prepared command with `Parameters.Clear()` per
row — not the same defect, left as-is. Verified via
`SqliteUpsertTests`/`SaveEdges`-related tests (9/9 pass) and a full solution
build (0 errors, 0 warnings).

**#45 (CLI dispatch untested):** added `tests/CliDispatchTests.cs`. Because
`Program.Main`/`HandlerBootstrap` call `Environment.Exit(1)` directly on
error paths, an in-process test would kill the test host, so these run the
built `Lurp.dll` as a subprocess (same approach as the audit's own CLI-smoke
evidence) and assert exit code + stdout/stderr: no-args/`--help`/
`--mode=help` all print help and exit 0; `--mode=bogus` and a missing
`--mode=` flag both exit 1 with `ERROR: Unknown mode`; `--mode=status` and
`--mode=get-source` with no `--output-dir` both exit 1 mentioning
`--output-dir`. 7/7 pass.

**Follow-up fix (2026-08-01):** a full-suite run (261 tests, ~7 min under
parallel load) caught `CliDispatchTests` flaking on the three non-zero-exit
cases with empty captured `stderr`. Root cause: `Process.WaitForExit(int)`
can return before the async `OutputDataReceived`/`ErrorDataReceived`
callbacks finish draining, and fast-exiting error paths lose the race under
load. Fixed per the documented .NET pattern — call the parameterless
`WaitForExit()` immediately after the timed overload to block until the
redirected-stream callbacks flush. Verified with 5 consecutive isolated runs
(7/7 pass each); the fix addresses the actual race, not just its timing
window, so it should hold under full-suite parallelism too.

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
`framework_derived` > `global_implementation_relation` > `possible` >
`convention` > `name_candidate` > `runtime_unknown`), then calls `SaveEdges`
once. The unique index `ux_edges_relation` is unchanged — no relaxation, no
migration-side dedup.
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

### CI reliability — Release/Debug path mismatch and a Console.Out capture race (2026-08-02)

CI (`.github/workflows/ci.yml`) had been red on every run since it was added
(`7c14046`, 2026-07-17) — never once green. Two independent, unrelated bugs
were compounding:

- **`CliDispatchTests` hardcoded a `bin/Debug/net10.0/Lurp.dll` path**
  (`tests/CliDispatchTests.cs`) with no fallback. CI builds and tests in
  `Release` only (`ci.yml` lines 25/30); the Debug artifact never exists on a
  clean CI clone, so all 7 subprocess-dispatch tests failed
  `File.Exists`. This passed locally only because a stale `bin/Debug/` from
  earlier local `dotnet build` runs happened to still be on disk. Fixed:
  `LurpDllPath` now checks `bin/Release` first, falls back to `bin/Debug`.
  Verified both with the stale Debug folder present and with it temporarily
  removed (matching a clean CI clone) — 7/7 pass either way.
- **A process-wide `Console.Out` race under xUnit's default parallel test
  execution.** `RealSolutionIntegrationTests`, `WorkspaceLoaderTests`, and
  `T9CompletenessTests` redirect `Console.Out` to a private `StringWriter` to
  capture pipeline output, but `WorkspaceLoader`/`IndexRunner`/
  `IncrementalIndexer` write via raw `Console.Write`/`WriteLine` with no
  locking, and the test project had no `[assembly: CollectionBehavior]`
  configured — xUnit v2's default is to run test classes in parallel. Any
  test writing to console while another test's capture is active can leak
  into that capture buffer. Observed directly on CI:
  `WorkspaceLoaderTests.Loader_RealFixtureLoad_EffectiveVersions_AndDisposal`
  captured `"Loading solution... \r\nIndex complete for "` — the second half
  is a string that only exists in `IndexRunner.cs`, a code path this test
  never calls, proving cross-test leakage rather than a formatting change.
  Fixed by adding `tests/AssemblyInfo.cs` with
  `[assembly: CollectionBehavior(MaxParallelThreads = 1)]`, serializing the
  whole test assembly. This is a real, accepted cost/correctness tradeoff:
  local full-suite time went from ~7-9 minutes (parallel) to 779s/~13 minutes
  (serial).

Validation: full suite 306/306 pass, 0 failed, 0 skipped (user-run, not
Claude — see the "no full test runs" operating rule).

**Fixed (2026-08-02):** the CI step "Run clean-rebuild equivalence test"
(`ci.yml` line 49) filtered on
`FullyQualifiedName=Lurp.Storage.Tests.CleanRebuildEquivalenceTest.IncrementalIndex_Matches_FullRebuild_AfterSingleFileChange`.
The class had been renamed to `PipelineEquivalenceTest`
(`tests/CleanRebuildEquivalenceTest.cs:18`) without updating the workflow
filter. `dotnet test` with a filter matching zero tests exits 0, so this step
had been silently passing while running nothing since the rename — the
"blocks the merge" gate described in its own comment never actually ran.
The filter now targets `Lurp.Storage.Tests.PipelineEquivalenceTest`, verified
against the current class/method names in source. Not yet re-run on CI to
confirm the step executes and passes for real.

A full CI run completed 2026-08-02 (~3h20m — the 20x flake-guard loop over a
now-serialized, ~13-minute suite) after the Debug/Release path and
`Console.Out` fixes above; the equivalence-test filter fix landed in a
separate, later commit (`d648d26`) and has not yet had its own CI run.

### PR-1 — Snapshot-scoped declaration joins in search and FQN resolution (2026-08-03)

Source: `LURP-INDEPENDENT-EVALUATION.md` (2026-08-03) findings D1/D2, prioritized
as PR-1 in `LURP-REMEDIATION-PLAN.md`. Both documents are external planning
artifacts (user's desktop), not part of this repository.

**Defect:** `SearchStore.SearchSymbols`, `SearchSymbolsBySubstring`, and
`ResolveSymbolByFqn` joined/subqueried `declarations` on `symbol_id` alone.
`declarations` rows are keyed to immutable document versions and are retained
across snapshots (retention keeps 3 by default), while `symbol_fts`/
`snapshot_symbols` are correctly snapshot-scoped. With N retained declaration
rows for one symbol, search returned that symbol N times and
`ResolveSymbolByFqn` reported `declarationCount` summed across all retained
snapshots, not the requested one.

**Fix (`src/Storage/SearchStore.cs`):** every declaration predicate is now
scoped through `snapshot_documents(snapshot_id, document_version_id)`.
- `SearchSymbols` / `SearchSymbolsBySubstring`: the unscoped `LEFT JOIN
  declarations` (which existed solely to evaluate `is_generated`, no
  projected column) is replaced with a scoped `NOT EXISTS`/`EXISTS`
  predicate pair — preserving admission of declaration-less (metadata-only
  external) symbols, which a naive scoped inner join would have dropped.
- `ResolveSymbolByFqn`: `decl_count` and `is_partial` subqueries and the
  existence check are scoped the same way, in both the exact-match and
  prefix-match branches.

No schema migration, no write-path change, no `DISTINCT`/application-side
dedup (rejected per the plan — would mask the defect and collapse genuine
partial-type multiplicity). Existing indexes
(`idx_declarations_symbol_id`, Migration 003; `ux_snapshot_documents`,
Migration 019) already support the scoped predicates.

**Tests added** (`tests/UnitTest1.cs`, `SymbolStoreTests`/`FtsSearchTests`
region, 12 new `[Fact]`s): a three-retained-snapshot fixture (`Retain.Alpha`,
one symbol, one declaration per snapshot) proving search returns each symbol
once and `ResolveSymbolByFqn` reports the requested snapshot's own
declaration count; a partial-type contract (`Retain.Beta`, two current
declarations) proving multiplicity is preserved, not just removed; a
declaration-less symbol (raw-SQL fixture, no public API inserts a
symbol without a declaration) proving external/metadata-only symbols remain
searchable; a `--limit` test proving retained-history fan-out no longer
consumes the result window; a generated-declaration scoping test; an
older-snapshot-view test; and an index-existence check
(`SearchSymbols_QueryPlan_DeclarationAndSnapshotDocumentIndexesExist`) — this
asserts index presence via `PRAGMA index_list`, not `EXPLAIN QUERY PLAN`
index usage, because SQLite's cost-based planner legitimately prefers a full
scan over either index on the handful of rows a unit-test fixture creates;
planner choice on a tiny table is not a meaningful regression signal.

Validation: `dotnet test tests/Lurp.Storage.Tests.csproj -c Release --filter
...` (narrow, PR-1-scoped) — new tests 16/16 pass; existing FTS/FQN suite
13/13 pass; equivalence + pruning suite 21/21 pass. An unfiltered
`dotnet test tests/Lurp.Storage.Tests.csproj -c Release` was also run
against project convention (full runs are user-run, not Claude — see the CI
section above); its result is not cited as evidence here.

Remaining from the remediation plan (PR-2 through PR-8) are out of scope for
this change — not started.

### PR-2 — Single completeness authority through status and context (2026-08-03)

Source: `LURP-INDEPENDENT-EVALUATION.md` findings D3/D4, prioritized as PR-2
in `LURP-REMEDIATION-PLAN.md` (external planning artifacts, user's desktop,
not part of this repository).

**Defect:** three call sites each held half the completeness answer.
`SnapshotManifest.FromStorageManifest` hydrated persisted `ActiveTfms`,
`SkippedAdapters`, and `ExtractorVersion` correctly, but
`ContextAssembler.PopulateContractSections` discarded that hydration by
constructing a brand-new `SnapshotCompleteness` from scratch (`active_tfms:
{}` reached capsules even though the snapshot's projects had recorded TFMs).
Separately, `StatusHandler.WithBindingCompleteness` kept the hydrated TFMs
but `AddRange`'d binding-incompleteness rows onto the `init`-only list
without recomputing `BindingIncompletenessSummary`/`BindingIncompletenessTotal`,
so `status --json` could show `binding_incompleteness_total: 0` next to a
non-empty detail list.

**Fix:**
- `src/Workspace/SnapshotCompleteness.cs` — converted to a `sealed record` and
  given one method, `WithBindingIncompleteness(records, includeDetail)`,
  that sets `BindingIncompleteness`, `BindingIncompletenessSummary`, and
  `BindingIncompletenessTotal` together via `this with { ... }`. The
  grouping logic (`BuildBindingIncompletenessSummary`, deterministic
  project/reason rollup) moved here from `ContextAssembler`, which is now
  the only place a completeness object gains binding detail.
- `src/Storage/SnapshotLifecycleStore.cs` — added `LoadSnapshotMetadata`, a
  cheap-per-snapshot query (extractor version, skipped adapters, project
  TFMs; no document read) so `ContextAssembler` can obtain the *requested*
  snapshot's persisted completeness rather than defaulting to empty. Storage
  cannot reference `Lurp.Workspace.SnapshotCompleteness` directly (opposite
  project-reference direction), so this returns a `SnapshotRow` and
  `ContextAssembler` translates it through the existing
  `SnapshotManifest.FromStorageManifest(...).Completeness` — the same
  hydration code path `StatusHandler` already used, not a second one.
- `src/Workspace/ContextAssembler.cs` — `PopulateContractSections` now loads
  that base completeness via an optional `SnapshotStore` property and calls
  `.WithBindingIncompleteness(...)` instead of constructing a fresh object.
- `src/Handlers/StatusHandler.cs` — `WithBindingCompleteness` calls the same
  `WithBindingIncompleteness(...)` instead of `AddRange`.
- `SnapshotManifest.Completeness` changed from `init` to a settable property
  (the class itself isn't a record, so `with` isn't available) so
  `StatusHandler` can replace it with the enriched copy.

No schema migration; `ISnapshotStore.LoadSnapshotMetadata` is additive (one
test fake, `FastTravelQueriesNarrowInterfaceTests.RecordingSnapshotStore`,
updated to throw `NotSupportedException` like its neighbors).

**Tests:** extended `T9CompletenessTests.StatusJson_IncludesLatestSnapshotManifestCompleteness`
with two `binding_incompleteness` rows and an assertion that
`binding_incompleteness_total == 8` (the D4 regression shape — total
silently reading `0` beside non-empty detail) and that the summary is
non-empty; updated `CapsuleBudgetEnforcerTests.CompletenessSummary_...` to
call the relocated `SnapshotCompleteness.BuildBindingIncompletenessSummary`.

Validation (narrow filters, not a full run):
`dotnet test tests/Lurp.Storage.Tests.csproj -c Release --filter
"FullyQualifiedName~Completeness|FullyQualifiedName~CapsuleBudgetEnforcer|FullyQualifiedName~FastTravelQueriesNarrowInterface|FullyQualifiedName~ContextCapsuleAcceptance|FullyQualifiedName~WorkspaceLoadGate"`
— 34/34 pass. `dotnet build src/Lurp.csproj -c Release` — 0 warnings, 0
errors.

Remaining from the remediation plan (PR-1 landed separately; PR-3 through
PR-8) are out of scope for this change — not started.

### PR-3 — Honest labelling of call-site dispatch candidates (2026-08-03)

Source: `LURP-INDEPENDENT-EVALUATION.md` finding D5 (part 1), prioritized as
PR-3 in `LURP-REMEDIATION-PLAN.md` (external planning artifacts, user's
desktop, not part of this repository).

**Defect:** `DirectCalleesTierBuilder.AddMayDispatchTargets` surfaced every
persisted `MayDispatchTo` implementation of a called interface member under
the called `MayDispatchTo` edge's own provenance — `compiler_proved` for a
direct implementation. That label is true of the global relation ("`T`
implements this interface member") but false of the projection into this
specific call site, which is never filtered by the call site's static
receiver type. A receiver-incompatible implementation could reach a capsule
labelled with Lurp's strongest confidence grade.

**Fix:**
- `src/Shared/Provenance.cs` — added `GlobalImplementationRelation`
  (`"global_implementation_relation"`) to the canonical provenance
  vocabulary, documented as a true relation projected into a call site
  without receiver-type filtering.
- `src/Workspace/DirectCalleesTierBuilder.cs` — `AddMayDispatchTargets` now
  emits candidates under `Provenance.GlobalImplementationRelation` instead
  of the persisted edge's own provenance, with an inclusion reason stating
  explicitly that the candidate is not filtered by the call site's static
  receiver type. The persisted edge (`EdgeStore`) and its provenance are
  unchanged — only the read-side capsule projection is relabelled.

No schema change, no other tier touched (`RegisteredImplementations` and the
`DirectCallers` dispatch-source path project different, correctly-labelled
facts — see the capsule dispatch-provenance fix below for the final state,
which keeps receiver-type filtering (commit `bdf252c`) while restoring the
`global_implementation_relation` read-side label that this section introduced).

**Tests:** updated
`ContextTypeAnchorContractTests.TypeAnchor_DirectCallees_IncludeInterfaceMemberAndItsDispatchImplementations`
to assert `global_implementation_relation` (and explicitly `!=
compiler_proved`) for the dispatch target instead of the prior
`compiler_proved` assertion.

Validation (narrow filter, not a full run): `dotnet test
tests/Lurp.Storage.Tests.csproj --filter
"FullyQualifiedName~ContextTypeAnchorContractTests"` — 6/6 pass. `dotnet
build src/Lurp.csproj -c Release` — 0 warnings, 0 errors.

Remaining from the remediation plan (PR-1, PR-2 landed separately; PR-4
through PR-8) are out of scope for this change — not started.

### PR-4 — Unambiguous metrics in console output (2026-08-03)

Source: `LURP-INDEPENDENT-EVALUATION.md` finding D7, prioritized as PR-4 in
`LURP-REMEDIATION-PLAN.md` (external planning artifact, user's desktop, not
part of this repository).

**Defect:** `IndexRunner` and `IncrementalIndexer` printed `Declarations`,
`Edges`, and `Diagnostics` totals computed mid-run — before orphan-edge
deletion for the full-index path — and labelled them as if they were the
snapshot's final persisted counts. The evaluation's reproduction showed the
gap directly: console reported 22,948 edges, the snapshot held 10,347 after
orphan cleanup. A consumer trusting the printed number had no way to tell
"this run's extraction total" from "what actually landed in the snapshot."

**Fix:**
- `src/Storage/ISnapshotStore.cs` / `SnapshotSymbolStore.cs` /
  `SqliteIndexStore.cs` — added `CountSymbolsInSnapshot(snapshotId)`, a plain
  `COUNT(*) FROM snapshot_symbols WHERE snapshot_id = ...` (declarations are
  keyed by `document_version_id`, but `snapshot_symbols` is already the
  correct per-snapshot membership table, so no join is needed here).
- `src/Storage/IEdgeStore.cs` / `EdgeOperationsStore.cs` /
  `DiagnosticStore.cs` / `EdgeStore.cs` / `SqliteIndexStore.cs` — added
  `CountEdges(snapshotId)` and `CountDiagnostics(snapshotId)`; both tables
  are already keyed directly by `snapshot_id`, so each is a single scoped
  `COUNT(*)`, no fan-out risk analogous to PR-1's `declarations` join.
- `src/Workspace/IndexRunner.cs` — moved the full-index summary print from
  immediately after the extraction loop (pre-cleanup) to after
  `DeleteOrphanEdges`, and renamed the printed fields to
  `declarations_extracted_this_run` / `declarations_in_snapshot`,
  `edge_relations_after_dedup_this_run` / `edge_relations_in_snapshot`,
  `diagnostics_extracted_this_run` / `diagnostics_in_snapshot`, plus
  `projects_reextracted_this_run: N/total`. The incremental-strategy print
  in `RunAsync` got the same field renames and now queries the same
  in-snapshot counts for the completed snapshot.
- `src/Workspace/IncrementalIndexer.cs` — `RunIncrementalAsync`'s final
  summary (already printed after `FinalizeSnapshotAsync`, i.e. after orphan
  cleanup, FTS rebuild, and diff) got the same renames, plus
  `documents_changed_this_run` / `documents_in_snapshot` and
  `projects_reextracted_this_run` / `projects_in_snapshot`.

No JSON export field changes (index-time JSON export is the snapshot
manifest, not this console metrics block); no schema migration; no
write-path change. `FastTravelQueriesNarrowInterfaceTests`'s test fake for
`ISnapshotStore` was updated with a `NotSupportedException` stub for the new
`CountSymbolsInSnapshot` member, matching its existing pattern for every
other interface method.

**Tests:** new `tests/SnapshotFactCountTests.cs` —
`CountEdges_ScopesToRequestedSnapshot`,
`CountDiagnostics_ScopesToRequestedSnapshot`,
`CountSymbolsInSnapshot_ScopesToRequestedSnapshot`, and
`EdgesInSnapshot_CanDifferFromRawExtractedCount_AfterOrphanCleanup` (pins
the exact D7 shape: the this-run edge count and the post-cleanup in-snapshot
count are legitimately different numbers for the same run).

Validation (narrow filter, not a full run): `dotnet test
tests/Lurp.Storage.Tests.csproj -c Release --filter
"FullyQualifiedName~SnapshotFactCountTests|FullyQualifiedName~SqliteUpsertTests|FullyQualifiedName~GraphNodeMembershipTests|FullyQualifiedName~FastTravelQueriesNarrowInterfaceTests"`
— 22/22 pass. `dotnet build src/Lurp.csproj -c Release` — 0 warnings, 0
errors.

Remaining from the remediation plan (PR-1 through PR-4 landed; PR-5 through
PR-8) are out of scope for this change — not started.

### PR-5 — Freshness stamp on every read (2026-08-03)

Source: `LURP-INDEPENDENT-EVALUATION.md` finding D6, prioritized as PR-5 in
`LURP-REMEDIATION-PLAN.md` (external planning artifact, user's desktop, not
part of this repository).

**Defect:** reads served whatever a snapshot said with no signal that the
workspace on disk had since diverged. The only freshness check that existed,
`WorkspaceFreshness.CheckFreshness(WorkspaceInfo, ...)`, requires a live
`Solution` — `StatusHandler.CheckCurrentWorkspaceAsync` builds one by calling
`MSBuildWorkspace.Create()` / `OpenSolutionAsync`, the same cost as a fresh
index run. That made it usable only behind explicit `status --solution=`,
never as a default on every read.

**Fix — a second, cheap tier that never loads Roslyn:**
- `src/Workspace/WorkspaceFreshness.cs` — added `FreshnessMode` (`Auto` /
  `Hash` / `Off`), `FreshnessStamp` (the `state`/`method`/
  `changed_document_count`/`changed_documents_sample`/`checked_at_utc`/
  `snapshot_id` shape from the plan's §3.4 design), and
  `CheckFreshnessCheap(ISnapshotStore, snapshotId, mode)`. It loads only
  `LoadSnapshotMetadata` (git root, build time — already the
  no-document-read query per its own doc comment) and
  `GetDocumentVersionIdsByPath` (a plain `snapshot_documents` ⋈
  `document_versions` ⋈ `documents` join, already used elsewhere), then does
  one `File.Exists` / `GetLastWriteTimeUtc` per document. A file whose
  `LastWriteTimeUtc` is newer than the snapshot's `BuiltAtUtc` is flagged
  `stale` in `Auto` mode; in `Hash` mode it is instead re-hashed
  (`DocumentVersionId.Compute` over `WorkspaceInfo`'s own byte-normalization,
  now exposed as `NormalizeSourceBytesForFreshnessCheck`) and only reported
  changed if the content actually differs from the persisted
  `document_version_id`. `document_version_id` is stored as
  `"{documentId}:{contentHash}"` (Migration_016) — the check compares against
  the substring after the final colon, not the whole composite string.
- `src/Handlers/HandlerBootstrap.cs` — added `ParseFreshnessMode` (reads
  `--freshness=auto|hash|off`, default `auto`), `ComputeFreshnessStamp`,
  `FreshnessJson` (the snake_case wire shape), `EnforceRequireFresh` (exits 2
  when `--require-fresh` is passed and state isn't `fresh`), and
  `PrintFreshnessLine` (the one-line stderr summary the design calls for
  alongside the JSON block).
- Wired into the four highest-traffic read paths named in the plan:
  `SearchHandler`, `FindSymbolHandler`, `ImpactHandler` (all three gained a
  `freshness` field in their existing JSON output) and `ContextHandler` (the
  capsule's JSON shape is a fixed, separately-serialized schema
  (`ContextCapsuleJson.Serialize`) already covered by acceptance tests, so
  freshness is surfaced there only via the stderr line and `--require-fresh`,
  not as a new capsule field — avoids widening that contract in this change).
  `StatusHandler`'s existing `--solution=`-gated full check is untouched.

Not done (correctly scoped out): auto-reindexing on read, refusing stale
reads by default, wiring freshness into every remaining handler
(`GetSourceHandler`, `GetSymbolHandler`, `NavigateHandler`, `DiffHandler`,
the `Simulate*Handlers`, `AuditHandler`, `AnnotationHandler`, `TimingsHandler`)
— left for a follow-up once the four-handler shape has been used in
practice. Session-level freshness caching (plan item 6, a TTL memo across a
burst of reads) also not done. The plan's open question 7 (actual stat-scan
latency on a large solution) remains unmeasured — this change adds the
mechanism, not a benchmark of it.

**Tests:** new `tests/FreshnessCheapCheckTests.cs` — six tests covering
fresh (back-dated mtime), `Auto`-mode false-stale on a touched-but-unchanged
file (the known limit of stat-only checking, documented rather than
"fixed"), `Hash`-mode resolving that same touch to `fresh`, `Hash`-mode
correctly catching a real content change, a removed document, and `Off`
mode. Writing the first version of these against the naive implementation
(comparing the current hash to the raw `document_version_id` column) failed
immediately — it caught that the stored value is the `id:hash` composite,
not a bare hash, which the fix above accounts for.

Validation (narrow filter, not a full run): `dotnet test
tests/Lurp.Storage.Tests.csproj -c Release --filter
"FullyQualifiedName~SnapshotFactCountTests|FullyQualifiedName~SqliteUpsertTests|FullyQualifiedName~GraphNodeMembershipTests|FullyQualifiedName~FastTravelQueriesNarrowInterfaceTests|FullyQualifiedName~FreshnessCheapCheckTests"`
— 28/28 pass. `dotnet build src/Lurp.csproj -c Release` — 0 warnings, 0
errors.

Remaining from the remediation plan (PR-1 through PR-5 landed; PR-6 through
PR-8) are out of scope for this change — not started.

### PR-7 — Keyset pagination for symbol search (2026-08-03)

Source: `LURP-INDEPENDENT-EVALUATION.md` finding D8, prioritized as PR-7 in
`LURP-REMEDIATION-PLAN.md` (external planning artifact, user's desktop, not
part of this repository). Scoped to the pagination mechanism itself, not the
full PR-7 design in the plan's §3.6 (which also covers `impact` grouping,
capsule tier continuation, and `--output=summary|json|jsonl`/`--quiet` — none
of that is touched here).

**Defect:** `search --type=symbol` has no way to fetch a second page.
`--limit` truncates silently; there is no cursor, so a caller who wants the
next N results has no option but to re-run with a larger `--limit` and
re-read everything already seen.

**Fix — opaque keyset cursor, snapshot-scoped, no schema change:**
- `src/Storage/SearchCursor.cs` (new) — `SearchCursor` record
  (`SnapshotId`, `Fingerprint`, `Mode`, `LastRank`, `LastFqn`,
  `LastSymbolId`), base64-JSON `Encode`/`TryDecode`, and
  `ComputeFingerprint(query, kind, includeGenerated)`. `SymbolSearchPage`
  wraps `Items` + `NextCursor`.
- `src/Storage/ISearchStore.cs` / `SearchStore.cs` — new
  `SearchSymbolsPage(query, snapshotId, limit, includeGenerated, kind, cursor)`,
  additive alongside the existing non-paginated `SearchSymbols` (unchanged,
  still used by `--type=all`/`--type=source`). Both the FTS and substring
  paths gained a `symbol_id` tiebreaker on `ORDER BY` (`rank, symbol_id` /
  `fqn, symbol_id`) — this is what PR-1's snapshot-scoped, duplicate-free
  order makes a *cursor* meaningful rather than one racing a moving window.
  Each page fetches `limit + 1` rows to detect a next page without a
  separate `COUNT` query; the keyset predicate is `(rank, symbol_id) >
  (lastRank, lastSymbolId)` for FTS and `(fqn, symbol_id) > (lastFqn,
  lastSymbolId)` for substring. A cursor whose `SnapshotId`/`Fingerprint`
  doesn't match the current request throws `ArgumentException` rather than
  silently resuming against a different query's keyset.
- `src/Handlers/SearchHandler.cs` — `--cursor=<token>` (only accepted with
  `--type=symbol`; rejected otherwise, since `--type=all`/`source` don't go
  through the paginated path), decodes and validates the cursor, and the
  response gains a `nextCursor` field (`null` on the last page).

**Tests:** four new tests in `tests/UnitTest1.cs` reusing the existing
`CreateThreeRetainedAlphaVersions`/`AddBetaAndGammaToSnapshot` fixture —
paging through with `limit=1` across three symbols yields no duplicates and
no gaps and matches the non-paginated result set; a single large-limit page
returns `nextCursor: null`; a cursor replayed against a different query
throws instead of returning wrong rows; and `SearchCursor.TryDecode` on
garbage input returns `null` instead of throwing.

Validation (narrow filter, not a full run): `dotnet test
tests/Lurp.Storage.Tests.csproj --filter
"FullyQualifiedName~SearchSymbolsPage|FullyQualifiedName~SearchCursor_TryDecode|FullyQualifiedName~SearchSymbols|FullyQualifiedName~SearchSource|FullyQualifiedName~ResolveSymbolByFqn"`
— 29/29 pass. `dotnet build src/Lurp.csproj -c Release` — 0 warnings, 0
errors.

Not done in this slice: `impact` path grouping/truncation cursor,
context-capsule `--tier=<name>&--cursor=<c>` continuation, `status
--detail=documents`, and `--output=summary|json|jsonl`/`--quiet` — all listed
in the plan's §3.6. **These landed separately; see §PR-7 (remainder) below.**

Remaining from the remediation plan as of this slice (PR-1 through PR-5
landed; this slice covers only the pagination mechanism for symbol search;
PR-6, PR-8, and the rest of PR-7's §3.6 surfaces were out of scope here).

### PR-7 (remainder) — impact grouping, tier continuation, and output modes (2026-08-03)

Completes the plan's §3.6 surfaces left out of the pagination slice above.
Source: `LURP-INDEPENDENT-EVALUATION.md` finding D8 (external planning
artifact, user's desktop, not part of this repository).

**Defect:** a budget-exhausted capsule tier was honestly declared in
`omittedTiers` but could not be fetched; a 1,700-line impact payload had no
grouping or page boundary; `status --json` always emitted the full
196-document manifest; and every read wrote its whole payload to stdout with
no concise or streaming form.

**Fix:**
- `src/Storage/SequenceCursor.cs` (new) — `SequenceCursor`
  (`SnapshotId`, `Fingerprint`, `Kind`, `Offset`) with base64-JSON
  `Encode`/`TryDecode` and a `Validate(snapshotId, fingerprint, kind)` that
  rejects a cursor from a different snapshot, query, or sequence kind. Used
  for the two offset-shaped sequences (impact paths, tier items), distinct
  from `SearchCursor`'s keyset shape; each handler decodes with its own type
  only, so there is no shared cross-decoding path.
- `src/Handlers/ImpactHandler.cs` — `--max-paths=<n>` (default 50) with
  `truncated.{reason,total,remaining,cursor}` and `--cursor=`. `groups`
  (paths by first hop) is computed over *all* paths before the page is cut,
  so the fan-out summary stays complete under truncation. Paths are sorted by
  path key for cursor stability.
- `src/Handlers/ContextHandler.cs`, `src/Workspace/ContextAssembler.cs` —
  `--tier=<name> --tier-limit=<n> --cursor=` fetches one tier on its own with
  no budget applied, which is how an `omittedTiers` `budget_exhausted` entry
  is acted on. The capsule states that continuation in-band via an
  `InclusionReasons["omittedTiers.budget_exhausted"]` entry.
- `src/Handlers/StatusHandler.cs` — `--detail=<list>`; the per-document
  version map is summarized as `documentCount` unless `documents`/`all` is
  requested. Separate from `SnapshotManifest.Save`, so the order-7
  `--output-json=` one-way export is untouched.
- `src/Handlers/HandlerBootstrap.cs`, `src/Program.cs` —
  `--output=<summary|json|jsonl>` and `--quiet` shared across the four read
  commands, plus a consolidated READ-COMMAND OPTIONS help block. `jsonl` is
  rejected for a whole capsule (single-document payload). `--quiet` gates the
  freshness stderr line and reduces `--mode=context` stdout to the written
  capsule path — never the file itself.
- `src/README.md` — documents these flags and the pre-existing
  `--cursor`/`--freshness`/`--require-fresh` from PR-5/PR-7, which the README
  had never carried, in one shared read-command-options section.

**Budget truthfulness is preserved, not asserted.** The unconditional
inclusion-reason entry is emitted in `PopulateContractSections`, which runs
*before* `CapsuleBudgetEnforcer.Enforce` — conditioning it on an existing
omission would miss the tiers the enforcer clears later, and emitting it
after the enforcer would leave its own cost outside the measurement.
`CapsuleBudgetEnforcer.Measure` counts it via
`SerializedChars(capsule.InclusionReasons)`, so `estimatedTokens` still
describes the emitted artifact. Verified as a uniform **+49** token shift
across all three baseline scenarios (1828→1877, 1776→1825, 3733→3782), which
matches the entry's ~196 serialized chars ÷ 4.

**Tests:** `tests/OutputContinuationTests.cs` (new) covers impact grouping,
`path_count_total`, page truncation and continuation, tier continuation,
`status --detail`, and the output modes. It is the only consumer of the
changed impact JSON shape — the outcome benchmark uses `--mode=context`, and
no doc or handler parses impact output.

Validation (narrow filter, not a full run): `dotnet test
tests/Lurp.Storage.Tests.csproj -c Release --filter
"FullyQualifiedName~OutcomeBenchmark|FullyQualifiedName~OutputContinuation|FullyQualifiedName~ContextCapsuleAcceptance|FullyQualifiedName~CapsuleBudgetEnforcer"`
— 21/21 pass, with `tests/benchmark-runs/baseline.json` unchanged by the run
(so the checked-in baseline matches the code on disk). A wider filter adding
`ContextBudgeter`/`ContextTypeAnchorContract` passed 30/30. `dotnet build
src/Lurp.csproj -c Release` — 0 warnings, 0 errors.

**Open follow-up:** `SearchCursor` has no `Validate` counterpart to
`SequenceCursor`'s — it trusts its decoded values. No CLI path feeds a cursor
to the wrong decoder today, so this is a latent asymmetry rather than a
defect, but the guard belongs on both.

Remaining from the remediation plan: PR-1 through PR-5 and PR-7 landed; PR-6
landed as commit `bdf252c` (its receiver-type constraints are retained and
documented in the capsule dispatch-provenance fix below); PR-8
(semantic-surface invalidation) is not started.

### Capsule dispatch-provenance fix — callers and callees (2026-08-03)

Closes the provenance-laundering clause of open finding 1
(`docs/reference/CAPSULE_PROVENANCE_FIX.md`). A caller reached through
`Calls → MayDispatchTo` was projected into a capsule as a direct
`compiler_proved` caller of the concrete implementation. The reachability is
legitimate; the presentation as a direct compiler-proved call is not.

**Caller side** (`DirectCallersTierBuilder`, `SecondDegreeContextTierBuilder`):
- Callers reached through an incoming `MayDispatchTo` edge are classified
  `relationship: indirect_dispatch_candidate` / `direct: false` with effective
  claim provenance `possible` — `framework_derived` only when an actual
  framework/DI-derived edge participates in the composed path
  (`EdgeDedup.ComposeDispatchClaimProvenance`; path-level provenance is never
  the strongest edge's grade).
- The inclusion reason names both underlying steps with their grades: the
  caller's `Calls` edge to the interface/abstract member (persisted
  `compiler_proved`) and the `MayDispatchTo` edge carrying the structural
  implementation candidate (persisted `compiler_proved`).
- Genuine direct calls stay `direct_caller` / `direct: true` /
  `compiler_proved`. The persisted `MayDispatchTo` edges are unchanged —
  `InterfaceDispatchExtractor` keeps emitting `compiler_proved` for direct
  structural implementations; the grade is composed at read time, not
  laundered at write time.

**Callee side** (`DirectCalleesTierBuilder`): PR-3's label was reverted by
PR-6's receiver-type commit (`bdf252c`), which left the live code hardcoding
`compiler_proved` for projected dispatch targets against this document's
PR-3 record. Reconciled: receiver-type constraint filtering is retained
(candidates must be assignable to the call site's persisted static receiver
types), and the projection is again labeled
`Provenance.GlobalImplementationRelation` with
`relationship: indirect_dispatch_candidate` / `direct: false`. The inclusion
reason names the `Calls` edge, the `MayDispatchTo` edge, and states that
receiver compatibility narrows but does not establish the runtime target.
Directly invoked concrete callees remain `direct_callee` / `direct: true` /
`compiler_proved`. `EdgeDedup.ProvenanceRank` now ranks the canonical
`global_implementation_relation` between `framework_derived` and `possible`,
and the T4 ladder above reflects it.

**Tests:** `tests/CapsuleProvenanceCompositionTests.cs` (new, 10 tests) locks
the full contract including the callee projection;
`ContextTypeAnchorContractTests.TypeAnchor_DirectCallees_IncludeInterfaceMemberAndItsDispatchImplementations`
asserts `global_implementation_relation` (and `!= compiler_proved`) for the
receiver-compatible dispatch target while keeping the receiver-incompatible
candidate exclusion. `tests/benchmark-runs/baseline.json` regenerated: the
DI-replacement scenario's dispatch-mediated caller items report `possible`
and its `directCallees` dispatch projections report
`global_implementation_relation`.

Validation: `dotnet build src/Lurp.csproj -c Release` — 0 warnings, 0
errors; `CapsuleProvenanceComposition|ContextTypeAnchorContract|
CapsuleBudgetEnforcer|ContextBudgeter|ContextCapsuleAcceptance` 29/29,
`EdgeDedup|CleanRebuildEquivalence|InterfaceDispatch|OutcomeBenchmark|
GraphNodeMembership|SqliteUpsert|SchemaStability` 25/25, and the full suite
(`dotnet test tests/Lurp.Storage.Tests.csproj -c Release`, ~800s) 0 errors.

## Explicitly postponed

- Multi-TFM per-framework indexing.
- Deterministic snapshot IDs.
- DI adapter matching by parameter type instead of containing-type name.
- Concurrency, daemon, or server architecture.
- `SqliteIndexStore` decomposition.
- Explicit source-generator execution with a `GeneratorDriver`.
