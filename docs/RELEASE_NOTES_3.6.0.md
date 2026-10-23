# Lurp 3.6.0

3.6.0 adds Linux as a supported platform. It also reads a snapshot manifest
built on Windows correctly on Linux, keeps the index-run lock files after a
run, and fails with a clear message on a manifest that has no
`git_root`/`solution_path`. No index run is needed.

## Upgrade from 3.5.2

Install 3.6.0. No `--mode=index` run is needed: the extractor version (1.11.0)
does not change, so the snapshot an existing index holds stays current. The
database schema (v37), the CLI/MCP contract (v6) and the output schema (v6) do
not change. On Windows the cache folder of a solution does not move.

## Changes

### Linux support

Lurp now runs on Linux. CI builds and tests every commit on `windows-latest`
and `ubuntu-latest`, and runs the packed-tool and runtime-only smoke tests on
both. The release still publishes one framework-dependent package, built on
Windows; the same package runs on both OSes. macOS is not tested.

The real-parity gate (R1 convergence, integrity, recall, and the per-OS perf
baseline) runs on Linux too, and a release needs both legs to pass.

On Linux the default cache folder is `~/.local/share/lurp/<hash>/` (under
`$XDG_DATA_HOME` when it is set), and the lock folder is `$TMPDIR/lurp-locks`
(usually `/tmp/lurp-locks`). `--help` now prints the real cache folder of the
current OS in place of the fixed text `%LOCALAPPDATA%`.

### Paths on Linux

- Symbolic links in the solution path are resolved. A solution reached through
  a link and through its target uses one cache folder and one lock.
- Paths are compared case-sensitively, as Git and the file system compare them
  on Linux. Two files that differ only in case (`Foo.cs` and `foo.cs`) stay two
  files in incremental invalidation, and `.gitignore` patterns match with
  their case. On Windows paths are still compared without case.
- The cache hash is taken from the canonical solution path as it is. On Windows
  that path is still lower-cased first.

### Manifests built on another OS

On Linux, a snapshot manifest built on Windows got a wrong `git_root`: Linux
did not treat a Windows root such as `C:/repo` as rooted, and put the current
folder in front of it. Windows drive paths and UNC paths are now rooted on
every OS.

A manifest whose `workspace_id` has only `value`, with no `git_root` and
`solution_path`, now fails with a message that names the missing fields and
says that `--mode=index --strategy=full` rebuilds it. Before, Lurp guessed the
root from `value`: on Windows it could pick a wrong root, and on Linux it
failed with an `ArgumentException`. Lurp writes all three fields, so only an
older manifest can reach this error.

### Lock files

The index-run lock files now stay in the lock folder after a run, on every OS.
Only the open handle is the lock. Each solution and each database has one small
empty file, named by a hash of its path. On Linux, deleting the file at the
end of a run could let two runs hold the lock at once.

### CLI/MCP contract

The CLI/MCP contract stays v6: no mode, flag, tool, parameter or JSON field is
added, removed, renamed or re-typed, and no default changes. The `--help` cache
line is description text, which is not contract (see `VERSIONING.md`).

### Contributors

The tests start PowerShell 7 (`pwsh`) on every OS, and the CI and smoke
scripts run under it. On Linux, the 10.0.3xx SDK that `global.json` pins does
not come from distro packages; see `CONTRIBUTING.md`.

## Versions

| Component | 3.5.2 | 3.6.0 |
|---|---|---|
| Tool | 3.5.2 | 3.6.0 |
| Database schema | v37 | v37 |
| Extractor | 1.11.0 | 1.11.0 |
| CLI/MCP contract | v6 | v6 |
| Output schema | v6 | v6 |
