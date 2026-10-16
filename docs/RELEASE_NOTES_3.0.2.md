# Lurp 3.0.2

3.0.2 stores the edges of a partial type at its hand-written part, and it makes
several errors visible that 3.0.1 hid. The extractor version changes, so
the next index run rebuilds the snapshot fully.

## Upgrade from 3.0.1

Install 3.0.2 and run `--mode=index` once for each index. The extractor version
changes from 1.9.0 to 1.10.0, and the extractor version is part of the snapshot
identity. So the run rebuilds the snapshot fully, and the snapshot id changes
once. The database schema (v37), the CLI/MCP contract (v5) and the output
schema (v6) do not change.

A snapshot that you pinned with `--mode=pin-snapshot` keeps its 3.0.1 facts,
and reads keep using it. Pin the new snapshot, or remove the pin with
`--mode=pin-snapshot --clear`.

## Changes

### No false warning for an implicit `base()` call

A constructor without an initializer is compiled as a call to the base type's default constructor. That constructor is not a stored symbol, so the edge was dropped and the index run printed a false `edges_dropped_reason_other` warning; the extractor no longer emits it, and the stored edge set is unchanged.

### The edges of a partial type point at a hand-written part

A partial type or member has more than one declaration. Its `Inherits`,
`Implements`, `Contains` and `References` edges store the location of one of
them. 3.0.1 took the first declaration in the compilation's file order. That
order can put a generated part first (for example `Form1.Designer.cs` or an EF
Core migration's `.Designer.cs` file). The edge then pointed at the generated
file and was marked as cross-generated.

3.0.2 takes a declaration in a hand-written file first, then the first by file
path, then the first in the file. The choice no longer depends on the file
order. When a type has a hand-written part, its edges are not marked as
cross-generated only because a tool wrote another part.

### Errors that 3.0.1 hid

- **Impact and context.** When a database read fails during the walk of an
  impact or context request (the CLI modes `impact` and `context`, the MCP tools
  `lurp_impact` and `lurp_context`), the request now fails with that error. 3.0.1
  wrote a warning to stderr, skipped the symbol, and returned a smaller result
  that looked complete.
- **Analyzer diagnostics.** When a project's analyzers fail during an index run,
  the run prints `WARNING: Analyzer diagnostics for project '<name>' are not
  stored: <error>. Compiler diagnostics are stored.` and continues. 3.0.1 printed
  nothing and could keep a part of the analyzer diagnostics.
- **Stale pin.** When a command that can write to the database finds a pin on a
  snapshot that is no longer complete, it removes the pin. If that removal
  fails, the command now fails with the database error. 3.0.1 ignored the error
  and used the latest snapshot. A read-only command (status, the MCP read tools)
  still uses the latest snapshot, because it cannot remove the pin.
- **Damaged index.** A missing pins table, or a stored source span outside its
  file, now fails with an error on every path. 3.0.1 answered "no pin", or left
  the source view out of a `get-symbol` answer.

### Inputs and output

- MCP `lurp_impact`: an empty `direction` (`""`) is now an invalid-params error,
  like every other unknown value. 3.0.1 read it as `downstream`. Leave out
  `direction` to get `downstream`. The CLI/MCP contract stays v5: no document,
  tool description or test ever listed `""` as an accepted value, so this only
  rejects an input that 3.0.1 accepted by accident.
- The text output of `dead-candidates` shows only the file path for a candidate
  that has no line. 3.0.1 printed `<path>:0`.

## Versions

| Component | 3.0.1 | 3.0.2 |
|---|---|---|
| Tool | 3.0.1 | 3.0.2 |
| Database schema | v37 | v37 |
| Extractor | 1.9.0 | 1.10.0 |
| CLI/MCP contract | v5 | v5 |
| Output schema | v6 | v6 |

The per-extractor and adapter versions (`reflection-v3`, `efcore-v1` and the
rest) do not change.
