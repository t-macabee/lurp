# Contributing to Lurp

## Prerequisites

- .NET 10 SDK. `global.json` pins `10.0.301` with `rollForward=latestMajor`, and
  CI installs the SDK from that same file (`setup-dotnet` `global-json-file`),
  so local and CI builds follow one selection rule. Roslyn 5.6 requires `net10.0`.
- Familiarity with C# and Roslyn is helpful but not required.

## Build and test

Lurp is shipped as a .NET global tool (`PackAsTool`); run from source via
`dotnet run --project src`. From the repo root:

```bash
dotnet build Lurp.slnx
dotnet test Lurp.slnx
dotnet test Lurp.slnx --settings tests/full.runsettings
```

The first `dotnet test` skips tests tagged `[Trait("Category", "Slow")]`
(`tests/Lurp.Tests.runsettings`). The second runs the full suite, as CI does.

Tests run on xunit 2.9.3 today. Moving to xunit.v3 is planned, not scheduled: it
swaps the `xunit` and runner packages in `tests/Lurp.Tests.csproj` and needs a v3
replacement for the v2-only skip helper, plus an audit of v2-only assertion and
attribute APIs. NuGet and GitHub Actions updates are watched by
`.github/dependabot.yml`.

## Repository layout

- `src/`: the tool. `Lurp` Exe (Program.cs, Handlers/, Workspace/, Adapters/), and `Lurp.Storage` Lib (Storage/, Migrations/). `src/Mcp/` holds the MCP server and tools (`McpServeHandler.cs`, `Tools/`), `src/Shared/` the path/FQN/symbol-ID helpers (`FqnNormalizer.cs`, `PathNormalizer.cs`, `SymbolIdFactory.cs`), `src/Queries/` the fast-travel queries (`FastTravelQueries.cs`) and `src/Helpers/` the compilation helper (`CompilationHelper.cs`).
- `tests/`: the `Lurp.Tests` project (xunit).
- `docs/`: [ARCHITECTURE.md](docs/ARCHITECTURE.md), [CLI_REFERENCE.md](docs/CLI_REFERENCE.md), the release notes ([RELEASE_NOTES_3.5.0.md](docs/RELEASE_NOTES_3.5.0.md), [RELEASE_NOTES_3.0.2.md](docs/RELEASE_NOTES_3.0.2.md), [RELEASE_NOTES_3.0.1.md](docs/RELEASE_NOTES_3.0.1.md), [RELEASE_NOTES_3.0.0.md](docs/RELEASE_NOTES_3.0.0.md), [RELEASE_NOTES_2.0.0.md](docs/RELEASE_NOTES_2.0.0.md)) and the point-in-time demo page `MODEL_VIEW.html` (generated, see `scripts/model-view/`).
- `notes/`: internal evidence log ([TRUST_KERNEL.md](notes/TRUST_KERNEL.md)).
- `scripts/`: convergence verification scripts (`r1-*`), the `ci/` helper module (`LurpCi.psm1`, `LurpCi.Tests.ps1`), the `parity/` harness (`gate/`, `oracle-b/`, `shared/`), the `perf/` generator and harness (`gen/`, `harness/`), the `smoke/` scripts, and the `MODEL_VIEW.html` generator (`model-view/`).
- Top level: `parity/targets.json`, `perf/baselines/`, and `.github/workflows/` (`ci`, `codeql`, `perf-gate`, `real-parity`, `release`).

## Conventions

- Handlers consume persisted facts; they must not create a second semantic-analysis engine.
- Snapshots are immutable. Do not alter persisted snapshots merely to make a test pass.
- Prefer narrow validation (the directly affected test, then its class, then the project).

## CLI/MCP contract versioning

Lurp's DB schema is versioned via `MigrationRunner.GetCurrentSchemaVersion` (the
migrations are tested by `SchemaMigrationRoundTripTests`). The equivalent for the CLI/MCP surface that
other agents integrate against is `VersionConstants.CliMcpContractVersion`
(`src/Workspace/VersionConstants.cs`), exposed in `status --json` as `contract_version`
and in the MCP `lurp_status` envelope/detail. See [VERSIONING.md](VERSIONING.md) for the
full policy.

Short rule: **breaking** = removing a mode/flag/tool param, renaming/changing the type
of a JSON field, or changing default behavior → bump `CliMcpContractVersion`. **Non-breaking**
= adding a new optional flag, new mode, or new additive JSON field/tool param → keep the
version but update the snapshot. Either way, any change to `Program.ModeRegistry`
(`src/Program.cs`) or `src/Mcp/Tools/*.cs` must update `tests/CliMcpContractSnapshotTests.cs`;
the test snapshot makes surface changes a visible, deliberate diff.

A second snapshot covers the JSON/JSONL *shape* (field paths and types) of every CLI mode
and MCP tool: `tests/OutputContractSnapshotTests.cs` compares against
`tests/Snapshots/output-contract.txt`. On a mismatch the test writes
`tests/Snapshots/output-contract.actual.txt` (gitignored) and fails with the diff. Review
the `.actual` file, copy it over `output-contract.txt`, and re-run. Bump
`CliMcpContractVersion` when the change is breaking. This test is tagged
`[Trait("Category", "Slow")]`, so `dotnet test` skips it; run it with
`dotnet test tests/Lurp.Tests.csproj --filter "FullyQualifiedName~OutputContractSnapshotTests" --settings tests/full.runsettings`.

A change to a flag or an MCP tool also changes the generated docs, so regenerate them with
`$env:LURP_UPDATE_DOCS = '1'; dotnet test tests/Lurp.Tests.csproj --filter "FullyQualifiedName~DocsInSyncTests"`
and review the diff.

## Releasing

1. Push to `main`. `ci.yml` builds, tests and packs the tool, and keeps the nupkg for 90 days.
2. Run the parity gate on that SHA:

```bash
gh workflow run real-parity.yml -f sha=<sha>
```

A pass writes the `real-parity` status and an attestation. The first run on a new runner OS fails until `perf/baselines/<ImageOS>.json` exists (see [the baseline gate](scripts/perf/README.md#baseline-gate)).

3. Optional dry run. It checks every step and stops before the push:

```bash
gh workflow run release.yml -f ref=<sha or tag> -f dry_run=true
```

4. Tag `vX.Y.Z` on that SHA and push the tag. The `nuget` environment asks for approval before the push.
5. Override. Use this only when the parity gate cannot pass. It skips only the parity status and the attestation, and the reason goes into the release body:

```bash
gh workflow run release.yml -f ref=vX.Y.Z -f dry_run=false -f override_reason="<why>"
```

6. The release body is `docs/RELEASE_NOTES_<Version>.md` at the tagged commit.

## Reporting issues

Strip any sensitive or proprietary code before sharing fixtures or logs.
