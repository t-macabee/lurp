# Acting on docs/ARCHITECTURE_AUDIT.md

## Context

`docs/ARCHITECTURE_AUDIT.md` proposes three ordered fixes. I visited every code
site it cites. The audit is **partly stale and partly wrong**, so this plan is
not "execute items 1–3". It implements the parts that survived inspection and
explicitly drops the parts that did not.

What inspection found:

- **Item 1 is real but overstated.** The audit says "14 handlers repeating the
  same 12-line bootstrap block". The actual shared shape is smaller and splits
  into two independent repeats: a ~6-line store scope (13 handlers) and a 3-line
  freshness triple (7 handlers). Pre-store arg parsing varies per handler and
  cannot be folded. The 3 `Simulate*Handler` files, however, are byte-identical
  apart from two tokens — verified below.
- **Item 2's premise is stale and its diagnosis is wrong.** The audit's
  "byte-identical `IsInScope`" was already fixed by commit `04e3730`; both
  callers now delegate to [ExtractionUtils.IsInScope](src/Workspace/ExtractionUtils.cs:43).
  More importantly, the "4 overlapping shapes" are not overlapping: they serve
  two opposite roles (see below). The proposed `sealed`-composition rewrite is
  churn against live extraction for no functional gain.
- **Item 3** the audit itself recommends deferring. Agreed — dropped.

## Evidence

Store scope, repeated verbatim in 13 handlers
([SimulateRenameHandler.cs:18-38](src/Handlers/SimulateRenameHandler.cs:18),
[FindSymbolHandler.cs:19-79](src/Handlers/FindSymbolHandler.cs:19),
[NavigateHandler.cs:15-34](src/Handlers/NavigateHandler.cs:15),
[ImpactHandler.cs:44-162](src/Handlers/ImpactHandler.cs:44)):

```
ResolveOutputDir → ResolveDbPath → OpenStore → try { ResolveSnapshotId; … } finally { store.Close(); }
```

Freshness triple, repeated in 7 handlers (e.g. [FindSymbolHandler.cs:38-40](src/Handlers/FindSymbolHandler.cs:38),
[ImpactHandler.cs:110-112](src/Handlers/ImpactHandler.cs:110)):

```
ComputeFreshnessStamp → EnforceRequireFresh → PrintFreshnessLine
```

The three simulate handlers are identical except for the `--mode=` name in the
error string and the engine method (`SimulateRename` / `SimulateRemove` /
`SimulateMove`). Registered at [Program.cs:73,76,79](src/Program.cs:73) via
`Sync(...)`.

Extraction-context roles (why item 2's consolidation is rejected):

| Type | Role |
|---|---|
| [AdapterExtractionContext](src/Adapters/AdapterExtractionContext.cs:7) | **input** to adapters: compilation, scope, semantic-model cache |
| [SymbolExtractionContext](src/Workspace/SymbolExtractionContext.cs:5), [ExtractionContextBase](src/Workspace/ExtractionContextBase.cs:6) | **input** to workspace extractors |
| [ExtractionContext](src/Adapters/ExtractionContext.cs:6), [DiExtractionContext](src/Adapters/DependencyInjectionAdapter.cs:17) | **output accumulators**: `Edges`, `Seen` |

Inputs and accumulators are not duplicates of each other. Only the last row is
a genuine near-duplicate pair.

## Plan

### 1. Collapse the three simulate handlers (highest confidence)

Replace the three files with one `src/Handlers/SimulateHandler.cs` parameterized
by mode name and a `Func<SimulationEngine, string, SimulationReport>` selector.
Update the three [Program.cs:73,76,79](src/Program.cs:73) registrations to pass
the selector; `Sync(...)` and the `ModeRegistry` shape are unchanged, so
`--mode=` strings, output JSON, and exit codes are all byte-identical.

Reuse: `HandlerBootstrap.ResolveSymbolArg`, `.Fail`, `.IndentedJson` (prefer the
existing shared `IndentedJson` over the per-file `new JsonSerializerOptions`
these three currently allocate).

### 2. Two composition helpers on `HandlerBootstrap`

Add to [HandlerBootstrap.cs](src/Handlers/HandlerBootstrap.cs), composing the
existing statics — no behavior change:

- `WithStore<T>(string[] args, string? snapshotArg, Func<SqliteIndexStore, string, T> body)`
  — resolve dir → db → open → resolve snapshot → `try/finally { store.Close() }`.
- `ResolveFreshness(string[] args, SqliteIndexStore store, string snapshotId)`
  — compute + `EnforceRequireFresh` + `PrintFreshnessLine`, returns the stamp.

Migrate handlers **incrementally**, simplest first: `NavigateHandler`,
`FindSymbolHandler`, `GetSourceHandler`, `GetSymbolHandler`, `DiffHandler`,
`SearchHandler`, then `ImpactHandler` / `ContextHandler` / `AnnotationHandler`.
Leave `IndexHandler` and `StatusHandler` alone — they don't fit the read-store
shape (`StatusHandler` is the only Roslyn-bearing path).

Arg parsing stays in each handler above the `WithStore` call, since it varies.

### 3. Fold `DiExtractionContext` into the shared accumulator record

Add `ExtractorVersion` and `Incompleteness` to
[ExtractionContext](src/Adapters/ExtractionContext.cs:6), then delete
`DiExtractionContext` and retype `DependencyInjectionAdapter` +
`DependencyInjectionConventionMatcher` call sites.

Two constraints:
- **Do not** touch `TestAdapter`'s private nested `ExtractionContext`
  ([TestAdapter.cs:17](src/Adapters/TestAdapter.cs:17)) — despite the name it
  carries test-method-specific fields and is not an accumulator-only shape. Note
  it currently *shadows* the namespace-level record; folding DI in makes that
  shadowing more confusing, so rename the nested one to `TestEmitContext` as
  part of this step.
- The tuple element casing differs (`(string Source, …)` vs `(string source, …)`);
  pick the namespace-level record's casing.

Do this step **only after** steps 1–2 are green, so a regression bisects cleanly.

### Explicitly dropped

- **`ExtractionContextBase` → sealed composition.** An abstract base with two
  subclasses that share real state (`_gitRoot`, semantic-model cache,
  `EdgeLocationResolver`) is not a defect. The rewrite touches live
  polymorphism/reflection extraction for zero functional gain.
- **`IMigration.Down`.** Audit's own recommendation; `--strategy=full` rebuild is
  the stated correctness reference.
- **Test-suite notes** (audit's last section) — informational, out of scope.

### Docs

Per project convention, update `docs/TRUST_KERNEL.md` where it describes the
handler bootstrap and (line ~378) `ExtractionContextBase`. Also correct
`docs/ARCHITECTURE_AUDIT.md` itself: its item-2 `IsInScope` claim is stale.

## Verification

1. `rtk dotnet build` — must be clean.
2. Narrow test runs only (no full suite), after each step:
   - `--filter FullyQualifiedName~Handler` after steps 1–2.
   - `--filter FullyQualifiedName~CleanRebuildEquivalence` after step 3 — the
     project's stated correctness reference for extraction-shape changes.
3. CLI parity spot-check against a real index, before vs after:
   `--mode=simulate-rename`, `--mode=find-symbol`, `--mode=impact` with
   `--output=json`, `--output=summary`, and `--require-fresh`. Output must be
   byte-identical.

## Note on scope

Per the standing prohibition in my project memory, this is analysis only. No
code will be touched until you explicitly approve.
