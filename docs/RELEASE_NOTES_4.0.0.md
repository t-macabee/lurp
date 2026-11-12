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

## Versions

| Component | 3.6.1 | 4.0.0 |
|---|---|---|
| Tool | 3.6.1 | 4.0.0 |
| Database schema | v37 | v37 |
| Extractor | 1.11.0 | 1.12.0 |
| CLI/MCP contract | v6 | v6 |
| Output schema | v6 | v6 |
