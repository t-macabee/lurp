# Lurp 4.0.0 (draft)

This file is the draft of the 4.0.0 changes. 4.0.0 is not released. Each change
of the fix plan adds its line here in the commit that makes it.

## Upgrade from 3.6.1

The extractor version changes, so every snapshot identity changes. Run one full
index after the upgrade. Old snapshots stay until they are pruned.

Schema v42 marks every existing snapshot as built by an incremental run. The
first `--strategy=full` after the upgrade rebuilds the current snapshot once.

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
- Reads and writes follow one rule. A compound assignment (`+=`), `??=`, `++`,
  a `ref` argument and a writable `ref` alias (a `ref` local or a `ref` return)
  read and write the member. The members of a tuple target (`(a, b) = ...`) are
  written, not read. Before, `+=` and `??=` gave only a write, a `ref` argument
  only a write, a `ref` local only a read, and a tuple target only a read.
- The bodies of event `add` and `remove` accessors are extracted. Their calls,
  reads, writes and type uses are facts of the accessor. Before, a method called
  only from an event accessor had no caller.
- The operation pass also writes `Throws`, so a `throw` in a field or property
  initializer, an event accessor or a top-level statement is a fact of the member
  that runs it. The `Throws` label is `operation-shapes-v2`; the label `throws-v1`
  is gone.
- A recursive call is a `Calls` fact from the member to itself (and a
  `StaticallyCalls` fact for a virtual member). No extractor drops an edge from a
  member to itself. The readers ignore such an edge: a member whose only incoming
  edges come from itself stays a dead-code candidate, and a capsule does not list
  the anchor as its own callee.
- Every static member access names its receiver type as a `References` fact:
  a method group, a field, a property, an event and a constant, not only an
  invocation (`Color.Red` references `Color`). An unqualified access names no
  type. A nested type of a constructed generic type also references the type
  arguments of its containing types (`Outer<Arg>.Inner` references `Arg`). The
  `ParameterDependencies` label is `parameter-deps-v2`.
- A type argument in a base list and each written constraint type are
  `References` facts from the declaring type, or from the method for a method
  constraint (`class D : B<A>` and `where T : A` reference `A`).
- The `using` target is the member that implements `IDisposable.Dispose` on the
  static type of the resource. Before, a public `Dispose()` that hides the base
  method or sits next to an explicit implementation was taken instead, and a
  resource of a type-parameter type gave no call.
- A user-defined `&&` or `||` also calls `operator false` or `operator true`
  (C# 12.15.3).
- A collection expression of a type with `Add` calls `Add` for each element, and
  a spread element (`[.. items]`) calls `GetEnumerator`, `MoveNext` and reads
  `Current`, like `foreach`. The capsule boundary
  `collection_expression_implicit_calls` is gone.
- An interpolated-string handler creation is a `Constructs` fact to the handler
  type and a `Calls` fact to its constructor. A `with` expression is a `Calls`
  fact to the record clone method.
- A dynamic object creation (`new T(d)` with a `dynamic` argument) is a
  `Constructs` fact to the created type. The constructor binds at run time, so
  there is no `Calls` fact and the site stays one `unsupported_syntax` row.
- A lambda or anonymous method references the type of each parameter and its
  return type, written or inferred: `Run(q => 1)` references the type of `q`
  like `Run((Q q) => 1)`. Before, only written types counted.
- A compiler-made member that Lurp stores (a synthesized member of a record, an
  accessor of a positional record property, a member of a delegate) has the
  structural facts of a written member: `Declares`, `Overrides`, `Returns` and
  the `References` of its signature. A delegate type has a `Returns` fact to its
  return type. A compiler-made member that Lurp does not store (an implicit
  constructor, a backing field) has no fact. The labels are `declares-v2`,
  `overrides-v2` and `returns-v2`.
- The provenance of an interface `MayDispatchTo` fact no longer depends on the
  order of the types or on the scope of an incremental run. It is
  `compiler_proved` when the type that declares the implementing member
  implements the interface member with it, and `possible` when only a derived
  type maps the member to an inherited implementation. The `Polymorphism` label
  is `polymorphism-v2`.
- `MayDispatchTo` goes from every member of an override chain to every override
  below it, not only from the root. A call on `V1.Do` can reach `V2.Do`, which
  overrides `V1.Do`. A call on a base record reaches the synthesized overrides of
  a derived record (`Equals`, `ToString`, `PrintMembers`, the clone method).
- An override event has an `Overrides` fact to the event it overrides. `Hides`
  follows the C# rules of hiding through inheritance: a member hides an
  inherited member also when the direct base type does not declare it; a method
  hides only a base method with the same signature (the number of type
  parameters and the type and the by-value or by-reference passing of each
  parameter) and every base member that is not a method; a field, an event and a
  nested type hide every base member with their name. The `Hides` label is
  `hides-v2`.
- The operation pass also writes `StaticallyCalls`, the marker of an access
  whose target can dispatch at run time: a virtual, abstract or overriding member
  (an interface member included) that is not sealed and whose type is not
  sealed. The marker covers invocations, property and indexer accesses, an event
  `+=` or `-=`, and the implicit calls and reads (for example `foreach`), in
  every member body and initializer. Before, only an explicit invocation in a
  method body was marked, a call on an override was not marked, and a `base.`
  call and a delegate invocation were marked although they cannot dispatch. The
  `StaticallyCalls` label is `operation-shapes-v2`; the label
  `statically-calls-v2` and the extractor row `StaticDispatch` are gone.
- The reflection facts (`ReflectionTypeRef`, `ReflectionMemberRef`,
  `ReflectionNameCandidate` and `ReflectionTargetUnknown`) follow the owner rule
  of the other body facts. An accessor owns its body, also the expression body of
  a property or an indexer. Each variable of a field declaration owns its own
  initializer. A type owns the arguments of its attributes. Before, the property
  owned an accessor body, the first variable owned every initializer of the
  declaration, and a `typeof`, a `nameof` or a reflection call in an indexer, an
  operator, a finalizer, an event accessor or a type attribute gave no fact. The
  `Reflection` label is `reflection-v4`.
- A name of one or two characters is a name candidate, for example the key
  property in `HasKey("Id")`. Before, a string shorter than three characters was
  dropped.
- A reflection sink is the runtime API that the call binds, not a member name in
  the source. New sinks: `Type.GetInterface`, `GetRuntimeMethod`,
  `GetRuntimeProperty`, `GetRuntimeField` and `GetRuntimeEvent`
  (`RuntimeReflectionExtensions`), and the `GetDeclared*` methods of `TypeInfo`.
  A method named `GetType` or `MakeGenericMethod` on another type is no sink.
  A name candidate matches the case of the name, unless the call passes
  `BindingFlags.IgnoreCase`, and binds only the member kind of the call:
  `GetMethod` a method, `GetProperty` a property, `GetField` a field, `GetEvent`
  an event, `GetNestedType` a nested type. A member lookup on a known type also
  finds the members of its base types; `GetNestedType` and `GetDeclared*` find
  only the members that the type declares. `Type.GetType`, `Assembly.GetType`
  and `Activator.CreateInstance` read the namespace-qualified type name
  (`N.Outer+Inner`), with or without an assembly name. Before, the match ignored
  case and kind, `Type.GetType("Target")` matched `N.Target`, and the name
  `Type.GetType("N.Target")` matched nothing.
- A framework name (EF Core `[ForeignKey]`, `[InverseProperty]` and the string
  APIs, xUnit `[MemberData]`, `PropertyChangedEventArgs`, `[CallerMemberName]`)
  also binds an inherited member, and a `[property: ...]` attribute on a
  positional record parameter binds like an attribute on the property. Each
  framework API binds only its member kind: `PropertyChangedEventArgs` a
  property, an EF Core name a property or a field, `[MemberData]` a property, a
  field or a method, an MVC action name a method.
- A name candidate to its own owner is recorded, for example from `Run` to `Run`
  for `typeof(C).GetMethod("Run")` inside `Run`, and from the setter of `Name`
  to `Name` for `PropertyChanged` with `"Name"`. Like every edge from a member to
  itself, it does not make the member live.
- Language-version recovery runs only when a project fails MSBuild evaluation
  and uses the project's explicit `LangVersion` or its target-framework default
  (read from the project file or `Directory.Build.props`), falling back to
  `LatestMajor` only when no target framework is declared. Projects that
  evaluate cleanly at C# 7.3 (such as `netstandard2.0` or .NET Framework
  targets, or projects inheriting `LangVersion=7.3`) keep their compiler
  options and diagnostics unchanged.
- One generated-code rule decides generated documents everywhere: Roslyn's
  file-name patterns (`TemporaryGeneratedFile_*`, `*.designer.cs`,
  `*.generated.cs`, `*.g.cs`, `*.g.i.cs`, case-insensitive), `*ModelSnapshot.cs`
  (case-insensitive), or a leading comment before the first token containing
  `<auto-generated` or `<autogenerated` (case-sensitive, no byte cap). `/generated/`
  paths, `[GeneratedCode(...)]` attributes, and string literals no longer mark a
  document as generated.
- Gitignored compile items are indexed like any other compile item (and a new
  gitignored `.cs` file invalidates the incremental no-op precheck). Compile
  items under `bin/` and `obj/` stay excluded and are omitted from
  `extractor_failure` and `project_unreadable` document lists.

## Dependency injection

- The DI adapter reads every MS.DI registration form: `Add*`, `TryAdd*`, keyed
  registrations, `ServiceDescriptor` values and `Replace`. The service and the
  implementation come from the bound type and value arguments, so named
  arguments and `?.` calls work. A factory, an instance whose type is an
  interface or an abstract class, and a `Type` value that is not a `typeof`
  give the runtime-unknown node, not a claimed implementation. A registration
  without an implementation registers the service type itself.
- The owner of a registration is the member that runs it: a constructor, a
  field initializer, or the method or entry point around a lambda.
- A method is a registration API only when its declaring type is the framework
  type. A user method named `Configure` or `Scan` gives no edge. An external
  method that takes an `IServiceCollection` gives the runtime-unknown node.
- A Scrutor scan `FromAssemblyOf<T>()` (or `FromAssembliesOf(typeof(...))`),
  `AddClasses()` or `AddClasses(publicOnly)`, `AsImplementedInterfaces()` and
  any lifetime gives the interface → class pairs that Scrutor registers. Any
  other scan gives one `convention:assembly_scan:<assembly>` placeholder for
  each assembly that it names, or the runtime-unknown node.
- When a scan and an explicit registration give the same pair, the pair keeps
  `framework_derived`. The extractor label is `di-v2`.

## ASP.NET Core

- The ASP.NET Core adapter writes only `RoutesTo`. Its `Declares`, `Returns`
  and `[FromServices]` `References` rows never reached the index; the compiler
  facts stay.
- Route node ids are the routes ASP.NET Core builds: `route://GET /api/Users/{id}`
  (tokens replaced, `~/` and `/` overrides applied, one node for each route
  attribute and HTTP method, `ANY` without a method constraint). Old ids such as
  `route://api/[controller]/{id}` are gone; reindex.
- A controller is a type that ASP.NET Core accepts (public, top-level, not
  abstract, not open generic, no `[NonController]`, a `Controller` suffix or
  `[Controller]`). Inherited public actions are routed on each concrete
  controller; the route uses the concrete controller's name and inherited
  `[Route]` attributes.

## MediatR

- `Handles` targets the method that implements each handler interface: a type
  with two handler interfaces, an explicit implementation and an inherited
  `Handle` are right. The unmodeled MediatR patterns are the seven MediatR 12
  interfaces (`IStreamPipelineBehavior` and `IRequestExceptionAction` added; the
  nonexistent `IAsyncStreamHandler` removed).
- Incremental runs no longer add a copy of each `unmodeled_mediatr_pattern`
  annotation; annotations are saved only for the documents a run re-extracts.

## EF Core

- `HasDatabaseName` is no longer reported as a unique index; `IsUnique()` is,
  on the entity, with its properties and database name. A configuration class
  for two entities applies each `Configure` to its own entity. Navigations in
  `IEntityTypeConfiguration<T>.Configure` give the same `References` as in
  `OnModelCreating`.

## Serialization

- The Serialization adapter writes a `serialization_contract` annotation for
  each member with a serialization attribute (`name=<wire name>`, `ignored` or
  `included`), read from the attribute type (qualified and aliased names and
  `[property:]` record attributes count), writing a value only when the library
  reads the attribute (`[DataMember]` only on a `[DataContract]` type;
  System.Text.Json `JsonPropertyName` and non-`Always` `JsonIgnore` on a
  non-public member only with `[JsonInclude]`). Its member `References` rows,
  which never reached the index, are gone. A `[JsonSerializable]` context also
  references the element types of generic arguments.

## Dead candidates

- A type candidate ignores every edge from its own subtree (including its own
  LIVE edges), and a property or event and its accessors ignore edges between
  one another.
- Public surface is the effective accessibility for types, ordinary methods,
  events and event accessors (a public method of an internal type can be
  `proved_dead`), and the declared accessibility for instance constructors,
  properties, property accessors and fields. Implicit implementations of
  external interface members and overrides of external members are
  `external_interface_implementation`.
- Internal and private-protected symbols of a project that grants
  `InternalsVisibleTo` to an assembly outside the snapshot (or an unnamed
  target `*`) are treated as public surface.
- A Scrutor scan no longer makes its own document `unresolved`; an unmodeled
  scan of a named assembly makes the non-abstract, non-static classes and
  delegates (and their instance constructors) of that assembly `unresolved`.
- A symbol is treated as generated only when every declaration of it is in a
  generated document.
- Serialization opt-in is read from `serialization_contract` annotations
  (`[DataMember]` counts only on a `[DataContract]` type).
- `ef_convention` covers private and private-protected fields, properties and
  instance constructors of a `MapsTo` entity; ordinary private methods follow
  the normal ladder.
- An unknown provenance value fails the call instead of reading
  `framework_convention`.
- Additive fields: `dead-candidates` and `lurp_dead_candidates` candidates add
  `effective_accessibility`; `status` and `lurp_status` manifests add
  `internals_visible_to`.

## Test coverage

- Test methods are found by attribute type, subclasses included: `[Theory]`,
  `[SkippableFact]`, `[DataTestMethod]`, NUnit `[TestCase]` and
  `[TestCaseSource]`, TUnit `[Test]`. An attribute that is only named `Test` no
  longer counts.
- One rule decides a test project for `TestedBy` and dead candidates: a
  reference to one of eight test-framework assemblies (`MSTest.TestFramework`
  and `TUnit.Core` added). The name of a project no longer counts.
- `TestedBy` follows the code a test runs: helpers and local functions of the
  test assembly, the test class constructor and initializers, setup methods, and
  target-typed `new()`.

## Storage

- Declarations are linked to the snapshots that show them. After an incremental
  or a full run in a database with older snapshots, `outline`, `navigate` and
  dead candidates no longer return the declarations of an older compilation.
  Schema v38.
- Schema v39 drops four write-only columns and two unused tables. No behavior
  changes except `snapshot_graph_nodes`, which now holds only boundary nodes
  that are not snapshot symbols.
- Schema v40 adds `edge_sites`: an edge keeps every site of its relation.
  Existing edges get one site.
- Every extractor reports every site of a relation. Reindex once.
- Impact hops (`impact`, `lurp_impact`) and capsule path hops (`incoming_paths`,
  `outgoing_paths`) include `site_count` and up to 20 ordered `sites`
  (`document`, `line`, `column`, `end_line`, `end_column`); `source_*` fields
  stay and equal the first site (additive, contract v6, output schema v6).
- Schema v43 adds a unique index on extractor annotations. The upgrade deletes
  duplicate rows that older incremental runs wrote (`unmodeled_mediatr_pattern`);
  user annotations are not touched.
- Schema v44 adds `document_versions.is_generated` and the covering index
  `idx_document_versions_generated` (replacing `idx_declarations_doc_version_generated`).
  `grep`, `search --type=source` and `diagnostics` now hide declaration-less and
  header-generated documents by default, and `diagnostics` also hides out-of-snapshot
  `bin/`/`obj/` build output by default.
- Excluded `bin/`/`obj/` compile items inside the repository are recorded in
  `binding_incompleteness` with reason `excluded_document`, and orphan edges to
  symbols declared only in those excluded documents are bucketed under
  `excluded_document` instead of `other`.
- Schema v45 adds `projects.internals_visible_to` (per-project
  `InternalsVisibleTo` targets as a JSON array, NULL when none; existing rows
  read as NULL). `InternalsVisibleTo` targets participate in snapshot identity
  and freshness (`InternalsVisibleToChanged`).
- Schema v47 adds `snapshot_symbols.bare_fqn` (the FQN without type-parameter
  lists) and indexes on (`snapshot_id`, `fqn`) and (`snapshot_id`, `bare_fqn`).

## Snapshot lifecycle

- The latest snapshot is the one that the last successful index run built or
  reused. After a revert or a branch switch to an indexed state, reads, `status`
  and the next incremental run use the reused snapshot, and pruning keeps it.
  Schema v41 adds `snapshots.selected_at_utc`.
- `--strategy=full` rebuilds a snapshot that an incremental run built, so it
  recovers a wrong incremental snapshot; it reuses a snapshot that a full run
  built. Schema v42 adds `snapshots.build_strategy`.
- `--strategy=incremental --force` on an existing snapshot deletes it and runs
  the incremental index into the same id; before, it failed with a SQLite
  `UNIQUE` error.
- A run that would rebuild a pinned snapshot stops with exit code 1 before it
  deletes anything, and the message names the flag.
- Retention keeps the 3 newest complete snapshots of a workspace. A pinned
  snapshot is kept in addition and does not take a slot. Failed runs no longer
  take slots; only the newest failure that no later run superseded is kept, so
  `status` can report it.
- `--mode=index`, `--mode=pin-snapshot` and `lurp_index` refuse a database of a
  newer schema with the same message as the read modes, and do not touch it. A
  store error no longer prints `ERROR: ERROR:`.

## Incremental indexing

- An incremental run extracts each document once. A null-path edge of a
  document outside the run's scope is kept; before, a run could drop it.
- An edit outside member bodies (a new or removed member, a signature, an
  attribute, a `using` or alias directive) and an added or deleted file
  re-extract the project and every project that depends on it. Before, such an
  edit could leave a caller bound to the old target until a full run.
- When an edit changes the symbol ids of an unchanged document (for example, a
  type that its signatures use is renamed or moved), `search` and the stored diff
  no longer keep the old ids.

## Semantic diff

- Adding the first attribute to a symbol or removing its last attribute now
  emits `attribute_changed` (an absent `attributes` key in non-empty symbol
  metadata is read as an empty list).

## Breaking changes (CLI/MCP contract version 7)

- `status` and `pin-snapshot` JSON: the key `built_at_latest_snapshot_id` is
  renamed `selected_latest_snapshot_id`. Read the new key.
- Reads that default to the latest snapshot use the snapshot that the last
  successful index run built or reused, not the snapshot built last.
- `--strategy=full` without `--force` rebuilds a snapshot that an incremental
  run built (before: reused it). On a pinned snapshot that an incremental run
  built, it now stops with exit code 1.
- `impact --provenance=` and MCP `lurp_impact` `provenance` no longer accept
  `global_implementation_relation`; it is a capsule-only grade, not an edge
  provenance, and is now rejected as an unknown value.
- `dead-candidates` and `lurp_dead_candidates` `live_provenance_rank` now lists
  only the two strong edge provenances (`compiler_proved`, `framework_derived`),
  without `global_implementation_relation`.
- Symbol declaration metadata (`get-symbol --view=metadata`): the key
  `implements_external_interface` is renamed `implements_external_member`. Read
  `implements_external_member`.

- Recompute incoming topology before comparing capsules across versions. Incoming paths now use the default upstream impact filter.

- Read callers at depth two or more from `second_degree_context`. Restart tier paging after upgrade.
- Check `omitted_tiers` for `unresolved` before acting on an empty list. Each tier has a binding scope.
- Read proved interface members implemented by the anchor from `contracts`.
- Read `affected_public_surfaces` using the effective-access and outside-friend rules. Instance constructors, properties, fields and property accessors still use declared access.
- Query snapshot annotations directly for facts outside the capsule scope.
- `shortest_path_count` counts shortest symbol routes. Parallel edges between two symbols count once, so a count can drop, and `--kinds=` no longer changes the count of a route that it keeps.
- `groups` with equal counts sort by the first-hop neighbor id.
- Over the budget, a capsule keeps the first three witness paths whole and drops the rest. Before, it cut each path to three hops.
- A gap capsule keeps its `location_gap` uncertainty under any budget.
- `anchor.extractor_identity` names the extractor version of the capsule's snapshot.
- `suggested_verification` commands name the indexed project or solution, not files found on disk at read time.
- A location anchor that resolves to a type gets the type default budget.
- `--symbol` (and MCP `symbol`) resolves only exact forms: the full id, the
  doc-comment id, the FQN, or the FQN without type-parameter lists. A prefix, a
  case variant or a wildcard no longer resolves, and a name that matches several
  symbols (overloads) is an error that lists their ids. A full id must name a
  symbol of the snapshot.
- `find-symbol`, `get-symbol --view=metadata`, `lurp_find_symbol` and
  `lurp_get_symbol`: `locations` lists every declaration, generated ones
  included, each with `is_generated`; `declaration_count` equals its length.
  `--include-generated` only decides whether a symbol with only generated
  declarations resolves.

## Symbol resolution and navigation

- `navigate`, `lurp_navigate` and `context --file --line` return the innermost
  declaration on the line: a field, a positional record property or an accessor
  that starts inside the line is found, and a record's own lines no longer
  return a compiler-made member.
- `get-symbol --include-generated` resolves generated symbols and applies to the
  `containing-type` and `surrounding` views.
- `get-annotations --document=` and `lurp_get_annotations` `document` include
  the annotations of every symbol declared in the document, user annotations
  included.

## CLI and MCP surface

- MCP `lurp_context` rejects a half `file`+`line` pair, as the CLI does (D86, additive). MCP and CLI error texts for impact and context use one wording.

## Versions

| Component | 3.6.1 | 4.0.0 |
|---|---|---|
| Tool | 3.6.1 | 4.0.0 |
| Database schema | v37 | v47 |
| Extractor | 1.11.0 | 1.12.0 |
| CLI/MCP contract | v6 | v7 |
| Output schema | v6 | v7 |
