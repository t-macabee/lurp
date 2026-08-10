using Lurp.Shared;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
/// Phase 5 characterization tests for the two correctness seams the audit
/// flagged as open questions.
///
/// §6.1 (generic-base edge loss): resolves via extraction-level observation —
/// SymbolIdFactory.Make normalizes constructed generic bases to their original
/// definition, which IS declared in the snapshot, so the Inherits edge must
/// survive. This test pins that contract.
///
/// §6.2 (BFS blind spot): a reference that failed to bind in V1 records
/// compiler_error incompleteness, which seeds the cross-document refresh
/// frontier; the incremental snapshot must therefore converge with a clean
/// rebuild once the typo is fixed.
/// </summary>
public sealed class KnownCorrectnessSeamTests : InMemoryTestBase
{
    static KnownCorrectnessSeamTests()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try { MSBuildLocator.RegisterDefaults(); }
            catch { }
        }
    }

    [Fact]
    public async Task GenericBaseInstantiation_InheritsEdgeTargetsOriginalDefinition()
    {
        var extraction = await ExtractAsync(
            new Dictionary<string, string>
            {
                ["Generic.cs"] = """
                    namespace N;

                    public class Base<T>
                    {
                        public T Value { get; set; }
                    }

                    public class Derived : Base<int>
                    {
                    }
                    """,
            });

        // The constructed base Base<int> is normalized to the declared
        // definition Base<T>, whose symbol is a snapshot member — so the
        // DeleteOrphanEdges filter keeps the edge rather than dropping it as
        // an unmatched generic instantiation.
        var edge = extraction.SingleEdge("Inherits", "global::N.Derived", "global::N.Base<T>");
        Assert.Equal(Provenance.CompilerProved, edge.Provenance);
        Assert.Equal("1.6.0", edge.ExtractorVersion);
        Assert.NotNull(edge.SourceDocumentPath);
        Assert.EndsWith("Generic.cs", edge.SourceDocumentPath);
    }

    [SkippableFact]
    public async Task PreviouslyUnboundReference_IncrementalConvergesWithFullRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        using var test = new IntegrationHost();
        try
        {
            // V1: Calculator.Add is a typo'd member ("pubic") that does not
            // exist, so the call site in Service.cs fails to bind and records
            // compiler_error incompleteness in the previous snapshot.
            test.CreateProject("TestProject",
                new Dictionary<string, string>
                {
                    ["Calculator.cs"] = """
                        namespace TestProject;

                        public class Calculator
                        {
                            pubic int Add(int a, int b) => a + b;
                        }
                        """,
                    ["Service.cs"] = """
                        namespace TestProject;

                        public class Service
                        {
                            public int Compute(int x, int y) => new Calculator().Add(x, y);
                        }
                        """,
                });

            await test.RunFullIndexAsync(test.DbPath);

            // V2: typo fixed — Add now binds.
            test.WriteFile("TestProject", "Calculator.cs", """
                namespace TestProject;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                }
                """);

            var snapshotB = await test.RunIncrementalIndexAsync();
            var snapshotC = await test.RunFullIndexAsync(test.CleanDbPath);

            SnapshotAssertions.CompareSnapshotsAreEquivalent(
                test.DbPath, snapshotB, test.CleanDbPath, snapshotC);
        }
        finally
        {
            test.Dispose();
        }
    }

    private sealed class IntegrationHost : IntegrationTestBase
    {
        public string CleanDbPath => Path.Combine(TestDir, "clean.db");
    }
}
