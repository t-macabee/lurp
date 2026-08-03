# Context-Capsule Completeness Audit: eNote CourseService

**Status:** investigation report (no code or tests edited)
**Date:** 2026-08-01
**Snapshot:** `ecbb52dc18214d4f88b4e867d37b22bb` (status `complete`, extractor `1.3.0`, schema 24)
**Anchor:** `T:eNote.Application.Features.Academic.Courses.Services.CourseService|eNote.Application, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null`: kind `Type`
**Capsule reproduced byte-identically** (SHA-256 `080b0587...` both before and after re-run).

---

## Observed capsule state

- `contracts = 1` (ICourseService), `incomingPaths = 1`, `outgoingPaths = 177` (498 hops, spans only, no source), `affectedPublicSurfaces = 2`, `likelyChangeSites = 1`
- Empty: `directCallers`, `directCallees`, `registeredImplementations`, `relevantTests`, `secondDegreeContext`, `surroundingSource`, `suggestedVerification`, `uncertainties`
- `estimatedTokens = 1466`, `budget = 4000`, `truncated = false`, JSON file is **996,274 bytes**

---

## 1. Observed facts per tier (with runtime trace and persisted evidence)

### A. Anchor source + ICourseService contract: included (works)

Route: `ContextHandler.Run` → `ContextAssembler.ResolveAndAssemble` → `ResolveSymbolByLocation`
(`DeclarationMaintenanceStore.cs:66`), which finds the smallest declaration span containing the line
offset. Line 7 is the class-header line; the type and its primary ctor share an identical span
`177–5248`, and the tie resolves to the **type**. Anchor source is `ViewKind.Declaration`
(5,071 chars). `ContractsTierBuilder.cs:17` queries outgoing `Implements`/`Overrides` from the
anchor id → 1 edge `Implements → T:...ICourseService` (persisted, `compiler_proved`). **Met.**

### B. `directCallers`: empty

Requirement: §20.1 priority 4, "direct callers and entry points".
Route: `DirectCallersTierBuilder.cs:18` runs `ImpactTraverser.TraceImpact(anchor, Upstream,
allowedKinds={Calls}, maxDepth=1)` plus incoming `RoutesTo`/`Handles`.
Persisted evidence: the anchor **type** has exactly **one incoming edge**: `ReflectionTypeRef`
from `AddApplicationServices` (DI registration, line 32). Incoming `Calls` to the type = 0;
incoming `Calls` to the type's **members** = 0; incoming `Calls` to `ICourseService` **methods** =
0: even though `InstructorCourseController.GetMyCourses`/`GetById` (lines 19, 27) call
`service.GetPagedForInstructorAsync`/`GetByIdForInstructorAsync` in source. Those `Calls` edges
were never extracted (see §2F).
Earliest divergence: **granularity + extraction degradation.** Calls edges attach to member IDs;
a type anchor has none. Independently, the controller→interface calls are missing from extraction
entirely.

### C. `directCallees`: empty despite facts existing

Requirement: §20.1 priority 3, "directly invoked members".
Route: `DirectCalleesTierBuilder.cs:19` runs `TraceImpact(anchor, Downstream,
{Calls, Constructs}, depth 1)`.
Persisted evidence: **13 `Calls` + `Constructs` edges DO exist from CourseService methods**
(e.g. `GetByIdForInstructorAsync → IInstructorAccessService.GetCurrentInstructorIdAsync`).
The type has zero outgoing `Calls`/`Constructs` (only 8 `Declares` + 1 `Implements`). Facts exist
but are unreachable from the type anchor. Control: anchoring line 11 (inside the method) yields
`directCallees = 1`.
Earliest divergence: **identity/granularity mismatch: facts at member IDs, tier queries type
ID, no type→member expansion.**

### D. `registeredImplementations`: empty despite facts existing

Requirement: §20 "registered or possible runtime targets"; §17 DI evidence ladder.
Route: `RegisteredImplementationsTierBuilder.cs:14,35` queries incoming/outgoing
`MayDispatchTo`/`Registers`/`Handles` on the anchor id.
Persisted evidence: **7 `MayDispatchTo` edges exist, `compiler_proved`, at method level**
(`ICourseService.GetByIdForInstructorAsync → CourseService.GetByIdForInstructorAsync`, etc.).
The type id has none. Also: `Registers`/`Handles`/`RoutesTo` counts in the whole snapshot are
**0**: the Scrutor DI registration (`services.Scan(...FromAssembliesOf(typeof(CourseService))...)`,
`ApplicationServiceExtensions.cs:32`) is captured only as `ReflectionTypeRef`; the DI adapter
models `AddScoped<,>`-style registrations, not Scrutor convention scanning. The capsule surfaces
the registration only via the 1 incoming path. Control: method anchor at line 11 →
`registeredImplementations = 1`.
Earliest divergence: **granularity (facts at member IDs) + adapter gap (Scrutor/reflection-driven
DI yields no `Registers` edge).**

### E. `relevantTests`: empty; partly genuine, partly extraction-suppressed

Requirement: §20.1 priority 6 "relevant tests"; §12/§17 `tested_by`.
Route: `RelevantTestsTierBuilder.cs:22` walks `TestedBy` from the anchor and its upstream `Calls`
callers, via `TestSymbolDiscovery.ExpandProductionSymbolIds`.
Persisted evidence: **49 `TestedBy` edges exist (`framework_derived`)** covering eNote.Application
DTOs/state machines and `UserSelfService`/`RecommendationService`. **Zero touch CourseService or
ICourseService** (queried). **No test document contains the string `CourseService`** (`instr` = 0
in all 22 eNote.Tests docs). `CourseEnrollmentService` has dedicated tests but **no TestedBy edge
either**: the adapter (`TestAdapter.cs`) scans only test-method bodies, and
`CourseEnrollmentServiceTests` constructs the service inside the non-test helper `CreateService`
(`new CourseEnrollmentService(...)` at lines 110–115), while the test body only invokes
`service.EnrollAsync(...)`: plus that document carries binding incompleteness
`compiler_error: 68, unresolved_metadata: 93`.
Earliest divergence: **extraction absence**: genuine (CourseService is untested) and
adapter-limited (helper-mediated construction + degraded compilation suppress TestedBy for
CourseEnrollmentService).

### F. `secondDegreeContext`: empty

Requirement: §20.1 priority 7, bounded upstream paths.
Route: `SecondDegreeContextTierBuilder.cs:21` runs `TraceImpact(anchor, Upstream, {Calls},
maxDepth=3)`.
Persisted evidence: incoming `Calls` to the type = 0; incoming `Calls` to CourseService/ICourseService
methods = 0. The controller calls are missing from extraction (same §2F root cause), and callers
target the interface, not the concrete impl: so even a healthy snapshot would return empty for the
concrete method unless interface dispatch is followed.
Earliest divergence: **extraction degradation + interface-mediated call pattern.**

### G. `surroundingSource`: empty; tier/store query mismatch (dead tier)

Requirement: §20 "surrounding source".
Route: `SurroundingSiblingsTierBuilder.cs:15` looks for an incoming **`Contains`** edge to find the
parent, then the parent's outgoing `Contains` for siblings.
Persisted evidence: all **12 `Contains` edges are nested-type relations** (test stub classes). The
type→member containment is persisted as **`Declares` (2,324 edges)**, never `Contains`; namespaces
are not graph nodes. Therefore the tier is empty for **every** ordinary type/method anchor (only
nested types ever populate it).
Earliest divergence: **query mismatch: tier queries `Contains`, extractor emits `Declares`.** This
is a genuine implementation gap, independent of the snapshot.

### H. `suggestedVerification`: empty (downstream of empty tiers)

Route: `UncertaintyDetector.PopulateSuggestedVerification` (`UncertaintyDetector.cs:160`):
per-test commands require `TestedBy` from the anchor (none: §E); full-suite escalation requires
≥3 distinct owning projects among **tier items only** (`AllCapsuleItems` = the 7 budgeted tiers).
Tier items here: anchor + `ICourseService`, both `eNote.Application` → 1 project. Git root is
available (`workspaces.git_root = <external eNote workspace>`) so owning-project
resolution would work if items existed.
Earliest divergence: **downstream of the empty tiers.** Note: the multi-project check ignores
`outgoingPaths`, which touch eNote.API/Domain/Tests: by design, but arguably a gap.

### I. Raw capsule ~1MB vs `estimatedTokens=1466`: budget bypass

Route: `ContextBudgeter.Apply` (`ContextAssembler.cs:342`) counts **only anchor source + tier-item
source** (`EstimateTokens = chars/4`). `PopulateContractSections` adds `incomingPaths`/`outgoingPaths`/
`topology`/`completeness.binding_incompleteness` **after** the budget, untoken-counted and untruncated.
Size breakdown of the serialized JSON: `outgoingPaths` 327KB (177 paths/498 hops, spans only),
`topology` 328KB (duplicates the paths), `completeness` 113KB (614 binding-incompleteness rows;
e.g. `compiler_error` 7,779 occurrences across 177 rows). Whole capsule ≈ 249K chars ≈ 249K tokens
by the same heuristic: **~170× the reported 1,466**. §20.1 ("accept an explicit budget and degrade
predictably") and §20 completion ("within a predictable token budget") are not met by the shipped
artifact. Path data is token-cheap (no source) but byte-huge; binding incompleteness is fully
unbudgeted.
Earliest divergence: **output-assembly/budget policy: post-budget sections are unbudgeted and the
estimate ignores them.**

### J. `--mode=search --query=Service --type=symbol` → no results

Route: `SearchHandler` → `SearchStore.SearchSymbols` → `symbol_fts MATCH 'Service'`.
Persisted evidence: `symbol_fts` is `fts5(... tokenize='unicode61')`. FQN
`global::eNote.Application...CourseService` tokenizes to the single lowercased token
`courseservice` (no camelCase split). Direct queries: `MATCH 'Service'` → **0 rows**;
`MATCH 'CourseService'` → 5 rows. Source search returns docs only because controller constructor
parameters are literally named `service` (standalone token). Not a persistence bug: deterministic
whole-token FTS semantics. Architecture §10 lists "identifier fragments" as an intended lexical
category; the implementation matches only whole tokens. **Divergence between documented intent and
behavior (open question whether "fragments" means substrings).**

### K. Systemic root cause affecting many edges: degraded compilation

Observed: **17,010 Error diagnostics** across all projects, dominated by `CS8370 "Feature ...
not available in C# 7.3"` (file-scoped namespaces, global usings). Snapshot records
`sdk_version 10.0.302`, `compiler_version 5.6.0.0`, status `complete`. The indexer sets no
`LanguageVersion`/`CSharpParseOptions` anywhere (grep: zero hits), so default parse options produced
a C# 7.3 compilation of C# 10+ source. Consequence: `CourseService.cs` binding incompleteness =
`compiler_error: 190`; `InstructorCourseController.cs` = `compiler_error: 19, unresolved_metadata:
15`; the controller→interface `Calls` edges and several `TestedBy` edges were never extracted. The
snapshot honestly records this (614 incompleteness rows, reason-coded), but the semantic graph is
materially incomplete.

---

## 2. Inferences

- **The tier builders are correct at member granularity.** The line-11 control capsule populated
  `directCallees=1` and `registeredImplementations=1`, proving the builders and store queries work;
  the observed empties come from the **type anchor** (file/line resolution correctly returned the
  type for the class-header line) combined with member-level edge storage. A type anchor has no
  member-level edges, and no tier expands a type anchor to its declared members (despite the
  all-kinds impact traversal doing exactly that via `Declares`).
- **The controller→interface call suppression is attributable to the C# 7.3 compilation**, because
  source lines 19/27 unambiguously invoke the interface methods yet no `Calls` edge and 19–15
  binding failures were recorded on that document.
- **`relevantTests` emptiness for CourseService is genuine** (no test references it), while the
  CourseEnrollmentService missing-edge case is adapter/compilation-limited, not a capsule defect.

---

## 3. Open questions

- Whether the C# 7.3 compilation is an indexer workspace-evaluation bug or a property of the
  (external) eNote project files. Requires inspecting eNote's `.csproj`/`Directory.Build.*` and the
  MSBuildWorkspace evaluation: outside this repository, not resolvable statically here.
- Whether architecture §10 "identifier fragments" requires substring/prefix search or is satisfied
  by whole-identifier-token matching.
- Whether a type anchor is *intended* to be member-granular in tiers (architecture §13 guarantees
  member-level identification only "for a changed method"; §20 anchor forms include
  `--file F --line N` without restricting granularity).

---

## 4. Requirement-by-requirement matrix

| Architecture requirement (§§12–22, §26) | Observed capsule behavior | Persisted evidence | Status |
|---|---|---|---|
| Anchor symbol + exact source span (§20, §26) | Anchor type, 5,071-char declaration source, span lines 5–121 | `snapshot_symbols`, `declarations` 177–5248, line starts | **Met** |
| Relevant contracts (§20; §13 impl/override) | 1 item: `ICourseService`, `Implements/compiler_proved` | 1 `Implements` edge type→interface | **Met** |
| Directly invoked members / `directCallees` (§20.1 #3) | Empty | 13 `Calls`+`Constructs` from members exist; 0 on type id | **Unmet** (granularity) |
| Direct callers & entry points (§20.1 #4) | Empty | 0 inbound Calls; DI reg only as `ReflectionTypeRef` | **Unmet** (granularity + degraded extraction) |
| Registered/possible runtime targets (§20; §17) | Empty | 7 method-level `MayDispatchTo` exist; `Registers/RoutesTo/Handles` = 0; Scrutor DI unmodeled | **Unmet** (granularity + adapter gap) |
| Relevant tests (§20.1 #6; §12/§17 tested_by) | Empty | 49 `TestedBy`; 0 for Course; CourseEnrollmentService tests untested-by-adapter | **Unmet for this symbol** (genuine + extraction-limited) |
| Second-degree context (§20.1 #7) | Empty | 0 inbound Calls to interface/impl members | **Unmet** (degraded extraction + interface dispatch) |
| Surrounding source (§20) | Empty for all ordinary anchors | `Contains`=12 (nested types only); type→member persisted as `Declares` | **Unmet** (tier/store query mismatch) |
| Incoming/outgoing paths with provenance + spans (§20; §26) | 1 in / 177 out, 498 hops, spans + `compiler_proved` | 5,111 edges, correct snapshot scope | **Met** |
| Suggested verification (§20; TRUST §25) | Empty | No TestedBy on anchor; 1 project among tier items | **Unmet downstream** (design check uses tiers only) |
| Budget: predictable tokens, degrade predictably (§20.1; §26) | `budget=4000`, `estimatedTokens=1466`, `truncated=false`, file 996KB (~249K tokens) | Paths/topology/completeness unbudgeted post-assembly | **Unmet** |
| Uncertainties & unchecked vectors (§20) | Empty (incl. completeness surface) | 614 binding-incompleteness rows surfaced via `Completeness`, not `uncertainties` | **Partial**: surfaced, but not as uncertainties |
| Exact-source/declaration retrieval & snapshot identity (§26) | Correct resolution, same doc version | `document_versions` + line starts | **Met** |
| Lexical "identifier fragments" search (§10) | `Service` → 0; `CourseService`/`Course` → results | FTS5 unicode61 whole-token; no camelCase split | **Indeterminate** (documented intent vs. behavior) |
| Every fact states provenance/extractor version (§26) | All tier items + hops carry provenance | `edges.provenance`, `extractor_version` | **Met** |
| Honest completeness limits (§26; AGENTS) | `Completeness.BindingIncompleteness` 113KB, reason-coded | 614 rows, 5 reason codes | **Met** (but unbudgeted) |

---

## 5. Smallest safe implementation tasks (only unmet requirements)

1. **Type-anchor member expansion for member-level tiers** (`directCallees`, `directCallers`,
   `registeredImplementations`, `relevantTests`, `secondDegreeContext`). Smallest change: in
   `ContextTierContext`/each tier builder, when `SymbolId.Kind` is a type, resolve its member IDs
   via outgoing `Declares` edges and query those IDs (the all-kinds traverser already proves this
   reachability). Add a contract test anchoring a service type.
2. **`surroundingSource` query fix.** In `SurroundingSiblingsTierBuilder`, treat incoming `Declares`
   (and `Contains`) as the parent relationship and siblings via the parent's outgoing
   `Declares`/`Contains`, so method/type anchors resolve their siblings. Smallest change: extend the
   containment-kind set.
3. **Budget honesty.** Include `incomingPaths`/`outgoingPaths`/`topology`/`Completeness` byte weight
   in `EstimatedTokens`, and cap/aggregate `BindingIncompleteness` in the capsule to a reason-coded
   summary so §20.1 degrades predictably.
4. **Search fragment decision.** Decide and (if confirmed) add substring/prefix support to
   `SearchSymbols` to satisfy §10 "identifier fragments"; otherwise document whole-token behavior as
   the product boundary.

**Blocked / not a capsule task:** C# 7.3 compilation fidelity (needs eNote project evidence);
TestedBy helper-construction suppression (extraction/adapter question, outside capsule).

---

## 6. Commands and SQL actually run

Commands (no tests run; no files edited):

- `src/bin/Debug/net10.0/Lurp.exe --mode=context --file=eNote.Application/Features/Academic/Courses/Services/CourseService.cs --line=7 --intent=modify --budget=4000 --max-hops=3 --snapshot=ecbb52dc18214d4f88b4e867d37b22bb --output-dir=<temporary-output-dir>` → exit 0; output re-hash `080b0587...` (byte-identical to pre-existing file).
- Same with `--line=11` (method anchor) → `directCallees=1`, `registeredImplementations=1`.
- `src/bin/Debug/net10.0/Lurp.exe --mode=search --query=Service --type=symbol ...` → `results: []`; `--query=Course` and `--query=CourseService --type=symbol` → results; `--query=Service --type=all` → 20 source hits.
- Read-only `python sqlite3` against `index.db` (`mode=ro`): schema dump; snapshots/workspaces/extractors rows; edge-kind×provenance histogram; incoming/outgoing edges for the anchor type; Calls into/out of CourseService and ICourseService members (=0/13); all 49 `TestedBy` rows; Contains edges (12); MayDispatchTo rows; `symbol_fts`/`source_fts` DDL + `MATCH 'Service'` (0) vs `MATCH 'CourseService'` (5); `binding_incompleteness` per reason/project/document; `diagnostics` counts (17,010) and CS8370 samples; declaration spans + line starts for `CourseService.cs`; `ApplicationServiceExtensions.cs` source + its edges; `CourseEnrollmentServiceTests.cs` source and its edges/incompleteness; `UserSelfServiceTests.cs` source + its TestedBy edges.

---

## Key divergence summary

Anchor, contracts, and impact paths are correct; the empty tiers are a **type-anchor granularity
gap** (facts exist at member level) compounded by a **degraded C# 7.3 compilation** that suppressed
many `Calls`/`TestedBy` edges; `surroundingSource` is a **store-query mismatch** (`Declares` vs
`Contains`); the **token budget is bypassed** by unbudgeted paths/topology/completeness; and the
**search miss is deterministic FTS5 whole-token behavior** diverging from §10's documented
"identifier fragments" intent.
