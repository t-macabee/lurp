# Lurp 4.1.0 (draft)

Run a full index after the upgrade.

## EF Core property accessors

Lurp gives non-public property accessors of mapped EF Core entities convention protection. This prevents a `proved_dead` verdict when EF Core can use the accessor. This protection also covers fields, properties and accessors that a mapped entity inherits from a class that is not mapped.

## Inherited EF Core entity sets

Lurp reads inherited `DbSet<T>` properties with the type arguments of the derived context. It records a `MapsTo` edge from that context to each inherited entity in the solution. These edges give the entity members EF Core convention protection.

## MCP tool results (CLI/MCP contract version 8)

Successful MCP tool calls return the payload object in `structuredContent`. Read payload fields directly from this object. Clients that used `structuredContent.result` must update their code. Clients can still parse the payload from `content[0].text`. Each tool lists the payload properties in its `outputSchema`. Tool errors use `isError: true` and a text message.

## Versions

| Component | 4.0.0 | 4.1.0 |
| --- | --- | --- |
| Tool | 4.0.0 | 4.1.0 |
| Database schema | v50 | v50 |
| Extractor | 1.12.0 | 1.13.0 |
| CLI/MCP contract | v7 | v8 |
| Output schema | v7 | v7 |
