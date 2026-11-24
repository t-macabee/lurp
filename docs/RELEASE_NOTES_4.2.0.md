# Lurp 4.2.0 (draft)

## Declaration spans and search (CLI/MCP contract version 9)

Declaration locations include attached comments and end on the line containing the last declaration token. Outline and navigation use the same line boundaries as symbol metadata. Indexing a multi-targeted project keeps each distinct conditional declaration span and collapses copies with identical spans.

Symbol search ranks exact simple names, name tokens and prefixes, simple-name substrings, then containing or fully qualified names. Within each tier, production symbols precede test symbols and types precede members. The search query and cursor flag descriptions and the `lurp_search` tool description reflect the new order.

Start a fresh page if you have a search or outline cursor from an earlier version. The database migration rebuilds symbol search for stored snapshots. Run a full index to extract the new declaration spans. Use `--force` if you already built a development snapshot with extractor 1.14.0.

## Versions

| Component | 4.1.0 | 4.2.0 |
| --- | --- | --- |
| Tool | 4.1.0 | 4.2.0 |
| Database schema | v50 | v52 |
| Extractor | 1.13.0 | 1.14.0 |
| CLI/MCP contract | v8 | v9 |
| Output schema | v7 | v7 |
