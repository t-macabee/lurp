# Capsule Provenance Fix: Dispatch-Path Evidence Composition

**Date:** 2026-08-03 (revised)
**Scope:** Context-capsule provenance for callers reached through interface dispatch.

---

## Root cause

A single layered defect caused a caller reached through an interface dispatch chain to be presented as a direct `compiler_proved` caller of the concrete implementation:

**Capsule composition** (`DirectCallersTierBuilder.cs`, `SecondDegreeContextTierBuilder.cs`) walked the incoming `MayDispatchTo` edge to find the interface member, then traced upstream `Calls` edges and built the capsule item from `hop.Provenance` alone: `compiler_proved` for a real call. The `MayDispatchTo` hop's meaning was discarded: the item was collapsed into a single direct-caller claim that the compiler never made.

Result: a path `Controller --Calls/compiler_proved--> Iface --MayDispatchTo/compiler_proved--> Impl` was projected as a single `compiler_proved` direct-caller item. The reachability is legitimate; the *presentation as a direct compiler-proved call* is not.

The stored graph facts were never wrong: `MayDispatchTo` produced through Roslyn `FindImplementationForInterfaceMember` is a compiler-established structural implementation candidate and remains `compiler_proved` (inherited implementations stay `possible`). The defect was confined to how those facts were composed into a call-site claim.

---

## Affected components

| Component | Defect |
|---|---|
| `src/Workspace/DirectCallersTierBuilder.cs` | Dispatch-mediated callers projected as direct `compiler_proved` callers; dispatch hop meaning discarded |
| `src/Workspace/SecondDegreeContextTierBuilder.cs` | Same misprojection for upstream dependencies reached through dispatch |

Deliberately unchanged: `src/Workspace/InterfaceDispatchExtractor.cs` keeps emitting `compiler_proved` for direct implementations: the edge is a compiler-established structural fact, and the grade must not be laundered at write time.

---

## Persisted data vs. presentation

**Only the presentation was wrong.**

- The persisted `MayDispatchTo` edge keeps its honest meaning: `compiler_proved` (direct structural implementation) or `possible` (inherited).
- The capsule composition now classifies the *projected claim* separately: a caller reached through `Calls → MayDispatchTo` is an **indirect dispatch candidate**, not a direct caller.

---

## Scope of potentially affected capsule sections

- `directCallers`: interface-mediated callers were mislabeled direct `compiler_proved` callers. **Fixed.**
- `secondDegreeContext`: same mislabeling for upstream dependencies reached through dispatch. **Fixed.**
- `registeredImplementations`: unchanged: dispatch sources are presented under the persisted edge's own provenance (`compiler_proved`), which is the structural fact.
- `directCallees`: **fixed.** `DirectCalleesTierBuilder.AddMayDispatchTargets` projects a call-site dispatch target under `global_implementation_relation` (never `compiler_proved`): the candidate is included only because it globally implements the called interface member. Receiver-type constraint filtering is retained: it narrows which global implementations are candidates, but reachability from this specific call is still not compiler-established. Directly invoked callees stay `compiler_proved` / `direct_callee` / `direct: true`.

---

## Binding semantic contract

### Stored graph facts

`MayDispatchTo` produced through Roslyn `FindImplementationForInterfaceMember` remains `compiler_proved` (direct) / `possible` (inherited). It represents a compiler-established structural implementation candidate. The runtime-dispatch conditionality is graded at composition time, not at extraction time.

### Composed caller claims

When a caller reaches a concrete implementation through `Calls → MayDispatchTo`, the capsule entry is represented as:

```json
{
  "relationship": "indirect_dispatch_candidate",
  "direct": false,
  "provenance": "possible"
}
```

The item is never a direct caller of the concrete method and never carries `compiler_proved` as its effective call-site provenance.

### Underlying evidence

Both underlying steps remain visible in the capsule (item `inclusionReason` and/or the `incomingPaths` topology):

1. The caller directly calls the interface method with `compiler_proved` provenance (Calls edge).
2. The interface method may dispatch to the implementation through a structurally `compiler_proved` `MayDispatchTo` edge.

### Direct concrete calls

A genuine source-level call directly targeting the concrete implementation remains:

```json
{
  "relationship": "direct_caller",
  "direct": true,
  "provenance": "compiler_proved"
}
```

Genuine direct calls are not downgraded.

### Framework and DI evidence

`framework_derived` is used only when an actual framework, registration, routing, or DI-derived edge participates in the composed path (e.g. a `framework_derived` Calls hop, or a `RoutesTo`/`Handles`/`Registers` edge). It is never used merely because runtime interface dispatch is conditional.

### Effective provenance composition

Composition distinguishes edge provenance from claim provenance:

- **Edge provenance**: why each graph edge exists (persisted, unchanged).
- **Claim provenance**: how strongly the complete projected statement is supported.

A path can contain individually compiler-proved structural edges while its projected runtime-target claim remains `possible`. Path-level provenance is **not** computed by taking the strongest (or weakest) provenance among the path's edges; the dispatch mediation itself caps the claim at `possible` unless a framework edge participates.

---

## Implemented fix

1. **`CapsuleModels.cs`**: added `CapsuleRelationship` vocabulary (`direct_caller`, `direct_callee`, `indirect_dispatch_candidate`) and `relationship` / `direct` fields on `CapsuleItem` (omitted when not part of the claim).

2. **`EdgeDedup.cs`**: replaced the weakest-edge composition helper with `ComposeDispatchClaimProvenance(pathProvenances, dispatchProvenance)`: `framework_derived` if any edge in the path is framework-derived, otherwise `possible`: regardless of individually compiler-proved structural edges. `ProvenanceRank` now also ranks the canonical `global_implementation_relation` (above `possible`, below `framework_derived`).

3. **`DirectCallersTierBuilder.cs`**: direct `Calls` items stay `direct_caller` / `direct: true` with the hop's own provenance. Callers traced through an incoming `MayDispatchTo` edge are classified `indirect_dispatch_candidate` / `direct: false` with the composed claim provenance. The inclusion reason names both underlying edges and their grades.

4. **`SecondDegreeContextTierBuilder.cs`**: same classification for upstream dependencies reached through dispatch; the framework check spans every hop of the composed path.

5. **`CapsuleBudgetEnforcer.cs`**: source-bounding preserves the new `relationship`/`direct` fields.

6. **`DirectCalleesTierBuilder.cs`**: directly invoked `Calls`/`Constructs` callees stay `direct_callee` / `direct: true` with the hop's own provenance (`compiler_proved`). `AddMayDispatchTargets` projects dispatch targets under `global_implementation_relation` with `relationship: indirect_dispatch_candidate` / `direct: false`; the inclusion reason names the `Calls` edge, the `MayDispatchTo` edge, and the receiver-type narrowing, and states that the candidate is not a direct callee. Receiver-type constraint filtering (PR-6) is retained. The direct pass runs unconditionally before the projection pass, so a target that is both directly called and dispatch-reachable always keeps the direct label regardless of edge enumeration order.

---

## Regression tests

New file: `tests/CapsuleProvenanceCompositionTests.cs` (11 tests):

| Test | Asserts |
|---|---|
| `DirectConcreteCall_IsDirectCompilerProvedCaller` | Direct `Calls` item stays `compiler_proved`, `direct_caller`, `direct: true` |
| `InterfaceMediatedCaller_IsIndirectDispatchCandidate` | Dispatch-mediated caller is `possible`, `indirect_dispatch_candidate`, `direct: false` |
| `DispatchMediatedItems_AreNeverDirectCompilerProved` | No `directCallers`/`secondDegreeContext` item from dispatch is `direct_caller`, `direct: true`, or `compiler_proved` |
| `DirectItems_AreNeverDowngraded` | Every `direct_caller` item keeps `compiler_proved` |
| `CapsulePreservesBothSteps_InInclusionReason` | Every composed caller's reason names both the `Calls` and the `MayDispatchTo` step |
| `MayDispatchToEdge_StaysCompilerProvedInTheGraph` | The persisted dispatch edge keeps `compiler_proved` |
| `EdgeDedup_ComposeDispatchClaimProvenance_GradesTheClaimNotTheEdges` | `(compiler_proved, compiler_proved) → possible`; framework participation → `framework_derived` |
| `FrameworkEdgeInPath_KeepsComposedClaimFrameworkDerived` | A `framework_derived` Calls hop keeps the composed claim `framework_derived` |
| `DirectConcreteCallee_StaysCompilerProvedDirectCallee` | Direct `Calls` callee stays `compiler_proved`, `direct_callee`, `direct: true` |
| `DispatchProjectedCallee_IsGlobalImplementationRelationNotDirectCallee` | Dispatch-projected callee is `global_implementation_relation`, `indirect_dispatch_candidate`, `direct: false`; reason names both steps |
| `DirectCalleeThatIsAlsoDispatchTarget_KeepsDirectLabel` | A target that is both directly called and dispatch-reachable keeps the direct label, regardless of edge enumeration order |

Updated: `tests/ContextTypeAnchorContractTests.cs` :
`TypeAnchor_DirectCallees_IncludeInterfaceMemberAndItsDispatchImplementations`
asserts `global_implementation_relation` for the receiver-compatible dispatch
target (and `!= compiler_proved`), keeps the receiver-incompatible
`RootOnlyHelper` exclusion, and asserts the directly invoked concrete callee
remains `compiler_proved` / `direct_callee` / `direct: true`.

Restored: `tests/UnitTest1.cs`: `InterfaceDispatch_ClassImplementsInterface_EmitsMayDispatchTo` asserts `compiler_proved` again, matching the unchanged extraction.

---

## Migration / snapshot rebuild

None. The stored facts are unchanged, so existing snapshots remain current: the capsule composition reads the persisted `MayDispatchTo` provenance and grades the claim at read time. No re-index is required.

---

## Example: before and after

Fixture: `tests/fixtures/LanguageVersionFallback/Modern/` (`ICourseService`, `CourseService`, `InstructorCourseController` calling `_service.GetByIdForInstructorAsync()`).

### Before

Capsule anchored on `T:Modern.CourseService`, `directCallers`:

```json
{
  "symbolId": "M:Modern.Api.InstructorCourseController.GetById(System.Guid)|...",
  "provenance": "compiler_proved",
  "edgeKind": "Calls",
  "inclusionReason": "Caller of a dispatched-from interface/abstract member. Weaker evidence than a direct call: the runtime dispatch target may differ from the anchor."
}
```

The `provenance` field said `compiler_proved` while the `inclusionReason` warned of weaker evidence: contradictory, and the dispatch hop's meaning was invisible.

### After

```json
{
  "symbolId": "M:Modern.Api.InstructorCourseController.GetById(System.Guid)|...",
  "provenance": "possible",
  "edgeKind": "Calls",
  "relationship": "indirect_dispatch_candidate",
  "direct": false,
  "inclusionReason": "Indirect dispatch candidate: this caller directly calls the interface/abstract member via a Calls edge (compiler_proved), which may dispatch to this implementation at runtime via a MayDispatchTo edge (compiler_proved). The compiler establishes the structural implementation, not the runtime dispatch target, so this caller is not a direct compiler-proved caller of the anchor."
}
```

`provenance` now states the composed claim (`possible`), the `relationship`/`direct` fields prevent the item from being read as a direct caller, and the inclusion reason preserves both underlying steps with their grades.

The same correction applies to `secondDegreeContext`. Direct calls to the concrete implementation remain `direct_caller` / `direct: true` / `compiler_proved`.

The mirror projection on the callee side (`directCallees`) uses the persisted
provenance vocabulary: a concrete implementation surfaced only because it
globally implements the called interface member is labeled
`global_implementation_relation` with `relationship: indirect_dispatch_candidate` /
`direct: false` (receiver-type constraints narrow the candidate set but do not
establish the runtime target), while a genuinely invoked concrete callee
remains `direct_callee` / `direct: true` / `compiler_proved`.
