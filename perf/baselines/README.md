# perf/baselines/

Committed perf baselines for the baseline gate in `real-parity.yml`, one file per runner OS
(`<ImageOS>.json`). Each file is a harness result file: the `enotev2.json` from a
`real-parity.yml` `perf-enotev2-<OS>` artifact, reviewed before it is committed.

`real-parity.yml` pins the runner labels:

| Matrix label | `ImageOS` | Baseline file |
|---|---|---|
| `windows-2025-vs2026` | `win25-vs2026` | `win25-vs2026.json` |
| `ubuntu-26.04` | `ubuntu26` | `ubuntu26.json` |

The baseline key is the runner OS only. `ImageVersion` changes about once a week on hosted
runners and is ignored (a different value only warns), so the file does not need a weekly
update. The workflow checks the `ImageOS` value against the pinned label. A new runner OS has
no baseline and fails the gate until one is committed.

A refreshed baseline records `peak_working_set_bytes` and `schema_version` 2. The gate uses
those exact bytes for the 1.25 memory comparison. `peak_working_set_mb` is a derived display
value only. A file with `schema_version` 1 stores whole MiB only. The gate cannot prove a
near-limit pass from that file, so it keeps the gate closed and asks for a refresh. Do not
derive an exact byte value from a schema 1 file.

The gate warns when the tools differ by a major version or by more than one minor
version. Replace the baseline in its own reviewed commit when this warning appears.

A baseline update gets its own reviewed commit. Collect both artifacts with a successful
`measure_only` run of `real-parity.yml`. This run keeps R1, integrity, recall, and performance
measurement enabled. It skips the baseline gate and the job that writes the release records.

The old Windows file measured Lurp 2.0.0. The proposed Windows comparison with 3.6.1 used
the wrong baseline version. Local measurements on one machine showed a lower peak with
4.0.0 than with either earlier version for `context.maxhops6.middle`.

Before accepting either artifact, check the runner and solution pin.
Require schema 2 and valid byte samples for every measured operation. To check the hard caps
on all candidate operations, pass the same artifact path to `--baseline` and `--candidate`.
Expect exit 1 with the self-check as the only failure. Review the time warnings. Stop on any
other failure. Run real parity again with the reviewed baselines. The release still requires
a passing gate on that commit.

The tree still holds `ubuntu24.json` from the old Linux runner. The pinned `ubuntu-26.04`
runner reports `ubuntu26`, so the Linux gate fails closed until a reviewed `ubuntu26.json`
exists.

See [scripts/perf/README.md](../../scripts/perf/README.md#baseline-gate) for the gate behaviour
and the limits.
