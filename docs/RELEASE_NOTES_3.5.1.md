# Lurp 3.5.1

3.5.1 corrects the 3.5.0 documentation and makes one bad CLI input fail for
every `get-symbol` view. The extraction output does not change, so the next index
run is not needed.

## Upgrade from 3.5.0

Install 3.5.1. No `--mode=index` run is needed: the extractor version (1.11.0)
does not change, so the snapshot an existing index holds stays current. The
database schema (v37), the CLI/MCP contract (v6) and the output schema (v6) do
not change.

## Changes

### `--context-lines=` is validated for every `get-symbol` view

A `--context-lines=` value that is not a non-negative integer is now an error for
every `get-symbol` view. 3.5.0 accepted it for five of the six views
(`metadata`, `signature`, `body`, `declaration`, `containing-type`) and ignored
it; only `surrounding` rejected it. MCP `lurp_get_symbol` already rejected a
negative `context_lines` for every view. The value is still used by
`--view=surrounding`. The CLI/MCP contract stays v6: no document, tool
description or test listed a non-number as an accepted value, so this only
rejects an input that 3.5.0 accepted by accident (see `VERSIONING.md`).

### Tool description

`lurp_get_annotations` describes its `document` argument as a
`solution-relative forward-slashed path`. 3.5.0 said `git-relative`; the value
was always relative to the solution directory.

### Documentation corrections

The 3.5.0 documents now correct the environment-variable sentence, the CLI error
example, the SDK note for solution-backed status, the intermediate-path
exception, the `status` schema exception, the migrating write modes, the stored
path base, the stale-pin `WARNING:` scope and the release override; two
eNoteV2 numbers were removed from the 3.5.0 notes because no file in the
repository backs them.

## Versions

| Component | 3.5.0 | 3.5.1 |
|---|---|---|
| Tool | 3.5.0 | 3.5.1 |
| Database schema | v37 | v37 |
| Extractor | 1.11.0 | 1.11.0 |
| CLI/MCP contract | v6 | v6 |
| Output schema | v6 | v6 |
