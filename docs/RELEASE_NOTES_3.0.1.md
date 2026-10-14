# Lurp 3.0.1

3.0.1 fixes one defect in 3.0.0: after you pin a second snapshot, the index run
can fail every time. Nothing else changes.

## Upgrade from 3.0.0

Install 3.0.1. You do not need to index again. The database schema (v37), the
extractor version (1.9.0), the CLI/MCP contract (v5) and the snapshot ids do not
change.

## Fixes

### An index run no longer fails after a re-pin

In 3.0.0, `--mode=pin-snapshot` on a second snapshot records the first one as
the previous pin. When that first snapshot became old enough to be pruned (the
database keeps three snapshots), the prune at the end of every index run failed
with `SQLite Error 19: 'FOREIGN KEY constraint failed'`, and the run exited with
that error. The new snapshot was already saved, but no old snapshot was pruned
again.

In 3.0.1 the prune clears the previous-pin pointer of a snapshot it deletes.
After that, `--mode=status` no longer shows `Previous pin:` for that snapshot,
because the snapshot no longer exists. The current pin is never pruned.

If 3.0.0 already fails this way for you, install 3.0.1 and run `--mode=index`
again; the prune now completes. On 3.0.0, `--mode=pin-snapshot --clear` is a
workaround.

### A pin-read error is reported as itself

If the prune cannot read the pins table, the run now fails with that error.
3.0.0 treated the error as "no pin" and later failed with a foreign-key error
that did not name the cause.

## Versions

| Component | 3.0.0 | 3.0.1 |
|---|---|---|
| Tool | 3.0.0 | 3.0.1 |
| Database schema | v37 | v37 |
| Extractor | 1.9.0 | 1.9.0 |
| CLI/MCP contract | v5 | v5 |
| Output schema | v6 | v6 |
