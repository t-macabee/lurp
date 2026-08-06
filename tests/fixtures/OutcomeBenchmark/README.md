# Lurp outcome benchmark

This benchmark implements T11 (see `docs/TRUST_KERNEL.md`). It measures agent-facing outcomes for three architecture scenarios, using a small committed multi-project fixture rather than database size or edge counts:

1. **Local validation change**: start at `Outcome.Validation.OrderValidator`, preserve its `IOrderValidator` contract and relevant validation test, and change one validation rule.
2. **Handler/DTO modification**: start at `Outcome.Application.OrderHandler`, include `IOrderHandler`, `IOrderValidator`, and the handler test, and change the request DTO plus handler behavior.
3. **DI implementation replacement**: start at `Outcome.Composition.ServiceRegistration.Configure`, identify the registered validator implementation, and declare the hosted-service runtime vector as unknown.

The machine-readable scenario contract, including the expected test symbols and patch scope, is in `scenarios.json`.

This fixture has no external package dependencies. The test project defines a minimal `FactAttribute`, which is sufficient for Lurp's test adapter while keeping the benchmark reproducible without a test framework restore.

## Recorded format

`tests/OutcomeBenchmarkTests.cs` is the repeatable runner. It writes `tests/benchmark-runs/baseline.json` (or the path in `LURP_OUTCOME_BENCHMARK_OUTPUT`) with one record per scenario:

- requested and resolved starting symbol, source retrieval, and capsule token estimate;
- every capsule item with tier, edge kind, provenance, symbol ID, FQN, and `relevant`/`irrelevant`/`unclassified` classification;
- expected versus actual contracts and missing contracts;
- declared uncertainty/runtime vectors;
- expected versus observed patch files;
- incremental snapshot versus clean full snapshot agreement and any comparison error;
- explicit failure flags used to prioritize later work.

The ten architecture measures are recorded as named measures in the output. The worker-token measure is intentionally not synthesized by the runner: this harness records capsule size, while the consuming agent or evaluation controller must fill in the post-capsule token count.

## Run

```powershell
dotnet test tests/Lurp.Tests.csproj --filter "FullyQualifiedName~OutcomeBenchmarkTests" --no-restore --no-build --verbosity normal -m:1
```

To choose an output path:

```powershell
$env:LURP_OUTCOME_BENCHMARK_OUTPUT = "tests/benchmark-runs/baseline.json"
dotnet test tests/Lurp.Tests.csproj --filter "FullyQualifiedName~OutcomeBenchmarkTests" --no-restore --no-build --verbosity normal -m:1
```

The runner uses a fresh temporary copy for each scenario, indexes it fully, assembles the capsule, applies the scenario edit, indexes incrementally, then performs a clean full index and compares the two snapshots with the repository's canonical equivalence assertion.
