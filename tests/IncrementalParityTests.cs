using System.Text.Json;
using Lurp.Shared;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
/// Phase 2 incremental-parity matrix: each scenario indexes V1 fully, edits to
/// V2, indexes incrementally (snapshot B), rebuilds V2 in a fresh DB (snapshot
/// C), and asserts B and C are equivalent across every persisted field.
/// </summary>
public sealed class IncrementalParityTests : IntegrationTestBase
{
    private readonly string _cleanRebuildDbPath;

    public IncrementalParityTests()
    {
        _cleanRebuildDbPath = Path.Combine(TestDir, "clean.db");
    }

    private async Task RunParityScenarioAsync(string projectName, IReadOnlyDictionary<string, string> v1Files, Action v2Mutation)
    {
        CreateProject(projectName, v1Files);
        var snapshotA = await RunFullIndexAsync(DbPath);

        v2Mutation();
        var snapshotB = await RunIncrementalIndexAsync();

        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [SkippableFact]
    public async Task ScenarioB_DeleteSymbol_PrunesSymbolAndEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
            },
            () => WriteFile("TestProject", "Calculator.cs", """
                namespace TestProject;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                }
                """));
    }

    [SkippableFact]
    public async Task ScenarioC_DeleteEntireFile_PrunesCrossDocumentEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
            },
            () => DeleteFile("TestProject", "Service.cs"));
    }

    [SkippableFact]
    public async Task ScenarioD_RenameMethod_SwapsSymbolIdentity()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
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

    [SkippableFact]
    public async Task ScenarioE_ChangeMethodSignature_UpdatesCallerAndParameterEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
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

    [SkippableFact]
    public async Task ScenarioF_ChangeBaseType_UpdatesInheritsEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
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

    [SkippableFact]
    public async Task ScenarioG_ChangeImplementedInterface_SwapsImplementsAndDispatchEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
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

    [SkippableFact]
    public async Task ScenarioH_CrossProjectCallerEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
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
                    """,
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

    [SkippableFact]
    public async Task ScenarioI_PartialTypeSpanningFiles_EditOnePartKeepsOtherPartEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
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

    [SkippableFact]
    public async Task ScenarioJ_AddFileWithNewInterfaceImplementation()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
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

    [SkippableFact]
    public async Task Parity_GenericBaseWithMultipleConcreteImplementations_MergesDispatchTypeArguments()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // One generic base class implementing a constructed generic interface,
        // with three closed implementations in a second project: every
        // implementation emits a MayDispatchTo edge for the same
        // (interface member, implementing member, kind) triple carrying its own
        // concrete interface type arguments. A clean rebuild merges the colliding
        // triples into one edge whose type_arguments_json is a nested array with
        // one variant per implementation; the incremental write path must produce
        // the same merged encoding.
        var baseReadServiceV1 = """
            namespace Services;

            public class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
            {
                public virtual TResponse Find(TSearch search) => default!;
            }
            """;

        CreateProject("Services",
            new Dictionary<string, string>
            {
                ["IBaseReadService.cs"] = """
                    namespace Services;

                    public interface IBaseReadService<TResponse, TSearch>
                    {
                        TResponse Find(TSearch search);
                    }
                    """,
                ["BaseReadService.cs"] = baseReadServiceV1,
            });

        CreateProject("Implementations",
            new Dictionary<string, string>
            {
                ["UserService.cs"] = """
                    using Services;

                    namespace Implementations;

                    public class User { }
                    public class UserDto { }
                    public class UserSearch { }

                    public class UserService : BaseReadService<User, UserDto, UserSearch>
                    {
                    }
                    """,
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
                    """,
            },
            projectReferences: ["Services"]);

        var snapshotA = await RunFullIndexAsync(DbPath);

        WriteFile("Services", "BaseReadService.cs", baseReadServiceV1 + "\n// comment-only edit\n");

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
            "Implementations.UserDto, Implementations.UserSearch",
        })
        {
            Assert.True(rebuildKeys.Contains(expectedKey),
                $"Fixture sanity check failed: clean rebuild is missing dispatch variant [{expectedKey}]. JSON: {rebuildEdge.TypeArgumentsJson ?? "(null)"}");
        }
        AssertNestedVariantArray(rebuildEdge.TypeArgumentsJson, rebuildVariants.Count);

        var incrementalVariants = EdgeMerge.DeserializeTypeArguments(incrementalEdge.TypeArgumentsJson);
        Assert.True(incrementalVariants.Count == rebuildVariants.Count,
            $"Incremental MayDispatchTo edge carries {incrementalVariants.Count} type-argument variant(s) but the clean rebuild carries {rebuildVariants.Count}. " +
            $"Incremental JSON: {incrementalEdge.TypeArgumentsJson ?? "(null)"}; clean rebuild JSON: {rebuildEdge.TypeArgumentsJson ?? "(null)"}");
        Assert.Equal(VariantKeys(rebuildVariants), VariantKeys(incrementalVariants));
        AssertNestedVariantArray(incrementalEdge.TypeArgumentsJson, incrementalVariants.Count);
    }

    [SkippableFact]
    public async Task Parity_FrameworkDerivedEdge_SurvivesIncrementalWritePath()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // Guards that framework-derived provenance survives the incremental
        // write path. The explicit DI registration (Registers,
        // framework_derived) and the string-literal reflection site
        // (ReflectionNameCandidate, name_candidate) land on the SAME target
        // type as two separate edges of different kinds — the near-miss the
        // original provenance-collision spec imagined, made explicit.
        //
        // This does NOT reproduce a provenance-loss collision, because none is
        // constructible at the fixture level: adapter kinds and reflection
        // kinds never overlap (adapter kinds are {Handles, MapsTo, References,
        // Registers, RoutesTo, TestedBy}; reflection kinds are {ReflectionTypeRef,
        // ReflectionMemberRef, ReflectionNameCandidate, ReflectionTargetUnknown}),
        // so the two edges never share a (source, target, kind) dedup key and
        // neither can clobber the other. The store-level provenance-rank
        // guarantee is exercised by
        // SaveEdges_SameTripleDifferentProvenance_KeepsHigherRank (Phase C),
        // which seeds two EdgeRecords with the same triple and different
        // provenance directly — the only construction that actually hits the
        // dedup collision.
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
                            _ = Type.GetType("Svc");
                        }
                    }
                    """,
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

        var registersB = GetEdgesByKindAndProvenance(DbPath, snapshotB, EdgeKind.Registers.ToString(), Provenance.FrameworkDerived);
        var registersC = GetEdgesByKindAndProvenance(_cleanRebuildDbPath, snapshotC, EdgeKind.Registers.ToString(), Provenance.FrameworkDerived);
        Assert.Equal(2, registersB.Count); // registration site -> Svc, and ISvc -> Svc
        Assert.Equal(registersC.Count, registersB.Count);
        Assert.All(registersB, edge => Assert.Equal(svcId, edge.TargetSymbolId));
        Assert.Equal(EdgeFacts(registersC), EdgeFacts(registersB));

        var nameCandidatesB = GetEdgesByKindAndProvenance(DbPath, snapshotB, EdgeKind.ReflectionNameCandidate.ToString(), Provenance.NameCandidate);
        var nameCandidatesC = GetEdgesByKindAndProvenance(_cleanRebuildDbPath, snapshotC, EdgeKind.ReflectionNameCandidate.ToString(), Provenance.NameCandidate);
        Assert.Single(nameCandidatesB);
        Assert.Equal(nameCandidatesC.Count, nameCandidatesB.Count);
        Assert.Equal(svcId, Assert.Single(nameCandidatesB).TargetSymbolId);
        Assert.Equal(EdgeFacts(nameCandidatesC), EdgeFacts(nameCandidatesB));
    }

    private List<EdgeRecord> GetEdgesByKindAndProvenance(string dbPath, string snapshotId, string kind, string provenance)
    {
        using var store = OpenStore(dbPath);
        try
        {
            return store.GetEdges(snapshotId)
                .Where(e => e.Kind == kind && e.Provenance == provenance)
                .ToList();
        }
        finally
        {
            store.Close();
        }
    }

    private static List<string> EdgeFacts(List<EdgeRecord> edges) =>
        edges
            .Select(e => string.Join("|",
                e.SourceSymbolId, e.TargetSymbolId, e.Kind, e.Provenance,
                e.ExtractorVersion, e.SourceDocumentPath ?? "", e.SourceStartLine, e.SourceStartColumn,
                e.SourceEndLine, e.SourceEndColumn, e.IsCrossGenerated,
                e.TypeArgumentsJson ?? "", e.ReceiverTypeConstraintsJson ?? "",
                e.SourceNodeKind?.ToString() ?? "", e.TargetNodeKind?.ToString() ?? ""))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    private List<EdgeRecord> GetMayDispatchEdges(string dbPath, string snapshotId)
    {
        using var store = OpenStore(dbPath);
        try
        {
            return store.GetEdgesByKind(snapshotId, EdgeKind.MayDispatchTo.ToString());
        }
        finally
        {
            store.Close();
        }
    }

    private static List<string> VariantKeys(List<List<string>> variants) =>
        variants
            .Select(variant => string.Join(", ", variant))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

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
    /// R6 regression: when an incremental edit lands in a project that also
    /// contains an OUT-OF-SCOPE document with external-target references, the
    /// binding-incompleteness carry-forward must preserve that document's full
    /// count. The bug was that the unscoped SaveBindingIncompleteness
    /// overwrote the copied-forward count with a partial (zero) re-extraction
    /// result.
    /// </summary>
    [SkippableFact]
    public async Task ScenarioR6_BindingIncompleteness_IncrementalCarryForward()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
            },
            packageReferences: ["Newtonsoft.Json@13.0.3"]);

        await RestoreSolutionAsync();

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

    [SkippableFact]
    public async Task ScenarioR3_NewOverloadInNewFile_RebindsUneditedCaller()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

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
                    """,
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
                    """,
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
}
