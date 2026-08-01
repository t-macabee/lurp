CREATE TABLE schema_metadata (
    version INTEGER NOT NULL,
    applied_at_utc TEXT NOT NULL,
    migration_id TEXT NOT NULL
);
INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
VALUES (22, '2026-07-31T00:00:00.0000000Z', 'Migration_022_BindingIncompleteness');

CREATE TABLE snapshots (
    snapshot_id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    built_at_utc TEXT NOT NULL,
    sdk_version TEXT,
    compiler_version TEXT,
    database_schema_version INTEGER NOT NULL DEFAULT 0,
    output_schema_version INTEGER NOT NULL DEFAULT 0,
    extractor_version TEXT,
    tool_version TEXT,
    previous_snapshot_id TEXT,
    status TEXT NOT NULL DEFAULT 'in_progress',
    skipped_adapters TEXT NOT NULL DEFAULT ''
);
