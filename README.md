# Lurp: a Roslyn semantic map for .NET agents

[![CI](https://github.com/t-macabee/lurp/actions/workflows/ci.yml/badge.svg)](https://github.com/t-macabee/lurp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/lurp)](https://www.nuget.org/packages/lurp)
![Windows and Linux](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-4c6b8a)

> Lurp runs on Windows and Linux. CI builds and tests it on `windows-latest`
> and `ubuntu-latest`. macOS is not tested.

Lurp indexes a .NET solution into SQLite with Roslyn, so an agent gets a small,
sufficient code neighborhood instead of re-grepping and re-parsing the whole
thing for every question. It loads the solution through the compiler and stores
symbols, typed relationships, source spans, and provenance in one database, then
serves retrieval, semantic diffs, impact reachability, and token-bounded context
capsules. Index once, query as many times as you want, and get back exact
source with evidence levels each time.

## [What the model sees](https://t-macabee.github.io/lurp/MODEL_VIEW.html)

A point-in-time map of one Lurp 4.2.0 index (extractor 1.14.0) of
[eNoteV2](https://github.com/t-macabee/eNoteV2) (`eNote/eNote.sln` at `4ba9901`, 7 projects):
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
| `dead-candidates` | List dead-code candidates: no incoming LIVE edge (one-hop liveness), after a suppression ladder. Public surface (effective accessibility for every symbol, intersected with its containing types, plus internals of projects granting `InternalsVisibleTo` outside the snapshot), EF, serialization, framework and record conventions, process entry points and generated/test code are never reported as `proved_dead`. |

## Worked example

Index a solution, then build a context capsule from a source location:

```bash
lurp --mode=index --solution=MySolution.slnx --output-dir=./out
lurp --mode=context --file=eNote.Application/Features/Academic/Courses/Services/CourseService.cs --line=11 --output-dir=./out --output=summary
```

`--output=summary` prints the handoff facts instead of the full JSON capsule. The `freshness:` line goes to stderr; the block below is stdout:

```
capsule global::eNote.Application.Features.Academic.Courses.Services.CourseService.GetByIdForInstructorAsync (Method)
  snapshot: 5ba62832c49646ef4c2dc8aa6b85c7a1  intent: inspect  maxHops: 3
  content tokens:  7729/8000  (estimated_tokens: the budget basis)
  delivery tokens: ~23294  (estimated_artifact_tokens: whole emitted file; size the context window from this)
  truncated: True
  contracts                    1
  direct_callees               25
  direct_callers               1
  registered_implementations   0
  relevant_tests               5
  second_degree_context        2
  surrounding_source           7
  incoming_witness_paths: 3  outgoing_witness_paths: 3  uncertainties: 6
  omitted: registered_implementations (unmodeled_construct)
  omitted: outgoing_paths (summarized)
  omitted: surrounding_source (summarized) : fetch with --tier=surrounding_source
  written: ./out/capsule-<id>.json
```

When a tier is `budget_exhausted`, fetch it on its own with no budget applied:

```bash
lurp --mode=context --file=src/Services/OrderService.cs --line=42 --output-dir=./out --tier=direct_callers
```

## Install

Supported platforms: Windows and Linux. macOS is not tested.

Requires a .NET 10 runtime to run any mode; the package declares
`RollForward=Major`, so a later major runtime (11, 12, …) also works, while an
older runtime does not. Indexing and solution-backed status
(`--mode=index`, `status --solution=`, MCP `lurp_index`, and MCP `lurp_status` with
`full:true`) also require a .NET SDK that can build the target solution, and the
solution should be restored (an unrestored project still indexes, with a warning and missing edges; see "Restore first" below).

```bash
dotnet tool install --global lurp --version 4.2.0
lurp --mode=index --solution=path/to/Your.slnx
```

Without `--output-dir`, the database and the capsule files go to
`%LOCALAPPDATA%\lurp\<sha256-12 of the solution path>\` on Windows and to
`~/.local/share/lurp/<sha256-12 of the solution path>/` on Linux (under
`$XDG_DATA_HOME` when it is set). Pass `--output-dir=./out` to put them
elsewhere. The MSBuild design-time intermediates go to that cache folder,
unless the project moves its own intermediate path or assigns
`CustomBeforeMicrosoftCommonTargets` in its project file; then the design-time
files stay in the project's own `obj/` and the run reports
`intermediate_redirect_not_applied`.

<details>
<summary>Build from source & environment variables</summary>

```bash
dotnet build Lurp.slnx
dotnet run --project src -- --mode=index --solution=path/to/Your.slnx --output-dir=./out
```

Environment variables `LURP_SOLUTION_PATH` and `LURP_OUTPUT_DIR` are equivalent to
`--solution=` and `--output-dir=`. Requires .NET 10 SDK (the root `global.json`
pins one exact version, see CONTRIBUTING; Roslyn 5.6 requires `net10.0`).

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
| Find dead-code candidates (public API and tests are never `proved_dead`; see [CLI Reference](docs/CLI_REFERENCE.md#--modedead-candidates)) | `lurp --mode=dead-candidates --output-dir=./out` |
| Search source text literally (not symbol search) | `lurp --mode=grep --query=<text> --output-dir=./out` |
| See what changed between two indexing runs | `lurp --mode=diff --from-snapshot=<id> --to-snapshot=<id> --output-dir=./out` |
| Pull just the code relevant to a change | `lurp --mode=context --file=<path> --line=<n> --output-dir=./out` |
| Find what depends on a symbol | `lurp --mode=impact --symbol=<id> --direction=upstream --output-dir=./out` |
| Find direct callers of a symbol | `lurp --mode=context --symbol=<id> --tier=direct_callers --output-dir=./out` |

## Framework adapters

Lurp models framework-mediated relationships through adapters that emit ordinary
typed facts into the shared model. Each fact keeps its evidence level. The adapters
and their facts are in [ARCHITECTURE.md §4.1](docs/ARCHITECTURE.md#41-framework-adapters);
the evidence ladder is in [§4.2](docs/ARCHITECTURE.md#42-evidence-ladder).

## Limitations

- **Windows and Linux only**: macOS is not tested.
- **Multi-target projects are indexed as a union**: one snapshot per index run holds every target framework the solution declares. The declared TFM list is recorded per project and the per-symbol TFM set is recorded per snapshot, but CLI/MCP reads do not surface the per-symbol set, so a member present in only one target framework cannot be told apart by a consumer from one present in all of them.
- **Source generators not executed**: `GeneratedTreesIncluded=false`; compile items under `bin/` and `obj/` are excluded from the snapshot and counted in the completeness block (`excluded_document`), while gitignored compile items are indexed. "Generated" is one rule across the index (Roslyn file-name patterns, EF Core `*ModelSnapshot.cs`, and a leading `<auto-generated` / `<autogenerated` comment).
- **Reflection string-literal candidates are `name_candidate`**, not `compiler_proved`.
- **3-snapshot retention**: the 3 newest complete snapshots and their document versions are retained; older ones are pruned automatically, except a snapshot pinned via `pin-snapshot`, which is kept in addition and does not take a slot.
- **Restore first**: an unrestored project still indexes, with a warning, but `MSBuildWorkspace` cannot resolve its references, so edges are missing and diagnostics are wrong. Run `dotnet restore` before `--mode=index`.
- **NuGet audit warnings look like load failures**: `MSBuildWorkspace` reports a NuGet vulnerability warning as a failure-level workspace diagnostic, so the index run prints `WARNING: workspace load reported N failure-level diagnostic(s)` with the package message. Such a warning does not stop the index.

## Performance

| Measurement | Value |
|---|---|
| eNoteV2 (402 docs at `4ba9901`) full index | 18.5 s |
| eNoteV2 incremental (no changes) | 0.2 s |
| Capsule token estimates | `estimated_tokens` (content) vs `estimated_artifact_tokens` (delivery) |

Measured on 2026-11-21 with Lurp 4.0.0 (Release build from `f683ccf` with the P2-S23 changes) and eNoteV2 at `4ba9901`. The performance harness (`--protocol=gate`) reports the median wall time of five runs after three warm-up runs. The machine used an Intel Core i5-12500H and 16 GB of RAM. It ran Fedora Linux 45 (KDE Plasma Desktop Edition) on AC power with the TuneD Balanced profile. A no-change run hashes every document. The first run after the machine has been idle for hours can be much slower, because the source files are read cold.

## Status & roadmap

Shipped as a global tool (`dotnet tool install lurp`). This source tree is <!-- generated:versions -->tool 4.2.0, schema v54, extractor 1.14.0, CLI/MCP contract v9, output schema v8<!-- /generated:versions -->. Each release, and whether it needs an index run, is listed under
[Documentation & license](#documentation--license). `windows-latest`
and `ubuntu-latest` CI plus a manual real-parity gate on eNoteV2 that runs on Windows and Linux
GitHub-hosted runners (`workflow_dispatch`); a release publishes only the CI-built package of a commit
that passed that gate, unless the release run is started with an `override_reason` (see [CONTRIBUTING.md](CONTRIBUTING.md#releasing)). Roadmap: surfacing the
per-symbol TFM set in reads and richer DI parameter-type matching are postponed
by design (see
[DeclaredBoundaries](notes/TRUST_KERNEL.md#declared-boundaries-registry-capsule-audit-task-7)).

## Documentation & license

- [ARCHITECTURE.md](docs/ARCHITECTURE.md): product model, storage, pipeline, rules.
- [CLI_REFERENCE.md](docs/CLI_REFERENCE.md): commands, options, output shapes, snapshot lifecycle, MCP.
- [RELEASE_NOTES_4.3.0.md](docs/RELEASE_NOTES_4.3.0.md) (draft)
- [RELEASE_NOTES_4.2.0.md](docs/RELEASE_NOTES_4.2.0.md): updated declaration spans, symbol search ranking, impact and context traversal, and expanded framework coverage. Run one full index after the upgrade.
- [RELEASE_NOTES_4.1.0.md](docs/RELEASE_NOTES_4.1.0.md): the 4.1.0 changes to EF Core mapping and the MCP tool results (CLI/MCP contract 8). Run one full index after the upgrade.
- [RELEASE_NOTES_4.0.0.md](docs/RELEASE_NOTES_4.0.0.md): the 4.0.0 changes to extraction, storage, indexing and the CLI/MCP contract. Run one full index after the upgrade.
- [RELEASE_NOTES_3.6.1.md](docs/RELEASE_NOTES_3.6.1.md): the 3.6.1 changes (release workflow waits up to 60 minutes for nuget.org to index); no index run is needed.
- [RELEASE_NOTES_3.6.0.md](docs/RELEASE_NOTES_3.6.0.md): the 3.6.0 changes (Linux support, Windows-built manifests read on Linux, lock files kept after a run, and a clear error for a manifest without `git_root`/`solution_path`); no index run is needed.
- [RELEASE_NOTES_3.5.2.md](docs/RELEASE_NOTES_3.5.2.md): the 3.5.2 changes (the dead-candidate `generated_excluded` text, the `lurp_dead_candidates` description, MCP `lurp_index` failure reporting, and the MSBuild/SDK failure cause); no index run is needed.
- [RELEASE_NOTES_3.5.1.md](docs/RELEASE_NOTES_3.5.1.md): the 3.5.1 changes (the `--context-lines=` rule for every `get-symbol` view, the annotations path description, and the 3.5.0 documentation corrections); no index run is needed.
- [RELEASE_NOTES_3.5.0.md](docs/RELEASE_NOTES_3.5.0.md): the 3.5.0 changes (CLI/MCP contract 6, primary-constructor and `nameof` facts, new input errors) and the one index run it needs.
- [RELEASE_NOTES_3.0.2.md](docs/RELEASE_NOTES_3.0.2.md): the 3.0.2 changes (partial-type edge locations, errors that 3.0.1 hid) and the one index run it needs.
- [RELEASE_NOTES_3.0.1.md](docs/RELEASE_NOTES_3.0.1.md): the 3.0.1 fix (an index run that failed after a re-pin).
- [RELEASE_NOTES_3.0.0.md](docs/RELEASE_NOTES_3.0.0.md): changes from 2.0.0 and how to upgrade.
- [RELEASE_NOTES_2.0.0.md](docs/RELEASE_NOTES_2.0.0.md): breaking changes from 1.x and how to migrate.
- [SECURITY.md](SECURITY.md): how to report a vulnerability, trust boundaries, MCP tool access and limits, what Lurp writes.
- [VERSIONING.md](VERSIONING.md): what counts as a breaking CLI/MCP change.
- MIT license, see [LICENSE](LICENSE).

Also MCP: `--mode=serve` exposes <!-- generated:mcp-tools:read -->16 read tools (`lurp_context`, `lurp_get_source`, `lurp_outline`, `lurp_navigate`, `lurp_find_symbol`, `lurp_search`, `lurp_impact`, `lurp_diff`, `lurp_get_symbol`, `lurp_get_annotations`, `lurp_diagnostics`, `lurp_grep`, `lurp_status`, `lurp_timings`, `lurp_refresh`, `lurp_dead_candidates`)<!-- /generated:mcp-tools:read --> over stdio; `--enable-write-tools` additionally registers <!-- generated:mcp-tools:write -->`lurp_index` and `lurp_retract_annotation`<!-- /generated:mcp-tools:write --> (background re-index and annotation retraction). Index first via CLI, then serve. See [CLI_REFERENCE.md#mcp-server---modeserve](docs/CLI_REFERENCE.md#mcp-server---modeserve).
