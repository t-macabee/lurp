# Lurp: a Roslyn semantic map for .NET agents

[![CI](https://github.com/t-macabee/lurp/actions/workflows/ci.yml/badge.svg)](https://github.com/t-macabee/lurp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/lurp)](https://www.nuget.org/packages/lurp)

Lurp indexes a .NET solution into SQLite with Roslyn, so an agent gets a small,
sufficient code neighborhood instead of re-grepping and re-parsing the whole
thing for every question. It loads the solution through the compiler and stores
symbols, typed relationships, source spans, and provenance in one database, then
serves retrieval, semantic diffs, impact paths, and token-bounded context
capsules. Index once, query as many times as you want, and get back exact
source with evidence levels each time.

## [What the model sees](https://t-macabee.github.io/lurp/MODEL_VIEW.html)

An interactive map of how Lurp structures an eCommerce codebase for agent
consumption: symbols, relationships, evidence levels, and capsule boundaries
as the model receives them.

## Why this exists

An agent working a C# codebase today loops search → read → guess, reopening and
re-parsing the whole solution for every question. Context windows fill up with
irrelevant source, and the agent ends up rediscovering the same call graph on
every turn.

Lurp flips that cost around. A one-time index builds a snapshot-bound semantic
graph. After that, every query just reads persisted facts: no Roslyn reload, no
re-grep. Capsules return the smallest neighborhood that's actually sufficient
for the task, each fact states its provenance (`compiler_proved` →
`runtime_unknown`), and any omitted tier is named so you can fetch it
separately.

## The mental model

| Command | What it does |
|---|---|
| `context` | Assemble a bounded capsule of relevant code from a symbol or source location. |
| `impact` | Follow typed relationships outward and explain each path. |
| `diff` | Show semantic changes between two snapshots. |
| `search` | Full-text search over source and symbols. |
| `grep` | Literal/exact-text search over source content with line numbers. |
| `find-symbol` | Resolve a symbol by its fully-qualified name. |
| `navigate` | Resolve an indexed declaration by file and line. |
| `get-symbol` | Look up symbol metadata (signature, provenance, declaration spans). |
| `get-source` | Retrieve source text for a document by relative path. |
| `index` | Build or update the snapshot-bound map in `index.db`. |
| `pin-snapshot` | Pin which snapshot reads default to when `--snapshot=` is omitted, without deleting or rewriting any snapshot. |
| `annotate` / `get-annotations` / `retract-annotation` | Attach, retrieve, and retract (by `annotation_id`) user-authored annotations on symbols. |
| `status` | Report whether the indexed snapshot still matches the workspace. |
| `timings` | Per-step performance data for a snapshot. |
| `outline` | List declarations in a document with line spans. |
| `diagnostics` | List compiler diagnostics captured at index time. |
| `dead-candidates` | List dead-code candidates: no incoming LIVE edge, after a suppression ladder that keeps public surface, EF/serialization conventions, and generated/test code out of the default view. |

## Worked example

Index a solution, then build a context capsule from a source location:

```bash
lurp --mode=index --solution=MySolution.slnx --output-dir=./out
lurp --mode=context --file=src/Services/OrderService.cs --line=42 --output-dir=./out --output=summary
```

`--output=summary` prints the handoff facts instead of the full JSON capsule:

```
anchor:       OrderService.CreateAsync (src/Services/OrderService.cs:42)
snapshot:     f3bff523b103462be239655c9b753be3
estimatedTokens:          283   (content bounded by --content-budget)
estimatedArtifactTokens:  571   (whole emitted file; size your window from this)
omittedTiers:
  directCallers     budget_exhausted
  relevantTests     budget_exhausted
```

When a tier is `budget_exhausted`, fetch it on its own with no budget applied:

```bash
lurp --mode=context --file=src/Services/OrderService.cs --line=42 --output-dir=./out --tier=directCallers
```

## Install

```bash
dotnet tool install --global lurp --version 1.1.0
lurp --mode=index --solution=path/to/Your.slnx --output-dir=./out
```

<details>
<summary>Build from source & environment variables</summary>

```bash
dotnet build Lurp.slnx
dotnet run --project src -- --mode=index --solution=path/to/Your.slnx --output-dir=./out
```

Environment variables `LURP_SOLUTION_PATH` and `LURP_OUTPUT_DIR` are equivalent to
`--solution=` and `--output-dir=`. Requires .NET 10 SDK 10.0.301 (pinned via
`src/global.json` `rollForward=latestMajor`; Roslyn 5.6 requires `net10.0`).

**Installing a local build as the global tool:** if you pack a local build and its
version number matches the version already on nuget.org (both `1.1.0`, for
example), `dotnet tool install --global --add-source <path> lurp` can silently
install the nuget.org copy instead of yours. `--add-source` only appends a source;
it does not remove nuget.org, and when two sources offer the same version, NuGet is
free to pick either one. Use `--source <path>` instead — it replaces every other
source, so only your local build is visible. Confirm afterward with
`lurp --version`, which prints the source commit hash. This will bite again on
every future local build that reuses the `1.1.0` version number; bump the local
package version before packing, or always install with `--source`, never
`--add-source`.

</details>

## Framework adapters

Lurp models framework-mediated relationships through six adapters that emit
ordinary typed facts into the shared model. Each fact retains its evidence level;
see [ARCHITECTURE.md](docs/ARCHITECTURE.md) for the ladder.

| Adapter | Facts emitted |
|---|---|
| ASP.NET Core | `RoutesTo` (route → controller action) |
| Dependency Injection | `Registers` (service → implementation) |
| MediatR | request/notification → handler chains |
| EF Core | entity, `DbSet`, and configuration relationships |
| Serialization | DTO/property contract participation |
| Test | `TestedBy` (production symbol → covering test) |

## Limitations

- **Single active TFM/configuration**: one snapshot per index run.
- **Source generators not executed**: `GeneratedTreesIncluded=false`; generated files under `obj/` are path-filtered out.
- **Reflection string-literal candidates are `name_candidate`**, not `compiler_proved`.
- **3-snapshot retention**: older snapshots and their document versions are pruned automatically, except a snapshot pinned via `pin-snapshot`, which pruning always skips.
- **Workspace must be restorable**: `MSBuildWorkspace` requires a successful restore; no index without it.

## Performance

| Measurement | Value |
|---|---|
| eNoteV2 (402 docs) full index | ~48 s |
| eNoteV2 incremental (no changes) | ~11 s |
| Capsule token estimates | `estimatedTokens` (content) vs `estimatedArtifactTokens` (delivery) |
| Incremental↔full convergence | 5 cycles; 0 changed docs after cycle 1 |

## Status & roadmap

Shipped 1.1.0 as a global tool (`dotnet tool install lurp`). Schema v29, extractor
1.6.0. `windows-latest` CI plus a self-hosted real-parity gate on FIT-RS2-2026 +
eNoteV2 (opt-in via `real-parity` PR label). Roadmap: multi-TFM and richer DI
parameter-type matching are postponed by design (see
[DeclaredBoundaries](notes/TRUST_KERNEL.md#declared-boundaries-registry-capsule-audit-task-7)).

## Documentation & license

- [ARCHITECTURE.md](docs/ARCHITECTURE.md): product model, storage, pipeline, rules.
- [CLI_REFERENCE.md](docs/CLI_REFERENCE.md): commands, options, output shapes, snapshot lifecycle, MCP.
- MIT license, see [LICENSE](LICENSE).

Also MCP: `--mode=serve` exposes 18 tools (`lurp_context`, `lurp_get_source`,
`lurp_outline`, `lurp_navigate`, `lurp_find_symbol`, `lurp_search`, `lurp_grep`,
`lurp_impact`, `lurp_diff`, `lurp_get_symbol`, `lurp_get_annotations`, `lurp_retract_annotation`,
`lurp_diagnostics`, `lurp_status`, `lurp_timings`, `lurp_refresh`, `lurp_index`,
`lurp_dead_candidates`) over stdio; all are read-only except `lurp_index` and `lurp_retract_annotation`
(background re-index). Index first, then serve. See
[CLI_REFERENCE.md#mcp](docs/CLI_REFERENCE.md).
