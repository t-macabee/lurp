# scripts/perf/

Performance measurement for Lurp. This folder **measures**, and it also holds the growth gate
(the `--evaluate` mode of the harness). Baseline files are a later phase.

- `gen/`: deterministic generator for synthetic C# solutions. `--size=1` writes 5 projects,
  `4` writes 20, `16` writes 80; every project has 50 `.cs` files (250 / 1,000 / 4,000
  documents). `--out` must be outside the Lurp repository; the generator refuses an in-repo
  path and never deletes. The same `(size, seed)` pair always writes byte-identical files.
- `harness/`: runs the tool under test as a child process and records wall time, peak working
  set, exit code, DB size and its change per run, the index-time step-1 metric, and every
  `--mode=timings` step.

## Generate and restore a fixture

```powershell
dotnet run --project scripts/perf/gen -c Release -- --size=4 --out=C:\perf\4x
dotnet restore C:\perf\4x\Perf.slnx
```

The fixture root carries `.lurp-perf-gen` and `perf-manifest.json`. The manifest holds the size
counts and an `anchors` object (see "Anchors" below). The harness refuses a `--fixture`
without the `.lurp-perf-gen` marker, so edit operations can never touch an unmarked tree.

## Run the harness

Fixture run (a generated tree; the edit operations run):

```powershell
dotnet run --project scripts/perf/harness -c Release -- --fixture=C:\perf\4x --work=C:\perf\work --out=C:\perf\results\4x.json --protocol=gate
```

Solution run (never edits the tree; anchors optional):

```powershell
dotnet run --project scripts/perf/harness -c Release -- --solution=C:\src\My.sln --work=C:\perf\work --out=C:\perf\results\my.json --protocol=gate --anchors=C:\perf\anchors.json
```

```
dotnet run --project scripts/perf/harness -c Release -- --fixture=<generated dir> | --solution=<path to .sln/.slnx> --work=<dir> --out=<result.json> [--protocol=gate] [--runs=5] [--warmup=1] [--anchors=<json>] [--lurp-cmd="<exe and leading args>"] [--keep-work]
```

- Each operation runs `--warmup` times unmeasured, then `--runs` times measured. Every measured
  run is recorded.
- `--protocol=gate` fixes `--runs=5 --warmup=3`, records `"protocol": "gate"`, and rejects an
  explicit `--runs` or `--warmup`. Use it for 1×/4×/16× comparisons. Warmup 3 matches the
  snapshot pruner, which keeps three snapshots: the first measured incremental run then already
  runs with pruning active. Without that warmup a 4× run stays under the keep limit and never
  prunes while a 16× run does, and the 16×/4× ratio compares two different regimes. Without
  `--protocol`, the harness behaves as before and records `"protocol": "custom"`.
- `--fixture` and `--solution` are mutually exclusive; exactly one is required.
- `--work` is scratch space: outside the repo, outside the fixture and outside a `--solution`
  tree. The harness writes a `.lurp-perf-work` marker and refuses a non-empty directory without
  that marker.
- `--out` must be outside the Lurp repo (checked from the path upward for `Lurp.slnx`, like the
  generator). Parent directories are created.
- `--anchors` applies to `--solution` runs only; fixtures carry their anchors in the manifest.
  The file may be either the anchors object itself or an object with an `anchors` member.
- `--keep-work` skips the cleanup described under "Work directories".

### Tool under test

The harness resolves the command in this order:

1. `--lurp-cmd="<exe and leading args>"`.
2. the `LURP_CMD` environment variable (same meaning as in `scripts/r1-verify-enote.sh`).
3. `dotnet <repo>\src\bin\Release\net10.0\Lurp.dll`.

The value is split on whitespace; double quotes group tokens, so a path with spaces is written
`--lurp-cmd='"C:\my tools\lurp.exe"'` (PowerShell single-quoted outer). Note that
`dotnet run --project ...` puts a launcher in front of the app, and the launcher's memory is
what gets sampled; prefer a direct DLL or exe path.

### Packed tool

Build and install the packed tool exactly as CI does, then pass it through `--lurp-cmd`:

```powershell
dotnet pack src/Lurp.csproj --no-build --configuration Release -o ./artifacts
$version = (Select-String -Path src/Lurp.csproj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
dotnet tool install --tool-path ./t --source ./artifacts lurp --version $version
dotnet run --project scripts/perf/harness -c Release -- --fixture=C:\perf\1x --work=C:\perf\work --out=C:\perf\results\1x-packed.json --runs=1 --warmup=0 --lurp-cmd="C:\repo\t\lurp.exe"
```

## Growth gate

The gate compares a 4x and a 16x result file. For each operation it divides the 16x
median by the 4x median, for `wall_ms` and for `peak_working_set_mb`. The limit is 5
and it is inclusive: a ratio of 5.0 passes.

The CI workflow runs by hand and nightly on main, not on pull requests. It takes about
60 to 80 minutes.

- A memory ratio above 5 fails the gate for every operation.
- A time ratio above 5 fails the gate for `full_index`, `incremental_1_file`,
  `incremental_20_files`, `diff` and `dead_candidates`. For every other operation it
  only warns.
- The gate fails on missing data: an operation that exists in only one file, one of
  the five operations above that is missing in both files, `failed: true`, a median
  of 0 or null, a skip that is not a documented solution-only skip, a protocol other
  than `gate`, and `runs` or `warmup` that differ between the files.

Exit codes: 0 = pass (warnings are allowed), 1 = gate failed, 2 = usage or data error.

The gate fails on growth ratio above 5: memory for all operations, time for the five
listed. Both sizes must run in the same CI job; absolute wall times only warn.

Time ratios are only valid when both sizes run in the same session. Wall time on this
machine swings 4-5x between sessions; memory is stable.

Run the gate by hand:

```powershell
dotnet run --project scripts/perf/harness -c Release -- --evaluate=C:\perf\results\4x.json,C:\perf\results\16x.json --summary=C:\perf\results\gate-summary.md
```

`--max-ratio=<n>` sets the limit (default 5). `--summary` writes the markdown report
to a file. Without `--summary` the report goes to stdout only.

## Result JSON (`--out`)

```jsonc
{
  "schema_version": 1,                 // bump when a field changes meaning
  "tool_cmd": "dotnet C:\\...\\Lurp.dll",
  "tool_version": "lurp 2.0.0",        // first line of `--version`
  "repo_commit": "27b5c3e",            // `git log --oneline -1`, null when not a repo
  "repo_dirty": true,                  // `git status --short` non-empty
  "os": "Microsoft Windows 10.0.26300",
  "runner_image": "20251001.1",        // env ImageVersion, null locally
  "fixture": { ...perf-manifest.json..., "path": "C:\\perf\\4x" },  // null for --solution
  "solution": "C:\\src\\My.sln",       // null for --fixture
  "work_dir": "...", "run_dir": "...", // run_dir holds this invocation's output dirs
  "started_at_utc": "...", "finished_at_utc": "...",
  "protocol": "gate",                  // gate | custom; custom when --protocol is absent
  "runs": 5, "warmup": 3, "keep_work": false,
  "operations": [
    {
      "name": "full_index",
      "anchor": null,                  // anchor role for impact/context ops
      "skipped": false, "skip_reason": null,
      "failed": false, "failure": null,
      "server_exit_code": null,        // MCP ops only
      "touched_files": null,           // freshness_*_touched ops: count whose mtime was touched, else null
      "freshness_state": null,         // freshness ops: state from the first measured run, else null
      "freshness_changed_document_count": null,  // same run's changed_document_count
      "runs": [
        {
          "wall_ms": 15443,            // CLI: process start to exit. MCP: tools/call latency
          "peak_working_set_mb": 232,  // polled PeakWorkingSet64, see below
          "exit_code": 0,              // MCP: 0 = call returned a result, 1 = call error
          "db_bytes": 35401728,        // index.db size after index runs, else null
          "db_delta_bytes": 4096,      // db_bytes minus the previous measured run of this operation; null on the first
          "timings_total_ms": 15443,   // --mode=timings total_ms; null when the run wrote no new snapshot
          "timings_steps": [ { "step": "load_workspace", "elapsed_ms": 1234 } ],  // from the same call; null with timings_total_ms
          "metric_peak_working_set_mb": 232,  // step-1 snapshot metric, index runs only
          "peak_cross_check": "ok",    // ok | mismatch | no-new-snapshot | no-metric
          "error": null
        }
      ],
      "median": { "wall_ms": 15443, "peak_working_set_mb": 232 },  // average of the two middle values
      "max": { "wall_ms": 15443, "peak_working_set_mb": 232 }
    }
  ]
}
```

`skipped` operations carry `skip_reason` and no runs. The incremental operations 2 and 3, and
every read operation, run against the output directory of the first measured full-index run.
Before the edit operations the harness pins that full-index snapshot (`--mode=pin-snapshot`) so
the later `diff` can still name it; snapshot pruning keeps three complete snapshots and would
otherwise drop it. The pin is cleared before the read operations run.

Index operations: `full_index`, `noop_incremental`, `incremental_1_file`,
`incremental_20_files`. Reads: `impact.{upstream|downstream}.d{3|6}.{anchor}`,
`context.{default|maxhops6}.{anchor}`, `search`, `grep`, `find_symbol`, `diff`,
`find_symbol.freshness_{auto|hash}_{untouched|touched}`, `dead_candidates`,
`mcp_status.{default|full}`. The two `_touched` operations run last, after `mcp_status`, so no
other operation observes the touched mtimes; see "Edits".

`timings_total_ms` and `timings_steps` come from one `--mode=timings --json` call per index run
that wrote a new snapshot. The tool keys timings by snapshot id, so a run that reused the
previous snapshot (a no-op) records both as null.

## Peak working set

The harness polls `Process.PeakWorkingSet64` every 50 ms while the child runs (calling
`Process.Refresh()` first; without it the value is cached and stays at its first sample).
`PeakWorkingSet64` is itself the peak over the process lifetime so far, so the last sample
before exit is the true peak unless the process grew inside the final 50 ms window. A read that
finishes faster than 50 ms can therefore under-report; the index runs are long enough that the
polled value should match the step-1 in-process metric, and the harness reports both
(`peak_working_set_mb`, `metric_peak_working_set_mb`) plus `peak_cross_check` ("ok" when they
agree within 15%). A no-op incremental that reuses the previous snapshot has no new metric:
`peak_cross_check` is `no-new-snapshot`, and `timings_total_ms`/`timings_steps` are null,
because `--mode=timings` is keyed by snapshot id and the run created no new snapshot.

BuildHost child processes (MSBuild's out-of-proc build servers) are **not** counted by either
number; both measure the process the harness started.

## Edits

Edit operations run only for a `--fixture` with the `.lurp-perf-gen` marker. `--solution` runs
skip them and say so in the output. The 1-file edit targets `anchors.edit_document`; the 20-file
edit targets the first 20 `Unit*.cs` files beside it. Each edit appends one method to the last
type in the file; the appended method alternates between an A shape (`PerfEditA<n>`) and a B
shape (`PerfEditB<n>`), with `n` continuing from the methods already in the file. The file
therefore never returns to a state that was already indexed, and every edit run produces a new
snapshot and a step-1 metric.

The touched freshness operations add one narrower change, and it is the only write the harness
performs besides the generated-file edit above. Before
`find_symbol.freshness_auto_touched` and `find_symbol.freshness_hash_touched` the harness
selects every 10th fixture document (`.cs` files outside `obj`/`bin`, sorted by relative path,
ordinal) and sets its `LastWriteTimeUtc` to now + 1 minute without changing its content. The two
operations record the count as `touched_files`. The harness restores every original mtime in a
`finally` block, on failure as well. The touched operations run last, after `mcp_status`, so no
other operation sees the touched files. `freshness_auto_touched` must report `stale` with
`changed_document_count` equal to `touched_files`; `freshness_hash_touched` must report `fresh`
with 0 changed documents. A mismatch marks the operation failed and records the observed values
in `failure`.

## Work directories

The harness creates `run-<utcstamp>-<pid>` under `--work`. On the next invocation it deletes
sibling `run-*` directories from earlier runs (output dirs it created) before starting, unless
`--keep-work` is given. It never deletes anything else and never touches the repository.

## Anchors

The generator's manifest carries role-based anchors that mean the same at every size:

```json
"anchors": {
  "upstream_wide": "Perf.P0.N2.A0_2.Op0",   // project-0 method called from the most projects
  "downstream_wide": "Perf.P79.N0.A79_0.Op0", // method in the last project
  "middle": "Perf.P40.N0.A40_0.Op0",        // method in the middle project
  "edit_document": "Perf.P79/Unit00.cs"     // leaf file the edit operations change
}
```

The harness checks each anchor with `--mode=find-symbol` (exit 0) after the first full index and
aborts with a clear error before measuring anything else if one fails to resolve. A `--solution`
run without `--anchors` skips every anchor-based operation.
