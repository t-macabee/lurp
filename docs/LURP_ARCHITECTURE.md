# Lurp Architecture and Creation Roadmap

**Status:** Conceptual design guide. All §23 phases are implemented — see
`TRUST_KERNEL.md` for per-phase verification and current deviations.
**Starting point:** Lurp v1.1.0, schema v2
**Target:** A persistent semantic workspace mirror for agent-assisted .NET development
**Scope:** C#/.NET through Roslyn; local, compiler-grounded, read-only analysis

---

## 1. Purpose of This Document

The existing indexer already covers the first stage: it loads a .NET solution
through Roslyn, discovers types and dependencies, tracks stale semantic data,
performs structural audits, and simulates several refactorings without
modifying source. Two developments complete the original idea:

1. **Deepen the semantic map:** represent code relationships at the precision
   an agent needs for change reasoning.
2. **Map → Map with fast travel:** make every indexed location lead immediately
   to the actual source, without reparse/reopen per question.

These are separate capabilities but not separate products; they must share one
document identity, one symbol identity, one workspace snapshot, one freshness
model, one persistent database, and one query interface. The final system:

> **A persistent, incremental semantic workspace mirror that combines
> compiler-derived relationships with immediate, snapshot-consistent source
> retrieval for software agents.**

It remains an indexer and context provider. It does not become an agent,
editor, dashboard, or autonomous refactoring system.

## 2. The Correct Product Model

Four responsibilities:

| Responsibility | Question answered |
|---|---|
| **Catalog** | What code entities exist? |
| **Map** | How are those entities related? |
| **Fast travel** | What source belongs to this entity, right now? |
| **Context assembly** | What is the smallest sufficient neighborhood for this task? |

The current indexer substantially implements the catalog and an early
type-level map. Tokensave accidentally supplied fast travel during testing.
The definitive indexer incorporates that capability natively and then uses
both halves to assemble task context.

### 2.1 The central data model

Current abstraction: `Type → classification + incoming types + outgoing types`.
Target abstraction:

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
exact source span`. There is no permanent translation layer between a
Tokensave chunk ID and an indexer symbol ID.

## 3. One Database Instead of Numerous JSON Files

### 3.1 Decision

Yes: one primary SQLite database, `.codeaudit/index.db`, replaces the
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
| `facts` | Attributes and other non-edge semantic facts |
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

### 4.1 Which gets implemented first?

**Track A's storage foundation comes first; then the tracks interleave.**
Do not fully complete the old type-level indexer in JSON and migrate later; do
not build the entire source cache before member identities either. Order:

1. establish schema and snapshot contracts;
2. introduce SQLite and store document contents;
3. introduce stable type/member identities and source spans;
4. deliver fast retrieval;
5. migrate existing indexer facts and workflows into the database;
6. deepen the graph stage by stage;
7. build context assembly on top of both.

This avoids two expensive mistakes: enriching a JSON representation that will
immediately be replaced, and creating a source cache whose chunk identities do
not match Roslyn symbols.

## 5. Non-Negotiable Design Rules

### 5.1 Product boundary

A capability belongs in the core only if it materially improves: code
localization; exact source retrieval; semantic context selection; consequence
tracing; freshness; verification selection; representation of uncertainty.

### 5.2 Read-only relationship to source

May: read source; index source; simulate changes in memory; compile
hypothetical solutions; report consequences. Must not: apply fixes; rewrite
the working tree; decide architectural intent; call an LLM to make changes;
become the worker agent.

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

---

# Part I: Build the Missing Fast-Travel Half

Each stage: **Objective** — what it proves; **Key decisions** — what to build;
**Done when** — the acceptance condition.

## 6. Stage A0: Define Identity and Version Contracts

**Objective:** every persisted item describes exactly one version of one
workspace under one compilation configuration.

**Key decisions:** four distinct identities:

| Identity | Purpose |
|---|---|
| `workspace_id` | Repository + solution identity |
| `snapshot_id` | One indexed state of that workspace |
| `document_id` | Stable logical file identity |
| `document_version_id` | Immutable content version identified by hash |

`document_id` is path-scoped and constant across snapshots; it is **not**
snapshot-scoped. A snapshot binds a document to a specific
`document_version_id` (via `snapshot_documents`), giving each snapshot an
isolated, immutable view of content. See `TRUST_KERNEL.md` for the
schema-level confirmation of this reading.

A snapshot must record: git root and solution identity; source document
hashes; SDK and compiler version; target framework and relevant MSBuild
properties; project-reference graph; indexer and extractor versions.

Freshness is content- and configuration-based, never time-based — no age rule
such as "baseline newer than 24 hours."

**Done when:** the tool can state whether the database represents the current
workspace and explain every mismatch.

## 7. Stage A1: Introduce SQLite Without Removing Existing Behavior

**Objective:** durable storage while keeping current commands functional.

**Key decisions:** a small storage boundary inside the indexer, not arbitrary
SQL from mode handlers:

```text
Extraction and analysis → Index store interface → SQLite implementation
```

The store owns: transactions; migrations; insert/update/delete operations;
indexed queries; snapshot activation; consistency checks. Existing JSON may be
written as compatibility output during migration, but must not remain a
parallel authority.

**Done when:** a database can be created, migrated, opened, validated, and
rebuilt without changing the current analytical results.

## 8. Stage A2: Store Immutable Documents

**Objective:** current source content available immediately without Roslyn
startup.

**Key decisions:** for every indexed document: (1) normalize path identity
without rewriting source text; (2) calculate a content hash; (3) store the
complete source once for that hash; (4) associate it with the active snapshot;
(5) retain encoding and line-start information. Line-start offsets must be
stored or cheaply derivable to translate between Roslyn spans and
line/column. Generated documents are representable but marked separately from
user-authored documents.

**Done when:** given a file identity and current snapshot, the CLI returns
exact source without loading the solution.

## 9. Stage A3: Link Symbols to Exact Source Spans

**Objective:** source caching becomes semantic fast travel, not a second
text-chunk system.

**Key decisions:** persist Roslyn-derived type and member identities with
declaration spans: full declaration span; signature span where practical;
body span where one exists; identifier span; declaring document version;
partial-declaration relationships. Stable identity is based on Roslyn semantic
identity and project/assembly context, not only an FQN — FQNs remain useful
lookup aliases but are not sufficient durable IDs across moves, overloads, and
project collisions.

**Done when:** given a symbol, the system returns metadata only, signature,
body, full declaration, containing type, or surrounding lines — all views from
the same document version used to extract the symbol.

## 10. Stage A4: Add Dual Retrieval

**Objective:** fast travel both when the agent knows the destination and when
it only knows a word or concept.

**Key decisions:**

- **Symbol retrieval:** indexed symbol identities and aliases for exact
  queries: `get symbol → retrieve stored span`.
- **Lexical retrieval:** SQLite FTS5 over identifier fragments (any
  case-insensitive substring of a symbol's FQN: whole tokens, camel-case
  segments, prefixes all qualify — a query of `Service` matches
  `CourseService`; implemented as whole-token FTS5 matching with a substring
  fallback when no whole token matches), source literals, route strings,
  attributes, comments when explicitly requested, filenames and paths.

Lexical search finds candidate locations; semantic traversal (built later)
explains their neighborhood.

**Done when:** "where is this and what does it contain?" no longer requires
filesystem search or Roslyn solution loading.

## 11. Stage A5: Replace JSON Coordination with Database State

**Objective:** SQLite is the only operational state store.

**Key decisions:**

| Existing artifact | Database replacement |
|---|---|
| `dirty-files.json` | changed-document/snapshot staging records |
| `file-index.json` | indexed document-to-symbol relationships |
| `semantic/*.semantic.json` | reproducible facts plus separate annotations |
| cached roots JSON | indexed semantic edges and materialized queries |
| diagnostic baseline | snapshot-bound diagnostic records |
| audit scaffolds | optional exported reports, not core state |

Keep a temporary import path for existing `.codeaudit` data only if it
contains irreplaceable annotations; reproducible compiler facts should be
rebuilt, not migrated indefinitely.

**Done when:** deleting operational JSON files removes no capability; JSON is
generated only as requested output or export.

---

# Part II: Deepening the Semantic Graph

## 12. Stage B0: Establish the Semantic Fact Model

**Objective:** replace coarse type dependency lists with a durable graph-shaped
fact model.

**Key decisions:** first-class nodes for projects, documents, namespaces,
types, members, parameters where needed, framework entry points, registrations,
tests. Explicit edges such as `declares`, `contains`, `inherits`, `implements`,
`overrides`, `calls`, `constructs`, `reads`, `writes`, `returns`, `throws`,
`references`, `tested_by`. Every edge records: source and target identity;
source location; provenance; extractor and version; snapshot membership.
Existing `discover`, `structure`, and `who-references` output become
projections of this fact model.

**Done when:** no core analysis needs `OutgoingTypeNames` as its primary
relationship representation.

## 13. Stage B1: Add Member-Level Compiler Relationships

**Objective:** the resolution required for targeted changes.

**Key decisions:** implement in this order: (1) inheritance, interface
implementation, overrides; (2) member calls; (3) object construction;
(4) field/property reads and writes; (5) parameter and return-type
dependencies; (6) attributes; (7) thrown exception and result relationships
where statically available; (8) production-to-test references. Do not attempt
exhaustive formal data-flow analysis — the purpose is reliable navigation and
bounded impact, not proof of every runtime value.

**Done when:** for a changed method, the indexer identifies the compiler-visible
callers, contracts, implementations, consumed members, modified state, and
relevant tests without inflating everything to the containing type.

## 14. Stage B2: Model Polymorphism Honestly

**Objective:** represent interface and virtual dispatch without claiming
runtime certainty that does not exist.

**Key decisions:** keep separate facts:

```text
Handler.Handle
  statically_calls
IRepository.SaveAsync

IRepository.SaveAsync
  may_dispatch_to
Repository.SaveAsync
```

Framework registration evidence may narrow candidates but remains a separate
edge; consumers can derive likely paths while retaining the derivation.

**Done when:** interface-mediated impact paths are visible, and every concrete
target states whether it is proven, framework-derived, or merely possible.

## 15. Stage B3: Implement Structured Semantic Diffing

**Objective:** replace "something changed" with "this semantic fact changed."

**Key decisions:** keep hashes as cheap change detectors, but compare stored
canonical facts to explain the difference. Record changes such as: type/member
added or removed; symbol renamed or moved; accessibility changed; signature or
nullability changed; base type/interface changed; attribute changed;
dependency edge added or removed; call/read/write edge changed; DI
registration or route changed; implementation-only body change. Never derive a
semantic diff from hashes alone — retain the previous canonical fact set or
snapshot.

**Done when:** `sweep` and `impact` explain the semantic cause of
invalidation, not only report fingerprint mismatch.

## 16. Stage B4: Index Generated Semantics

**Objective:** stop treating generated code as nonexistent while avoiding
source-context pollution.

**Current limitation:** `MSBuildWorkspace.OpenSolutionAsync` does not provide
source-generator output; generator-produced documents are absent from the
compilations exposed to the indexer. The snapshot manifest records this via
`generated_trees_included: false`. A future generator-driver stage closes the
gap.

**Key decisions:** index generated symbols/relationships when Roslyn exposes
them; mark generated document status, generator identity when available,
whether a declaration is user-authored or generated, and references crossing
the authored/generated boundary. Exclude generated source bodies from normal
agent context unless directly relevant or explicitly requested.

**Done when:** generated symbols participate in dependency and impact queries
without automatically consuming agent tokens.

## 17. Stage B5: Add Framework Adapters

**Objective:** turn important framework-mediated blind spots into
evidence-graded relationships.

**Key decisions:** add adapters in the order most relevant to eNoteV2 and
similar .NET applications: (1) ASP.NET Core routes/controllers/actions/endpoint
metadata; (2) built-in DI service-to-implementation registration and lifetime;
(3) MediatR request/notification-to-handler; (4) EF Core entities,
configurations, `DbSet` exposure, migrations where relevant; (5) serialization
DTO/property contract participation; (6) test frameworks (test-to-production
relationships and discovery metadata). Adapters emit ordinary typed facts into
the shared model — no parallel cache. For DI, distinguish: explicit recognized
registration; registration derived by following local helper methods;
convention-based candidate set; external/runtime registration unknown.

**Done when:** the indexer describes the common application path

```text
HTTP route → action → request/handler → interface call
→ registered implementation → persistence operation → relevant tests
```

while preserving the evidence level of each step.

## 18. Stage B6: Add a Reflection Evidence Ladder

**Objective:** capture statically visible reflection without pretending
arbitrary runtime reflection is solved.

**Key decisions:** separate relationships: `reflection_type_reference` for
`typeof(T)`; `reflection_member_reference` for semantically resolvable
`nameof` patterns; `reflection_name_candidate` for string literals;
`reflection_target_unknown` for runtime-computed names or external discovery.

**Done when:** reflection blind spots are narrower and more explicit, not
falsely eliminated.

## 19. Stage B7: Build Impact Paths

**Objective:** convert isolated facts into explainable routes through the
codebase.

**Key decisions:** impact returns paths, not an undifferentiated set of
"related" symbols:

```text
CustomerDto.Email changed
  ← consumed by UpdateCustomerCommand
  ← handled by UpdateCustomerHandler
  ← invoked by CustomerController.Update
  ← exposed by PUT /customers/{id}
```

Each hop retains its evidence. Traversal has: direction; allowed edge types;
depth limit; project boundary control; source inclusion control; cycle
detection; explanation for truncation. Avoid universal numeric "impact
scores" — explicit paths and tiers are more inspectable.

**Done when:** an agent can see why a code location is considered affected and
inspect the exact source at every hop.

---

# Part III: Unite Both Halves

## 20. Stage C0: Context Capsules

**Objective:** the original product: immediate, bounded, evidence-backed
codebase context for an agent task.

**Key decisions:** the first version accepts concrete anchors, not
natural-language task understanding inside the indexer:

```text
context --symbol X --intent inspect
context --symbol X --intent modify
context --file F --line N --intent diagnose
```

The middle-layer agent translates a vague human task into anchor candidates;
the indexer deterministically expands and retrieves context from those
anchors. A capsule contains: anchor symbols; likely change sites; exact source
spans; relevant contracts; incoming and outgoing paths; registered or possible
runtime targets; affected public surfaces; relevant tests; uncertainties and
unchecked vectors; suggested verification commands; the reason every item was
included.

### 20.1 Token budgeting

Context generation accepts an explicit budget and degrades predictably.
Priority: (1) anchor declaration/body; (2) directly constraining contracts;
(3) directly invoked members; (4) direct callers and entry points;
(5) registered implementations; (6) relevant tests; (7) second-degree context;
(8) optional surrounding source. When information is omitted, the capsule says
what category was truncated and why.

**Done when:** the agent receives enough code and semantic context to begin
the task without repository-wide exploration, within a predictable token
budget.

## 21. Stage C1: Convert Existing Features into Consumers

**Objective:** one core, not several implementations sharing a CLI.

**Key decisions:**

| Existing mode | Final role |
|---|---|
| `discover` | Symbol catalog projection |
| `structure` | Local semantic-neighborhood projection |
| `who-references` | Reference-edge query with source sites |
| `fingerprint` | Snapshot/change optimization and compatibility query |
| `sweep` | Incremental snapshot update and invalidation |
| `impact` | Evidence-backed path query |
| `simulate-*` | In-memory change preflight consuming stored context |
| `audit` | Optional heuristic consumer |

Simulations may load Roslyn (they create hypothetical compilations); ordinary
lookup and context retrieval should not.

**Done when:** one fact store, one freshness model, one identity system behind
all modes.

## 22. Stage C2: Incremental Operation

**Objective:** the persistent mirror stays current cheaply during real
development.

**Key decisions:** when nothing changed, serve all read queries from SQLite.
When files change: (1) hash candidate documents; (2) create new immutable
document versions; (3) identify affected projects; (4) update the required
Roslyn compilation; (5) delete facts produced from invalid
declarations/documents; (6) extract replacement facts; (7) update reverse
edges and full-text search; (8) compute semantic changes; (9) commit the new
snapshot atomically. A clean rebuild remains the recovery and verification
mechanism; development tests compare incremental output with clean rebuild
output.

**Done when:** repeated reads are database-fast, ordinary edits cause bounded
updates, and a clean rebuild produces the same canonical current facts.

## 23. Unified Creation Order

The actual implementation roadmap, combining both tracks in dependency order.
(All phases complete — see `TRUST_KERNEL.md`.)

| Phase | Build | Why now |
|---:|---|---|
| 1 | Product constitution and schema/version rules | Prevents output drift before new persistence exists |
| 2 | Workspace, snapshot, document, and configuration identities | Everything else depends on freshness and identity |
| 3 | SQLite storage boundary and migrations | Establishes the final persistence foundation |
| 4 | Immutable document versions and source storage | Supplies the missing persistent memory |
| 5 | Stable type/member identities and declaration spans | Joins semantic map to actual code |
| 6 | Fast `get` and lexical `search` queries | Delivers the first usable fast-travel result |
| 7 | Migrate dirty state, fingerprints, diagnostics, and existing facts | Removes dual JSON/SQLite authority |
| 8 | Typed member-level semantic edges | Begins the deep semantic-graph work |
| 9 | Polymorphism and dispatch candidates | Closes the largest compiler-visible graph gap |
| 10 | Structured semantic snapshot diffs | Gives temporal precision and better invalidation |
| 11 | Generated-code provenance | Restores semantic facts currently hidden by exclusions |
| 12 | ASP.NET, DI, MediatR, EF, serialization, and test adapters | Adds framework-visible application paths |
| 13 | Reflection evidence ladder | Narrows the remaining important dynamic blind spot |
| 14 | Evidence-backed impact paths | Turns facts into navigable consequence routes |
| 15 | Context capsules with source and token budgets | Completes the original map-plus-fast-travel vision |
| 16 | Rebase simulations and audits on the shared store | Consolidates the product without changing its core identity |
| 17 | Optimize incremental updates from measurements | Avoids premature complexity while achieving daily-use speed |

### 23.1 First meaningful milestone (after Phase 6)

One database; snapshot-bound source content; type/member lookup; immediate
signature/body/declaration retrieval; lexical search without reparsing. At
this point the system can begin replacing Tokensave operationally.

### 23.2 Second meaningful milestone (after Phase 10)

Persistent fast travel; member-level semantic relationships; polymorphic
paths; explainable changes between snapshots. The first proper
deep-semantic core.

### 23.3 Completion milestone (after Phase 15)

The system takes an anchor and produces a bounded package containing both the
relevant semantic neighborhood and the actual code required to work there.
This completes the original concept.

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
- **General-purpose architecture scoring** — complexity/redundancy/dead-code
  checks may remain optional candidate generators, not authoritative judgments
  or the organizing center.
- **Premature daemon/server architecture** — begin with a local CLI and
  persistent SQLite store; a long-running service is justified only if measured
  Roslyn startup and incremental-update costs remain unacceptable after
  database-backed reads exist.

## 25. Validation Strategy

Judge by agent outcomes, not database size or indexed edge counts. Build a
benchmark set from real eNoteV2 tasks and previous agent failures: vague
cleanup request; local validation change; handler/DTO modification;
cross-project interface change; apparently unused type deletion; entity change
affecting EF or serialization; DI implementation replacement; route contract
change.

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
- simulations and audits consume the common model;
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
