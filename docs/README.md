# Lurp reference documents

This folder contains the deeper material behind the [root README](../../README.md).
It is organized by purpose so visitors can choose the right level of detail.

## Architecture and design

- [Lurp Architecture](LURP_ARCHITECTURE.md): the product model, persistence
  boundaries, semantic graph, context capsules, and longer-term roadmap.

## Current implementation status

- [Trust Kernel](TRUST_KERNEL.md): the evidence-backed record of implemented
  phases, verified behavior, deliberate deviations, and postponed work.

## Operational CLI reference

- [CLI reference](../../src/README.md): commands, options, output shapes,
  snapshot lifecycle, and migration notes.

These references are not a task queue. Compare the Trust Kernel and
architecture guide, then inspect the live code and repository state. The live
repository is the final authority for what exists. (The `tests/` tree and all
fixtures were removed in the Aug 2026 cleanup, commit `f1254fc`; no test
project is present until the suite is rebuilt.)
