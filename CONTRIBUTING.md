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

- `src/`: the tool. `Lurp` Exe (Program.cs, Handlers/, Workspace/, Adapters/), and `Lurp.Storage` Lib (Storage/, Migrations/).
- `tests/`: the `Lurp.Tests` project (xunit).
- `docs/`: [ARCHITECTURE.md](docs/ARCHITECTURE.md), [CLI_REFERENCE.md](docs/CLI_REFERENCE.md), the release notes ([RELEASE_NOTES_2.0.0.md](docs/RELEASE_NOTES_2.0.0.md)) and the point-in-time demo page `MODEL_VIEW.html` (generated, see `scripts/model-view/`).
- `notes/`: internal evidence log ([TRUST_KERNEL.md](notes/TRUST_KERNEL.md)).
- `scripts/`: convergence verification scripts (`r1-*`) and the `MODEL_VIEW.html` generator (`model-view/`).

## Conventions

- Handlers consume persisted facts; they must not create a second semantic-analysis engine.
- Snapshots are immutable. Do not alter persisted snapshots merely to make a test pass.
- Prefer narrow validation (the directly affected test, then its class, then the project).

## CLI/MCP contract versioning

Lurp's DB schema is versioned via `MigrationRunner.GetCurrentSchemaVersion` (30 migrations,
enforced by `SchemaMigrationRoundTripTests`). The equivalent for the CLI/MCP surface that
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

## Reporting issues

Strip any sensitive or proprietary code before sharing fixtures or logs.
