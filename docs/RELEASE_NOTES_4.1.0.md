# Lurp 4.1.0 (draft)

Run a full index after the upgrade.

## EF Core property accessors

Lurp gives non-public property accessors of mapped EF Core entities convention protection. This prevents a `proved_dead` verdict when EF Core can use the accessor.

## Inherited EF Core entity sets

Lurp reads inherited `DbSet<T>` properties with the type arguments of the derived context. It records a `MapsTo` edge from that context to each inherited entity in the solution. These edges give the entity members EF Core convention protection.

## Versions

| Component | 4.0.0 | 4.1.0 |
| --- | --- | --- |
| Tool | 4.0.0 | 4.1.0 |
| Database schema | v50 | v50 |
| Extractor | 1.12.0 | 1.13.0 |
| CLI/MCP contract | v7 | v7 |
| Output schema | v7 | v7 |
