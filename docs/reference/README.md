# Lurp reference documents

This folder contains the deeper material behind the [root README](../../README.md).
It is organized by purpose so visitors can choose the right level of detail.

## Architecture and design

- [Lurp Architecture](LURP_ARCHITECTURE.md): the product model, persistence
  boundaries, semantic graph, context capsules, and longer-term roadmap.
- [Development Search Tools](SEARCH_TOOLS.md): the repository discovery
  workflow used by contributors and agents.

## Current implementation status

- [Trust Kernel](TRUST_KERNEL.md): the evidence-backed record of implemented
  phases, verified behavior, deliberate deviations, and postponed work.

## Operational CLI reference

- [CLI reference](../../src/README.md): commands, options, output shapes,
  snapshot lifecycle, and migration notes.

## Historical investigations and implementation notes

- [Context-capsule completeness audit](CONTEXT_CAPSULE_COMPLETENESS_AUDIT.md):
  a historical investigation that records the evidence behind later capsule
  fixes. Read the Trust Kernel for current status.
- [Capsule provenance fix](CAPSULE_PROVENANCE_FIX.md): an implementation note
  describing how dispatch-mediated claims are presented and tested.

These references are not a task queue. Compare the Trust Kernel and
architecture guide, then inspect the live code, tests, and repository state.
The live repository is the final authority for what exists.
