# Ground-truth fixtures (audit Phase 5)

Committed, indexable source corpora for the B1 ground-truth work. No test consumes
them yet: Phase 4 fixtures the packed tool, Phase 6 uses `CallShapes` for the
tree-integrity check, Phase 7 builds Oracle A/B and the exact-set tests against
these corpora, and Phase 10 runs the doc examples against them.

Restore a solution before an MSBuildWorkspace consumer loads it; no `obj/` assets
are committed. Index output belongs outside the fixture tree.

## Layout

- `CallShapes/` — Fixture 1: two projects (`CallShapes.Core` declares every
  target, `CallShapes.App` contains every call shape and the top-level
  statements), net10.0, ASP.NET Core shared framework referenced for the DI and
  minimal-API shapes.
- `CallShapes/call-shapes.golden.json` — Oracle C: the reviewed ground truth for
  Fixture 1. Each fact is `source` / `kind` / `target` in Roslyn
  documentation-comment id form plus a one-line reason. `excluded` lists the
  constructs that stay behind a `DeclaredBoundaries` id; `notes` records the
  measured/undecided details. The expected kinds are `Calls`, `Constructs`,
  `MethodGroupRef` (the B1 decision), `Reads`, `Writes`, `References`.
- `CrossProject/` — Fixture 2: `CrossProject.Lib` multi-targets
  `net9.0;net10.0` with `InternalsVisibleTo("CrossProject.App")`, a partial type
  across two files, generics across assemblies, `#if NET9_0` / `#if NET10_0`
  members; `CrossProject.App` consumes public, internal, generic, nested and
  partial members; `VbLib` is a Visual Basic project.
- `CrossProject/CrossProject.slnx` — the full Fixture 2 solution (C# + VB).
- `CrossProject/CrossProject.CSharp.slnx` — C#-only copy for consumers that need
  a solution that indexes today. The full solution currently fails the full
  index on the VB project (`Compilation loader: GetCompilationAsync returned
  null for project 'VbLib'` from `src/Helpers/CompilationHelper.cs:19`); the
  audit's B6/Q12b expected a reported boundary warning instead. The Phase 7 VB
  test should load the full solution; everything else should use the C#-only
  one until that fix lands.
- `Directory.Build.props` — turns analyzers and warnings-as-errors off for these
  projects only: the fixtures deliberately use instance members a consumer
  pattern requires, public fields, and other shapes the repo rule set flags.

## CallShapes shape map

| Shape | Owning member |
|---|---|
| Top-level statement, DI registration, minimal-API handler | `App/Program.cs` (`M:Program.{Main}$(System.String[])`) |
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

Source-generated code and Razor/Blazor stay permanent declared boundaries; their
registry entries and existing provenance tests cover them, and neither can be
represented as a hand-written source row in the corpus.

## Measured while building the corpus (2026-10-03, local Release build)

Recorded for Phase 7; only the items needed to review the golden are repeated in
its `notes`:

- A VB project in the solution hard-fails a full index (see above).
- The implementation part of a partial method is never walked: no edge from
  `OnShapePartial`.
- Operator declaration bodies are not walked (`OperatorDeclarationSyntax` is not
  in the method-declaration switch), so their reads/constructions are absent
  until the Phase 7 fix.
- A C# 14 extension block call binds to a compiler-generated nested symbol and
  emits no `ExtensionReceiver` edge, while the classic extension emits both.
- A dynamic invocation produces `unsupported_syntax` binding-incompleteness rows
  and no edge; `CoreApi.DynamicTarget()` still produces its normal `Calls` edge.
