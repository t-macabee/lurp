# Lurp 3.5.2

3.5.2 corrects the dead-candidate `generated_excluded` text and the
`lurp_dead_candidates` description, reports a failed MCP index read as
failed, keeps a cancelled MCP run's timings, and shows the cause of an
MSBuild/SDK discovery failure. No index run is needed.

## Upgrade from 3.5.1

Install 3.5.2. No `--mode=index` run is needed: the extractor version (1.11.0)
does not change, so the snapshot an existing index holds stays current. The
database schema (v37), the CLI/MCP contract (v6) and the output schema (v6) do
not change.

## Changes

### Dead-candidate `generated_excluded` wording

The uncertainty text for a dead candidate with reason `generated_excluded`
previously said the symbol "was excluded because includeGenerated is set to
false", but that entry is emitted only when generated code is included. It now
says that at least one declaration is generated code (`is_generated=1`) and no
LIVE incoming edge reaches it. The context capsule's own `generated_excluded`
uncertainty text is unchanged.

### `lurp_dead_candidates` description

The `lurp_dead_candidates` tool description and `docs/CLI_REFERENCE.md` no
longer say that every dead-candidate uncertainty uses verbatim capsule wording.
Descriptions that state the same fact as a capsule uncertainty now share their
wording; the two surfaces keep their own uncertainty kinds and symbol lists.

### MCP `lurp_index`

- If Lurp cannot read the latest snapshot after a run, the operation now fails
  with `index_failed` instead of reporting `completed`.
- A cancelled MCP run no longer deletes that run's timings and metrics; the next
  index run prunes it, as the CLI does.

### MSBuild/SDK discovery

An MSBuild/SDK discovery failure now keeps its cause: with `--verbose` or
`LURP_DEBUG=1` the CLI prints the full exception after the message.

### CLI/MCP contract

The CLI/MCP contract stays v6: no mode, flag, tool, parameter or JSON field is
added, removed, renamed or re-typed, and no default changes. The dead-candidate
changes are description text, which is not contract, and `lurp_index`
reports the read failure through its existing `failed` status and `error_code`
field (see `VERSIONING.md`).

### Internal

The dead-candidate classification moved into one method, shared uncertainty
text has one owner, dead limit clamps were removed, and the scoped
binding-incompleteness delete is one statement. None of these changes the
output.

## Versions

| Component | 3.5.1 | 3.5.2 |
|---|---|---|
| Tool | 3.5.1 | 3.5.2 |
| Database schema | v37 | v37 |
| Extractor | 1.11.0 | 1.11.0 |
| CLI/MCP contract | v6 | v6 |
| Output schema | v6 | v6 |
