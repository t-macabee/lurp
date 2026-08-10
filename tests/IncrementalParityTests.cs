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
}
