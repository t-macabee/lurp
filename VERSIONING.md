# CLI / MCP Contract Versioning

`VersionConstants.CliMcpContractVersion` (`src/Workspace/VersionConstants.cs`) is the single integer that versions the externally-visible CLI and MCP surface that other agents integrate against.

It is intentionally separate from `DatabaseSchemaVersion` (SQLite schema), `OutputSchemaVersion` (capsule JSON shape), and `ExtractorVersion` (semantic fact derivation). Those cover storage and derivation; this covers the *invocation* shape.

It is surfaced programmatically so callers can check it without scraping help text:

- **CLI**: `status --json` / `status --output=json` includes `contract_version` alongside `schema_version` in every branch — never-indexed, snapshot-only, and freshness-checked. The human-readable `status` summary also prints `Contract version: N`.
- **MCP**: `lurp_status` includes `contract_version` at the top-level envelope and inside `detail` (mirroring `schema_version`). See `src/Mcp/Tools/StatusTool.cs` — the same pattern `StatusHandler` uses for `GetCurrentSchemaVersion`.

## What counts as breaking vs non-breaking

### Breaking (requires bumping `CliMcpContractVersion`)

- Removing a CLI mode (`--mode=...`) or an MCP tool (`lurp_*`).
- Removing a flag (`--foo=`, `--bar`) or a tool parameter (`foo`, `bar_baz`).
- Renaming a flag, a tool, a tool parameter, or a JSON field name in any response payload (CLI `--output=json` / `jsonl` or MCP structured content).
- Changing the type of a flag / parameter / JSON field (e.g. `string` → `int`, `string` → `array`, `int` → `bool`).
- Changing default behavior (e.g. a flag that previously defaulted to `false` now defaults to `true`, a `limit` default changing from 20 to 50, a search that previously returned `all` now returning `symbol` only).
- Making an optional flag/param required, or tightening its accepted values (e.g. rejecting a previously accepted enum value).
- Changing the semantics of an existing flag/param without renaming it.

Breaking changes must bump `CliMcpContractVersion` by `+1`, update `tests/CliMcpContractSnapshotTests.cs` to reflect the new surface, and be noted in the commit/PR description. The test snapshot is the review gate — a silent shape change fails the build.

### Non-breaking (does not bump the contract version, but still updates the snapshot)

- Adding a new CLI mode.
- Adding a new optional flag to an existing mode (with a backward-compatible default).
- Adding a new MCP tool.
- Adding a new optional parameter to an existing MCP tool (with a backward-compatible default).
- Adding a new JSON field to a response payload (additive; old fields remain with the same name/type/semantics).
- Adding a new enum value to an accepted-values set where the previous values remain valid.

Non-breaking changes still require updating `tests/CliMcpContractSnapshotTests.cs` — the snapshot test makes the addition a visible, deliberate diff rather than a silent drift. The contract version stays the same.

## Process

1. Change `Program.ModeRegistry` (`src/Program.cs`) or any `src/Mcp/Tools/*.cs` tool definition.
2. Run `dotnet test` — `CliMcpContractSnapshotTests` will fail with a diff showing the surface delta.
3. Decide whether the delta is breaking (see above). If breaking, bump `VersionConstants.CliMcpContractVersion` (`+1`).
4. Update the expected snapshot in `tests/CliMcpContractSnapshotTests.cs` to the new surface and re-run tests.
5. Document breaking bumps in the PR description (what was removed/renamed/re-typed and why). No migration is needed — callers compare `contract_version`.

## Non-goal

This does not freeze the CLI/MCP surface. Active development continues; the version exists to make future changes *visible and versioned*, not to block them. See `CONTRIBUTING.md` § CLI/MCP contract versioning.
