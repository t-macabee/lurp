# Contributing to Lurp

## Prerequisites

- .NET 10 SDK (the repo's `global.json` pins SDK `10.0.301`).
- Familiarity with C# and Roslyn is helpful but not required.

## Build and test

Lurp is a console application (not a .NET global tool). From the repo root:

```bash
dotnet build Lurp.slnx
dotnet test Lurp.slnx
```

## Repository layout

- `src/` — the solution: `Lurp` Exe (Program.cs, Handlers/, Workspace/, Adapters/), and `Lurp.Storage` Lib (Storage/, Migrations/).
- `tests/` — the `Lurp.Tests` project (xunit).
- `docs/` — architecture and status documents.
- `scripts/` — convergence verification scripts (`r1-*`).

## Conventions

- Read `AGENTS.md` and `docs/README.md` for the project's evidence-before-conclusions
  rules and architectural invariants before changing behavior.
- Handlers consume persisted facts; they must not create a second semantic-analysis engine.
- Snapshots are immutable — do not alter persisted snapshots merely to make a test pass.
- Prefer narrow validation (the directly affected test, then its class, then the project).

## Reporting issues

Strip any sensitive or proprietary code before sharing fixtures or logs.
