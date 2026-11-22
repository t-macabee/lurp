# scripts/perf/

Performance measurement for Lurp. This folder measures, and it holds the growth gate (`--evaluate`) and the baseline gate (`--baseline`). Committed baselines are in `perf/baselines/`.

- `gen/`: deterministic generator for synthetic C# solutions. `--size=1` writes 5 projects,
  `4` writes 20, `16` writes 80; every project has 50 `.cs` files (250 / 1,000 / 4,000
  documents). Put `--out` outside all Lurp checkouts. The generator never deletes files.
  In a marked directory, it requires a readable manifest with the requested size and seed.
  The same size and seed always produce byte-identical files.
- `harness/`: runs the tool under test as a child process and records wall time, peak working
  set, exit code, DB size and its change per run, the index-time step-1 metric, and every
  `--mode=timings` step.

## Generate and restore a fixture

```powershell
dotnet run --project scripts/perf/gen -c Release -- --size=4 --out=C:\perf\4x
dotnet restore C:\perf\4x\Perf.slnx
```

```bash
dotnet run --project scripts/perf/gen -c Release -- --size=4 --out=$HOME/perf/4x
dotnet restore $HOME/perf/4x/Perf.slnx
```

The fixture root contains `.lurp-perf-gen` and `perf-manifest.json`. The manifest holds the
size, counts and an `anchors` object. The harness requires the marker before it edits a
fixture. It also checks that the number of `.cs` files outside `obj` and `bin` matches the
manifest's `documents` count.

## Run the harness

Fixture run (a generated tree; the edit operations run):

```powershell
dotnet run --project scripts/perf/harness -c Release -- --fixture=C:\perf\4x --work=C:\perf\work --out=C:\perf\results\4x.json --protocol=gate
```

```bash
dotnet run --project scripts/perf/harness -c Release -- --fixture=$HOME/perf/4x --work=$HOME/perf/work --out=$HOME/perf/results/4x.json --protocol=gate
```

Solution run (never edits the tree; anchors optional):

```powershell
dotnet run --project scripts/perf/harness -c Release -- --solution=C:\src\My.sln --work=C:\perf\work --out=C:\perf\results\my.json --protocol=gate --anchors=C:\perf\anchors.json
```

```bash
dotnet run --project scripts/perf/harness -c Release -- --solution=$HOME/src/My.sln --work=$HOME/perf/work --out=$HOME/perf/results/my.json --protocol=gate --anchors=$HOME/perf/anchors.json
```

```
dotnet run --project scripts/perf/harness -c Release -- --fixture=<generated dir> | --solution=<path to .sln/.slnx> --work=<dir> --out=<result.json> [--protocol=gate] [--runs=5] [--warmup=1] [--anchors=<json>] [--lurp-cmd="<exe and leading args>"] [--keep-work]
```

- Each operation runs `--warmup` times unmeasured, then `--runs` times measured. Every measured
  run is recorded.
- `--protocol=gate` sets `--runs=5 --warmup=3` and records `"protocol": "gate"`. It rejects
  an explicit `--runs` or `--warmup`. The pruner keeps three complete snapshots. After
  the three warmup edits, measured edits run with pruning active at every fixture size.
  Each full-index run uses a fresh database. A no-op incremental can reuse a snapshot.
  Without `--protocol`, the harness records `"protocol": "custom"`.
- Specify exactly one of `--fixture` and `--solution`.
- Put `--work` outside the fixture or solution tree. The harness writes a `.lurp-perf-work`
  marker and refuses a non-empty directory without the marker.
- Put `--work` and `--out` outside all Lurp checkouts. The harness checks the checkout of
  its binary, the checkout of the working directory, and any `Lurp.slnx` above the given path.
  It creates parent directories for `--out`.
- Use `--anchors` for `--solution` runs. Fixtures carry their anchors in the manifest.
  The file can contain the anchors directly or in an `anchors` member.
- `--keep-work` skips the cleanup described under "Work directories".

### Tool under test

The harness resolves the command in this order:

1. `--lurp-cmd="<exe and leading args>"`.
2. the `LURP_CMD` environment variable (same meaning as in `scripts/r1-verify-enote.sh`).
3. `dotnet <repo>/src/bin/Release/net10.0/Lurp.dll` (`\` on Windows).

The value is split on whitespace; double quotes group tokens, so a path with spaces is written
`--lurp-cmd='"C:\my tools\lurp.exe"'` (PowerShell single-quoted outer). Note that
`dotnet run --project ...` puts a launcher in front of the app, and the launcher's memory is
what gets sampled; prefer a direct DLL or exe path.

### Packed tool

Build and install the packed tool exactly as CI does, then pass it through `--lurp-cmd`:

```powershell
dotnet pack src/Lurp.csproj --no-build --configuration Release -o ./artifacts
Import-Module ./scripts/ci/LurpCi.psm1 -Force; $version = Get-LurpVersion
dotnet tool install --tool-path ./t --source ./artifacts lurp --version $version
dotnet run --project scripts/perf/harness -c Release -- --fixture=C:\perf\1x --work=C:\perf\work --out=C:\perf\results\1x-packed.json --runs=1 --warmup=0 --lurp-cmd="C:\repo\t\lurp.exe"
```

```bash
dotnet pack src/Lurp.csproj --no-build --configuration Release -o ./artifacts
version=$(pwsh -NoProfile -Command 'Import-Module ./scripts/ci/LurpCi.psm1 -Force; Get-LurpVersion')
dotnet tool install --tool-path ./t --source ./artifacts lurp --version "$version"
dotnet run --project scripts/perf/harness -c Release -- --fixture=$HOME/perf/1x --work=$HOME/perf/work --out=$HOME/perf/results/1x-packed.json --runs=1 --warmup=0 --lurp-cmd="$PWD/t/lurp"
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
- Pass a size-4 fixture result first and a size-16 fixture result second. Both must
  have the same generator version and seed and a null `solution`.
- The gate requires `protocol: gate`, five runs and three warmups in each file.
- The gate fails if an operation appears in only one result, or if either result omits
  one of the five operations above. It also fails on every failed or skipped operation.
- For each measured operation the gate requires five run records with exit code 0.
  It calculates both medians from those records. A missing, nonpositive or incorrect
  stored median fails the gate.

Exit codes are 0 for pass, 1 for a failed gate, 2 for a usage or data error, and 3 for a
harness defect. A file-system error on a given path or a tool that cannot start returns 2.

Time ratios are only valid when both sizes run in the same session. Wall time on this
machine swings 4-5x between sessions; memory is stable.

Run the gate by hand:

```powershell
dotnet run --project scripts/perf/harness -c Release -- --evaluate=C:\perf\results\4x.json,C:\perf\results\16x.json --summary=C:\perf\results\gate-summary.md
```

```bash
dotnet run --project scripts/perf/harness -c Release -- --evaluate=$HOME/perf/results/4x.json,$HOME/perf/results/16x.json --summary=$HOME/perf/results/gate-summary.md
```

`--max-ratio=<n>` sets the limit (default 5). `--summary` writes the markdown report
to a file. Without `--summary` the report goes to stdout only.

## Baseline gate

The baseline gate compares a candidate result file against a committed baseline result file
for the same solution. The baseline is a harness result file (the `--out` output); it is not a
new format.

Run it by hand:

```powershell
dotnet run --project scripts/perf/harness -c Release -- --baseline=perf\baselines\win25-vs2026.json --candidate=C:\perf\results\enotev2.json --summary=C:\perf\results\baseline-summary.md
```

```bash
dotnet run --project scripts/perf/harness -c Release -- --baseline=perf/baselines/ubuntu24.json --candidate=$HOME/perf/results/enotev2.json --summary=$HOME/perf/results/baseline-summary.md
```

A value equal to its limit passes.

- A candidate peak above 1.25 times the baseline peak fails for every operation.
- A candidate peak above 300 MB fails for `impact.*`, `context.*`, `search`, `grep`,
  `find_symbol`, `outline`, `navigate`, `get_symbol`, `get_annotations.document`
  and `grep.ignore_case`.
- A candidate wall time above 1.25 times the baseline wall time warns when the baseline
  median is at least 25 ms.
- Wall times above the caps also warn. The caps are 2,000 ms for `impact.*.d6.*`,
  3,000 ms for `context.*` and 500 ms for `search`.
- Both files must come from `--solution` runs and have the same `solution_commit` and
  `runner_os`. Both require `protocol: gate`, five runs and three warmups.
- Each measured operation must have five run records with exit code 0. A missing,
  nonpositive or incorrect stored median fails the gate.
- The gate requires measured `impact.*` and `context.*` operations. It accepts a skip
  only when both results skip the operation and each gives one of these reasons:
  `--solution runs never edit files` or `--solution runs never touch files`.
  A solution run records `diff` as skipped with the first reason.

The report shows both tool versions. It warns when a version is unreadable, the major
versions differ, or the minor versions differ by more than one.

The baseline key is the runner OS only (`ImageOS`, for example `win25-vs2026`), so
`perf/baselines/win25-vs2026.json`. `ImageVersion` changes about once a week on hosted runners; a
different image only warns. A new runner OS (for example a move from `win25-vs2026` to a later image) has no
baseline and fails until one is committed.

When `perf/baselines/<ImageOS>.json` does not exist, the gate runs the candidate as its own
baseline (memory ratios are 1.00, so only the 300 MB cap and the time caps can show), prints
the result, and then fails with the commit instruction.

To take a new baseline: download the `perf-enotev2-<OS>` artifact from a `real-parity.yml` run,
review the `enotev2.json` it holds, and commit it as `perf/baselines/<ImageOS>.json` in its own
commit.

Exit codes are 0 for pass, 1 for a failed gate, 2 for a usage or data error, and 3 for a
harness defect. A file-system error on a given path or a tool that cannot start returns 2.

## Result JSON (`--out`)

```jsonc
{
  "schema_version": 1,                 // bump when a field changes meaning
  "tool_cmd": "dotnet C:\\...\\Lurp.dll",
  "tool_version": "lurp 2.0.0",        // first line of `--version`
  "repo_commit": "27b5c3e",            // `git log --oneline -1`, null when not a repo
  "repo_dirty": true,                  // tracked changes only
  "os": "Microsoft Windows 10.0.26300",
  "runner_image": "20251001.1",        // env ImageVersion, null locally
  "runner_os": "win25-vs2026",                // env ImageOS, null locally
  "solution_commit": "4ba9901a...",     // `git rev-parse HEAD` of the solution directory; null for --fixture
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
`context.{default|maxhops6}.{anchor}`, `search`, `grep`, `find_symbol`, `outline`,
`navigate`, `get_symbol`, `get_annotations.document`, `grep.ignore_case`, `diff`,
`find_symbol.freshness_{auto|hash}_{untouched|touched}`,
`find_symbol.freshness_auto_new_file`, `dead_candidates`, `mcp_status.{default|full}`.
The five reads from `outline` to `grep.ignore_case` use the middle anchor. The harness
skips them when no anchor is available. The `get_symbol` read uses `--view=metadata`.
The touched and new-file operations run after `mcp_status`. See "Edits".

`timings_total_ms` and `timings_steps` come from one `--mode=timings --json` call per index run
that wrote a new snapshot. The tool keys timings by snapshot id, so a run that reused the
previous snapshot (a no-op) records both as null.

## Peak working set

The harness records `peak_working_set_mb` in MiB and rounds up to a whole MiB.
One MiB is 1,048,576 bytes. For `mcp_status.*`, each value is the peak of the server
process up to that call. The five values share one server lifetime. Their median
represents the post-start peak.

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

A fixture `diff` must report a positive `change_count`.

Before `find_symbol.freshness_auto_touched` and `find_symbol.freshness_hash_touched`, the harness
selects every 10th fixture document (`.cs` files outside `obj`/`bin`, sorted by relative path,
ordinal) and sets its `LastWriteTimeUtc` to now + 1 minute without changing its content. The two
operations record the count as `touched_files`. The harness restores every original mtime in a
`finally` block, on failure as well. `freshness_auto_touched` must report `stale` with
`changed_document_count` equal to `touched_files`; `freshness_hash_touched` must report `fresh`
with 0 changed documents. A mismatch marks the operation failed and records the observed values
in `failure`.

After the touched operations, the harness adds a temporary C# source file to the first
fixture project and runs `find_symbol.freshness_auto_new_file`. The result must report
`stale` and one changed document. The harness deletes the source file in a `finally` block.

## Work directories

The harness creates `run-<utcstamp>-<pid>` under `--work`. On the next invocation it deletes
sibling `run-*` directories from earlier runs (output dirs it created) before starting, unless
`--keep-work` is given.
The harness keeps its work and result paths outside the Lurp repository.

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
