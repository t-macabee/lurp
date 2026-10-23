# perf/baselines/

Committed perf baselines for the baseline gate in `real-parity.yml`, one file per runner OS
(`<ImageOS>.json`, for example `win25-vs2026.json`). Each file is a harness result file: the
`enotev2.json` from a `real-parity.yml` `perf-enotev2-<OS>` artifact, reviewed before it is
committed.

The baseline key is the runner OS only. `ImageVersion` changes about once a week on hosted
runners and is ignored (a different value only warns), so the file does not need a weekly
update. A new runner OS has no baseline and fails the gate until one is committed.

A baseline file changes only in its own reviewed commit, on its own: a baseline update must be
reviewable without an unrelated change beside it.

See [scripts/perf/README.md](../../scripts/perf/README.md#baseline-gate) for the gate behaviour
and the limits.
