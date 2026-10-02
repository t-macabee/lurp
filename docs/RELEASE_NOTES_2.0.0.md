# Lurp 2.0.0

2.0.0 replaces impact path enumeration with a reachability traversal.
`--mode=impact` / `lurp_impact` return reached symbols with one deterministic
witness path each, and context capsules carry witness paths instead of every
simple path. The deprecated path-listing opt-in does not ship in 2.0.0.

## Why

Impact used to enumerate every simple path up to `--max-depth`. The number of
paths grows exponentially while the number of affected symbols stays small.
Measured on the eNoteV2 index (402 documents) with a Debug build of 1.4.0:

| Run | Result | Wall | Peak working set |
|---|---|---|---|
| `impact SaveChangesAsync` upstream, depth 3 | 68,107 paths | 1.0 s | 279 MB |
| `impact SaveChangesAsync` upstream, depth 4 | 861,886 paths over 1,858 symbols | 7.3 s | 3,372 MB |
| `impact IStudentContext.GetCurrentStudentIdAsync` upstream, depth 5 | 239,911 paths | 2.9 s | 1,244 MB |
| `impact SaveChangesAsync` upstream, depth 5 | did not finish | — | 8–11 GB |
| `context SaveChangesAsync`, `--max-hops` 1 / 3 / 4 | ok / ok / `Out of memory.` | 1.3 / 2.5 / 9.9 s | 64 / 568 / 4,348 MB |

2.0.0 visits each symbol once and records its depth, a witness path, a
shortest-path count and a frontier flag. The same runs (symbol counts measured
on the pre-T8 `reflection-v2` build; the released 2.0.0 build on the same
eNoteV2 gives 2,012 / 2,147 / 2,252 and 1,637 / 1,979, see below):

| Run | Result | Wall | Peak working set |
|---|---|---|---|
| `impact SaveChangesAsync` upstream, depth 4 / 5 / 6 | 1,857 / 1,962 / 2,088 symbols | 0.52 / 0.52 / 0.54 s | 56–60 MB |
| `impact IStudentContext.GetCurrentStudentIdAsync` upstream, depth 5 / 6 | 1,517 / 1,832 symbols | 0.45 / 0.49 s | 53–55 MB |
| `context SaveChangesAsync`, `--max-hops` 4 / 5 / 6 | exit 0 | 1.76 / 1.88 / 1.81 s | 87–93 MB |

The 1.4.0 depth-4 count (1,858) includes the anchor and the 2.0.0 count (1,857)
excludes it, so the reached set is unchanged. The 1.4.0 numbers come from an
index built with `reflection-v1`, before Lurp's T4 edge-merge change; a scratch
reindex with the pre-T8 2.0.0 build (`reflection-v2`, eNoteV2 commit `9d18682`)
gives the same reached-symbol counts, 1,857 and 1,517. The `reflection-v3`
restriction changes which reflection edges an index carries, so a released-build
reindex can differ in the symbols it reaches through them (see "Reflection name
candidates" below).

## Breaking changes

### Run `--mode=index` once after the upgrade

Read commands no longer migrate `index.db`. Every read mode and the MCP server
first check the database schema version: at session start, on every
`lurp_refresh` call, and before `lurp_retract_annotation`. When the version is
older than this build needs, the command fails with `ERROR: Index database at
<path> uses schema v<n>; this Lurp needs v<m>. Run 'lurp --mode=index' to
update it.` When it is newer, the error says to update Lurp. Only
`--mode=index`, `--mode=pin-snapshot` and MCP `lurp_index` migrate.
`--mode=status` reports the mismatch and exits 0.

1.4.0 wrote schema v29 and 2.0.0 needs v30. So after the upgrade, run
`lurp --mode=index` once for each index before you use a read command or start
an MCP session on it. That run migrates the database to v30 and rebuilds the
snapshot fully (see "Versions").

In a running MCP session, `lurp_refresh {}` returns this error when another
process changed the database schema under the session.

### `--mode=impact` and `lurp_impact` (CLI/MCP contract version 2)

The default response is now reached symbols, not paths.

| Removed | Added / changed |
|---|---|
| `path_count_total` | `symbol_count_total` (reached symbols; the anchor is not included) |
| `paths[]` | `symbols[]` (`symbol_id`, `depth`, `shortest_path_count`, `frontier`, `witness_path`) |
| per-path `truncated`, `truncation_reason` | `symbols[].frontier` (true at `--max-depth` with followable edges beyond the witness path) |
| `groups[].path_count`, `groups[].max_total_steps` | `groups[].symbol_count`, `groups[].max_depth` |
| per-path `semantic_causes` | top-level `semantic_causes` (anchor-scoped, once) |
| `--max-paths=`, MCP `max_paths` | `--limit=`, `limit` (default 50) |
| `truncated.reason: "max_paths"` | `truncated.reason: "limit"` |

`witness_path` carries `total_steps` and `hops[]` with `source_symbol_id`,
`target_symbol_id`, `edge_kind`, `provenance`, `source_document` and
`source_line` (1-based). `groups` still covers all reached symbols before the
page cut and is still sorted by count descending, then
`first_hop_target_symbol_id`. `--output=summary` prints
`symbols: <total> total, <returned> in this page (offset N); <groups> distinct
first hop(s); <frontier> at max depth` plus one row per group;
`--output=jsonl` emits one `{"type":"symbol", …}` record per page entry.

Cursors issued before 2.0.0 are not valid: the cursor fingerprint appends an
output-shape marker that 1.x did not carry, and a mismatch fails with `Cursor
was issued for a different request; re-run without --cursor.` The marker is
kept for cursor compatibility; with one output shape it no longer varies.

### Context capsule JSON (output schema version 5)

`--mode=context` topology changes:

| Removed | Added |
|---|---|
| `topology.current.incoming_path_count` | `topology.current.incoming_symbol_count` (reached symbols) |
| `topology.current.outgoing_path_count` | `topology.current.outgoing_symbol_count` |
| | `topology.current.incoming_witness_path_count` |
| | `topology.current.outgoing_witness_path_count` |

`total_hop_count` remains, now summed over the witness paths. The symbol and
witness-path counts are taken before the budget trim.

`incoming_paths` / `outgoing_paths` no longer carry every simple path. Each
entry is the deterministic witness path to a reached symbol that no other
emitted path passes through, so no emitted path is a prefix of another and the
lists are bounded by the reached-symbol count. Frontier symbols keep
`truncated: true` with `truncation_reason: "max depth reached"`.

`--output=summary` label rename: `incomingPaths` / `outgoingPaths` are now
`incomingWitnessPaths` / `outgoingWitnessPaths`.

Every capsule records its own origin at the top level: `snapshot_id`,
`tool_version` and `output_schema_version`, so a capsule found on disk can be
checked against `status` without the MCP envelope. Capsules are written
atomically (a sibling `capsule-<symbol>.json.tmp` is renamed over the target).
A failed or interrupted run leaves the previous file intact, and a reader
never sees a partial one.

`suggested_verification` is assembled before the budget pass, so it is measured
like other content and can be dropped to fit `--content-budget` (an
`omitted_tiers` record with category `suggestedVerification` and reason
`budget_exhausted`). When one test class contributes more than one covering
test, the section carries one entry for the class with a
`dotnet test --filter "FullyQualifiedName~<Class>"` command instead of one
entry per test; a class with a single test keeps its exact
`FullyQualifiedName=<Test>` entry. Running the class filter runs every test it
holds, so no covering test is lost. `uncertainties` is appended after the
budget pass and is never trimmed, and `estimated_artifact_tokens` is stamped
last over the emitted file, so it includes both sections instead of
understating the delivery size.

### Token estimates are a size hint (characters ÷ 3)

`estimated_tokens` and `estimated_artifact_tokens` are a model-neutral,
character-based size hint, not a token count for any model. The divisor changed
from 4 to 3. Measured on eNoteV2 capsules (whole file), OpenAI tokenizers give
3.82–4.08 characters per token and DeepSeek V4.1-Flash gives 3.17–3.51, so 4
under-counted DeepSeek by 15–20 %. 3 is below every measured value. Claude was
not measured. The same `--content-budget` now keeps about 25 % less content,
and every estimate is about 33 % larger for the same text. Lurp ships no
tokenizer.

### Tier item order and `--tier=` cursors

Items in `direct_callers`, `second_degree_context` and `relevant_tests` now come
in depth order, then symbol ID, instead of path-enumeration order. The item set
is unchanged. `--tier=` pages over that list, so page contents change, and a
`--tier=` cursor taken with a 1.x build is not valid against 2.0.0.

### Dispatch provenance

The dispatch branch now composes item provenance from the witness path instead
of the first enumerated path. On the compared anchors (`SaveChangesAsync`,
`IStudentContext.GetCurrentStudentIdAsync`,
`CurrentActor.GetCurrentStudentIdAsync`) no item's `edge_kind`, `provenance` or
`relationship` changed: tier traces follow only `Calls` edges, which are
`compiler_proved`, so the composed provenance depends only on the dispatch edge.
It would differ only if a `Calls` edge ever carried `framework_derived`.

### Capsule content difference

The default-hops capsules for `CorsExtensions`, `LoggingExtensions` and
`ValidationExtensions` are unchanged. The default-hops `SaveChangesAsync`
capsule has one difference: an `uncertainties` entry (binding incompleteness)
changed from "1785 binding(s) in eNote.Infrastructure, eNote.Tests" to
"1762 binding(s) in eNote.Application, eNote.Infrastructure, eNote.Tests". The
budget step keeps three different incoming witness paths than before, so
`UncertaintyDetector` sees documents from a different set of paths. This
comparison predates the `suggested_verification` grouping and write-order
change above, which adds its own differences for anchors with repeated test
classes.

## Removed: full path listing

2.0.0 does not ship the deprecated path-listing opt-in: `--enumerate-paths`
(MCP `enumerate_paths`) and `--max-enumerated-paths=` (MCP
`max_enumerated_paths`) are gone, and impact has one output shape, the
reached-symbols response. `ImpactTraverser` and
`ImpactPathCeilingExceededException` are removed from the codebase.

## Reflection name candidates (`reflection-v3`)

2.0.0 narrows `ReflectionNameCandidate` edges. A string literal is emitted only
when it is the name argument of a runtime name-binding API, resolved
semantically (by receiver or method symbol, not method name):

- `System.Type` member lookup (`GetMethod`, `GetProperty`, `GetField`,
  `GetMember`, `GetEvent`, `GetNestedType`, `InvokeMember`) and the other
  runtime type-resolution sinks (`Type.GetType`, `Assembly.GetType` /
  `GetExportedTypes`, `Activator.CreateInstance`). When the receiver is a
  `typeof(X)`, candidates are limited to `X`'s members; with an unknown
  `System.Type` receiver, every matching member can still be a candidate.
- `PropertyChanged`-style names: `new PropertyChangedEventArgs("X")`, and
  arguments to a `[CallerMemberName]` string parameter, limited to the
  containing type.
- EF Core string APIs on `EntityTypeBuilder<T>` and its navigation builders
  (`Property`, `HasOne`, `HasMany`, `WithOne`, `WithMany`, `Navigation`,
  `HasForeignKey`, `HasKey`, `HasIndex`, `Ignore`) and `Include(string)`,
  limited to `T` when it is known. Untyped `EntityTypeBuilder` model-snapshot
  builders are rejected.
- Binding attributes: `[ForeignKey]`, `[InverseProperty]` and xUnit
  `[MemberData]` name arguments.
- MVC action-name APIs: `RedirectToAction`, `CreatedAtAction`,
  `AcceptedAtAction`, `Url.Action`, `ActionLink`, limited to controller types.

Everything else is rejected, including other attribute arguments
(`JsonPropertyName`, `Route`, …), logging and message constants, dictionary
and config keys, test data, pattern and switch constants, and same-named
methods on other receivers: `JsonElement.GetProperty("source")` is checked by
receiver type and no longer produces a candidate.

The extractor version moves from `reflection-v2` to `reflection-v3`, so the
next index rebuilds fully (stale-extractor detection). An index rebuilt with
2.0.0 therefore carries fewer `ReflectionNameCandidate` edges, and everything
that consumes them changes with it: impact follows all edge kinds by default,
and `dead-candidates` classifies incoming edges including `name_candidate`.

## ASP.NET Core routes (`aspnetcore-v2`)

With a class-level `[Route]`, the ASP.NET Core adapter gave every ordinary
method of a controller a `RoutesTo` edge, including private helpers and
protected overrides. On eNoteV2, 9 of 159 `RoutesTo` edges pointed at such
methods (private helpers such as `UploadsController.Serve` and
`IsSafeFileName`, and `protected override GetId`).

2.0.0 emits `RoutesTo` only for methods that ASP.NET Core treats as actions. The
rule is the one in ASP.NET Core's `DefaultApplicationModelProvider.IsAction`: an
action is public, not static, not abstract and not generic. It is not
`[NonAction]` (also when the attribute is on the method it overrides), not an
`object` override and not `IDisposable.Dispose`. The adapter's `Returns` and
`[FromServices]` reference edges follow the same rule. On eNoteV2, each of the
9 methods also has a compiler-proved `Calls` or `MayDispatchTo` edge, so they
stay live for `dead-candidates`.

The adapter version moves from `aspnetcore-v1` to `aspnetcore-v2`, so an index
that holds ASP.NET Core edges rebuilds fully on the next run.

## Other changes since 1.4.0

- **Concurrent index runs.** A second `--mode=index` or `lurp_index` on the same
  solution or the same database fails at once with `another Lurp index run is
  using solution <path>` (or `database <path>`).
- **Workspace load failures.** Failure-level MSBuild workspace diagnostics are
  printed on stderr under `WARNING: workspace load reported <n> failure-level
  diagnostic(s):`.
- **Non-UTF-8 source files.** `--mode=index` skips a document that is not valid
  UTF-8/UTF-16 text and warns once (`WARNING: Skipped <n> document(s) that are
  not valid UTF-8/UTF-16 text`). 1.4.0 stopped with an unhandled exception.
- **Incremental index with nothing to do.** When no document or build input
  changed, `--mode=index` does not load the workspace. It prints `Workspace load
  skipped: stored snapshot is still current.`
- **`--mode=timings --snapshot=latest`** resolves to the latest (or pinned)
  snapshot. 1.4.0 printed `No timing data for snapshot latest.` and exited 0.
- **Index summary counts.** Edges to ASP.NET Core OpenAPI-generated symbols and
  to `InterceptsLocationAttribute` count as compiler-synthesized drops instead
  of `other`. The test adapter no longer emits `TestedBy` edges from namespace
  symbols.
- **Speed.** Incoming-edge lookups use a composite (snapshot, target) index
  (database migration 30). The semantic diff skips source and location reads
  for unchanged declarations. Metadata reference hashes are computed once per
  workspace load.

## Versions

| Version | 1.4.0 | 2.0.0 |
|---|---|---|
| Tool | 1.4.0 | 2.0.0 |
| CLI/MCP contract | 1 | 2 |
| Output schema (capsule JSON) | 4 | 5 |
| Database schema | 29 | 30 |
| Extractor | 1.6.0 | 1.6.0 |
| Reflection extractor | `reflection-v1` | `reflection-v3` |
| ASP.NET Core adapter | `aspnetcore-v1` | `aspnetcore-v2` |

The 1.4.0 column is the `v1.4.0` tag. `output_schema_version` is recorded on
each snapshot. Database migration 30 adds an index only; no data changes. The
aggregate extractor version stays 1.6.0. The reflection and ASP.NET Core
component changes are what make the next index run rebuild fully
(stale-extractor detection).

## Migrating an agent that reads `path_count_total`

- Use `symbol_count_total` for the number of symbols reached at `--max-depth`
  (the anchor is excluded) and `frontier_count` for how many sit at the depth
  bound with followable edges beyond their witness path.
- Read `symbols[]` instead of `paths[]`, paged with `--limit=` / `limit`
  (default 50) and continued with `truncated.cursor`.
- Each symbol's `witness_path` gives the anchor-to-symbol hops; `hops[]` carries
  the same edge fields the old path hops did.
- `shortest_path_count` counts routes, not distinct neighbors: one static call
  site emits both `Calls` and `StaticallyCalls`, and each counts.
- `groups` is now a symbol fan-out summary, and `semantic_causes` is top level
  rather than repeated per path.
