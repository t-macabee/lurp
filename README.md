# Lurp

[![CI](https://github.com/t-macabee/lurp/actions/workflows/ci.yml/badge.svg)](https://github.com/t-macabee/lurp/actions/workflows/ci.yml)

Lurp is a Roslyn-native semantic context engine for C# solutions. It loads a
solution through the compiler and stores symbols, relationships, source spans,
provenance, and completeness data in SQLite — a durable map that an agent can
query instead of reopening and re-parsing the whole codebase for every question.

## Install / Build

Prerequisites:

- .NET 10 SDK (the repo's `global.json` pins SDK `10.0.301`).
- A C# solution (`.sln` or `.slnx`) to index.

Lurp is a console application, not a .NET global tool. Build and run it from the repo:

```bash
dotnet build Lurp.slnx
dotnet run --project src -- --mode=index --solution=path/to/Your.sln --output-dir=./out
```

Or run the built binary directly:

```bash
./src/bin/Debug/net10.0/lurp --mode=index --solution=path/to/Your.sln --output-dir=./out
```

Environment variables `LURP_SOLUTION_PATH` and `LURP_OUTPUT_DIR` are equivalent to
`--solution=` and `--output-dir=`. The older `INDEXER_SOLUTION_PATH` / `INDEXER_OUTPUT_DIR`
names are accepted for backwards compatibility.

## The mental model

| Command | What it does |
|---|---|
| `index` | Builds or updates the snapshot-bound map in `index.db`. |
| `search` | Full-text search over source and symbols. |
| `find-symbol` | Resolve a symbol by its fully-qualified name. |
| `get-symbol` | Look up symbol metadata (signature, provenance, declaration spans). |
| `get-source` | Retrieve source text for a document by relative path. |
| `navigate` | Resolve an indexed declaration by file and line. |
| `context` | Assemble a bounded capsule of relevant code from a symbol or source location. |
| `impact` | Follow typed relationships outward and explain each path. |
| `diff` | Show semantic changes between two snapshots. |
| `annotate` / `get-annotations` | Attach and retrieve user-authored annotations on symbols. |
| `status` | Report whether the indexed snapshot still matches the workspace. |
| `timings` | Per-step performance data for a snapshot. |

## A real example journey

Start with the partial `Widget` type in the committed sample fixture. Its
declarations live in both `Widget.cs` and `Widget.Extra.cs`. A context request
can resolve the source location, return the exact declaration and member spans,
and include the relevant `Declares` and `Reads` relationships. The included
graph is generated from that fixture and keeps unresolved external bindings as
explicit uncertainty instead of presenting them as missing relationships.

![Fixture-derived context graph for the partial Widget type, its declarations and member reads](Screenshot%202026-08-06%20193025.png)

This is the useful path: start at a location, retrieve the source that belongs
to it, then follow only the relationships needed for the task.

## Quick start

Index a solution:

```bash
lurp --mode=index --solution=MySolution.slnx --output-dir=./out
```

Then build a context capsule from a source location:

```bash
lurp --mode=context --file=src/Services/OrderService.cs --line=42 --output-dir=./out
```

The capsule is written to `./out` and includes the anchor source, contracts,
callers and callees, relevant tests, likely change sites, evidence levels, and
uncertainties within the requested budget.

Hand a capsule over as a digest, and fetch any budget-exhausted tier unbudgeted:

```bash
# Summary mode prints the handoff facts instead of the full JSON: anchor,
# snapshot, the two token estimates (content = what --content-budget bounded,
# delivery = the whole emitted file, size the context window from it), and
# each omitted tier with its fetch command.
lurp --mode=context --file=src/Services/OrderService.cs --line=42 --output-dir=./out --output=summary

# When the summary reports a tier as budget_exhausted, fetch that tier alone
# with no budget applied; --tier= takes the category named in the summary.
lurp --mode=context --file=src/Services/OrderService.cs --line=42 --output-dir=./out --tier=directCallers
```

## What an agent gets

- Exact source retrieved from the indexed document version.
- Symbols, declarations, callers, callees, contracts, and tests.
- Typed relationships with provenance, including possible dispatch targets.
- Snapshot freshness and completeness information.
- Explicit uncertainty when the compiler or workspace cannot establish a fact.

## Framework adapters

Lurp models framework-mediated relationships through six adapters that emit
ordinary typed facts into the shared model:

| Adapter | Facts emitted |
|---|---|
| ASP.NET Core | `RoutesTo` (route → controller action) |
| Dependency Injection | `Registers` (service → implementation) |
| MediatR | request/notification → handler chains |
| EF Core | entity, `DbSet`, and configuration relationships |
| Serialization | DTO/property contract participation |
| Test | `TestedBy` (production symbol → covering test) |

Each fact retains its evidence level: `compiler_proved`, `framework_derived`,
or `possible`.

## Documentation and status

The [docs](docs/) folder contains the architecture guide and implementation status.

## License

MIT. See [src/LICENSE](src/LICENSE).
