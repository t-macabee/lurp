# Contributing to Lurp

## Prerequisites

- .NET 10 SDK 10.0.301 (preview), pinned at `src/global.json`
  `rollForward=latestMajor`. Roslyn 5.6 requires `net10.0`.
- Familiarity with C# and Roslyn is helpful but not required.

## Build and test

Lurp is shipped as a .NET global tool (`PackAsTool`); run from source via
`dotnet run --project src`. From the repo root:

```bash
dotnet build Lurp.slnx
dotnet test Lurp.slnx
```

## Repository layout

- `src/`: the tool. `Lurp` Exe (Program.cs, Handlers/, Workspace/, Adapters/), and `Lurp.Storage` Lib (Storage/, Migrations/).
- `tests/`: the `Lurp.Tests` project (xunit).
- `docs/`: [ARCHITECTURE.md](docs/ARCHITECTURE.md) and [CLI_REFERENCE.md](docs/CLI_REFERENCE.md).
- `notes/`: internal evidence log ([TRUST_KERNEL.md](notes/TRUST_KERNEL.md)).
- `scripts/`: convergence verification scripts (`r1-*`).

## Conventions

- Read [AGENTS.md](AGENTS.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
  for the project's evidence-before-conclusions rules and architectural
  invariants before changing behavior.
- Handlers consume persisted facts; they must not create a second semantic-analysis engine.
- Snapshots are immutable. Do not alter persisted snapshots merely to make a test pass.
- Prefer narrow validation (the directly affected test, then its class, then the project).

## CLI/MCP contract versioning

Lurp's DB schema is versioned via `MigrationRunner.GetCurrentSchemaVersion` (28+ migrations,
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
