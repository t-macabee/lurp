# CLI / MCP Contract Versioning

`VersionConstants.CliMcpContractVersion` (`src/Workspace/VersionConstants.cs`) is the single integer that versions the externally-visible CLI and MCP surface that other agents integrate against.

It is intentionally separate from `DatabaseSchemaVersion` (SQLite schema), `OutputSchemaVersion` (capsule JSON shape), and `ExtractorVersion` (semantic fact derivation). Those cover storage and derivation; this covers the *invocation* shape.

Bump the version of each edge extractor whose output changes (`ExtractorConstants`, or an adapter's version). Bump `ExtractorVersion` for every other change to extraction output: declarations, symbol metadata, structural edges, binding-incompleteness rows. Both versions are part of the snapshot identity, so either bump produces a new snapshot.

It is surfaced programmatically so callers can check it without scraping help text:

- **CLI**: `status --json` / `status --output=json` includes `contract_version` alongside `schema_version` in every branch — never-indexed, snapshot-only, and freshness-checked. The human-readable `status` summary also prints `Contract version: N`.
- **MCP**: `lurp_status` includes `contract_version` at the top-level envelope and inside `detail` (mirroring `schema_version`). See `src/Mcp/Tools/StatusTool.cs` — the same pattern `StatusHandler` uses for `GetCurrentSchemaVersion`.

## What counts as breaking vs non-breaking

### Breaking (requires bumping `CliMcpContractVersion`)

- Removing a CLI mode (`--mode=...`) or an MCP tool (`lurp_*`).
- Removing a flag (`--foo=`, `--bar`) or a tool parameter (`foo`, `bar_baz`).
- Renaming a flag, a tool, a tool parameter, or a JSON field name in any response payload (CLI `--output=json` / `jsonl`, or the JSON text that an MCP tool returns: the `result` string of `structuredContent`, also in `content[0].text`).
- Changing the type of a flag / parameter / JSON field (e.g. `string` → `int`, `string` → `array`, `int` → `bool`).
- Changing default behavior (e.g. a flag that previously defaulted to `false` now defaults to `true`, a `limit` default changing from 20 to 50, a search that previously returned `all` now returning `symbol` only).
- Making an optional flag/param required, or tightening its accepted values (e.g. rejecting a previously accepted enum value).
- Changing the semantics of an existing flag/param without renaming it.

Breaking changes must bump `CliMcpContractVersion` by `+1`, update `tests/CliMcpContractSnapshotTests.cs` to reflect the new surface, and be noted in the commit/PR description. The test snapshot is the review gate for the **named** surface: a mode, flag, tool, or parameter that is silently added, removed, or renamed fails the build. JSON field paths and types for every CLI `--output=json`/`jsonl` mode and every MCP tool are covered by `tests/OutputContractSnapshotTests.cs` (`tests/Snapshots/output-contract.txt`); a field that is added, removed, renamed, or re-typed shows as a diff in that snapshot, for every shape the snapshot fixture produces. A shape the fixture does not produce (for example a `diff` `detail` of a change type it never makes) is not covered. One surface is deliberately excluded: `serve` (the MCP stdio transport; its payloads are the MCP tool payloads). `get-source` and the five source-text `get-symbol` views (`signature`, `body`, `declaration`, `containing-type`, `surrounding`) print raw text, so each is recorded as a single `$ text` line with no field shape. The generated sections of `docs/CLI_REFERENCE.md` and `README.md` (the flag tables, the MCP tool list and table, the version lines) are compared with the code by `tests/DocsInSyncTests.cs`. A change to a flag's value hint, allowed values, requirement or description, or to an MCP parameter's type or default, fails that test until the docs are regenerated. Defaults of CLI flags are part of the flag description, so a changed default relies on review.

### Non-breaking (does not bump the contract version, but still updates the snapshot)

- Adding a new CLI mode.
- Adding a new optional flag to an existing mode (with a backward-compatible default).
- Adding a new MCP tool.
- Adding a new optional parameter to an existing MCP tool (with a backward-compatible default).
- Adding a new JSON field to a response payload (additive; old fields remain with the same name/type/semantics).
- Adding a new enum value to an accepted-values set where the previous values remain valid.
- Rejecting a value that no document, tool description or test ever listed as accepted, and that an earlier version accepted only by accident (for example an empty string read as the default). The release notes must name the case. A value that appears in docs, tool descriptions or tests is not covered.
- Rejecting an MCP tool argument whose name the tool's input schema does not declare. The CLI rejects an unknown flag in the same way (`ERROR: unknown flag ...`). The release notes must name the case.

Non-breaking changes still require updating `tests/CliMcpContractSnapshotTests.cs` — the snapshot test makes the addition a visible, deliberate diff rather than a silent drift. The contract version stays the same.

## Process

1. Change `Program.ModeRegistry` (`src/Program.cs`; each flag is a `FlagSpec` from `src/FlagCatalog.cs`) or any `src/Mcp/Tools/*.cs` tool definition, or any code path that shapes JSON/JSONL output.
2. Run `dotnet test` — `CliMcpContractSnapshotTests` will fail with a diff showing the named-surface delta. `OutputContractSnapshotTests` is tagged `[Trait("Category", "Slow")]`, so `dotnet test` skips it; run it with `dotnet test tests/Lurp.Tests.csproj --filter "FullyQualifiedName~OutputContractSnapshotTests" --settings tests/full.runsettings`. It (`tests/Snapshots/output-contract.txt`) will fail with a diff showing any JSON field-path/type change across all CLI modes and MCP tools. Run `DocsInSyncTests` too (it needs no slow settings).
3. Decide whether the delta is breaking (see above). If breaking, bump `VersionConstants.CliMcpContractVersion` (`+1`).
4. Update the expected snapshot in `tests/CliMcpContractSnapshotTests.cs` to the new named surface and re-run tests. For `OutputContractSnapshotTests`, review the `.actual.txt` the test writes, copy it over `tests/Snapshots/output-contract.txt`, and re-run. Bump `CliMcpContractVersion` if the change is breaking. Regenerate the docs with `$env:LURP_UPDATE_DOCS = '1'; dotnet test tests/Lurp.Tests.csproj --filter "FullyQualifiedName~DocsInSyncTests"`, then read the diff of the docs. `DocExamplesInDocsTests` fails when a command in the docs no longer parses against the registry.
5. Document breaking bumps in the PR description (what was removed/renamed/re-typed and why). No migration is needed — callers compare `contract_version`.

## Non-goal

This does not freeze the CLI/MCP surface. Active development continues; the version exists to make future changes *visible and versioned*, not to block them. See `CONTRIBUTING.md` § CLI/MCP contract versioning.
