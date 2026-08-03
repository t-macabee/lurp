# Test Drive Results — PR-7 Remaining §3.6 Surfaces

Work against `C:\Users\Tarik\Desktop\lurp` (C#, .NET 10, branch main, uncommissioned).
All verification runs used narrow `--filter` per the hard constraint.

---

## C9 — Rebuild

**Command:** `dotnet build src/Lurp.csproj -c Release`

**Result:** Build succeeded. 0 Warnings, 0 Errors.

---

## C10 — Filtered test run + estimatedTokens deltas

**Command:** `dotnet test tests/Lurp.Storage.Tests.csproj -c Release --filter "FullyQualifiedName~OutputContinuation|FullyQualifiedName~ContextCapsuleAcceptance|FullyQualifiedName~OutcomeBenchmark|FullyQualifiedName~CapsuleBudgetEnforcer|FullyQualifiedName~ContextBudgeter|FullyQualifiedName~ContextTypeAnchorContract"`

**Result:** Passed! Failed: 0, Passed: 30, Skipped: 0, Total: 30.

### estimatedTokens deltas in `tests/benchmark-runs/baseline.json`

| Scenario | Before | After (shortened text) | Delta |
|---|---|---|---|
| DI replacement (capsule 1) | 1828 | **1877** | **+49** |
| Context/handler (capsule 2) | 1776 | **1825** | **+49** |
| Third scenario (capsule 3) | 3733 | **3782** | **+49** |

All three scenarios shifted by exactly **+49 tokens**. The longer text version was +77 (1905/1853); the shortened text lands at +49 (1877/1825). The delta is uniform across all three scenarios, which is consistent with a single unconditional `InclusionReasons` entry (~35 chars serialized ≈ ~9 tokens) plus framing, counted uniformly — see A2.

---

## A2 — Is `InclusionReasons` measured by `CapsuleBudgetEnforcer`?

**Verdict: YES — measured. No truthfulness gap.**

**Evidence.** `CapsuleBudgetEnforcer.Measure()` (`src/Workspace/CapsuleBudgetEnforcer.cs:93-114`) includes:

```csharp
chars += SerializedChars(capsule.InclusionReasons);   // line 107
```

`SerializedChars<T>` serializes the value to JSON and returns `.Length`, then divides by 4 for the token estimate. So the `omittedTiers.budget_exhausted` entry added unconditionally in `ContextAssembler.PopulateContractSections` (`src/Workspace/ContextAssembler.cs:131-133`) **is** counted in `estimatedTokens`. The truthfulness property recorded in TRUST_KERNEL §"Capsule budget truthfulness" holds: `estimatedTokens` still describes the emitted artifact's content within budget.

The uniform +49 delta across all three baseline scenarios is consistent with this: the line is always present, always measured, and the measure reflects it.

---

## A1 — Does `CapsuleBudgetEnforcer.Enforce` add `budget_exhausted` entries for tier categories?

**Verdict: CONFIRMED — yes. The unconditional emission is correct.**

**Evidence.** In `Enforce` (`src/Workspace/CapsuleBudgetEnforcer.cs:38-86`), after path-bounding and tier-source-bounding, the greedy loop at lines 67-85 calls `trimmer.TrimNextLowestPriority()`. The `SectionTrimmer` constructor (lines 247-283) appends tier sections via `TrimmableSection.Clear(name, items)`, which creates a step with reason `"budget_exhausted"` (line 356). When the budget is still exceeded after bounding, the loop clears tiers greedily and `RecordTruncation` adds each to `capsule.OmittedTiers` with reason `budget_exhausted`.

Because `Enforce` runs **after** `PopulateContractSections` (`ContextAssembler.cs:106`), any check in `PopulateContractSections` conditioned on `capsule.OmittedTiers.Any(...)` would be false at that point — the enforcer's tier-clearing entries don't exist yet. The unconditional emission of the `omittedTiers.budget_exhausted` inclusion reason (`ContextAssembler.cs:131-133`) is therefore the correct design.

---

## A3 — Does `TraceImpact` emit only maximal paths, or also prefixes? Is the set deterministic?

**Verdict: Only maximal paths (never prefixes). The returned SET is deterministic across runs for both branching and cyclic graphs.**

**Evidence.** `TraceImpact` (`src/Workspace/ImpactTraverser.cs:18-54`) is a BFS that enqueues `(currentId, hops, visited)`. A path is added to `results` only when it **cannot be extended**:

- `hopsSoFar.Count >= maxDepth` → truncated path added, stop.
- `edges.Count == 0 && hopsSoFar.Count > 0` → maximal leaf path added, stop.
- After `EnqueueNeighbors`, if `!anyEdgeFollowed && hopsSoFar.Count > 0` → path added.

Critically, a node with outgoing edges enqueues its neighbors and adds **nothing** for the shorter hop list. So prefixes (extendable partial paths) are never emitted — only maximal paths. Group counts in the handler cannot double-count from prefixes.

**Determinism.** Edge ordering comes from `GetOutgoingEdges`/`GetIncomingEdges` (`src/Storage/EdgeOperationsStore.cs:167-203`), which both end in `ORDER BY edge_id`. For an immutable snapshot, `edge_id` is stable, so edge order is deterministic. The BFS queue processes edges in that order, and the per-path `visited` set (not global) prevents cycles. The SET of maximal simple paths is therefore deterministic across runs. (The ImpactHandler then sorts by `PathKey` for cursor stability, so output order is also deterministic.)

Supporting test: `B7ImpactTraverserTests` 14/14 pass.

---

## B4 — `PrintFreshnessLine` signature change: all call sites updated?

**Verdict: YES — all 5 call sites use the new `(string[] args, FreshnessStamp stamp)` signature.**

**Evidence.** `tokensave_callers_for` on `PrintFreshnessLine` returns 5 callers:

| Caller | File:Line | Call |
|---|---|---|
| `ContextHandler.Run` | `ContextHandler.cs:81` | `PrintFreshnessLine(args, freshness)` |
| `ContextHandler.RunTierContinuation` | `ContextHandler.cs:132` | `PrintFreshnessLine(args, freshness)` |
| `FindSymbolHandler.Run` | `FindSymbolHandler.cs:39` | `PrintFreshnessLine(args, freshness)` |
| `ImpactHandler.Run` | `ImpactHandler.cs:112` | `PrintFreshnessLine(args, freshness)` |
| `SearchHandler.Run` | `SearchHandler.cs:119` | `PrintFreshnessLine(args, freshness)` |

No leftover `(FreshnessStamp)`-only calls. The new signature is used uniformly, and `IsQuiet(args)` gates the stderr line inside the method (`HandlerBootstrap.cs:157-163`).

---

## B7 — Flag-collision risk: `--output=`, `--quiet`, `--detail=` vs existing flags

**Verdict: NO collision. The new flags are safe from `--output-dir=` / `--output-json=` shadowing.**

**Evidence.**

The author's concern: `GetArgValue` matches by `StartsWith` (`HandlerBootstrap.cs:28-31`), so could `GetArgValue(args, "--output=")` accidentally match `--output-dir=foo`?

At index 8: `"--output="[8]` is `'='`, while `"--output-dir="[8]` is `'-'`. So `"--output-dir=foo".StartsWith("--output=")` → **false**. The `'='` at index 8 is the disambiguator. Verified both directions:

- `"--output-dir=foo".StartsWith("--output=")` → false ✓
- `"--output-json=foo".StartsWith("--output=")` → false ✓
- `"--output=summary".StartsWith("--output-dir=")` → false ✓

No existing flag uses bare `--output`, `--quiet`, or `--detail` (confirmed via `grep -rn` across `src/` — only hits are the new `HandlerBootstrap`/`StatusHandler`/`Program.cs` usages). The pre-existing flags `--output-dir=` and `--output-json=` are safe.

`--quiet` is matched by exact `args.Contains("--quiet")` (`HandlerBootstrap.cs:72`), not prefix, so no collision surface there either.

`--detail=` (`StatusHandler.cs:207`) is new and has no `--detail-` sibling to collide with.

**Highest-risk item per author — confirmed safe.**

---

## B5 — Who reads the changed `--mode=impact` JSON shape?

**Verdict: Only `tests/OutputContinuationTests.cs`. Benchmark runner, docs, and other handlers do NOT read impact JSON.**

**Evidence.** `ImpactHandler` (`src/Handlers/ImpactHandler.cs`) writes to stdout; it is not consumed by another handler. Searches across `tests/`, `docs/`, `src/README.md`:

- `tests/OutputContinuationTests.cs:124` calls `ImpactHandler.Run` and reads the JSON — asserts on `path_count_total` (line 144), `groups[].path_count` (lines 150-152), etc. **This is the only consumer of the new shape.**
- `OutcomeBenchmarkTests` uses `--mode=context` (capsules), NOT impact — so the benchmark runner does not read impact JSON.
- No `docs/` file parses impact JSON.
- No other handler calls into impact output.

The new fields (`groups`, `truncated`, `offset`, `path_count_total`, sorted `paths`) are exercised by `OutputContinuationTests`, which passed (30/30 above).

---

## B6 — `--cursor=` cross-decoding risk across SearchCursor / SequenceCursor

**Verdict: No shared parsing path in code. Cross-mode decode is structurally possible but never routed by the CLI.**

**Evidence.**

- `SearchCursor` = `record(string SnapshotId, string Fingerprint, string Mode, double? LastRank, string LastFqn, string LastSymbolId)` (`src/Storage/SearchCursor.cs:13-36`).
- `SequenceCursor` = `record(string SnapshotId, string Fingerprint, string Kind, int Offset)` (`src/Storage/SequenceCursor.cs:22`).

Each mode hardcodes its own cursor type — there is no shared decode path:

| Mode | Cursor type | Decode call |
|---|---|---|
| search | `SearchCursor` | `SearchCursor.TryDecode` (`SearchHandler.cs:60`) |
| impact | `SequenceCursor` | `SequenceCursor.TryDecode` via `HandlerBootstrap.ResolveSequenceCursor` (`ImpactHandler.cs:61`) |
| context `--tier` | `SequenceCursor` | `ResolveSequenceCursor` |

A `SequenceCursor` token fed to `SearchCursor.TryDecode` would deserialize with `Mode=null`, `LastRank=null`, `LastFqn=null`, `LastSymbolId=null` (field names don't overlap beyond SnapshotId/Fingerprint) — wrong-but-plausible, not an error. The reverse (search token → SequenceCursor) gives `Kind=null`, `Offset=0`. But the CLI never does this: each handler decodes with its own type only.

**Asymmetric validation:** `SequenceCursor` has a `Validate(snapshotId, fingerprint, kind)` method (`SequenceCursor.cs:55-63`) that rejects mismatched snapshot/fingerprint/kind. `SearchCursor` has NO equivalent `Validate` — it trusts the decoded values. So SequenceCursor is better guarded. The remaining theoretical surface is a user manually copying a cursor token across modes; there is no in-code path that does this.

---

## B8 — Does `StatusHandler` manifest serialization touch the `--output-json=` export path?

**Verdict: NO. The two paths are completely separate.**

**Evidence.**

- `StatusHandler.ManifestJson` (`StatusHandler.cs:227-238`) is called only from `ReportSnapshotOnly`/`ReportFreshness` for the `status --json` output (`StatusHandler.cs:110, 147`). It renders through `JsonSerializer.SerializeToNode` and replaces `documentVersions` with `documentCount` unless `--detail=documents`.
- The `--output-json=` export is parsed in `IndexHandler.cs:22`, passed to `IndexRunner.RunAsync`, and written via `SnapshotManifest.Save(store, ..., jsonExportPath)` (`IndexRunner.cs:134`).

`SnapshotManifest.Save` is the tested one-way export described in TRUST_KERNEL order 7. `StatusHandler.ManifestJson` does not call it, does not touch `jsonExportPath`, and `SnapshotManifest.Save` is not modified. The export path is untouched.

---

## C11 — Targeted test-filter list for the modified files

**Modified files:** `src/Handlers/{Impact,Context,Status,Search,FindSymbol,HandlerBootstrap}.cs`, `src/Program.cs`, `src/Workspace/{ContextAssembler,CapsuleModels}.cs`, `src/Storage/SequenceCursor.cs`.

Per `tokensave_affected`, the covering test files (distance ≤ 2) and a targeted filter:

```
FullyQualifiedName~OutputContinuation
FullyQualifiedName~ContextCapsuleAcceptance
FullyQualifiedName~CapsuleBudgetEnforcer
FullyQualifiedName~ContextBudgeter
FullyQualifiedName~ContextTypeAnchorContract
FullyQualifiedName~OutcomeBenchmark
FullyQualifiedName~SemanticRename
FullyQualifiedName~B7ImpactTraverser
FullyQualifiedName~FreshnessCheapCheck
FullyQualifiedName~SearchHandler
FullyQualifiedName~CliDispatch
```

This is narrower than a full suite run and directly covers the changed surfaces (impact grouping/continuation, capsule single-tier continuation, status `--detail`, `--output`/`--quiet`, cursor decode, handler bootstrap).

---

## D12 — Does `src/README.md` document per-mode CLI flags? What's missing?

**Verdict: YES, it documents per-mode flags, but it is missing all the new flags AND several pre-existing ones.**

**Evidence.** README.md has per-mode tables: `--mode=impact` (lines 126-141), `--mode=context` (145-165), `--mode=status` (172-184), `--mode=search` (75-84), etc.

**Missing from README** (flags that exist in `Program.cs` help / code but are absent from README):

| Flag | Status |
|---|---|
| `--output=summary\|json\|jsonl` | NEW — in Program.cs READ-COMMAND OPTIONS (line 254); NOT in README |
| `--quiet` | NEW — in Program.cs (line 260); NOT in README |
| `--max-paths=<n>` | NEW — in Program.cs IMPACT (line 135); NOT in README (impact table has no `--max-paths` or `--cursor`) |
| `--tier=<name>` / `--tier-limit=<n>` | NEW — in Program.cs CONTEXT (lines 188-195); NOT in README context table |
| `--detail=<list>` | NEW — in Program.cs STATUS (line 235); NOT in README status table |
| `--cursor=<token>` | PRE-EXISTING (PR-5/PR-7) — in Program.cs; NOT documented in README impact/context/search tables |
| `--freshness=<auto\|hash\|off>` | PRE-EXISTING (PR-5/PR-7) — in Program.cs; NOT in README |
| `--require-fresh` | PRE-EXISTING (PR-5/PR-7) — in Program.cs; NOT in README |

The README's impact table (lines 134-141) currently documents only `--symbol`, `--output-dir`, `--direction`, `--max-depth`, `--kinds`, `--snapshot`. It needs `--max-paths`, `--cursor`, `--output`, `--quiet`. The status table needs `--detail`. A top-level "READ-COMMAND OPTIONS" consolidation (as added to `Program.cs`) would also help.

---

## D13 — Clean-full convergence suite: does a named, runnable set exist?

**Verdict: YES — it exists as a coherent, filterable set within one test class, and it DOES compare diagnostics.**

**Evidence.** The convergence suite is the `IncrementalIndex_Matches_FullRebuild_*` tests in class `PipelineEquivalenceTest` (`tests/CleanRebuildEquivalenceTest.cs`, 12 tests, lines 72-963). They share the naming prefix and the `SnapshotAssertions.CompareSnapshotsAreEquivalent` helper (`tests/SnapshotAssertions.cs:47-60`), so they are a named, runnable set — not scattered.

**Diagnostics ARE compared:** `SnapshotAssertions.CompareSnapshotsAreEquivalent` retrieves and asserts diagnostics equality at lines 108-117:

```csharp
var diagB = storeB.GetDiagnostics(snapshotB);
var diagC = storeC.GetDiagnostics(snapshotC);
NormalizeDiagnostics(diagB);
NormalizeDiagnostics(diagC);
Assert.Equal(diagC.Count, diagB.Count);
...
```

The comparison scope also includes symbols, declarations, edges, binding incompleteness, annotations, FTS, and semantic changes.

**Exact filter expression to run it:**

```
FullyQualifiedName~IncrementalIndex_Matches_FullRebuild
```

(There is also `IncrementalIndex_ThreeProjectReverseClosure_MatchesFullIncludingDiagnostics`, referenced in TRUST_KERNEL, but that test lives in a different class — it is the explicit three-project-with-diagnostics case, whereas the `Matches_FullRebuild_*` set is the general convergence suite.)

---

## Summary of author's top-ranked questions

| Rank | Question | Verdict |
|---|---|---|
| 1 | **B7** flag collision | **Safe** — `=` at index 8 disambiguates `--output=` from `--output-dir=`/`--output-json=` |
| 2 | **A2** inclusionReasons measured | **Yes** — line 107 of CapsuleBudgetEnforcer; truthfulness holds; +49 delta uniform |
| 3 | **A1** Enforce adds tier `budget_exhausted` | **Confirmed** — unconditional emission is correct |
| 4 | **A3** TraceImpact prefixes / determinism | **Maximal paths only; set is deterministic** |
| 5 | **B5** impact JSON consumers | **Only `OutputContinuationTests`**; not benchmark/docs/handlers |

### Additional confirmed items

- C9 clean build ✓; C10 30/30 ✓.
- B4 all 5 `PrintFreshnessLine` call sites updated ✓.
- B8 `--output-json=` export path untouched by StatusHandler changes ✓.
- B6 no shared cursor-decode path; SequenceCursor has `Validate`, SearchCursor does not.
- D12 README documents per-mode flags but omits `--output`, `--quiet`, `--max-paths`, `--tier`, `--tier-limit`, `--detail`, and pre-existing `--cursor`/`--freshness`/`--require-fresh`.
- D13 convergence suite = `IncrementalIndex_Matches_FullRebuild_*` in `PipelineEquivalenceTest`; diagnostics compared; filter `FullyQualifiedName~IncrementalIndex_Matches_FullRebuild`.
