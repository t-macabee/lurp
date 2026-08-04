# Lurp

[![CI](https://github.com/t-macabee/lurp/actions/workflows/ci.yml/badge.svg)](https://github.com/t-macabee/lurp/actions/workflows/ci.yml)

Lurp is a Roslyn-backed semantic map for C# solutions that helps agents jump
to relevant code, dependencies, callers, and tests.

## Why Lurp

Text search finds words. It does not tell you which invocation resolves to
which member, which type implements an interface, or which tests are connected
to a change. Lurp loads the solution through the compiler and stores the
resulting symbols, relationships, source spans, provenance, and completeness
data in SQLite.

The result is a durable map that an agent can query instead of reopening and
re-parsing the whole codebase for every question.

## The mental model

| Command | What it does |
|---|---|
| `index` | Builds or updates the snapshot-bound map in `index.db`. |
| `context` | Navigates from a symbol or source location to a bounded capsule of relevant code. |
| `impact` | Follows typed relationships outward and explains each path. |
| `status` and freshness options | Report whether the indexed snapshot still matches the workspace. |

## A real example journey

Start with the partial `Widget` type in the committed sample fixture. Its
declarations live in both `Widget.cs` and `Widget.Extra.cs`. A context request
can resolve the source location, return the exact declaration and member spans,
and include the relevant `Declares` and `Reads` relationships. The included
graph is generated from that fixture and keeps unresolved external bindings as
explicit uncertainty instead of presenting them as missing relationships.

![Fixture-derived context graph for the partial Widget type, its declarations and member reads](docs/assets/context-graph-example.svg)

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
# snapshot, the two token estimates (content = what --budget bounded,
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

## Documentation and status

The [CLI reference](src/README.md) documents every mode and option. The
[reference index](docs/reference/README.md) groups the architecture guide,
implementation status, operational notes, and historical investigations.

The [Trust Kernel](docs/reference/TRUST_KERNEL.md) is the current,
evidence-backed implementation status. The
[architecture guide](docs/reference/LURP_ARCHITECTURE.md) describes the
design model and longer-term direction.

## License

MIT. See [src/LICENSE](src/LICENSE).
