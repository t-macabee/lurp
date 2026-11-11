# Lurp 4.0.0 (draft)

This file is the draft of the 4.0.0 changes. 4.0.0 is not released. Each change
of the fix plan adds its line here in the commit that makes it.

## Upgrade from 3.6.1

The extractor version changes, so every snapshot identity changes. Run one full
index after the upgrade. Old snapshots stay until they are pruned.

## Extraction

- A failing extraction stage no longer removes the project from the snapshot.
  Only the declaration stage is required. When another stage (member edges,
  structural edges, polymorphism, reflection or an adapter) throws, the facts of
  the other stages are saved, each document of the project gets an
  `extractor_failure` binding-incompleteness row, and the index run prints a
  warning.
- One operation pass writes the member-body facts: `Calls`, `Constructs`,
  `Reads`, `Writes`, `MethodGroupRef`, `ExtensionReceiver` and body
  `References`. Their extractor label is `operation-shapes-v2`. The labels
  `calls-v2`, `constructs-v1`, `readswrites-v1` and `extension-receiver-v1` are
  gone. New facts: the `ExtensionReceiver` edge and the receiver-type constraints
  of calls in top-level statements and initializers, and the `ExtensionReceiver`
  edge of an extension `Add` in a collection initializer.
- The `occurrence_count` of a `binding_incompleteness` row is the number of
  distinct binding sites (document, node span, reason). A site that several
  extractors see counts once. Before, a call that did not resolve was also counted
  for its member name, and a call to an external method was counted once for each
  extractor and once more for each external type argument.

## Versions

| Component | 3.6.1 | 4.0.0 |
|---|---|---|
| Tool | 3.6.1 | 4.0.0 |
| Database schema | v37 | v37 |
| Extractor | 1.11.0 | 1.12.0 |
| CLI/MCP contract | v6 | v6 |
| Output schema | v6 | v6 |
