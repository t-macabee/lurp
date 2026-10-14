# Lurp 3.0.0

3.0.0 moves the default database out of the analyzed tree, gates the MCP write
tools, and makes dead-code candidates and the extracted edges much more exact.
Several inputs that 2.0.0 accepted silently are now errors. `lurp_status` gets
a response size cap and one freshness shape. Capsules carry the anchor's
semantic causes once, at the top level.

## Upgrade from 2.0.0

### Run `--mode=index` once for each index

3.0.0 needs database schema v37; 2.0.0 wrote v30. Read commands and the MCP
server refuse an older schema with `ERROR: Index database at <path> uses schema
v30; this Lurp needs v37. Run 'lurp --mode=index' to update it.` The index run
migrates the database (migrations 031 to 037) and then rebuilds the snapshot
fully, because the extractor version changed. Snapshot ids change once: the
per-extractor versions are now part of the snapshot identity.

A snapshot that you pinned with `--mode=pin-snapshot` keeps its 2.0.0 facts,
and reads keep using it. Pin the new snapshot, or remove the pin with
`--mode=pin-snapshot --clear`.

### The default database location moved (CLI/MCP contract version 4)

Without `--output-dir=`, the database, the capsule files and the MSBuild
design-time intermediates go to `%LOCALAPPDATA%\lurp\<sha256-12 of the solution
path>\`. 2.0.0 wrote `index.db` next to the solution. An old `index.db` there
is not found automatically: pass `--output-dir=<old directory>` (or
`LURP_OUTPUT_DIR`) to keep using it, or index again with the new default and
delete the old file. An explicit `--output-dir=` inside the solution directory
gets a warning on stderr.

An index run no longer creates or changes `bin/` or `obj/` files in the
analyzed tree. Lurp redirects the design-time intermediates per project into
the cache (`obj\<project>\`) and still reads restore assets from the real
`obj/`. A solution that sets its own `CustomBeforeMicrosoftCommonTargets` hook
(for example in `Directory.Build.props`) keeps that hook. A project that
assigns the hook in its project file, or that moves its own intermediate path,
keeps its design-time files in its own `obj/`; Lurp prints a warning and
reports the declared boundary `intermediate_redirect_not_applied`.

### MCP server (CLI/MCP contract version 3)

- `lurp_index` and `lurp_retract_annotation` are registered only with
  `--mode=serve --enable-write-tools`. Without the flag the server has 16 read
  tools. Add the flag to your MCP client's `serve` arguments if the agent must
  index or retract annotations.
- `lurp_index` has no `solution` parameter. It indexes the solution that the
  server was started with (`--solution=`).
- `lurp_status` uses the cheap stat-based freshness check by default. Pass
  `full: true` for the MSBuild workspace check (needs `serve --solution=`).
  2.0.0 ran the full check whenever the server had a solution.
- Each tool call opens its own short-lived read-only connection, so parallel
  calls are safe. The write tools use separate short-lived writable
  connections.
- Size-driving inputs have upper bounds: `limit` and `tier_limit` 500 (`limit`
  on `lurp_dead_candidates` 200), `content_budget` 200,000, `max_depth` and
  `max_hops` 20, `snippet_tokens` 2,000, and on `lurp_status` `max_documents`,
  `max_mismatches` and the length of `documents` 500, `max_bytes` 400,000. A
  larger value is an invalid-params error. The bounds table in
  `docs/CLI_REFERENCE.md` and `SECURITY.md` is generated from `McpLimits`.

### Inputs that are now errors

Each of these prints one `ERROR:` line (CLI, exit 1) or returns an
invalid-params error (MCP). 2.0.0 accepted them and answered a different
question.

- An unknown `--freshness=` value. 2.0.0 used `auto`.
- An unknown `--mode=search --type=` value. 2.0.0 returned an empty result.
- An unknown `--mode=pin-snapshot --output=` value. 2.0.0 printed text.
- A value flag given twice, for example `--limit=5 --limit=10`. The list flags
  `--skip-adapter=` and `--affected-project=` stay repeatable.
- An unknown `lurp_status` `sections` value, or an unknown `--mode=status
  --detail=` value.
- A `lurp_status` `detail` value other than `true`, `false`, `1`, `0` (case
  does not matter) or a blank string. 2.0.0 read every other non-blank string
  as `true`.

The CLI `--mode=dead-candidates --limit=` error is now `ERROR: --limit must be
<= 200.` (the old text named a reason that no longer applies).

## Dead-code candidates

`--mode=dead-candidates` and `lurp_dead_candidates` give far fewer false
`proved_dead` answers:

- A property accessor takes the liveness and the verdict of its property (a
  getter every edge except `Writes`, a setter or `init` only `Writes`), and an
  event accessor the edges of its event.
- A type is live when one of its members or nested types is live. Type
  candidates also take the type-use edges `References`, `Returns`, `Inherits`,
  `Implements` and `Throws` from outside the type. The response lists them in
  the new field `type_use_edge_kinds_checked`.
- Static constructors and compiler-synthesized members (for example the
  members the compiler adds to a record) are not candidates.
- An explicit implementation of an interface member declared outside the
  compilation is `uncertain_dead` with the new reason
  `external_interface_implementation`.
- The type that contains the process entry point is `uncertain_dead` with
  reason `entry_point_convention`, like the entry point itself.
- `serialization_convention` needs an exact opt-in attribute on a non-public
  property or field (`JsonInclude`, Newtonsoft `JsonProperty` or
  `DataMember`). 2.0.0 applied it to every internal property and proved dead
  the members a serializer was told to use.
- Test projects are recognized by a reference to a test framework
  (`Microsoft.VisualStudio.TestPlatform.ObjectModel`,
  `Microsoft.Testing.Platform`, `xunit.core`, `xunit.v3.core`,
  `nunit.framework` or `Microsoft.VisualStudio.TestPlatform.TestFramework`).
  Project facts are matched by project name.
- Unused members of C# 14 extension blocks are candidates; 2.0.0 never listed
  them. Extension-block marker types are not candidates.
- The ladder text in the tool description and in the CLI reference states the
  order the code uses, including the last weak-edge case (any other weak
  provenance gives `uncertain_dead`, reason `framework_convention`).

## More exact edges (extractor 1.9.0)

These changes add or correct edges, so `impact`, capsules and dead candidates
change with them:

- Implicit calls are bound by Roslyn: `await` (`GetAwaiter`, `GetResult`),
  `foreach` (`GetEnumerator`, `MoveNext`, `Current`, `Dispose`), `using`
  (`Dispose`), deconstruction (`Deconstruct`), and the calls in patterns,
  `foreach` deconstruction, index and range expressions and collection
  expressions (new component `operation-shapes-v1`). A shape that Lurp does not
  model is a declared boundary.
- A method group passed as a delegate gives a `MethodGroupRef` edge. It is a
  live edge kind (dead candidates now check 15 kinds) and it is followed with
  `--provenance=compiler_proved`.
- `new T(...)` gives a `Constructs` edge to the called constructor. Use of an
  event gives an edge to the event.
- Code in parameter default values, attribute arguments, enum member values and
  event initializers is walked like other code.
- Type uses give `References` edges: a type named only inside a body, as a
  generic argument or as an array element; the parameter types of delegates,
  local functions, C# 14 extension receivers and function pointers; and the
  explicitly typed parameters of a lambda. A member-signature `References`
  edge has the member as its source. No edge targets `System.Void`.
- Members of nested types get their `Calls`, `Reads`, `Writes` and
  `Constructs` edges. 2.0.0 skipped nested types.
- A write through a member access is no longer also recorded as a read.
- Extension-block members of generic blocks are normalized, and each block
  member is declared once.
- The primary-constructor fallback is gone: Lurp no longer guesses a target and
  records it as `compiler_proved`.
- Multi-target projects are indexed as a union of their target frameworks; the
  per-symbol framework set is recorded (reads do not show it yet).
- A change of `<AssemblyName>` alone gives a new snapshot id, and the freshness
  check reports it.

## `lurp_status` and `--mode=status` (CLI/MCP contract version 5)

- `lurp_status` responses are capped at `max_bytes` UTF-8 bytes (default
  40,000, at most 400,000). Over the cap, the largest manifest parts are
  summarized or left out, largest first; `truncated` is true and
  `detail.manifest.omitted_fields` lists each part with the reason
  `summarized` or `budget_exhausted`. `projects` builds the manifest from the
  named projects only. The CLI has no cap.
- `sections=references` and `sections=all` keep the manifest. 2.0.0 lost it.
- `sections` (MCP) and `--detail=` (CLI) share one vocabulary: `documents`,
  `references`, `completeness` and `all`, plus `freshness` and `manifest` on
  MCP. Values combine as a union; `documents` returns the per-document
  `document_versions` map.
- The freshness object has the same twelve keys on the cheap and the full
  path, including `is_fresh` (`true`, `false`, or `null` when unknown),
  `mismatches`, `mismatches_truncated` and `mismatches_total`. On the full path
  `changed_document_count` counts documents.
- With `full: true`, `freshness.full_check_error` is `null` when the full check
  ran, or says why it did not run (no `--solution=`, or the check failed).
- CLI `--mode=status` accepts `--max-mismatches=`.
- CLI `status` and MCP `lurp_status` render the manifest with one renderer and
  report a store error instead of hiding it.

## Context capsules (output schema version 6)

- `semantic_causes` is a top-level capsule field: the anchor's persisted
  semantic changes, listed once. Paths no longer carry their own
  `semantic_causes` (in 2.0.0 that list was always empty). Over the budget the
  list is cut to 10 and `omitted_tiers` names `semantic_causes` as
  `summarized`. `lurp_impact` already reported the causes once, at the top
  level.
- A cancelled `lurp_context` call stops inside the capsule assembly, not only
  at its start.

## Other changes

- **Hash mode.** `--freshness=hash` and the incremental precheck re-hash every
  document. `auto` keeps the time-stamp shortcut. On a 4,000-document solution
  the precheck costs about 0.13 s more.
- **Missing SDK.** `--mode=index` and `--mode=status --solution=` without a
  usable .NET SDK print `ERROR: Indexing requires a .NET SDK that can build the
  target solution. Install a .NET SDK, restore the solution, and retry.` and
  exit 2. Read modes need only the .NET runtime.
- **Wide incremental edits.** An edit to a project that many projects
  reference no longer fails the incremental run with `too many SQL
  variables`. Id lists bind as one JSON parameter.
- **Timings.** `--mode=timings --json` and `lurp_timings` return a `metrics`
  object (for example `peak_working_set_mb`, Lurp process only). New timing
  steps: `prune_snapshots`, `incremental_precheck`, the configuration check,
  `change_scope`, `orphan_edge_cleanup`, and `solution_load` on incremental
  runs.
- **Help and reference.** `--help` and the flag tables, the MCP tool table, the
  bounds table and the version lines of the docs are generated from the code.
  Every Lurp command example in the docs is parsed in the tests, and the
  runnable ones run.
- **Warnings.** A full index prints the unrestored-projects warning once.
- **Declared boundaries.** The registry names the constructs Lurp does not
  model, so a capsule can say what it cannot see.

## Speed

Measured on the synthetic perf fixtures (16x: 80 projects, 4,000 documents)
unless stated:

| Operation | 2.0.0 line | 3.0.0 |
|---|---|---|
| `dead-candidates`, 16x / 4x | 309 s / 34 s | 3.1 s / 1.1 s |
| `diff`, 16x | 8.97 s | 4.80 s |
| `change_scope` after an edit in the root project, 16x | 19.2 s | 0.28 s |
| incremental `semantic_diff` after a leaf edit, 16x | 3.4 s | 0.6–0.8 s |
| incremental index run (two leaf edits), 16x | 29.6 s | 23.9 s |
| `prune_snapshots`, 16x | 6.0 s | 3.5 s |

The "2.0.0 line" column is the code before each change. The planner
statistics (`sqlite_stat1`) are written after every index run. Migrations 035
to 037 add the declaration indexes and an index on
`document_versions.document_id`, and drop six indexes that no query used.

## Project and release process

- `SECURITY.md` (repository root): how to report a vulnerability (GitHub
  private vulnerability reporting), trust boundaries, MCP tool access and
  limits, and what Lurp writes.
- A release publishes the nupkg that CI built, tested and packed for the tagged
  commit, byte for byte. It needs a recorded `real-parity` pass on that commit
  (eNoteV2 parity, recall and tree-integrity gates, and a memory baseline
  gate) and an attestation. See `CONTRIBUTING.md`, "Releasing".
- CI checks the packed tool on a runtime-only machine and checks that the
  analyzed tree stays byte-identical while every CLI mode and every MCP tool
  runs.

## Versions

| Version | 2.0.0 | 3.0.0 |
|---|---|---|
| Tool | 2.0.0 | 3.0.0 |
| CLI/MCP contract | 2 | 5 |
| Output schema (capsule JSON) | 5 | 6 |
| Database schema | 30 | 37 |
| Extractor | 1.6.0 | 1.9.0 |
| Operation-shapes extractor | none | `operation-shapes-v1` |

The 2.0.0 column is the `v2.0.0` tag. The other component versions
(`reflection-v3`, `aspnetcore-v2` and the rest) did not change. Contract 3 is
the MCP change, contract 4 the default database location, contract 5 the
`lurp_status` cap and its new parameters.

## Known limits

- Windows only. Linux and macOS are not tested.
- Reads do not show the per-symbol target framework set.
- The database keeps three snapshots, and each one stores its own rows. A rework
  that shares unchanged rows between snapshots (smaller databases) is planned
  after this release.
- The first index run after the machine has been idle for hours can be much
  slower, because the source files are read cold.
