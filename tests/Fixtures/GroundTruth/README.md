# Ground-truth fixtures (audit Phase 5)

Committed, indexable source corpora for the ground-truth tests. Consumers:
`TargetTreeIntegrityTests` checks that the CLI and MCP surface leave the
`CallShapes` tree unchanged. `GroundTruthGoldenTests` (Oracle C) compares the
extractor output on `CallShapes` and `CrossProject` with their complete goldens
as exact sets. `GroundTruthOracleBTests` (Oracle B) checks `SymbolFinder` caller
recall on the same two corpora. `CrossProjectBoundaryTests` runs the VB boundary
test against `NonCSharp`. `ExtractorVersionGateTests` extracts `CallShapes`,
`CrossProject` and `ExtractorGate`. The doc examples run against them.

Restore a solution before an MSBuildWorkspace consumer loads it; no `obj/` assets
are committed. Index output belongs outside the fixture tree.

## Layout

- `CallShapes/` — Fixture 1: two projects (`CallShapes.Core` declares every
  target, `CallShapes.App` contains every call shape and the top-level
  statements), net10.0, ASP.NET Core shared framework referenced for the DI and
  minimal-API shapes.
- `CallShapes/call-shapes.golden.json` — Oracle C for Fixture 1. The list is
  complete: it holds every fact of the kinds in `compared_kinds` (`Calls`,
  `Constructs`, `MethodGroupRef`, `Reads`, `Writes`, `References`) whose source
  and target are both declared in the fixture. `GroundTruthGoldenTests` compares
  the extractor output in that scope with the list as an exact set. Each fact is
  `source` / `kind` / `target` in Roslyn documentation-comment id form, with a
  one-line reason and a `basis`: the evidence that makes the fact true without
  the extractor. `basis_values` defines each basis. The golden is independent of
  the extractor only through this field. A fact with `pending` is true, but the
  extractor does not write it yet; the test requires it to be absent. `mapping`
  states the rules; `excluded` lists the constructs that stay behind a
  `DeclaredBoundaries` id; `notes` records details of single shapes.
- `CrossProject/` — Fixture 2: `CrossProject.Lib` multi-targets
  `net9.0;net10.0` with `InternalsVisibleTo("CrossProject.App")`, a partial type
  across two files, generics across assemblies, `#if NET9_0` / `#if NET10_0`
  members; `CrossProject.App` consumes public, internal, generic, nested and
  partial members; `VbLib` is a Visual Basic project.
- `CrossProject/CrossProject.slnx` — the full Fixture 2 solution (C# + VB).
- `CrossProject/CrossProject.CSharp.slnx` — C#-only copy of Fixture 2.
- `CrossProject/cross-project.golden.json` — Oracle C for Fixture 2, in the
  same form and with the same rules as the CallShapes golden. Its facts are the
  union over the net9.0 and net10.0 compilations.
- `NonCSharp/NonCSharp.slnx` — one C# project plus the VB project. A full index
  skips the VB project and prints the `non_csharp_projects` boundary warning
  (audit B6/Q12b). `CrossProjectBoundaryTests` runs this through the CLI.
- `ExtractorGate/` — Fixture 3: the corpus of the extractor version gate
  (`ExtractorVersionGateTests`, D103). `ExtractorGate.App` holds one shape for
  each framework adapter (controller, DI registration and Scrutor scan, MediatR
  handler, EF Core `DbContext` with a query filter, `[JsonPropertyName]`) and for
  the `Hides` and `Reflection` extractors; `ExtractorGate.Tests` is an xUnit
  project for the test adapter. It references MediatR, EF Core, Scrutor and
  xUnit, so a restore needs those packages.
- `Directory.Build.props` — turns analyzers and warnings-as-errors off for these
  projects only: the fixtures deliberately use instance members a consumer
  pattern requires, public fields, and other shapes the repo rule set flags.

## CallShapes shape map

| Shape | Owning member |
|---|---|
| Top-level statement, DI registration, minimal-API handler | `CallShapes/App/Program.cs` (`M:Program.{Main}$(System.String[])`) |
| Field / property initializer | `ShapeCallers.FieldInitializerValue`, `PropertyInitializerValue` |
| Expression-bodied property | `ShapeCallers.get_ExpressionBodiedProperty` |
| Constructor initializer (`: this(...)`) | `ShapeCallers.#ctor` |
| Base constructor (`: base(...)`) | `DerivedShape.#ctor` |
| Primary-constructor base arguments | `PrimaryShape.#ctor(System.Int32)` |
| Method group | `ShapeCallers.FromMethodGroup` |
| Event `+=` | `ShapeCallers.FromEventSubscription` |
| LINQ query lambda | `ShapeCallers.FromQueryLambda` |
| Local function | `ShapeCallers.FromLocalFunction` |
| Partial method (implementation part) | `ShapeCallers.OnShapePartial` |
| Operators: `+=`, unary `-`, implicit / explicit conversion | `ShapeCallers.FromOperators`, `FromUnaryOperator`, `FromImplicitConversion`, `FromExplicitConversion`, plus the operator bodies on `ShapeNumber` |
| `foreach` | `ShapeCallers.FromForeach` |
| `using` / `await` / deconstruction / collection initializer | `ShapeCallers.FromUsing`, `FromAwait`, `FromDeconstruction`, `FromCollectionInitializer` |
| Collection expression, `with`, attribute, omitted optional argument | `ShapeCallers.FromCollectionExpression`, `FromWith`, `FromAttribute`, `FromOmittedArgument` |
| Property pattern | `ShapeCallers.FromPropertyPattern` |
| `dynamic` | `ShapeCallers.FromDynamic` |
| Async iterator | `ShapeCallers.FromAsyncIterator` |
| Interpolated-string handler | `ShapeCallers.FromInterpolatedHandler`, `ShapeLogHandler` |
| Classic extension / C# 14 extension block | `ShapeTextExtensions.ClassicWordCount`, `BlockWordCount` |
| Explicit interface implementation, default interface member, static abstract member | `ShapeContractImpl`, `IShapeDefault.DefaultValue`, `ShapeFactoryUser.CreateShape``1` |
| Static constructor, finalizer, init accessor, accessor-bodied getter | `ShapeLifecycle` |
| `ref struct`, unsafe pointer | `ShapeRefStruct`, `ShapeUnsafe` |
| Records (including the `with` receiver) | `ShapeRecord` |
| `using` and `await using` targets: interface, explicit implementation, ref struct, `IAsyncDisposable`, inherited implementation, nullable reference, interface constraint, `dynamic` | `ShapeUsingCallers` (resource types in `ShapeDisposables.cs`) |

Source-generated code and Razor/Blazor stay permanent declared boundaries; their
registry entries and existing provenance tests cover them, and neither can be
represented as a hand-written source row in the corpus.

## Measured while building the corpus (2026-10-03, local Release build)

Recorded before the Phase 7 fixes; only the items needed to review the golden are
repeated in its `notes`. Status after Phase 7:

- A VB project in the solution hard-failed a full index. Fixed: it is skipped
  with a boundary warning.
- The implementation part of a partial method was never walked. Fixed by
  `OperationShapeExtractor`.
- Operator declaration bodies were not walked. Fixed by `OperationShapeExtractor`.
- A C# 14 extension block call binds to a compiler-generated nested symbol and
  emitted no `ExtensionReceiver` edge. Fixed: `SymbolIdFactory` normalizes block
  extension members to the declared member.
- A dynamic invocation produces `unsupported_syntax` binding-incompleteness rows
  and no edge; `CoreApi.DynamicTarget()` still produces its normal `Calls` edge.
