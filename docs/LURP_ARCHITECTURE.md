# Lurp Architecture

**Status:** Design reference. All architecture phases are implemented — see
`TRUST_KERNEL.md` for per-phase verification, evidence, and current deviations.
**Current:** schema v26, extractor 1.6.0, tool 1.4.0
**Scope:** C#/.NET through Roslyn; local, compiler-grounded, read-only analysis

---

## 1. Purpose of This Document

This document describes Lurp's **design**: the product model, the data model,
the non-negotiable rules, and the two development tracks that were merged into
one system. It is a *what-and-why* reference, not a build roadmap.

For implementation status, verification evidence, gap audits, and recorded
deviations, see `TRUST_KERNEL.md`.

### 1.1 Current state

Lurp today:

> **A persistent, incremental semantic workspace mirror that combines
> compiler-derived relationships with immediate, snapshot-consistent source
> retrieval for software agents.**

It loads a .NET solution through Roslyn, discovers types and dependencies,
tracks stale semantic data, and assembles bounded context capsules for agent
tasks. All capabilities share one document identity, one symbol identity, one
workspace snapshot, one freshness model, one persistent database, and one query
interface.

It remains an indexer and context provider. It does not become an agent,
editor, dashboard, or autonomous refactoring system.

## 2. The Product Model

Four responsibilities:

| Responsibility | Question answered |
|---|---|
| **Catalog** | What code entities exist? |
| **Map** | How are those entities related? |
| **Fast travel** | What source belongs to this entity, right now? |
| **Context assembly** | What is the smallest sufficient neighborhood for this task? |

### 2.1 The central data model

```text
Snapshot
  ├─ immutable document contents
  ├─ semantic entities linked to exact source spans
  ├─ typed relationships between entities
  ├─ evidence and provenance for every relationship
  └─ structured changes from the previous snapshot
```

A semantic fact has this shape: `subject + relationship + object + source
location + provenance + snapshot identity + extractor version`. Example:

```text
UpdateCustomerHandler.Handle
    calls
ICustomerRepository.EmailExistsAsync

Evidence: compiler_proved
Location: UpdateCustomerHandler.cs, invocation span
Snapshot: workspace-0184
Extractor: invocation-v3
```

Source retrieval uses the same identity: `symbol ID → document version →
exact source span`. There is no translation layer between a symbol ID and its
source.

## 3. One Database Instead of Numerous JSON Files

### 3.1 Decision

Yes: one primary SQLite database (`index.db` in the output directory) replaces the
operational JSON files as the indexer's canonical persisted output. JSON
remains appropriate for CLI response output, portable exports, debugging
snapshots, test fixtures/golden files, and optional human inspection — it is
no longer the internal database or the coordination mechanism between parts of
the indexer.

### 3.2 Why SQLite fits this project

The indexer is a local single-binary CLI that needs many small indexed
lookups, atomic related-record updates, safe concurrent readers, no database
server, and explicit schema migrations. SQLite provides indexed lookup,
transactions, foreign keys, full-text search, and reliable atomic commits
without external infrastructure. A graph database would add operational
complexity without solving anything SQLite cannot handle at this scale; the
data model may be graph-shaped while storage stays relational.

### 3.3 The database is not the ultimate authority

1. **Repository source files** — authoritative source content.
2. **Roslyn compilation** — authoritative static semantics for a specific build configuration.
3. **`index.db`** — persistent materialized representation of a known workspace snapshot.
4. **Context capsule** — task-specific projection of database facts and source.
5. **Annotations** — human or agent interpretation, kept separate from compiler-derived facts.

The database is a single source of truth for the **indexed snapshot**, not a
replacement for the repository or compiler.

### 3.4 Recommended logical tables

Start with the minimum stable foundation; grow only when a roadmap stage
requires it.

| Table | Role |
|---|---|
| `schema_metadata` | Database format and migration state |
| `workspaces` | Repository and solution identity |
| `snapshots` | Indexed workspace versions and build configuration |
| `projects` | Project identity, target framework, references |
| `documents` | Logical source files and current document identity |
| `document_versions` | Immutable content hashes, source text, encoding |
| `symbols` | Types and members with stable semantic identity |
| `declarations` | Symbol-to-document spans and declaration metadata |
| `edges` | Typed semantic relationships |
| `facts` | Attributes and other non-edge semantic facts — **rejected** (stored in `metadata_json` instead; see TRUST_KERNEL.md §Architecture alignment) |
| `diagnostics` | Snapshot-bound compiler/build diagnostics |
| `semantic_changes` | Structured differences between snapshots |
| `annotations` | Explicitly non-compiler-authored knowledge |
| `extractors` | Extractor identity and version |

Full-text search via an FTS5 virtual table over indexed source content and
selected symbol names.

### 3.5 How source should be stored

Store complete document contents once per content hash; never make separately
copied method bodies the canonical representation:

```text
document → immutable document version → complete source text
symbol declaration → span inside that document version
```

This gives instant method-body retrieval without duplicating overlapping
source, and supports containing-type context, surrounding lines, partial
declarations, local functions, attributes, and historical comparisons.
Source text may be compressed later if measurement shows database size
matters; compression is not a first-stage concern.

## 4. The Two Development Tracks

**Track A — Fast Travel and Persistent Memory:** immediate retrieval without
reopening or reparsing for normal reads: persistent documents/source, stable
symbol-to-source links, lexical + symbol lookup, exact body/signature/
declaration retrieval, snapshot consistency, database-backed freshness.

**Track B — Deepening the Semantic Map:** member-level entities, typed
semantic edges, polymorphic dispatch candidates, structured semantic changes,
generated-code awareness, framework-mediated relationships, bounded impact
paths, task-specific context capsules.

Both tracks are implemented. Track A's storage foundation came first; then the
tracks interleaved. For the phase-by-phase build order and its completion
evidence, see `TRUST_KERNEL.md` §"Architecture Phase completion status".

---

# Design Reference

The following sections are the active design contract. For the build sequence
and its completion evidence, see `TRUST_KERNEL.md`.

---

## 5. Non-Negotiable Design Rules

### 5.1 Product boundary

A capability belongs in the core only if it materially improves: code
localization; exact source retrieval; semantic context selection; consequence
tracing; freshness; verification selection; representation of uncertainty.

### 5.2 Read-only relationship to source

May: read source; index source; report consequences. Must not: apply fixes;
compile hypothetical or in-memory modified solutions; rewrite the working tree;
decide architectural intent; call an LLM to make changes; become the worker
agent.

### 5.3 C#/.NET specialization

Remain Roslyn-native; do not weaken the semantic model for generic syntax
trees or multiple languages. Flutter/Dart, if ever needed, is a separate
analyzer whose facts may be joined at application boundaries — not a reason to
become a generic parser platform.

### 5.4 Fact before interpretation

The core records `implements MediatR.IRequest<CustomerDto>`; it does not claim
"query" as compiler-proved business intent. Project conventions may derive
such classifications with distinct provenance.

### 5.5 Unknown is a valid result

Do not eliminate `not_determinable` by guessing; make each blind spot the
strongest honest form available: compiler-proved relationship;
framework-derived relationship; possible target set; string/name candidate;
explicitly unknown runtime target.

### 5.6 Schema stability

Every field is a contract with future consumers:

- Never silently change a field's meaning.
- Add compatible optional fields only in minor schema revisions.
- Rename, removal, retyping, or semantic change requires a major revision.
- Store database schema, output schema, extractor version, and tool version separately.
- Test migrations from real prior database fixtures.
- Treat unknown enum values as survivable input.

## 23. Phases (reference)

The 17-phase build order that combined both tracks is defined in
`TRUST_KERNEL.md` §"Architecture Phase completion status". All phases are complete except Phase 16, whose deliverables (simulate and
audit modes) were withdrawn from the product in the Aug 2026 cleanup; see
`TRUST_KERNEL.md` for the row.

### 23.1 Historical milestones

- **After Phase 6:** one database; snapshot-bound source content; type/member
  lookup; immediate signature/body/declaration retrieval; lexical search
  without reparsing.
- **After Phase 10:** persistent fast travel; member-level semantic
  relationships; polymorphic paths; explainable changes between snapshots.
- **After Phase 15:** the system takes an anchor and produces a bounded package
  containing both the relevant semantic neighborhood and the actual code
  required to work there.

## 24. What Not to Build

Explicitly outside the core even when individually useful:

- **UI, dashboard, or visualization** — a separate consumer may render
  database queries; the indexer optimizes for correct facts and stable
  machine-readable responses.
- **Multi-language genericization** — Roslyn compiler semantics are the
  project's advantage; do not replace them with a lowest-common-denominator
  syntax model.
- **Autonomous editing** — the indexer is the map and retrieval system, not
  the hand changing the code.
- **LLM-authored canonical summaries** — interpretive summaries go stale and
  cannot be reproduced deterministically; if useful, store them as annotations
  with their own provenance and snapshot association, never as compiler facts.
- **General-purpose architecture scoring** — complexity, redundancy, and
  dead-code audits are not part of the product (the audit mode was withdrawn
  in the Aug 2026 cleanup); the indexer optimizes for correct facts and stable
  machine-readable retrieval, not authoritative judgments about code quality.
- **Change simulation / preflight** — the `simulate-rename|move|remove` family
  of modes was withdrawn in the Aug 2026 cleanup. Pre-computing the consequences
  of hypothetical source edits is outside core scope.
- **Premature daemon/server architecture** — begin with a local CLI and
  persistent SQLite store; a long-running service is justified only if measured
  Roslyn startup and incremental-update costs remain unacceptable after
  database-backed reads exist.

## 25. Validation Strategy

Judge by agent outcomes, not database size or indexed edge counts. The intended
benchmark set — drawn from real eNoteV2 tasks and previous agent failures —
covers eight scenarios: vague cleanup request; local validation change;
handler/DTO modification; cross-project interface change; apparently unused
type deletion; entity change affecting EF or serialization; DI implementation
replacement; route contract change. Three were implemented in
`tests/OutcomeBenchmarkTests.cs` (`local-validation-change`,
`handler-dto-modification`, `di-implementation-replacement`), but that test
file was deleted with the rest of the suite in the Aug 2026 cleanup (commit
`f1254fc`); those scenarios and their verification are not currently
reproducible pending a rebuilt suite.

Measure: correct starting symbol found? actual source retrieved without
project exploration? all constraining contracts included? affected callers and
implementations represented? relevant tests included? unknown runtime vectors
declared? how much irrelevant source entered the capsule? how many tokens the
worker required after receiving it? did the resulting patch avoid unrelated
changes? did incremental and clean indexing agree?

Every proposed major feature should fix a demonstrated failure in these
benchmarks or strengthen a foundational invariant.

## 26. Definition of the Definitive Version

The tool has reached its definitive conceptual form when:

- one SQLite database holds the current indexed workspace state;
- source documents and semantic facts share snapshot identity;
- symbols link directly to exact, retrievable code spans;
- ordinary reads do not require Roslyn reload;
- changed documents update the index incrementally;
- member-level typed edges power relationship queries;
- polymorphism and framework indirection retain honest evidence levels;
- generated semantics participate without flooding context;
- semantic diffs explain what changed;
- impact results are paths with reasons, not opaque scores;
- context capsules return bounded relevant code plus its surroundings;
- every fact states its provenance and extractor version;
- the indexer never becomes the actor modifying source.

The final one-line technical description:

> **A Roslyn-grounded, persistent and incremental semantic workspace mirror
> that provides software agents with immediate, evidence-backed, token-bounded
> access to both code and its relationships.**

## 27. Final Answer to the Sequencing Question

The missing fast-travel half is implemented first **at the foundation level**,
because both future source retrieval and the richer semantic map need the same
database, identities, spans, snapshots, and freshness rules. The implementation
is not two independent projects:

```text
shared identity and SQLite foundation
    → stored documents and symbol-linked fast travel
    → migrate current map into the database
    → deepen it into the full semantic graph
    → combine retrieval and graph traversal into context capsules
```

This preserves the existing indexer's capabilities, replaces Tokensave's
accidental role deliberately, and prevents either half from becoming a second
source of truth.
