# scripts/

- `r1-compare/`: incremental-vs-full parity comparison harness.
- `r1-verify-enote.sh`: five-cycle incremental convergence gate for eNoteV2. Runs the local Debug build by default; set `LURP_CMD` to run the packed tool instead (`LURP_CMD='/c/tools/lurp/lurp.exe'`).
- `smoke/packed-tool-smoke.ps1`: smoke test of an installed packed tool (`-Lurp <tool>/lurp.exe`). Runs every CLI mode and every MCP tool against `tests/Fixtures/Smoke`, then kills stdin mid-`lurp_index` and checks the next index recovers. Needs Python 3 for `smoke/snapshot-state.py`; `-SkipKillTest` skips the slow repo-index half.
- `smoke/runtime-only-smoke.ps1`: same packed tool, run with `DOTNET_ROOT`/`PATH` pointed at a runtime-only .NET install. Read modes must work and `--mode=index` must exit 2 with the diagnosed "requires a .NET SDK" message.
- `smoke/snapshot-state.py`: prints `{failed, incomplete_unpruned}` snapshot counts for one `index.db`; used by the smoke script.
- `model-view/generate.py`: regenerates `docs/MODEL_VIEW.html` from one index. Python 3 standard library only. With `--solution`, it runs a full index into an empty `--work-dir` and renders the page; with `--db` and `--index-log`, it renders from an index you already have. The page styles come from the first `<style>` block of the current page.

  ```bash
  dotnet build src/Lurp.csproj -c Release
  python scripts/model-view/generate.py --lurp src/bin/Release/net10.0/Lurp.exe --solution ../eNoteV2/eNote/eNote.sln --work-dir <empty folder>
  ```
- `perf/gen/`: deterministic generator of synthetic C# solutions for performance measurement. `--size=<1|4|16>` (5, 20, or 80 projects; 50 `.cs` files each) and `--out=<dir>` are required; `--seed=<int>` defaults to 1. `--out` must be outside the Lurp repository; the generator refuses an in-repo path and never deletes. The same size and seed always write byte-identical files.

  ```bash
  dotnet run --project scripts/perf/gen -c Release -- --size=<1|4|16> --out=<outside-repo-dir>
  ```
- `perf/harness/`: performance measurement harness (audit B3). Runs the tool under test as a child process and records wall time, peak working set and the index-time `peak_working_set_mb` metric for full/incremental index, edit, impact, context, search/grep/find-symbol, diff, freshness, dead-candidate and MCP `lurp_status` runs. It measures only: it does not gate and does not compare against baselines. A fixture run may edit a generated tree (`.lurp-perf-gen` marker only); a `--solution` run never edits and says so. Tool resolution: `--lurp-cmd`, else `LURP_CMD`, else the local Release build. Command lines, JSON schema and peak-measurement limits: `scripts/perf/README.md`.

  ```powershell
  dotnet run --project scripts/perf/harness -c Release -- --fixture=<generated dir> --work=<outside-repo-dir> --out=<result.json> --runs=5 --warmup=1
  ```
