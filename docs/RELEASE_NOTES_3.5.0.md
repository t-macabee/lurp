# Lurp 3.5.0

3.5.0 changes the CLI/MCP contract to version 6. Every JSON answer of a mode
has the same keys, `latest_failure` uses snake_case, and an upstream `impact`
follows only the edges of dependents. 3.5.0 also locates a primary constructor
at its parameter list and records a `nameof` as a name reference. The
extractor version changes, so the next index run rebuilds the snapshot fully.

## Upgrade from 3.0.2

Install 3.5.0 and run `--mode=index` once for each index. The extractor version
changes from 1.10.0 to 1.11.0, and the extractor version is part of the
snapshot identity. So the run rebuilds the snapshot fully, and the snapshot id
changes once. The database schema (v37) and the output schema (v6) do not
change.

The CLI/MCP contract changes from v5 to v6. The tool version does not tell you
that the contract changed; `contract_version` does (`status --json`,
`lurp_status`, `lurp --version`). The sections below list each change.

A snapshot that you pinned with `--mode=pin-snapshot` keeps its 3.0.2 facts,
and reads keep using it. Pin the new snapshot, or remove the pin with
`--mode=pin-snapshot --clear`.

## Changes (CLI/MCP contract version 6)

### JSON keys

- Every CLI and MCP JSON answer prints every key of its mode. A key with no
  value is `null`. 3.0.2 left null keys out of `status --json` (for example
  `latest_failure`, `previous_pinned_snapshot_id` and `pin_active`), but its
  never-indexed answer printed them. 3.0.2 also left null keys out of the
  `context --tier=` page with `--output=json` (for example `next_cursor` on
  the last page). The context capsule and the snapshot manifest are separate
  documents with their own schema. They still leave null keys out.
- A yes/no key is `true` or `false`. It is `null` only when the list or check
  that it describes was not produced. 3.0.2 printed `null` instead of `false`
  for `pin_active`, `mismatches_truncated` and
  `changed_documents_sample_truncated`. `lurp_status` now always prints
  `document_freshness` and `truncated`.
- `status --json` writes the keys of `latest_failure` in snake_case:
  `snapshot_id`, `reason_code`, `message`, `created_at_utc`. 3.0.2 wrote
  `SnapshotId`, `ReasonCode`, `Message` and `CreatedAtUtc`. This fixes the
  known issue of 3.0.2.
- `latest_failure` has a value only when the newest snapshot failed. A failed
  snapshot that is older than the newest complete snapshot is not reported.
  3.0.2 reported the newest failed snapshot even when a later index run
  succeeded. The text output of `status` now prints a `Latest failed index:`
  line in the same case.
- The index summary label `declarations_in_snapshot` is now
  `symbols_in_snapshot`. It counts distinct symbols: a partial type with two
  declarations counts once.

### Results

- `impact --direction=upstream` (MCP `lurp_impact` with
  `direction: "upstream"`) follows every edge kind except `Declares`,
  `Contains` and `TestedBy` when you give no kinds. These three kinds lead from
  a member to its type and from a test to the code that the test covers. That
  is not "what depends on this symbol". On eNoteV2, upstream of one service
  method went from 521 symbols to 6. `--kinds=` (MCP `kinds`) still replaces
  the default set. The downstream direction does not change.
- `outline` and `lurp_outline` do not list compiler-generated members (for
  example the `Equals` and `ToString` of a record). They list only
  declarations that have source syntax, as `dead-candidates` already did.

### Facts in the index (extractor 1.11.0)

- A primary constructor (`class Host(Foo a, Bar b)`, `record R(int A)`) is
  located at its parameter list: its declaration, the `declaration` and
  `signature` views of `get-symbol`, `outline`, and the location of its edges.
  Its name position is the type name. A line in the parameter list resolves to
  the constructor in `context --file= --line=` and `navigate`. 3.0.2 gave the
  constructor the span of the whole type. So an edit anywhere in the type moved
  the constructor's edges, and a line inside the type could resolve to the
  type or to the constructor.
- The `References` edges from a member to its parameter types are located at
  each parameter's type. 3.0.2 used the span of the whole member, so an edit in
  the body moved them (`edge_location_changed` in `diff`).
- A `nameof(...)` records every symbol that it names as a
  `ReflectionMemberRef` edge (`compiler_proved`), for example both `_holder`
  and `Value` in `nameof(_holder.Value)`. It records no `Reads` edge, because
  a `nameof` reads nothing at run time. In 3.0.2, a `nameof` of a method group
  also added a false `unsupported_syntax` completeness row, and that row made
  the dead-candidate results of the whole document `unresolved`. On eNoteV2
  the `unresolved` count went from 12 to 0.

### Inputs that are now errors

Each of these values was accepted by accident in 3.0.2 (see `VERSIONING.md`).
Each error is one `ERROR:` line on stderr with exit code 1.

- `search`: `--limit=` and `--snippet-tokens=` must be positive integers
  (`ERROR: --limit must be a positive integer.`).
- `get-symbol`: `--context-lines=` must be a non-negative integer. 3.0.2
  ignored a value that was not a number.
- `context`: `--symbol=` together with `--file=` and `--line=` is an error.
  `--file=` without `--line=`, or `--line=` without `--file=`, is an error
  (`ERROR: --file and --line must be given together.`). 3.0.2 ignored the
  half anchor.
- `pin-snapshot`: `--clear` together with `--snapshot=<id>` is an error.
  `--snapshot=latest --clear` stays valid.
- `impact`: an empty `--kinds=` or `--provenance=`, or a list with an empty
  item (`Calls,`), is an error. MCP `lurp_impact` rejects an empty `kinds` or
  `provenance` array, or an empty item, as invalid params. 3.0.2 read an empty
  filter as "no filter".
- `timings` with no index database fails with
  `ERROR: Index database not found at <path>`. 3.0.2 printed a note to stdout
  and exited with 0.

### MCP error texts

- A `snapshot_id` that does not exist in the database now gives
  `snapshot '<id>' not found.`, the CLI text. A `snapshot_id` that exists but
  is not the session's pinned snapshot still gives the `snapshot mismatch`
  text.
- Error texts name the MCP property (`max_depth`, `max_documents`,
  `max_mismatches`, `to_snapshot`). 3.0.2 used the CLI spelling with a hyphen.
- `lurp_retract_annotation` without `annotation_id` gives the invalid-params
  error `annotation_id is required.`. 3.0.2 gave a general error text.

### Pins

- Only `annotate`, `retract-annotation` and `pin-snapshot` remove a stale pin
  (a pin on a snapshot that is missing or not complete). They print
  `NOTICE: removed the stale pin on snapshot '<id>' (the snapshot is not
  complete).` to stderr. Every other command, `index` and `lurp_index`
  included, leaves the pin in place, uses the latest complete snapshot and
  prints the stale-pin `WARNING:`. 3.0.2 removed the pin without a message in
  every command that opened the database for writing.
- `timings` now prints the stale-pin `WARNING:` like the other read commands.
- One database can hold more than one workspace (two solutions indexed into
  one output folder). Reads then use the most recently set pin whose snapshot
  is complete, and `pin-snapshot --clear` removes every pin. 3.0.2 read one pin
  row in no defined order, so a stale pin of one workspace could hide the
  active pin of another.

## Versions

| Component | 3.0.2 | 3.5.0 |
|---|---|---|
| Tool | 3.0.2 | 3.5.0 |
| Database schema | v37 | v37 |
| Extractor | 1.10.0 | 1.11.0 |
| CLI/MCP contract | v5 | v6 |
| Output schema | v6 | v6 |

The per-extractor and adapter versions (`reflection-v3`, `efcore-v1` and the
rest) do not change.
