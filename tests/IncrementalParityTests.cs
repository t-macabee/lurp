using Lurp.Storage;
using Lurp.Workspace;
using System.Text.Json;

namespace Lurp.Tests;

/// <summary>
///     Phase 2 incremental-parity matrix: each scenario indexes V1 fully, edits to
///     V2, indexes incrementally (snapshot B), rebuilds V2 in a fresh DB (snapshot
///     C), and asserts B and C are equivalent across every persisted field.
/// </summary>
[Trait("Category", "Slow")]
public sealed class IncrementalParityTests : IntegrationTestBase
{
    private readonly string _cleanRebuildDbPath;

    private static readonly string[] Net10Only = ["net10.0"];
    private static readonly string[] Net10AndNet9 = ["net10.0", "net9.0"];

    public IncrementalParityTests()
    {
        _cleanRebuildDbPath = Path.Combine(TestDir, "clean.db");
    }

    private async Task RunParityScenarioAsync(string projectName, IReadOnlyDictionary<string, string> v1Files,
        Action v2Mutation)
    {
        CreateProject(projectName, v1Files);
        var snapshotA = await RunFullIndexAsync(DbPath);

        v2Mutation();
        var snapshotB = await RunIncrementalIndexAsync();

        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [Fact]
    public async Task ScenarioB_DeleteSymbol_PrunesSymbolAndEdges()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                                    namespace TestProject;

                                    public class Calculator
                                    {
                                        public int Add(int a, int b) => a + b;
                                        public int Subtract(int a, int b) => a - b;
                                    }
                                    """,
                ["Service.cs"] = """
                                 namespace TestProject;

                                 public class Service
                                 {
                                     public int Compute(int x, int y) => new Calculator().Subtract(x, y);
                                 }
                                 """
            },
            () => WriteFile("TestProject", "Calculator.cs", """
                                                            namespace TestProject;

                                                            public class Calculator
                                                            {
                                                                public int Add(int a, int b) => a + b;
                                                            }
                                                            """));
    }

    [Fact]
    public async Task ScenarioC_DeleteEntireFile_PrunesCrossDocumentEdges()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                                    namespace TestProject;

                                    public class Calculator
                                    {
                                        public int Add(int a, int b) => a + b;
                                    }
                                    """,
                ["Service.cs"] = """
                                 namespace TestProject;

                                 public class Service
                                 {
                                     public int Compute(int x, int y) => new Calculator().Add(x, y);
                                 }
                                 """
            },
            () => DeleteFile("TestProject", "Service.cs"));
    }

    [Fact]
    public async Task ScenarioD_RenameMethod_SwapsSymbolIdentity()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                                    namespace TestProject;

                                    public class Calculator
                                    {
                                        public int Add(int a, int b) => a + b;
                                    }
                                    """,
                ["Service.cs"] = """
                                 namespace TestProject;

                                 public class Service
                                 {
                                     public int Compute(int x, int y) => new Calculator().Add(x, y);
                                 }
                                 """
            },
            () =>
            {
                WriteFile("TestProject", "Calculator.cs", """
                                                          namespace TestProject;

                                                          public class Calculator
                                                          {
                                                              public int Sum(int a, int b) => a + b;
                                                          }
                                                          """);
                WriteFile("TestProject", "Service.cs", """
                                                       namespace TestProject;

                                                       public class Service
                                                       {
                                                           public int Compute(int x, int y) => new Calculator().Sum(x, y);
                                                       }
                                                       """);
            });
    }

    [Fact]
    public async Task ScenarioE_ChangeMethodSignature_UpdatesCallerAndParameterEdges()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                                    namespace TestProject;

                                    public class Calculator
                                    {
                                        public int Add(int a, int b) => a + b;
                                    }
                                    """,
                ["Service.cs"] = """
                                 namespace TestProject;

                                 public class Service
                                 {
                                     public int Compute(int x, int y) => new Calculator().Add(x, y);
                                 }
                                 """
            },
            () =>
            {
                WriteFile("TestProject", "Calculator.cs", """
                                                          namespace TestProject;

                                                          public class Calculator
                                                          {
                                                              public double Add(double a, double b) => a + b;
                                                          }
                                                          """);
                WriteFile("TestProject", "Service.cs", """
                                                       namespace TestProject;

                                                       public class Service
                                                       {
                                                           public double Compute(double x, double y) => new Calculator().Add(x, y);
                                                       }
                                                       """);
            });
    }

    [Fact]
    public async Task ScenarioF_ChangeBaseType_UpdatesInheritsEdges()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Types.cs"] = """
                               namespace TestProject;

                               public class BaseV1
                               {
                                   public int X => 1;
                               }

                               public class Derived : BaseV1
                               {
                               }
                               """
            },
            () => WriteFile("TestProject", "Types.cs", """
                                                       namespace TestProject;

                                                       public class BaseV2
                                                       {
                                                           public int Y => 2;
                                                       }

                                                       public class Derived : BaseV2
                                                       {
                                                       }
                                                       """));
    }

    [Fact]
    public async Task ScenarioG_ChangeImplementedInterface_SwapsImplementsAndDispatchEdges()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Service.cs"] = """
                                 namespace TestProject;

                                 public interface IFoo
                                 {
                                     void Do();
                                 }

                                 public class Service : IFoo
                                 {
                                     public void Do() { }
                                 }
                                 """
            },
            () => WriteFile("TestProject", "Service.cs", """
                                                         namespace TestProject;

                                                         public interface IBar
                                                         {
                                                             void Run();
                                                         }

                                                         public class Service : IBar
                                                         {
                                                             public void Run() { }
                                                         }
                                                         """));
    }

    [Fact]
    public async Task ScenarioH_CrossProjectCallerEdit()
    {
        CreateProject("Lib",
            new Dictionary<string, string>
            {
                ["Lib.cs"] = """
                             namespace Lib;

                             public class Lib
                             {
                                 public void Foo() { }
                                 public void Bar() { }
                             }
                             """
            });

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Consumer.cs"] = """
                                  namespace App;

                                  public class Consumer
                                  {
                                      public void Use(Lib.Lib lib) => lib.Foo();
                                  }
                                  """
            },
            projectReferences: ["Lib"]);

        await RunFullIndexAsync(DbPath);

        WriteFile("App", "Consumer.cs", """
                                        namespace App;

                                        public class Consumer
                                        {
                                            public void Use(Lib.Lib lib) => lib.Bar();
                                        }
                                        """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
    }

    [Fact]
    public async Task ScenarioI_PartialTypeSpanningFiles_EditOnePartKeepsOtherPartEdges()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Calc.cs"] = """
                              namespace TestProject;

                              public partial class Calc
                              {
                                  private int X;
                              }
                              """,
                ["CalcExtensions.cs"] = """
                                        namespace TestProject;

                                        public partial class Calc
                                        {
                                            private int Y;
                                        }
                                        """
            },
            () => WriteFile("TestProject", "Calc.cs", """
                                                      namespace TestProject;

                                                      public partial class Calc
                                                      {
                                                          private int X;

                                                          public int Add(int a, int b) => a + b;
                                                      }
                                                      """));
    }

    [Fact]
    public async Task ScenarioI2_PartialTypeGainsAnEarlierFile_KeepsStructuralEdgeLocations()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Base.cs"] = """
                              namespace TestProject;

                              public abstract class Base { public abstract void Run(); }
                              """,
                ["Widget.cs"] = """
                                namespace TestProject;

                                public partial class Widget : Base, System.IDisposable
                                {
                                    public override void Run() { }
                                    public void Dispose() { }
                                }
                                """,
                ["Widget.Extra.cs"] = """
                                      namespace TestProject;

                                      public partial class Widget
                                      {
                                          private int _count;
                                      }
                                      """
            },
            () => WriteFile("TestProject", "AWidget.Part.cs", """
                                                              namespace TestProject;

                                                              public partial class Widget
                                                              {
                                                                  public void Added() { }
                                                              }
                                                              """));
    }

    [Fact]
    public async Task ScenarioJ_AddFileWithNewInterfaceImplementation()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                                    namespace TestProject;

                                    public class Calculator
                                    {
                                    }
                                    """,
                ["Service.cs"] = """
                                 namespace TestProject;

                                 public class Service
                                 {
                                     public int Compute(Calculator calc) => 0;
                                 }
                                 """
            },
            () =>
            {
                WriteFile("TestProject", "Calculator.cs", """
                                                          namespace TestProject;

                                                          public class Calculator : ICalc
                                                          {
                                                          }
                                                          """);
                WriteFile("TestProject", "ICalc.cs", """
                                                     namespace TestProject;

                                                     public interface ICalc
                                                     {
                                                     }
                                                     """);
            });
    }

    [Fact]
    public async Task Parity_GenericBaseWithMultipleConcreteImplementations_MergesDispatchTypeArguments()
    {
        // One generic base class implementing a constructed generic interface,
        // with three closed implementations in a second project: every
        // implementation emits a MayDispatchTo edge for the same
        // (interface member, implementing member, kind) triple carrying its own
        // concrete interface type arguments. A clean rebuild merges the colliding
        // triples into one edge whose type_arguments_json is a nested array with
        // one variant per implementation; the incremental write path must produce
        // the same merged encoding.
        var baseReadService = CreateCrossProjectGenericDispatchFixture();

        var snapshotA = await RunFullIndexAsync(DbPath);

        WriteFile("Services", "BaseReadService.cs", baseReadService + "\n// comment-only edit\n");

        await AssertIncrementalDispatchVariantsMatchCleanRebuildAsync(snapshotA);
    }

    [Fact]
    public async Task Parity_GenericBaseWithMultipleConcreteImplementations_EditedImplementor_KeepsAllDispatchTypeArguments()
    {
        // R7 regression: the same aggregated MayDispatchTo edge as the test above,
        // but the comment-only edit lands in ONE closed implementation's own
        // document (UserService.cs), not in the base file. The reverse-edge closure
        // reaches the dispatch target's document (BaseReadService.cs) through the
        // implementor's edge, and the cross-document refresh must re-extract the
        // changed implementor too; when step 7 excluded it from the refresh scope,
        // its type-argument variant vanished from the aggregated edge even though a
        // clean rebuild still had all three.
        CreateSameProjectGenericDispatchFixture();

        var snapshotA = await RunFullIndexAsync(DbPath);

        WriteFile("Implementations", "UserService.cs", GenericDispatchUserServiceSource + "\n// comment-only edit\n");

        await AssertIncrementalDispatchVariantsMatchCleanRebuildAsync(snapshotA);
    }

    [Fact]
    public async Task Parity_FrameworkDerivedEdge_SurvivesIncrementalWritePath()
    {
        // Guards that framework-derived provenance survives the incremental
        // write path. The explicit DI registration (Registers,
        // framework_derived) and the string-literal reflection site
        // (ReflectionNameCandidate, name_candidate) land on the SAME target
        // type as two separate edges of different kinds. One relation with two
        // provenances (a scan and an explicit registration of the same pair) is
        // covered by DependencyInjectionScanTests and by the store-level rank
        // test SaveEdges_SameTripleDifferentProvenance_KeepsHigherRank (D6, D27).
        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Di.cs"] = """
                            namespace Microsoft.Extensions.DependencyInjection;

                            public interface IServiceCollection
                            {
                            }

                            public static class ServiceCollectionServiceExtensions
                            {
                                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
                                    where TService : class
                                    where TImplementation : class, TService
                                    => services;
                            }
                            """,
                ["App.cs"] = """
                             using Microsoft.Extensions.DependencyInjection;

                             namespace TestProject;

                             public interface ISvc
                             {
                             }

                             public class Svc : ISvc
                             {
                             }

                             public static class Startup
                             {
                                 public static void Register(IServiceCollection services)
                                 {
                                     services.AddScoped<ISvc, Svc>();
                                     _ = Type.GetType("TestProject.Svc");
                                 }
                             }
                             """
            });

        var snapshotA = await RunFullIndexAsync(DbPath);

        // Comment-only edit to the stub file: App.cs is unchanged, so its DI
        // and reflection edges must survive the incremental write path by
        // copy-forward, not by re-extraction.
        WriteFile("TestProject", "Di.cs", """
                                          namespace Microsoft.Extensions.DependencyInjection;

                                          public interface IServiceCollection
                                          {
                                          }

                                          public static class ServiceCollectionServiceExtensions
                                          {
                                              public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
                                                  where TService : class
                                                  where TImplementation : class, TService
                                                  => services;
                                          }
                                          // comment-only edit
                                          """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);

        var svcId = ResolveSymbolId(snapshotB, "global::TestProject.Svc");

        var registersB = GetEdgesByKindAndProvenance(DbPath, snapshotB, nameof(EdgeKind.Registers),
            Provenance.FrameworkDerived);
        var registersC = GetEdgesByKindAndProvenance(_cleanRebuildDbPath, snapshotC, nameof(EdgeKind.Registers),
            Provenance.FrameworkDerived);
        Assert.Equal(2, registersB.Count); // registration site -> Svc, and ISvc -> Svc
        Assert.Equal(registersC.Count, registersB.Count);
        Assert.All(registersB, edge => Assert.Equal(svcId, edge.TargetSymbolId));
        Assert.Equal(EdgeFacts(registersC), EdgeFacts(registersB));

        var nameCandidatesB = GetEdgesByKindAndProvenance(DbPath, snapshotB,
            nameof(EdgeKind.ReflectionNameCandidate), Provenance.NameCandidate);
        var nameCandidatesC = GetEdgesByKindAndProvenance(_cleanRebuildDbPath, snapshotC,
            nameof(EdgeKind.ReflectionNameCandidate), Provenance.NameCandidate);
        Assert.Single(nameCandidatesB);
        Assert.Equal(nameCandidatesC.Count, nameCandidatesB.Count);
        Assert.Equal(svcId, Assert.Single(nameCandidatesB).TargetSymbolId);
        Assert.Equal(EdgeFacts(nameCandidatesC), EdgeFacts(nameCandidatesB));
    }

    private static List<EdgeRecord> GetEdgesByKindAndProvenance(string dbPath, string snapshotId, string kind,
        string provenance)
    {
        using var store = OpenStore(dbPath);
        try
        {
            return [.. store.GetEdges(snapshotId)
                .Where(e => e.Kind == kind && e.Provenance == provenance)];
        }
        finally
        {
            store.Close();
        }
    }

    private static List<string> EdgeFacts(List<EdgeRecord> edges)
    {
        return [.. edges
            .Select(e => string.Join("|",
                e.SourceSymbolId, e.TargetSymbolId, e.Kind, e.Provenance,
                e.ExtractorVersion, e.SourceDocumentPath ?? "", e.SourceStartLine, e.SourceStartColumn,
                e.SourceEndLine, e.SourceEndColumn, e.IsCrossGenerated,
                e.TypeArgumentsJson ?? "", e.ReceiverTypeConstraintsJson ?? "",
                e.SourceNodeKind?.ToString() ?? "", e.TargetNodeKind?.ToString() ?? ""))
            .OrderBy(f => f, StringComparer.Ordinal)];
    }

    // Shared by both generic-dispatch fixtures: one generic base class implementing
    // a constructed generic interface, with three closed implementations in a second
    // project. Every implementation emits a MayDispatchTo edge for the same
    // (interface member, implementing member, kind) triple carrying its own concrete
    // interface type arguments, so the merged edge aggregates one type-argument
    // variant per implementation. The two fixtures differ only in where the base
    // class lives.
    private const string GenericDispatchInterfaceSource = """
                                                          namespace Services;

                                                          public interface IBaseReadService<TResponse, TSearch>
                                                          {
                                                              TResponse Find(TSearch search);
                                                          }
                                                          """;

    private const string GenericDispatchUserServiceSource = """
                                                            using Services;

                                                            namespace Implementations;

                                                            public class User { }
                                                            public class UserDto { }
                                                            public class UserSearch { }

                                                            public class UserService : BaseReadService<User, UserDto, UserSearch>
                                                            {
                                                            }
                                                            """;

    private static Dictionary<string, string> GenericDispatchImplementorFiles()
    {
        return new Dictionary<string, string>
        {
            ["UserService.cs"] = GenericDispatchUserServiceSource,
            ["OrderService.cs"] = """
                                  using Services;

                                  namespace Implementations;

                                  public class Order { }
                                  public class OrderDto { }
                                  public class OrderSearch { }

                                  public class OrderService : BaseReadService<Order, OrderDto, OrderSearch>
                                  {
                                  }
                                  """,
            ["ProductService.cs"] = """
                                    using Services;

                                    namespace Implementations;

                                    public class Product { }
                                    public class ProductDto { }
                                    public class ProductSearch { }

                                    public class ProductService : BaseReadService<Product, ProductDto, ProductSearch>
                                    {
                                    }
                                    """
        };
    }

    /// <summary>
    ///     Base class in the interface's project (Services), implementors in the dependent
    ///     project: a base edit must reach the implementors across the project boundary.
    ///     Returns the base file's source.
    /// </summary>
    private string CreateCrossProjectGenericDispatchFixture()
    {
        const string baseReadServiceV1 = """
                                         namespace Services;

                                         public class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
                                         {
                                             public virtual TResponse Find(TSearch search) => default!;
                                         }
                                         """;

        CreateProject("Services",
            new Dictionary<string, string>
            {
                ["IBaseReadService.cs"] = GenericDispatchInterfaceSource,
                ["BaseReadService.cs"] = baseReadServiceV1
            });

        CreateProject("Implementations", GenericDispatchImplementorFiles(), projectReferences: ["Services"]);

        return baseReadServiceV1;
    }

    /// <summary>
    ///     Base class in the implementors' project so it can name the implementor types in
    ///     ReferenceImplementors. Those member-signature References edges put the aggregated
    ///     edge's anchor document (the base file) into the reverse-edge closure when an
    ///     implementor's OWN file is the change; without that arc the closure for an
    ///     implementor edit is empty and step 7 never rebuilds the edge. The base cannot
    ///     stay in Services for this: Services would have to reference Implementations,
    ///     which already references Services.
    /// </summary>
    private void CreateSameProjectGenericDispatchFixture()
    {
        CreateProject("Services",
            new Dictionary<string, string> { ["IBaseReadService.cs"] = GenericDispatchInterfaceSource });

        var implementationFiles = GenericDispatchImplementorFiles();
        implementationFiles["BaseReadService.cs"] = """
                                                    using Services;

                                                    namespace Implementations;

                                                    public class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
                                                    {
                                                        public virtual TResponse Find(TSearch search) => default!;

                                                        public static void ReferenceImplementors(UserService user, OrderService order, ProductService product)
                                                        {
                                                            _ = (user, order, product);
                                                        }
                                                    }
                                                    """;
        CreateProject("Implementations", implementationFiles, projectReferences: ["Services"]);
    }

    private async Task AssertIncrementalDispatchVariantsMatchCleanRebuildAsync(string snapshotA)
    {
        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);

        var incrementalEdge = Assert.Single(GetMayDispatchEdges(DbPath, snapshotB));
        var rebuildEdge = Assert.Single(GetMayDispatchEdges(_cleanRebuildDbPath, snapshotC));

        Assert.Equal(rebuildEdge.SourceSymbolId, incrementalEdge.SourceSymbolId);
        Assert.Equal(rebuildEdge.TargetSymbolId, incrementalEdge.TargetSymbolId);

        var rebuildVariants = EdgeMerge.DeserializeTypeArguments(rebuildEdge.TypeArgumentsJson);
        var rebuildKeys = VariantKeys(rebuildVariants);
        foreach (var expectedKey in new[]
                 {
                     "Implementations.OrderDto, Implementations.OrderSearch",
                     "Implementations.ProductDto, Implementations.ProductSearch",
                     "Implementations.UserDto, Implementations.UserSearch"
                 })
            Assert.True(rebuildKeys.Contains(expectedKey),
                $"Fixture sanity check failed: clean rebuild is missing dispatch variant [{expectedKey}]. JSON: {rebuildEdge.TypeArgumentsJson ?? "(null)"}");
        AssertNestedVariantArray(rebuildEdge.TypeArgumentsJson, rebuildVariants.Count);

        var incrementalVariants = EdgeMerge.DeserializeTypeArguments(incrementalEdge.TypeArgumentsJson);
        Assert.True(incrementalVariants.Count == rebuildVariants.Count,
            $"Incremental MayDispatchTo edge carries {incrementalVariants.Count} type-argument variant(s) but the clean rebuild carries {rebuildVariants.Count}. " +
            $"Incremental JSON: {incrementalEdge.TypeArgumentsJson ?? "(null)"}; clean rebuild JSON: {rebuildEdge.TypeArgumentsJson ?? "(null)"}");
        Assert.Equal(VariantKeys(rebuildVariants), VariantKeys(incrementalVariants));
        AssertNestedVariantArray(incrementalEdge.TypeArgumentsJson, incrementalVariants.Count);
    }

    private static List<EdgeRecord> GetMayDispatchEdges(string dbPath, string snapshotId)
    {
        using var store = OpenStore(dbPath);
        try
        {
            return store.GetEdgesByKind(snapshotId, nameof(EdgeKind.MayDispatchTo));
        }
        finally
        {
            store.Close();
        }
    }

    private static List<string> VariantKeys(List<List<string>> variants)
    {
        return [.. variants
            .Select(variant => string.Join(", ", variant))
            .OrderBy(key => key, StringComparer.Ordinal)];
    }

    private static void AssertNestedVariantArray(string? typeArgumentsJson, int expectedVariantCount)
    {
        Assert.False(string.IsNullOrWhiteSpace(typeArgumentsJson),
            "Expected the nested-array type_arguments_json encoding, but the field is empty.");

        using var doc = JsonDocument.Parse(typeArgumentsJson);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(expectedVariantCount, doc.RootElement.GetArrayLength());
        foreach (var element in doc.RootElement.EnumerateArray())
            Assert.Equal(JsonValueKind.Array, element.ValueKind);
    }

    /// <summary>
    ///     R6 regression: when an incremental edit lands in a project that also
    ///     contains an OUT-OF-SCOPE document with external-target references, the
    ///     binding-incompleteness carry-forward must preserve that document's full
    ///     count. The bug was that the unscoped SaveBindingIncompleteness
    ///     overwrote the copied-forward count with a partial (zero) re-extraction
    ///     result.
    /// </summary>
    [Fact]
    public async Task ScenarioR6_BindingIncompleteness_IncrementalCarryForward()
    {
        // Helper.cs has no external references — will be edited to trigger
        // incremental re-extraction of the App project.
        // ExternalRef.cs references Newtonsoft.Json (external NuGet assembly)
        // and must NOT be in the extraction scope.
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Helper.cs"] = """
                                namespace App;

                                public class Helper
                                {
                                    public int Add(int a, int b) => a + b;
                                }
                                """,
                ["ExternalRef.cs"] = """
                                     using Newtonsoft.Json;

                                     namespace App;

                                     public class ExternalRef
                                     {
                                         public string Serialize(object obj)
                                         {
                                             return JsonConvert.SerializeObject(obj);
                                         }
                                     }
                                     """
            },
            ["Newtonsoft.Json@13.0.3"]);

        RestoreSolution();

        var snapshotA = await RunFullIndexAsync(DbPath);

        // Semantics-preserving comment edit — only Helper.cs is dirty.
        WriteFile("App", "Helper.cs", """
                                      namespace App;

                                      public class Helper
                                      {
                                          public int Add(int a, int b) => a + b;
                                      }
                                      // comment-only edit
                                      """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);

        // The parity oracle must succeed — binding-incompleteness counts for
        // the out-of-scope ExternalRef.cs must be preserved by carry-forward.
        // The R6 lockstep fix (scope-filtering SaveBindingIncompleteness to the
        // deletion set) ensures this holds even if an extractor scope gate
        // leak produces partial records for out-of-scope documents.
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
    }

    /// <summary>
    ///     R6 end-to-end (GAP 2(c)): a null-path (doc-less) <c>extractor_failure</c>
    ///     binding-incompleteness row seeded into a full-rebuild snapshot must carry
    ///     forward byte-identically through an incremental pass over a changed
    ///     in-scope document. The null bucket's stored <c>document_path</c> is
    ///     <c>""</c> — never in the extraction scope, so no document-scoped delete
    ///     can retire it — and the scoped re-extraction save drops null-path rows
    ///     (EXCLUDE-AND-CARRY-FORWARD), so the only way the row lands in the new
    ///     snapshot is the verbatim <c>CopyBindingIncompleteness</c>. This promotes
    ///     the R6 null-bucket guarantee from the predicate-level
    ///     <see cref="BindingIncompletenessScopingTests" /> pin to end-to-end.
    /// </summary>
    [Fact]
    public async Task ScenarioR6_NullPathExtractorFailure_CarriesForwardAcrossIncremental()
    {
        // Same fixture as the predicate-level R6 test: Helper.cs (edited below)
        // has no external references; ExternalRef.cs targets Newtonsoft.Json and
        // stays out of the incremental extraction scope.
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Helper.cs"] = """
                                namespace App;

                                public class Helper
                                {
                                    public int Add(int a, int b) => a + b;
                                }
                                """,
                ["ExternalRef.cs"] = """
                                     using Newtonsoft.Json;

                                     namespace App;

                                     public class ExternalRef
                                     {
                                         public string Serialize(object obj)
                                         {
                                             return JsonConvert.SerializeObject(obj);
                                         }
                                     }
                                     """
            },
            ["Newtonsoft.Json@13.0.3"]);

        RestoreSolution();

        var snapshotA = await RunFullIndexAsync(DbPath);

        // Fault injection (CompilationFactExtractorRunStageTests pattern): a
        // throwing stage degrades through the RunStage catch arm into
        // RecordExtractorFailure with a null document path. Seed that single
        // record into the persisted snapshot A.
        var ctx = new CompilationFactExtractor.StageContext(
            "App",
            [],
            new BindingIncompletenessCollector("App", TestDir),
            []);

        CompilationFactExtractor.RunStage(
            ctx, "Polymorphism", null, null,
            _ => _,
            () => throw new InvalidOperationException("injected null-path failure"));

        var injectedRow = Assert.Single(ctx.Incompleteness.ToRecords());
        Assert.Equal("extractor_failure", injectedRow.Reason);
        Assert.Null(injectedRow.DocumentPath);
        Assert.Equal(1, injectedRow.Count);
        Assert.Equal(VersionConstants.ExtractorVersion, injectedRow.ExtractorVersion);

        using (var store = OpenStore(DbPath))
        {
            store.SaveBindingIncompleteness(snapshotA, [injectedRow]);
        }

        // Semantics-preserving comment edit — only Helper.cs is dirty.
        WriteFile("App", "Helper.cs", """
                                      namespace App;

                                      public class Helper
                                      {
                                          public int Add(int a, int b) => a + b;
                                      }
                                      // comment-only edit
                                      """);

        var snapshotB = await RunIncrementalIndexAsync();

        Assert.NotEqual(snapshotA, snapshotB);

        // Byte-identical carry-forward: the null-path row in B must equal the
        // row in A. (The R6 parity oracle is intentionally NOT invoked here:
        // SnapshotAssertions.CompareSnapshotsAreEquivalent compares the full
        // binding_incompleteness row sets, and a clean rebuild of the same
        // workspace cannot reproduce the injected extractor_failure row — so
        // the assertion narrows to this direct row-equality check.)
        using (var store = OpenStore(DbPath))
        {
            var rowsA = store.GetBindingIncompleteness(snapshotA, "App")
                .Where(r => r is { Reason: "extractor_failure", DocumentPath: null })
                .ToList();
            var rowsB = store.GetBindingIncompleteness(snapshotB, "App")
                .Where(r => r is { Reason: "extractor_failure", DocumentPath: null })
                .ToList();

            var rowA = Assert.Single(rowsA);
            Assert.Equal(rowA, Assert.Single(rowsB));
        }
    }

    [Fact]
    public async Task ScenarioR3_NewOverloadInNewFile_RebindsUneditedCaller()
    {
        // V1: only M(long) exists; c.M(42) binds to M(long) via implicit
        // conversion. V2 adds M(int) in a NEW file — the unedited caller now
        // binds to M(int) by overload resolution. The incremental
        // cross-document BFS must re-extract the unedited caller even though
        // only a file was added (not edited).
        CreateProject("Calc",
            new Dictionary<string, string>
            {
                ["Provider.cs"] = """
                                  namespace Calc;

                                  public partial class Calc
                                  {
                                      public long M(long x) => x;
                                  }
                                  """
            });

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Caller.cs"] = """
                                namespace App;

                                public class Caller
                                {
                                    public long Use(Calc.Calc c) => c.M(42);
                                }
                                """
            },
            projectReferences: ["Calc"]);

        var snapshotA = await RunFullIndexAsync(DbPath);

        WriteFile("Calc", "Overloads.cs", """
                                          namespace Calc;

                                          public partial class Calc
                                          {
                                              public int M(int x) => x;
                                          }
                                          """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);

        // The parity oracle must succeed — the caller's call edge must point
        // at the newly preferred overload (M(int)), not the stale M(long).
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
    }

    /// <summary>
    ///     DI adapter: changing the implementation type in an AddScoped registration
    ///     must delete old Registers edges and re-extract new ones that match a clean
    ///     rebuild. Exercises the incremental delete+re-extract path for
    ///     framework-derived edges (the existing Parity_FrameworkDerivedEdge test only
    ///     covers copy-forward).
    /// </summary>
    [Fact]
    public async Task ScenarioK_DIRegistrationChange_UpdatesRegistersEdges()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Di.cs"] = """
                            namespace Microsoft.Extensions.DependencyInjection;

                            public interface IServiceCollection { }

                            public static class ServiceCollectionServiceExtensions
                            {
                                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
                                    where TService : class
                                    where TImplementation : class, TService => services;
                                public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services)
                                    where TService : class
                                    where TImplementation : class, TService => services;
                            }
                            """,
                ["App.cs"] = """
                             using Microsoft.Extensions.DependencyInjection;

                             namespace TestProject;

                             public interface ISvc { }
                             public class Svc : ISvc { }

                             public static class Startup
                             {
                                 public static void Register(IServiceCollection services)
                                 {
                                     services.AddScoped<ISvc, Svc>();
                                 }
                             }
                             """
            },
            () =>
            {
                WriteFile("TestProject", "App.cs", """
                                                   using Microsoft.Extensions.DependencyInjection;

                                                   namespace TestProject;

                                                   public interface ISvc { }
                                                   public class Svc : ISvc { }
                                                   public class SvcV2 : ISvc { }

                                                   public static class Startup
                                                   {
                                                       public static void Register(IServiceCollection services)
                                                       {
                                                           services.AddScoped<ISvc, SvcV2>();
                                                       }
                                                   }
                                                   """);
            });
    }

    /// <summary>
    ///     DI adapter: adding a new typeof()-form registration to an existing file
    ///     must extract new Registers edges that match a clean rebuild. Exercises
    ///     ResolveRegistrationTypeArgs's TypeOfExpressionSyntax fallback path.
    /// </summary>
    [Fact]
    public async Task ScenarioL_DIRegistrationAdd_ExtractsNewEdges()
    {
        await RunParityScenarioAsync("TestProject",
            new Dictionary<string, string>
            {
                ["Di.cs"] = """
                            namespace Microsoft.Extensions.DependencyInjection;

                            public interface IServiceCollection { }

                            public static class ServiceCollectionServiceExtensions
                            {
                                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
                                    where TService : class
                                    where TImplementation : class, TService => services;
                                public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services)
                                    where TService : class
                                    where TImplementation : class, TService => services;
                                public static IServiceCollection AddTransient(this IServiceCollection services, Type serviceType, Type implementationType) => services;
                            }
                            """,
                ["App.cs"] = """
                             using Microsoft.Extensions.DependencyInjection;

                             namespace TestProject;

                             public interface ISvc { }
                             public class Svc : ISvc { }

                             public static class Startup
                             {
                                 public static void Register(IServiceCollection services)
                                 {
                                     services.AddScoped<ISvc, Svc>();
                                 }
                             }
                             """
            },
            () =>
            {
                WriteFile("TestProject", "App.cs", """
                                                   using Microsoft.Extensions.DependencyInjection;

                                                   namespace TestProject;

                                                   public interface ISvc { }
                                                   public class Svc : ISvc { }
                                                   public interface ILog { }
                                                   public class ConsoleLog : ILog { }

                                                   public static class Startup
                                                   {
                                                       public static void Register(IServiceCollection services)
                                                       {
                                                           services.AddScoped<ISvc, Svc>();
                                                           services.AddTransient(typeof(ILog), typeof(ConsoleLog));
                                                       }
                                                   }
                                                   """);
            });
    }

    /// <summary>
    ///     ASP.NET Core adapter: changing a controller route attribute must delete
    ///     the old RoutesTo edge (with its synthetic route:// source node) and
    ///     re-extract a new one matching the updated template. Exercises the
    ///     delete+re-extract path for edges with synthetic source nodes.
    /// </summary>
    [Fact]
    public async Task ScenarioM_ControllerRouteChange_UpdatesRoutesToEdges()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["ItemsController.cs"] = """
                                         using Microsoft.AspNetCore.Mvc;

                                         namespace App;

                                         [Route("api/[controller]")]
                                         public class ItemsController : ControllerBase
                                         {
                                             [HttpGet("{id}")]
                                             public Item Get(int id) => new Item();
                                         }

                                         public class Item { public int Id { get; set; } }
                                         """
            },
            frameworkReferences: ["Microsoft.AspNetCore.App"]);

        RestoreSolution();
        var snapshotA = await RunFullIndexAsync(DbPath);

        WriteFile("App", "ItemsController.cs", """
                                               using Microsoft.AspNetCore.Mvc;

                                               namespace App;

                                               [Route("api/[controller]")]
                                               public class ItemsController : ControllerBase
                                               {
                                                   [HttpGet("{id}/details")]
                                                   public Item Get(int id) => new Item();
                                               }

                                               public class Item { public int Id { get; set; } }
                                               """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
    }

    [Fact]
    public async Task ScenarioM2_RenamedDerivedController_RetiresItsInheritedRoute()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Base.cs"] = """
                              using Microsoft.AspNetCore.Mvc;

                              namespace App;

                              [Route("api/[controller]")]
                              public abstract class ApiBase : ControllerBase
                              {
                                  [HttpGet("ping")]
                                  public int Ping() => 1;
                              }
                              """,
                ["Orders.cs"] = """
                                using Microsoft.AspNetCore.Mvc;

                                namespace App;

                                public class OrdersController : ApiBase
                                {
                                    [HttpGet("list")]
                                    public int List() => 1;
                                }
                                """
            },
            frameworkReferences: ["Microsoft.AspNetCore.App"]);

        RestoreSolution();
        var snapshotA = await RunFullIndexAsync(DbPath);

        WriteFile("App", "Orders.cs", """
                                      using Microsoft.AspNetCore.Mvc;

                                      namespace App;

                                      public class PurchasesController : ApiBase
                                      {
                                          [HttpGet("list")]
                                          public int List() => 1;
                                      }
                                      """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        var routesB = QueryEdges(DbPath, snapshotB, "RoutesTo");
        Assert.DoesNotContain(routesB, r => r.SourceSymbolId == "route://GET /api/Orders/ping");
    }

    [Fact]
    public async Task ScenarioMediatR_UnmodeledAnnotation_IsNotDuplicatedByIncrementalRuns()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Handlers.cs"] = """
                                  using System.Collections.Generic;
                                  using System.Runtime.CompilerServices;
                                  using System.Threading;
                                  using MediatR;

                                  namespace App;

                                  public record SR : IStreamRequest<int>;

                                  public class SH : IStreamRequestHandler<SR, int>
                                  {
                                      public async IAsyncEnumerable<int> Handle(SR request, [EnumeratorCancellation] CancellationToken cancellationToken)
                                      {
                                          yield return 1;
                                      }
                                  }
                                  """,
                ["Dtos.cs"] = """
                              namespace App;

                              public class Dto { }
                              """
            },
            ["MediatR@12.4.1"]);

        RestoreSolution();
        await RunFullIndexAsync(DbPath);

        WriteFile("App", "Dtos.cs", """
                                    namespace App;

                                    public class Dto { }
                                    // edit 1
                                    """);
        var snapshotB = await RunIncrementalIndexAsync();

        WriteFile("App", "Dtos.cs", """
                                    namespace App;

                                    public class Dto { }
                                    // edit 2
                                    """);
        var snapshotC = await RunIncrementalIndexAsync();

        var snapshotD = await RunFullIndexAsync(_cleanRebuildDbPath);

        void AssertSingleAnnotation(string dbPath, string snapshotId)
        {
            using var store = OpenStore(dbPath);
            try
            {
                var shId = store.GetSymbolIdsInSnapshot(snapshotId)
                    .Single(id => store.GetSymbolInfo(id, snapshotId)?.FullyQualifiedName == "global::App.SH");
                var annotations = store.GetAnnotations(snapshotId)
                    .Where(a => a.Kind == "unmodeled_mediatr_pattern")
                    .ToList();
                var row = Assert.Single(annotations);
                Assert.Equal(shId, row.SymbolId);
                Assert.Equal("unmodeled_mediatr_pattern", row.Kind);
                Assert.Equal("IStreamRequestHandler", row.Value);
                Assert.Equal("src/App/Handlers.cs", row.DocumentPath);
            }
            finally
            {
                store.Close();
            }
        }

        AssertSingleAnnotation(DbPath, snapshotB);
        AssertSingleAnnotation(DbPath, snapshotC);
        AssertSingleAnnotation(_cleanRebuildDbPath, snapshotD);
    }

    [Fact]
    public async Task ScenarioTestedBy_HelperChange_UpdatesTheTestOfAnotherDocument()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                            namespace App;

                            public class A { }
                            public class B { }
                            """
        });

        CreateProject("App.Tests", new Dictionary<string, string>
        {
            ["Helpers.cs"] = """
                             namespace App.Tests;

                             public static class Helpers
                             {
                                 public static object Make() => new App.A();
                             }
                             """,
            ["Tests.cs"] = """
                           using Xunit;

                           namespace App.Tests;

                           public class T
                           {
                               [Fact]
                               public void Uses() { Helpers.Make(); }
                           }
                           """
        },
        packageReferences: ["xunit@2.9.3"],
        projectReferences: ["App"]);

        RestoreSolution();
        await RunFullIndexAsync(DbPath);

        WriteFile("App.Tests", "Helpers.cs", """
                                            namespace App.Tests;

                                            public static class Helpers
                                            {
                                                public static object Make() => new App.B();
                                            }
                                            """);
        var incrementalSnapshot = await RunIncrementalIndexAsync();
        var cleanSnapshot = await RunFullIndexAsync(_cleanRebuildDbPath);

        SnapshotAssertions.CompareSnapshotsAreEquivalent(
            DbPath, incrementalSnapshot,
            _cleanRebuildDbPath, cleanSnapshot);

        using var store = OpenStore(DbPath);
        try
        {
            var aId = store.GetSymbolIdsInSnapshot(incrementalSnapshot)
                .Single(id => store.GetSymbolInfo(id, incrementalSnapshot)?.FullyQualifiedName == "global::App.A");
            var bId = store.GetSymbolIdsInSnapshot(incrementalSnapshot)
                .Single(id => store.GetSymbolInfo(id, incrementalSnapshot)?.FullyQualifiedName == "global::App.B");
            var usesId = store.GetSymbolIdsInSnapshot(incrementalSnapshot)
                .Single(id => store.GetSymbolInfo(id, incrementalSnapshot)?.FullyQualifiedName == "global::App.Tests.T.Uses");

            var testedBy = store.GetOutgoingEdges(incrementalSnapshot, aId)
                .Concat(store.GetOutgoingEdges(incrementalSnapshot, bId))
                .Where(e => e.Kind == "TestedBy")
                .ToList();

            Assert.DoesNotContain(testedBy, e => e.SourceSymbolId == aId && e.TargetSymbolId == usesId);
            Assert.Contains(testedBy, e => e.SourceSymbolId == bId && e.TargetSymbolId == usesId);
        }
        finally
        {
            store.Close();
        }
    }

    // ── Phase 7.8: per-symbol TFM attribution on a multi-target project ──────

    /// <summary>
    ///     7.8.3: the incremental TFM reset (7.4). v1 declares M() for both TFMs;
    ///     v2 wraps M() in #if NET10_0. The copied-forward {net9.0, net10.0} set
    ///     must be reset for the re-extracted symbol, so the increment matches the
    ///     clean rebuild: M -> {net10.0}.
    /// </summary>
    [Fact]
    public async Task ScenarioTfm1_WrapMemberInIf_ResetsCopiedForwardTargetFrameworks()
    {
        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Types.cs"] = """
                               namespace TestProject;

                               public class Types
                               {
                                   public int M() => 1;
                               }
                               """
            },
            targetFramework: "net9.0;net10.0");

        RestoreSolution();
        var snapshotA = await RunFullIndexAsync(DbPath);
        Assert.Equal(Net10AndNet9,
            SnapshotAssertions.ReadTargetFrameworksForSymbol(DbPath, snapshotA, "M:TestProject.Types.M"));

        WriteFile("TestProject", "Types.cs", """
                                              namespace TestProject;

                                              public class Types
                                              {
                                              #if NET10_0
                                                  public int M() => 1;
                                              #endif
                                              }
                                              """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        Assert.Equal(Net10Only,
            SnapshotAssertions.ReadTargetFrameworksForSymbol(DbPath, snapshotB, "M:TestProject.Types.M"));
        Assert.Equal(Net10Only,
            SnapshotAssertions.ReadTargetFrameworksForSymbol(_cleanRebuildDbPath, snapshotC, "M:TestProject.Types.M"));
    }

    /// <summary>
    ///     7.8.4: the reverse edit. v1 wraps M() in #if NET10_0; v2 removes the
    ///     guard so M() is declared for both TFMs again. The increment must recover
    ///     {net9.0, net10.0}, not keep only the copied-forward net10.0 row.
    /// </summary>
    [Fact]
    public async Task ScenarioTfm2_RemoveIf_RecoversBothTargetFrameworks()
    {
        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Types.cs"] = """
                               namespace TestProject;

                               public class Types
                               {
                               #if NET10_0
                                   public int M() => 1;
                               #endif
                               }
                               """
            },
            targetFramework: "net9.0;net10.0");

        RestoreSolution();
        var snapshotA = await RunFullIndexAsync(DbPath);
        Assert.Equal(Net10Only,
            SnapshotAssertions.ReadTargetFrameworksForSymbol(DbPath, snapshotA, "M:TestProject.Types.M"));

        WriteFile("TestProject", "Types.cs", """
                                              namespace TestProject;

                                              public class Types
                                              {
                                                  public int M() => 1;
                                              }
                                              """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        Assert.Equal(Net10AndNet9,
            SnapshotAssertions.ReadTargetFrameworksForSymbol(DbPath, snapshotB, "M:TestProject.Types.M"));
        Assert.Equal(Net10AndNet9,
            SnapshotAssertions.ReadTargetFrameworksForSymbol(_cleanRebuildDbPath, snapshotC, "M:TestProject.Types.M"));
    }

    /// <summary>
    ///     7.8.5 (proves 7.9.1): a partial type with parts in two files on a
    ///     multi-target project. Editing only file 1 must not lose the TFM rows the
    ///     7.4 reset deletes, because the type's parts are re-extracted together:
    ///     the class keeps {net9.0, net10.0}.
    /// </summary>
    [Fact]
    public async Task ScenarioTfm3_PartialTypeAcrossFiles_KeepsAllTargetFrameworks()
    {
        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Calc.Part1.cs"] = """
                                    namespace TestProject;

                                    public partial class Calc
                                    {
                                        public int FromPartOne() => 1;
                                    }
                                    """,
                ["Calc.Part2.cs"] = """
                                    namespace TestProject;

                                    public partial class Calc
                                    {
                                        public int FromPartTwo() => 2;
                                    }
                                    """
            },
            targetFramework: "net9.0;net10.0");

        RestoreSolution();
        var snapshotA = await RunFullIndexAsync(DbPath);
        Assert.Equal(Net10AndNet9,
            SnapshotAssertions.ReadTargetFrameworksForSymbol(DbPath, snapshotA, "T:TestProject.Calc"));

        WriteFile("TestProject", "Calc.Part1.cs", """
                                                  namespace TestProject;

                                                  public partial class Calc
                                                  {
                                                      public int FromPartOne() => 1;
                                                  }
                                                  // comment-only edit
                                                  """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        Assert.Equal(Net10AndNet9,
            SnapshotAssertions.ReadTargetFrameworksForSymbol(DbPath, snapshotB, "T:TestProject.Calc"));
    }
}