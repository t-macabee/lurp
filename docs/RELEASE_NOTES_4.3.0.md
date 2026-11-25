# Lurp 4.3.0 (draft)

## Incremental indexing

- `TestAdapter` extracts only the bodies reached by the test methods in scope.
- Body-only edits load and extract only the projects that own a document in the extraction scope; untouched dependent projects keep their copied facts and diagnostics.
- Added `--skip-analyzers` on `index` (and `skip_analyzers` on `lurp_index`) to skip Roslyn analyzer diagnostics while keeping compiler diagnostics. Snapshots store `analyzers_skipped` (database schema v54), include it in the snapshot identity, prevent mixing on incremental runs, and report it in `status`, `lurp_status`, `diagnostics` and `lurp_diagnostics`.

### Changed descriptions

- `lurp_index`: documents the optional `skip_analyzers` parameter.
- `lurp_diagnostics`: documents `analyzers_skipped`.

## Versions

| Component | 4.2.0 | 4.3.0 |
| --- | --- | --- |
| Tool | 4.2.0 | 4.3.0 |
| Database schema | v53 | v54 |
| Extractor | 1.14.0 | 1.14.0 |
| CLI/MCP contract | v9 | v9 |
| Output schema | v8 | v8 |
