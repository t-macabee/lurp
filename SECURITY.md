# Security

## Supported versions

Security fixes go into the newest release on nuget.org only. Update with
`dotnet tool update --global lurp`.

## Report a vulnerability

Report it privately on GitHub:
[report a vulnerability](https://github.com/t-macabee/lurp/security/advisories/new).
Do not open a public issue. Give the output of `lurp --version`, the CLI mode or
MCP tool, and the smallest input that shows the problem.

## Trust boundaries

### The MCP client is untrusted input

An agent can follow instructions that it read in a file. Lurp treats every tool
argument as untrusted:

- `--mode=serve` takes the solution and the database location from its own launch
  arguments. No tool takes a solution path or a database path.
- The write tools are registered only when `serve` starts with
  `--enable-write-tools`. Without the flag, an agent cannot start an index run or
  delete an annotation.
- Read tools open the database read-only.
- Every size-driving parameter has an upper bound (see "Input limits").
- The MCP host is built empty: it reads no `appsettings*.json` file of the target
  repository and does not bind environment variables as host configuration.

### The analyzed solution runs code

Lurp loads projects through MSBuild design-time builds. A design-time build runs
the solution's MSBuild targets and tasks, including `Directory.Build.props`,
`Directory.Build.targets` and the build files of NuGet packages. This is code
execution by design. It happens in `--mode=index`, in CLI `status` with
`--solution=`, in MCP `lurp_index`, and in MCP `lurp_status` with `full: true`.
`lurp_status` is a read tool, so `full: true` needs no `--enable-write-tools`; it
works only when `serve` was started with `--solution=`. Index only a solution that
you would build.

### The index database is untrusted data

A database that someone else made can hold any rows.

- Read connections open the file read-only, with `query_only=ON` and
  `trusted_schema=OFF`, so a crafted schema cannot call SQL functions from a view
  or a trigger.
- Write modes (`index`, `annotate`, `retract-annotation`, `pin-snapshot`) open the
  file read-write and migrate an older schema. Point them only at a database that
  Lurp made.
- The freshness check reads the time stamp, and in `hash` mode the content, of
  each document path that the database stores, relative to the repository root. A
  crafted path can name a file outside the repository. The check reports only
  whether that file changed. It returns no file content.

## MCP tools

`serve` has <!-- generated:mcp-tools:total -->18<!-- /generated:mcp-tools:total --> tools.
This table is generated from the tool attributes in the code. The parameters of
each tool are in [CLI_REFERENCE.md](docs/CLI_REFERENCE.md), section "MCP server".

<!-- generated:mcp-tools:access -->
| Tool | Registered | Effect (MCP annotations) |
|---|---|---|
| `lurp_context` | always | read-only |
| `lurp_get_source` | always | read-only |
| `lurp_outline` | always | read-only |
| `lurp_navigate` | always | read-only |
| `lurp_find_symbol` | always | read-only |
| `lurp_search` | always | read-only |
| `lurp_impact` | always | read-only |
| `lurp_diff` | always | read-only |
| `lurp_get_symbol` | always | read-only |
| `lurp_get_annotations` | always | read-only |
| `lurp_diagnostics` | always | read-only |
| `lurp_grep` | always | read-only |
| `lurp_status` | always | read-only |
| `lurp_timings` | always | read-only |
| `lurp_refresh` | always | read-only |
| `lurp_dead_candidates` | always | read-only |
| `lurp_index` | only with `--enable-write-tools` | writes, destructive |
| `lurp_retract_annotation` | only with `--enable-write-tools` | writes, destructive |
<!-- /generated:mcp-tools:access -->

`lurp_index` starts a background index run of the session's solution. It runs
MSBuild, writes a new snapshot and prunes old snapshots as `--mode=index` does.
`lurp_retract_annotation` deletes an annotation from the database.

## Input limits

Each bound is a constant in `src/Mcp/McpLimits.cs`. A larger value is an
invalid-params error.

<!-- generated:mcp-limits -->
| Parameter | Maximum |
|---|---|
| `limit` and `tier_limit` on every tool except `lurp_dead_candidates`; `max_documents`, `max_mismatches` and the length of `documents` on `lurp_status` | 500 |
| `limit` on `lurp_dead_candidates` | 200 |
| `content_budget` on `lurp_context` (estimated tokens) | 200000 |
| `max_depth` on `lurp_impact` | 20 |
| `max_hops` on `lurp_context` | 20 |
| `snippet_tokens` on `lurp_search` | 2000 |
| `max_bytes` on `lurp_status` (UTF-8 bytes) | 400000 |
<!-- /generated:mcp-limits -->

## What Lurp writes

An index run does not create or change files in the analyzed source tree. Lurp
writes only here:

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\lurp\<sha256-12 of the solution path>\`, or the `--output-dir=` path | `index.db` and its `-wal` and `-shm` files; CLI `context` capsule files `capsule-<symbol>.json` |
| `%LOCALAPPDATA%\lurp\<sha256-12 of the solution path>\` (also with `--output-dir=`) | two MSBuild redirect files and the design-time intermediates under `obj\<project>\` |
| `%TEMP%\lurp-locks\` | two lock files per index run (one for the solution, one for the database); deleted when the run ends |
| the `--output-json=` path | the snapshot manifest export of `--mode=index` |

An explicit `--output-dir=` inside the solution directory gets a warning on
stderr. A project that moves its own intermediate path, or assigns
`CustomBeforeMicrosoftCommonTargets` in its project file, keeps its design-time
files in its own `obj/` and is reported with the boundary
`intermediate_redirect_not_applied`. See
[ARCHITECTURE.md](docs/ARCHITECTURE.md), section 8, rule 1.
