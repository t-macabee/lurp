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
`--solution=` and `--output-dir=`.

## MCP server (`--mode=serve`)

Lurp also runs as an MCP server over stdio (13 tools via `tools/list`, verified
2026-08-17 in `.tmp_test/MCP_LIVE_TEST_REPORT_MCP_SURFACE_2026-08-17_P2.md` §Tools present):
`lurp_find_symbol, lurp_diff, lurp_get_symbol, lurp_index, lurp_get_annotations,
lurp_status, lurp_get_source, lurp_context, lurp_refresh, lurp_navigate,
lurp_search, lurp_timings, lurp_impact` — `lurp_timings` is the 13th tool (added
2026-08-17). There is no `lurp_annotate` tool by design: the MCP session opens
SQLite with `PRAGMA query_only=ON` (`src/Storage/SqliteIndexStore.cs:74`,
`src/Mcp/McpSessionContext.cs:Create` → `EnableQueryOnly()`), so the transport
is read-only; annotation writes remain CLI-only (`--mode=annotate`).

Stdio transport is JSON-RPC pure: every non-empty stdout line parses as JSON
and `Microsoft.Hosting.Lifetime` / `ModelContextProtocol` logs are routed to
stderr (`src/Mcp/McpServeHandler.cs:Configure<ConsoleLoggerOptions>(o =>
o.LogToStandardErrorThreshold = LogLevel.Trace)` plus `IOutputSink` plumbing);
regression enforced by `tests/Mcp/McpStdioPurityTests.cs`. Report §J confirms
158 stdout lines (eNoteV2) and 129 lines (eCommerce) with zero leaks.

`--mode=serve` requires an existing indexed snapshot before it can start — it
will not build one on first launch. `McpSessionContext.Create` (`src/Mcp/McpSessionContext.cs:47`)
throws `ERROR: No snapshots found in the database` if `GetLatestSnapshotId()` is
null. Index first via CLI (`--mode=index`) or MCP `lurp_index`, then serve:

```bash
# 1. Index once (creates index.db with a snapshot)
dotnet run --project src -- --mode=index --solution=path/to/Your.sln --output-dir=./out

# 2. Serve from that output dir (pins that snapshot)
dotnet run --project src -- --mode=serve --solution=path/to/Your.sln --output-dir=./out
```

Example MCP client config (stdio):

```json
{
  "mcpServers": {
    "lurp": {
      "command": "dotnet",
      "args": ["path/to/Lurp.dll", "--mode=serve", "--solution=path/to/Your.sln", "--output-dir=./out"]
    }
  }
}
```

Pin semantics: every read tool serves the snapshot pinned at startup, not
`latest`. After any `lurp_index` completion, reads keep returning the old
`snapshot_id` until `lurp_refresh {}` (and if `changed:true`, `lurp_refresh
{"ack": new_id}`) — verified in report §Pin semantics / §A–B.

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

Index a solution, then build a context capsule or trace impact from a source
location. See [src/README.md](src/README.md) for the full command reference.

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
- Typed relationships with provenance, including possible dispatch targets and framework-mediated relationships.
- Snapshot freshness and completeness information.
- Explicit uncertainty when the compiler or workspace cannot establish a fact.
- Token-bounded context capsules with inclusion reasons and evidence levels.

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
`global_implementation_relation`, `possible`, `convention`, `name_candidate`,
or `runtime_unknown`.

## Documentation and status

The [docs](docs/) folder contains the architecture guide and implementation status.

## License

MIT. See [src/LICENSE](src/LICENSE).
