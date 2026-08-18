# Contributing to Lurp

## Prerequisites

- .NET 10 SDK 10.0.301 (preview) — pinned at `src/global.json`
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

- `src/` — the tool: `Lurp` Exe (Program.cs, Handlers/, Workspace/, Adapters/), and `Lurp.Storage` Lib (Storage/, Migrations/).
- `tests/` — the `Lurp.Tests` project (xunit).
- `docs/` — [ARCHITECTURE.md](docs/ARCHITECTURE.md) and [CLI_REFERENCE.md](docs/CLI_REFERENCE.md).
- `notes/` — internal evidence log ([TRUST_KERNEL.md](notes/TRUST_KERNEL.md)).
- `scripts/` — convergence verification scripts (`r1-*`).

## Conventions

- Read [AGENTS.md](AGENTS.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
  for the project's evidence-before-conclusions rules and architectural
  invariants before changing behavior.
- Handlers consume persisted facts; they must not create a second semantic-analysis engine.
- Snapshots are immutable — do not alter persisted snapshots merely to make a test pass.
- Prefer narrow validation (the directly affected test, then its class, then the project).

## Reporting issues

Strip any sensitive or proprietary code before sharing fixtures or logs.
