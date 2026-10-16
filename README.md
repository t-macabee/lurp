# Lurp: a Roslyn semantic map for .NET agents

[![CI](https://github.com/t-macabee/lurp/actions/workflows/ci.yml/badge.svg)](https://github.com/t-macabee/lurp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/lurp)](https://www.nuget.org/packages/lurp)
![Windows only](https://img.shields.io/badge/platform-Windows%20only-4c6b8a)

> **Windows only, today.** Lurp is developed and CI-tested on Windows
> (`windows-latest`); cross-platform support is on the roadmap but not there yet.

Lurp indexes a .NET solution into SQLite with Roslyn, so an agent gets a small,
sufficient code neighborhood instead of re-grepping and re-parsing the whole
thing for every question. It loads the solution through the compiler and stores
symbols, typed relationships, source spans, and provenance in one database, then
serves retrieval, semantic diffs, impact reachability, and token-bounded context
capsules. Index once, query as many times as you want, and get back exact
source with evidence levels each time.

## [What the model sees](https://t-macabee.github.io/lurp/MODEL_VIEW.html)

A point-in-time map of one Lurp 2.0.0 index of
[eNoteV2](https://github.com/t-macabee/eNoteV2) (`eNote/eNote.sln`, 7 projects):
the evidence level of every edge, the DI, routing and EF Core surfaces, and the
full edge ledgers behind them. [`scripts/model-view/`](scripts/model-view/)
regenerates it.

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
| `impact` | Follow typed relationships outward and list the affected symbols with a witness path. |
| `diff` | Show semantic changes between two snapshots. |
| `search` | Full-text search over source and symbols. |
| `grep` | Literal/exact-text search over source content with line numbers. |
| `find-symbol` | Resolve a symbol by its fully-qualified name. |
| `navigate` | Resolve an indexed declaration by file and line. |
| `get-symbol` | Look up symbol metadata (signature, provenance, declaration spans). |
| `get-source` | Retrieve source text for a document by relative path. |
| `index` | Build or update the snapshot-bound map in `index.db`. |
| `pin-snapshot` | Pin which snapshot reads default to when `--snapshot=` is omitted, without deleting or rewriting any snapshot. |
| `annotate` / `get-annotations` / `retract-annotation` | Attach and retrieve user-authored annotations on symbols; retract (by `annotation_id`) any annotation regardless of provenance. |
| `status` | Report whether the indexed snapshot still matches the workspace. |
| `timings` | Per-step performance data for a snapshot. |
| `outline` | List declarations in a document with line spans. |
| `diagnostics` | List compiler diagnostics captured at index time. |
| `dead-candidates` | List dead-code candidates: no incoming LIVE edge, after a suppression ladder. Public surface, EF/serialization conventions, process entry points and generated/test code are never reported as `proved_dead`. |

## Worked example

Index a solution, then build a context capsule from a source location:

```bash
lurp --mode=index --solution=MySolution.slnx --output-dir=./out
lurp --mode=context --file=eNote.Application/Features/Academic/Courses/Services/CourseService.cs --line=11 --output-dir=./out --output=summary
```

`--output=summary` prints the handoff facts instead of the full JSON capsule:

```
freshness: state=fresh method=stat changedDocuments=0 snapshot=3a69acadd9659d0f27d349c73d63fb9c scope=documents_only (not a full compilation check)
capsule global::eNote.Application.Features.Academic.Courses.Services.CourseService.GetByIdForInstructorAsync (Method)
  snapshot: 3a69acadd9659d0f27d349c73d63fb9c  intent: inspect  maxHops: 3
  content tokens:  6908/8000  (estimated_tokens: the budget basis)
  delivery tokens: ~15026  (estimated_artifact_tokens: whole emitted file; size the context window from this)
  truncated: True
  contracts                    0
  direct_callees               6
  direct_callers               2
  registered_implementations   1
  relevant_tests               5
  second_degree_context        2
  surrounding_source           0
  incoming_witness_paths: 3  outgoing_witness_paths: 3  uncertainties: 2
  omitted: contracts (empty)
  omitted: incoming_paths (summarized)
  omitted: outgoing_paths (summarized)
  omitted: surrounding_source (budget_exhausted) : fetch with --tier=surrounding_source
  written: ./out/capsule-<id>.json
```

When a tier is `budget_exhausted`, fetch it on its own with no budget applied:

```bash
lurp --mode=context --file=src/Services/OrderService.cs --line=42 --output-dir=./out --tier=direct_callers
```

## Install

Supported platform: Windows. Lurp 3.x is not tested on Linux or macOS.

Requires a .NET 10 runtime to run any mode; the package declares
`RollForward=Major`, so a later major runtime (11, 12, …) also works, while an
older runtime does not. Indexing and solution-backed status
(`--mode=index`, `status --solution=`, MCP `lurp_index`, and MCP `lurp_status` with
`full:true`) also require a .NET SDK that can build the target solution, and the
solution must be restored.

```bash
dotnet tool install --global lurp --version 3.0.2
lurp --mode=index --solution=path/to/Your.slnx
```

Without `--output-dir`, the database and the capsule files go to
`%LOCALAPPDATA%\lurp\<sha256-12 of the solution path>\`. Pass `--output-dir=./out`
to put them elsewhere. The MSBuild design-time intermediates always go to that
`%LOCALAPPDATA%\` folder.

<details>
<summary>Build from source & environment variables</summary>

```bash
dotnet build Lurp.slnx
dotnet run --project src -- --mode=index --solution=path/to/Your.slnx --output-dir=./out
```

Environment variables `LURP_SOLUTION_PATH` and `LURP_OUTPUT_DIR` are equivalent to
`--solution=` and `--output-dir=`. Requires .NET 10 SDK (pinned via the root
`global.json` `rollForward=latestMajor`; Roslyn 5.6 requires `net10.0`).

**Installing a local build as the global tool:** if you pack a local build and its
version number matches the version already on nuget.org, `dotnet tool install
--global --add-source <path> lurp` can silently
install the nuget.org copy instead of yours. `--add-source` only appends a source;
it does not remove nuget.org, and when two sources offer the same version, NuGet is
free to pick either one. Use `--source <path>` instead — it replaces every other
source, so only your local build is visible. Confirm afterward with
`lurp --version`, which prints the schema/extractor/CLI-MCP-contract versions the
installed binary was built with — compare that against the docs you're reading; a
mismatch means `dotnet tool install` pulled an older published build than the repo
this doc came from. This will bite again on every future local build that reuses
an already-published version number; bump the local package version before packing, or
always install with `--source`, never `--add-source`.

</details>

## Common recipes

Task-first lookup for things you already know Lurp can do but not which mode does it:

| Task | Command |
|---|---|
| Find unused `using` directives | `lurp --mode=diagnostics --severity=hidden --id=CS8019 --output-dir=./out` |
| Confirm the installed build matches these docs | `lurp --version` |
| Find dead-code candidates (public API and tests are never `proved_dead`) | `lurp --mode=dead-candidates --output-dir=./out` |
| Search source text literally (not symbol search) | `lurp --mode=grep --query=<text> --output-dir=./out` |
| See what changed between two indexing runs | `lurp --mode=diff --from-snapshot=<id> --to-snapshot=<id> --output-dir=./out` |
| Pull just the code relevant to a change | `lurp --mode=context --file=<path> --line=<n> --output-dir=./out` |
| Find every caller of a symbol | `lurp --mode=impact --symbol=<id> --direction=upstream --output-dir=./out` |

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

- **Windows only**: 3.x supports Windows; Linux and macOS are not tested.
- **Multi-target projects are indexed as a union**: one snapshot per index run holds every target framework the solution declares. The declared TFM list is recorded per project and the per-symbol TFM set is recorded per snapshot, but CLI/MCP reads do not surface the per-symbol set, so a member present in only one target framework cannot be told apart by a consumer from one present in all of them.
- **Source generators not executed**: `GeneratedTreesIncluded=false`; generated files under `obj/` are path-filtered out.
- **Reflection string-literal candidates are `name_candidate`**, not `compiler_proved`.
- **3-snapshot retention**: older snapshots and their document versions are pruned automatically, except a snapshot pinned via `pin-snapshot`, which pruning always skips.
- **Restore first**: an unrestored project still indexes, with a warning, but `MSBuildWorkspace` cannot resolve its references, so edges are missing and diagnostics are wrong. Run `dotnet restore` before `--mode=index`.
- **NuGet audit warnings look like load failures**: `MSBuildWorkspace` reports a NuGet vulnerability warning as a failure-level workspace diagnostic, so the index run prints `WARNING: workspace load reported N failure-level diagnostic(s)` with the package message. Such a warning does not stop the index.

## Performance

| Measurement | Value |
|---|---|
| eNoteV2 (402 docs) full index | ~18 s |
| eNoteV2 incremental (no changes) | ~0.3 s |
| Capsule token estimates | `estimated_tokens` (content) vs `estimated_artifact_tokens` (delivery) |
| Incremental↔full convergence | 5 cycles; 0 changed docs after cycle 1 |

Measured 2026-10-14 on commit `cfa43f3` (Release build; the 3.0.0 changes touch no extraction or query code) and eNoteV2 at `4ba9901`: wall time, median of three warm runs; 12th Gen Intel(R) Core(TM) i5-12500H, 16 GB RAM, NVMe SSD, Microsoft Windows 11 Education, AC power, Best performance mode. A no-change run hashes every document; in a synthetic 4,000-document solution that takes about 0.6 s. The first run after the machine has been idle for hours can be much slower, because the source files are read cold.

## Status & roadmap

Shipped as a global tool (`dotnet tool install lurp`); the published version is
3.0.2. This source tree is <!-- generated:versions -->tool 3.0.2, schema v37, extractor 1.10.0, CLI/MCP contract v5, output schema v6<!-- /generated:versions -->. 3.0.0 moves the default database to
`%LOCALAPPDATA%\lurp\`, registers the MCP write tools only with
`--enable-write-tools`, and needs one `--mode=index` run on an index built by
2.0.0: see [RELEASE_NOTES_3.0.0.md](docs/RELEASE_NOTES_3.0.0.md). 3.0.1 fixes an index run that failed after a re-pin: see [RELEASE_NOTES_3.0.1.md](docs/RELEASE_NOTES_3.0.1.md). 3.0.2 stores the edges of a partial type at its hand-written part and needs one `--mode=index` run: see [RELEASE_NOTES_3.0.2.md](docs/RELEASE_NOTES_3.0.2.md). `windows-latest`
CI plus a manual real-parity gate on eNoteV2 (GitHub-hosted runner,
`workflow_dispatch`); a release publishes only the CI-built package of a commit
that passed that gate. Roadmap: surfacing the
per-symbol TFM set in reads and richer DI parameter-type matching are postponed
by design (see
[DeclaredBoundaries](notes/TRUST_KERNEL.md#declared-boundaries-registry-capsule-audit-task-7)).

## Documentation & license

- [ARCHITECTURE.md](docs/ARCHITECTURE.md): product model, storage, pipeline, rules.
- [CLI_REFERENCE.md](docs/CLI_REFERENCE.md): commands, options, output shapes, snapshot lifecycle, MCP.
- [RELEASE_NOTES_3.0.2.md](docs/RELEASE_NOTES_3.0.2.md): the 3.0.2 changes (partial-type edge locations, errors that 3.0.1 hid) and the one index run it needs.
- [RELEASE_NOTES_3.0.1.md](docs/RELEASE_NOTES_3.0.1.md): the 3.0.1 fix (an index run that failed after a re-pin).
- [RELEASE_NOTES_3.0.0.md](docs/RELEASE_NOTES_3.0.0.md): changes from 2.0.0 and how to upgrade.
- [RELEASE_NOTES_2.0.0.md](docs/RELEASE_NOTES_2.0.0.md): breaking changes from 1.x and how to migrate.
- [SECURITY.md](SECURITY.md): how to report a vulnerability, trust boundaries, MCP tool access and limits, what Lurp writes.
- [VERSIONING.md](VERSIONING.md): what counts as a breaking CLI/MCP change.
- MIT license, see [LICENSE](LICENSE).

Also MCP: `--mode=serve` exposes <!-- generated:mcp-tools:read -->16 read tools (`lurp_context`, `lurp_get_source`, `lurp_outline`, `lurp_navigate`, `lurp_find_symbol`, `lurp_search`, `lurp_impact`, `lurp_diff`, `lurp_get_symbol`, `lurp_get_annotations`, `lurp_diagnostics`, `lurp_grep`, `lurp_status`, `lurp_timings`, `lurp_refresh`, `lurp_dead_candidates`)<!-- /generated:mcp-tools:read --> over stdio; `--enable-write-tools` additionally registers <!-- generated:mcp-tools:write -->`lurp_index` and `lurp_retract_annotation`<!-- /generated:mcp-tools:write --> (background re-index and annotation retraction). Index first via CLI, then serve. See [CLI_REFERENCE.md#mcp-server---modeserve](docs/CLI_REFERENCE.md#mcp-server---modeserve).
