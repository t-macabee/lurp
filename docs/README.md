# Lurp reference documents

This folder contains the deeper material behind the [root README](../../README.md).
It is organized by purpose so visitors can choose the right level of detail.

Product intent and boundaries (what a code query can't tell you) live in the
root [`AGENTS.md`](../AGENTS.md) and [`CLAUDE.md`](../CLAUDE.md) — read those
first; they're loaded automatically for agents working in this repo. This
folder is on-demand deep reference: pull the section you need for a specific
question rather than reading a file end-to-end.

## Architecture and design

- [Lurp Architecture](LURP_ARCHITECTURE.md): the product model, storage
  boundaries, semantic graph, context capsules, and non-negotiable rules.

## Current implementation status

- [Trust Kernel](TRUST_KERNEL.md): the evidence-backed record of implemented
  capabilities, verified behavior, deliberate deviations, and postponed work.

## Operational CLI reference

- [CLI reference](../../src/README.md): commands, options, output shapes,
  snapshot lifecycle, and migration notes.

These references are not a task queue. Compare the Trust Kernel and
architecture guide, then inspect the live code and repository state. The live
repository is the final authority for what exists.
