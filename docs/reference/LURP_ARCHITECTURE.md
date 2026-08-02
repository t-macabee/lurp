# Lurp Architecture and Creation Roadmap

**Status:** Conceptual design guide  
**Starting point:** Lurp v1.1.0, schema v2  
**Target:** A persistent semantic workspace mirror for agent-assisted .NET development  
**Scope:** C#/.NET through Roslyn; local, compiler-grounded, read-only analysis

---

## 1. Purpose of This Document

The existing indexer already covers the first stage: it loads a .NET solution through Roslyn, discovers types and dependencies, tracks stale semantic data, performs structural audits, and simulates several refactorings without modifying source.

Two developments are still required to realize the original idea completely:

1. **Deepen the semantic map:** represent code relationships at the precision an agent needs for change reasoning.
2. **Map → Map with fast travel:** make every indexed location lead immediately to the actual source code, without making the agent rediscover or reparse the project for every question.

These are separate capabilities, but they are not separate products. They must share:

- one document identity;
- one symbol identity;
- one workspace snapshot;
- one freshness model;
- one persistent database;
- one query interface.

The final system is therefore:

> **A persistent, incremental semantic workspace mirror that combines compiler-derived relationships with immediate, snapshot-consistent source retrieval for software agents.**

It remains an indexer and context provider. It does not become an agent, editor, dashboard, or autonomous refactoring system.

---

## 2. The Correct Product Model

The finished system has four responsibilities.

| Responsibility | Question answered |
|---|---|
| **Catalog** | What code entities exist? |
| **Map** | How are those entities related? |
| **Fast travel** | What source belongs to this entity, right now? |
| **Context assembly** | What is the smallest sufficient neighborhood for this task? |

The current indexer substantially implements the catalog and an early type-level map. Tokensave accidentally supplied fast travel during testing. The definitive indexer incorporates that capability natively and then uses both halves to assemble task context.

### 2.1 The central data model

The current abstraction is approximately:

```text
Type → classification + incoming types + outgoing types
```

The target abstraction is:

```text
Snapshot
  ├─ immutable document contents
  ├─ semantic entities linked to exact source spans
  ├─ typed relationships between entities
  ├─ evidence and provenance for every relationship
  └─ structured changes from the previous snapshot
```

A semantic fact has this conceptual shape:

```text
subject + relationship + object
+ source location
+ provenance
+ snapshot identity
+ extractor version
```

Example:

```text
UpdateCustomerHandler.Handle
    calls
ICustomerRepository.EmailExistsAsync

Evidence: compiler_proved
Location: UpdateCustomerHandler.cs, invocation span
Snapshot: workspace-0184
Extractor: invocation-v3
```

The source retrieval layer uses the same identity:

```text
symbol ID → document version → exact source span
```

There is no permanent translation layer between a Tokensave chunk ID and an indexer symbol ID.

---

## 3. One Database Instead of Numerous JSON Files

### 3.1 Decision

Yes. The target architecture should use one primary SQLite database:

```text
.codeaudit/index.db
```

This database replaces the collection of operational JSON files as the indexer's canonical persisted output.

JSON does not have to disappear completely. It remains appropriate for:

- CLI response output;
- portable exports;
- debugging snapshots;
- test fixtures and golden files;
- optional human inspection.

JSON should no longer be the internal database or the mechanism through which different parts of the indexer coordinate.

### 3.2 Why SQLite fits this project

SQLite is the best fit here because the indexer is:

- a local single-binary CLI;
- expected to support many small indexed lookups;
- expected to update related records atomically;
- expected to serve concurrent readers safely;
- not intended to require a database server;
- expected to evolve through explicit schema migrations.

It provides indexed lookup, transactions, foreign keys, full-text search, and reliable atomic commits without introducing external infrastructure.

An actual graph database would add operational complexity without solving a problem SQLite cannot handle at this project’s scale. The data model may be graph-shaped while the storage remains relational.

### 3.3 The database is not the ultimate authority

The authority hierarchy remains:

1. **Repository source files** — authoritative source content.
2. **Roslyn compilation** — authoritative static semantics for a specific build configuration.
3. **`index.db`** — persistent materialized representation of a known workspace snapshot.
4. **Context capsule** — task-specific projection of database facts and source.
5. **Annotations** — human or agent interpretation, kept separate from compiler-derived facts.

The database is a single source of truth for the **indexed snapshot**, not a replacement for the repository or compiler.

### 3.4 Recommended logical tables

The schema should begin with the minimum stable foundation and grow only when a roadmap stage requires it.

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

Full-text search can be supplied by an FTS5 virtual table over indexed source content and selected symbol names.

### 3.5 How source should be stored

Store complete document contents once per content hash. Do not make separately copied method bodies the canonical representation.

```text
document
  → immutable document version
      → complete source text

symbol declaration
  → span inside that document version
```

This design provides instant method-body retrieval without duplicating overlapping source. It also supports containing-type context, surrounding lines, partial declarations, local functions, attributes, and historical comparisons.

Source text may be compressed later if measurement shows the database size matters. Compression is not a first-stage concern.

---

## 4. The Two Development Tracks

The two tracks are conceptually distinct.

### Track A — Fast Travel and Persistent Memory

This track makes indexed code immediately retrievable without reopening or reparsing the solution for normal read queries.

It supplies:

- persistent documents and source content;
- stable symbol-to-source links;
- lexical and symbol lookup;
- exact body/signature/declaration retrieval;
- snapshot consistency;
- database-backed freshness.

### Track B — Deepening the Semantic Map

This track deepens what the map knows.

It supplies:

- member-level entities;
- typed semantic edges;
- polymorphic dispatch candidates;
- structured semantic changes;
- generated-code awareness;
- framework-mediated relationships;
- bounded impact paths;
- task-specific context capsules.

### 4.1 Which gets implemented first?

**Track A’s storage foundation comes first. Then Track A and Track B interleave.**

Do not fully complete the old type-level indexer in JSON and migrate later. Do not build the entire source cache before introducing member identities either.

The correct order is:

1. establish schema and snapshot contracts;
2. introduce SQLite and store document contents;
3. introduce stable type/member identities and source spans;
4. deliver fast retrieval;
5. migrate existing indexer facts and workflows into the database;
6. deepen the graph stage by stage;
7. build context assembly on top of both.

This order avoids two expensive mistakes:

- enriching a JSON representation that will immediately be replaced;
- creating a source cache whose chunk identities do not match Roslyn symbols.

---

## 5. Non-Negotiable Design Rules

These rules prevent the system from accumulating unrelated attachments.

### 5.1 Product boundary

A capability belongs in the core only if it materially improves:

- code localization;
- exact source retrieval;
- semantic context selection;
- consequence tracing;
- freshness;
- verification selection;
- representation of uncertainty.

### 5.2 Read-only relationship to source

The indexer may:

- read source;
- index source;
- simulate changes in memory;
- compile hypothetical solutions;
- report consequences.

It must not:

- apply fixes;
- rewrite the working tree;
- decide architectural intent;
- call an LLM to make changes;
- become the worker agent.

### 5.3 C#/.NET specialization

Remain Roslyn-native. Do not weaken the semantic model to support generic syntax trees or multiple languages.

If Flutter/Dart integration is needed later, it should be a separate analyzer whose facts may be joined at application boundaries. It should not turn this indexer into a generic parser platform.

### 5.4 Fact before interpretation

The core records:

```text
implements MediatR.IRequest<CustomerDto>
```

It does not claim “query” as compiler-proved business intent. Project conventions may derive such a classification with distinct provenance.

### 5.5 Unknown is a valid result

The objective is not to eliminate `not_determinable` by guessing. Each blind spot should become the strongest honest form available:

- compiler-proved relationship;
- framework-derived relationship;
- possible target set;
- string/name candidate;
- explicitly unknown runtime target.

### 5.6 Schema stability

Every field is a contract with future consumers.

- Never silently change a field’s meaning.
- Add compatible optional fields only in minor schema revisions.
- Rename, removal, retyping, or semantic change requires a major revision.
- Store database schema, output schema, extractor version, and tool version separately.
- Test migrations from real prior database fixtures.
- Treat unknown enum values as survivable input.

---

# Part I — Build the Missing Fast-Travel Half

## 6. Stage A0 — Define Identity and Version Contracts

### Objective

Ensure every persisted item describes exactly one version of one workspace under one compilation configuration.

### What fits this project best

Use four distinct identities:

| Identity | Purpose |
|---|---|
| `workspace_id` | Repository + solution identity |
| `snapshot_id` | One indexed state of that workspace |
| `document_id` | Stable logical file identity |
| `document_version_id` | Immutable content version identified by hash |

`document_id` is path-scoped and constant across snapshots; it is **not**
snapshot-scoped. A snapshot binds a document to a specific
`document_version_id` (via `snapshot_documents`), which is what gives each
snapshot an isolated, immutable view of content — the document's identity
itself does not vary per snapshot. See
the current-status summary in `TRUST_KERNEL.md` for
the schema-level confirmation of this reading.

A snapshot must record at least:

- Git root and solution identity;
- source document hashes;
- SDK and compiler version;
- target framework and relevant MSBuild properties;
- project-reference graph;
- indexer and extractor versions.

Do not use an age rule such as “baseline newer than 24 hours.” Freshness is content- and configuration-based, not time-based.

### Completion condition

The tool can state whether the database represents the current workspace and explain every mismatch.

---

## 7. Stage A1 — Introduce SQLite Without Removing Existing Behavior

### Objective

Establish the durable storage layer while keeping current commands functional.

### What fits this project best

Add a small storage boundary inside the indexer rather than allowing mode handlers to issue arbitrary SQL.

Conceptually:

```text
Extraction and analysis
        ↓
Index store interface
        ↓
SQLite implementation
```

The store should own:

- transactions;
- migrations;
- insert/update/delete operations;
- indexed queries;
- snapshot activation;
- consistency checks.

During migration, existing JSON may be written as compatibility output. It should not remain a parallel authority.

### Completion condition

A database can be created, migrated, opened, validated, and rebuilt without changing the current analytical results.

---

## 8. Stage A2 — Store Immutable Documents

### Objective

Make current source content available immediately without Roslyn startup.

### What fits this project best

For every indexed document:

1. normalize path identity without rewriting source text;
2. calculate a content hash;
3. store the complete source once for that hash;
4. associate it with the active snapshot;
5. retain encoding and line-start information.

Line-start offsets should be stored or cheaply derivable so the system can translate between Roslyn spans and human line/column locations.

Generated documents should also be representable, but marked separately from user-authored documents.

### Completion condition

Given a file identity and current snapshot, the CLI can return exact source without loading the solution.

---

## 9. Stage A3 — Link Symbols to Exact Source Spans

### Objective

Turn source caching into semantic fast travel rather than a second text-chunk system.

### What fits this project best

Persist Roslyn-derived type and member identities with declaration spans:

- full declaration span;
- signature span where practical;
- body span where one exists;
- identifier span;
- declaring document version;
- partial-declaration relationships.

Stable identity should be based on Roslyn semantic identity and project/assembly context, not only a fully qualified name. FQNs remain useful lookup aliases but are not sufficient durable IDs across moves, overloads, and project collisions.

### Completion condition

Given a symbol, the system can return:

- metadata only;
- signature;
- body;
- full declaration;
- containing type;
- surrounding lines.

All views must come from the same document version used to extract the symbol.

---

## 10. Stage A4 — Add Dual Retrieval

### Objective

Allow fast travel both when the agent knows the destination and when it only knows a word or concept.

### What fits this project best

Provide two retrieval paths.

#### Symbol retrieval

Use indexed symbol identities and aliases for exact queries:

```text
get symbol → retrieve stored span
```

#### Lexical retrieval

Use SQLite FTS5 for:

- identifier fragments — any case-insensitive substring of a symbol's fully qualified name. Whole
  tokens, camel-case segments, and prefixes all qualify: a query of `Service` matches
  `CourseService`. Implemented as whole-token FTS5 matching with a substring fallback when no
  whole token matches;
- source literals;
- route strings;
- attributes;
- comments when explicitly requested;
- filenames and paths.

Lexical search finds candidate locations. Semantic traversal, implemented later, explains their neighborhood.

### Completion condition

The common question “where is this and what does it contain?” no longer requires filesystem search or Roslyn solution loading.

---

## 11. Stage A5 — Replace JSON Coordination with Database State

### Objective

Make SQLite the only operational state store.

### What fits this project best

Migrate the existing responsibilities:

| Existing artifact | Database replacement |
|---|---|
| `dirty-files.json` | changed-document/snapshot staging records |
| `file-index.json` | indexed document-to-symbol relationships |
| `semantic/*.semantic.json` | reproducible facts plus separate annotations |
| cached roots JSON | indexed semantic edges and materialized queries |
| diagnostic baseline | snapshot-bound diagnostic records |
| audit scaffolds | optional exported reports, not core state |

Keep a temporary import path for existing `.codeaudit` data only if that data contains irreplaceable annotations. Reproducible compiler facts should be rebuilt rather than migrated indefinitely.

### Completion condition

Deleting operational JSON files does not remove any capability. JSON is generated only as requested output or export.

---

# Part II — Deepening the Semantic Graph

## 12. Stage B0 — Establish the Semantic Fact Model

### Objective

Replace coarse type dependency lists with a durable graph-shaped fact model.

### What fits this project best

Use first-class nodes for:

- projects;
- documents;
- namespaces;
- types;
- members;
- parameters where needed;
- framework entry points;
- registrations;
- tests.

Use explicit edges such as:

- `declares`;
- `contains`;
- `inherits`;
- `implements`;
- `overrides`;
- `calls`;
- `constructs`;
- `reads`;
- `writes`;
- `returns`;
- `throws`;
- `references`;
- `tested_by`.

Every edge records:

- source and target identity;
- source location;
- provenance;
- extractor and version;
- snapshot membership.

Existing `discover`, `structure`, and `who-references` output should become projections of this fact model.

### Completion condition

No core analysis needs `OutgoingTypeNames` as its primary relationship representation.

---

## 13. Stage B1 — Add Member-Level Compiler Relationships

### Objective

Reach the resolution required for targeted changes.

### What fits this project best

Implement high-value relations in this order:

1. inheritance, interface implementation, and overrides;
2. member calls;
3. object construction;
4. field/property reads and writes;
5. parameter and return-type dependencies;
6. attributes;
7. thrown exception and result relationships where statically available;
8. production-to-test references.

Do not attempt exhaustive formal data-flow analysis. The purpose is reliable navigation and bounded impact, not proof of every runtime value.

### Completion condition

For a changed method, the indexer can identify the compiler-visible callers, contracts, implementations, consumed members, modified state, and relevant tests without inflating everything to the containing type.

---

## 14. Stage B2 — Model Polymorphism Honestly

### Objective

Represent interface and virtual dispatch without claiming runtime certainty that does not exist.

### What fits this project best

Keep separate facts:

```text
Handler.Handle
  statically_calls
IRepository.SaveAsync

IRepository.SaveAsync
  may_dispatch_to
Repository.SaveAsync
```

Framework registration evidence may narrow candidates, but should remain a separate edge. Consumers can derive likely paths while retaining the derivation.

### Completion condition

Interface-mediated impact paths are visible, and every concrete target states whether it is proven, framework-derived, or merely possible.

---

## 15. Stage B3 — Implement Structured Semantic Diffing

### Objective

Replace “something changed” with “this semantic fact changed.”

### What fits this project best

Keep hashes as cheap change detectors, but compare stored canonical facts to explain the difference.

Record changes such as:

- type/member added or removed;
- symbol renamed or moved;
- accessibility changed;
- signature or nullability changed;
- base type/interface changed;
- attribute changed;
- dependency edge added or removed;
- call/read/write edge changed;
- DI registration or route changed;
- implementation-only body change.

Do not attempt to derive a semantic diff from hashes alone. The previous canonical fact set or snapshot must be retained.

### Completion condition

`sweep` and `impact` can explain the semantic cause of invalidation, not only report fingerprint mismatch.

---

## 16. Stage B4 — Index Generated Semantics

### Objective

Stop treating generated code as nonexistent while avoiding source-context pollution.

### Current limitation

The current `MSBuildWorkspace.OpenSolutionAsync` loading path does not provide source-generator output. Documents produced by Roslyn source generators (e.g., `IIncrementalGenerator` implementations) are not present in the compilations exposed to the indexer. As a result, symbols and relationships derived exclusively from generated source are absent from the index. The snapshot manifest records this limitation via its completeness record (`generated_trees_included: false`). A future generator-driver stage will close this gap.

### What fits this project best

Index generated symbols and relationships when Roslyn exposes them. Mark:

- generated document status;
- generator identity when available;
- whether a declaration is user-authored or generated;
- references crossing the authored/generated boundary.

Generated source bodies should be excluded from normal agent context unless they are directly relevant or explicitly requested.

### Completion condition

Generated symbols participate in dependency and impact queries without automatically consuming agent tokens.

---

## 17. Stage B5 — Add Framework Adapters

### Objective

Turn important framework-mediated blind spots into evidence-graded relationships.

### What fits this project best

Add adapters in the order most relevant to eNoteV2 and similar .NET applications:

1. **ASP.NET Core:** routes, controllers, actions, endpoint metadata.
2. **Built-in dependency injection:** service-to-implementation registration and lifetime.
3. **MediatR:** request-to-handler and notification-to-handler relationships.
4. **EF Core:** entities, configurations, `DbSet` exposure, migrations where relevant.
5. **Serialization:** DTO/property contract participation.
6. **Test frameworks:** test-to-production relationships and test discovery metadata.

Adapters emit ordinary typed facts into the shared model. They do not create their own parallel cache.

For DI, distinguish:

- explicit recognized registration;
- registration derived by following local helper methods;
- convention-based candidate set;
- external or runtime registration unknown.

### Completion condition

The indexer can describe the common application path:

```text
HTTP route → action → request/handler → interface call
→ registered implementation → persistence operation → relevant tests
```

while preserving the evidence level of each step.

---

## 18. Stage B6 — Add a Reflection Evidence Ladder

### Objective

Capture statically visible reflection without pretending arbitrary runtime reflection is solved.

### What fits this project best

Represent separate relationships:

- `reflection_type_reference` for `typeof(T)`;
- `reflection_member_reference` for semantically resolvable `nameof` patterns;
- `reflection_name_candidate` for string literals;
- `reflection_target_unknown` for runtime-computed names or external discovery.

### Completion condition

Reflection blind spots become narrower and more explicit, not falsely eliminated.

---

## 19. Stage B7 — Build Impact Paths

### Objective

Convert isolated facts into explainable routes through the codebase.

### What fits this project best

Impact should return paths, not an undifferentiated set of “related” symbols.

Example:

```text
CustomerDto.Email changed
  ← consumed by UpdateCustomerCommand
  ← handled by UpdateCustomerHandler
  ← invoked by CustomerController.Update
  ← exposed by PUT /customers/{id}
```

Each hop retains its evidence. Traversal should have:

- direction;
- allowed edge types;
- depth limit;
- project boundary control;
- source inclusion control;
- cycle detection;
- explanation for truncation.

Avoid universal numeric “impact scores.” Explicit paths and tiers are more inspectable.

### Completion condition

An agent can see why a code location is considered affected and inspect the exact source at every hop.

---

# Part III — Unite Both Halves

## 20. Stage C0 — Context Capsules

### Objective

Deliver the original product: immediate, bounded, evidence-backed codebase context for an agent task.

### What fits this project best

The first version should accept concrete anchors rather than attempting natural-language task understanding inside the indexer:

```text
context --symbol X --intent inspect
context --symbol X --intent modify
context --file F --line N --intent diagnose
```

The middle-layer agent may translate a vague human task into anchor candidates. The indexer deterministically expands and retrieves context from those anchors.

A capsule should contain:

- anchor symbols;
- likely change sites;
- exact source spans;
- relevant contracts;
- incoming and outgoing paths;
- registered or possible runtime targets;
- affected public surfaces;
- relevant tests;
- uncertainties and unchecked vectors;
- suggested verification commands;
- reason every item was included.

### 20.1 Token budgeting

Context generation must accept an explicit budget and degrade predictably.

Recommended priority:

1. anchor declaration/body;
2. directly constraining contracts;
3. directly invoked members;
4. direct callers and entry points;
5. registered implementations;
6. relevant tests;
7. second-degree context;
8. optional surrounding source.

When information is omitted, the capsule should say what category was truncated and why.

### Completion condition

The agent receives enough code and semantic context to begin the task without repository-wide exploration, while remaining within a predictable token budget.

---

## 21. Stage C1 — Convert Existing Features into Consumers

### Objective

Ensure the system has one core rather than several implementations sharing a CLI.

### What fits this project best

Rebuild the existing modes as queries or consumers of the common database:

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

Simulations may load Roslyn because they create hypothetical compilations. Ordinary lookup and context retrieval should not.

### Completion condition

There is one fact store, one freshness model, and one identity system behind all modes.

---

## 22. Stage C2 — Incremental Operation

### Objective

Make the persistent mirror cheap enough to stay current during real development.

### What fits this project best

When nothing has changed, serve all read queries from SQLite.

When files change:

1. hash candidate documents;
2. create new immutable document versions;
3. identify affected projects;
4. update the required Roslyn compilation;
5. delete facts produced from invalid declarations/documents;
6. extract replacement facts;
7. update reverse edges and full-text search;
8. compute semantic changes;
9. commit the new snapshot atomically.

A clean rebuild remains the recovery and verification mechanism. Development tests should compare incremental output with clean rebuild output.

### Completion condition

Repeated reads are database-fast, ordinary edits cause bounded updates, and a clean rebuild produces the same canonical current facts.

---

## 23. Unified Creation Order

This is the actual implementation roadmap, combining both conceptual tracks in dependency order.

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

### 23.1 First meaningful milestone

After Phase 6, the system already provides:

- one database;
- snapshot-bound source content;
- type/member lookup;
- immediate signature/body/declaration retrieval;
- lexical search without reparsing.

At this point it can begin replacing Tokensave operationally.

### 23.2 Second meaningful milestone

After Phase 10, the system provides:

- persistent fast travel;
- member-level semantic relationships;
- polymorphic paths;
- explainable changes between snapshots.

This is the first proper deep-semantic core.

### 23.3 Completion milestone

After Phase 15, the system can take an anchor and produce a bounded package containing both the relevant semantic neighborhood and the actual code required to work there.

This completes the original concept.

---

## 24. What Not to Build

The following remain explicitly outside the core even when individually useful.

### UI, dashboard, or visualization

A separate consumer may render database queries. The indexer should optimize for correct facts and stable machine-readable responses, not graphical presentation.

### Multi-language genericization

Roslyn compiler semantics are the project’s advantage. Do not replace them with a lowest-common-denominator syntax model.

### Autonomous editing

The indexer is the map and retrieval system, not the hand changing the code.

### LLM-authored canonical summaries

Interpretive summaries become stale and cannot be reproduced deterministically. If useful, store them as annotations with their own provenance and snapshot association, never as compiler facts.

### General-purpose architecture scoring

Complexity, redundancy, and dead-code checks may remain optional candidate generators. They do not become authoritative judgments or the organizing center of the system.

### Premature daemon/server architecture

Begin with a local CLI and persistent SQLite store. A long-running service is justified only if measured Roslyn startup and incremental-update costs remain unacceptable after database-backed reads exist.

---

## 25. Validation Strategy

The system should be judged by agent outcomes, not database size or number of indexed edges.

Build a benchmark set from real eNoteV2 tasks and previous agent failures:

- vague cleanup request;
- local validation change;
- handler/DTO modification;
- cross-project interface change;
- apparently unused type deletion;
- entity change affecting EF or serialization;
- DI implementation replacement;
- route contract change.

Measure:

- Was the correct starting symbol found?
- Was its actual source retrieved without project exploration?
- Were all constraining contracts included?
- Were affected callers and implementations represented?
- Were relevant tests included?
- Were unknown runtime vectors declared?
- How much irrelevant source entered the capsule?
- How many tokens did the worker require after receiving it?
- Did the resulting patch avoid unrelated changes?
- Did incremental and clean indexing agree?

Every proposed major feature should fix a demonstrated failure in these benchmarks or strengthen a foundational invariant.

---

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

The final one-line technical description is:

> **A Roslyn-grounded, persistent and incremental semantic workspace mirror that provides software agents with immediate, evidence-backed, token-bounded access to both code and its relationships.**

---

## 27. Final Answer to the Sequencing Question

The missing fast-travel half is implemented first **at the foundation level**, because both future source retrieval and the richer semantic map need the same database, identities, spans, snapshots, and freshness rules.

The implementation should not proceed as two independent projects. The working sequence is:

```text
shared identity and SQLite foundation
    → stored documents and symbol-linked fast travel
    → migrate current map into the database
    → deepen it into the full semantic graph
    → combine retrieval and graph traversal into context capsules
```

This preserves the existing indexer's capabilities, replaces Tokensave’s accidental role deliberately, and prevents either half from becoming a second source of truth.
