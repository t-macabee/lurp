using Lurp.Storage;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
/// Phase 4: store read paths must return the persisted facts (declaration
/// source, incoming/outgoing edges, search) and the "blind project" capsule
/// contract must record unreadability as incompleteness, never as emptiness.
/// </summary>
public sealed class RetrievalCorrectnessTests : IntegrationTestBase
{
    private const string CalculatorSource = """
        namespace TestProject;

        public class Calculator
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private const string ServiceSource = """
        namespace TestProject;

        public class Service
        {
            public int Compute(int x, int y)
            {
                return new Calculator().Add(x, y);
            }
        }
        """;

    private async Task<string> IndexTwoClassSolutionAsync()
    {
        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = CalculatorSource,
                ["Service.cs"] = ServiceSource,
            });
        return await RunFullIndexAsync(DbPath);
    }

    [SkippableFact]
    public async Task GetSymbolSource_ReturnsDeclarationSpanText()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        var snapshotId = await IndexTwoClassSolutionAsync();
        var calculatorId = ResolveSymbolId(snapshotId, "global::TestProject.Calculator");

        using var store = OpenStore(DbPath);
        try
        {
            var source = store.GetSymbolSource(calculatorId, snapshotId, ViewKind.Declaration);
            Assert.NotNull(source);
            Assert.Contains("class Calculator", source);
            Assert.Contains("public int Add(int a, int b)", source);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task GetIncomingEdges_ReturnsCallsEdgeToCallee()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        var snapshotId = await IndexTwoClassSolutionAsync();
        var addId = ResolveSymbolId(snapshotId, "global::TestProject.Calculator.Add");

        using var store = OpenStore(DbPath);
        try
        {
            var incoming = store.GetIncomingEdges(snapshotId, addId)
                .Where(e => e.Kind == "Calls")
                .ToList();
            var edge = Assert.Single(incoming);
            Assert.Equal(ResolveSymbolId(snapshotId, "global::TestProject.Service.Compute"), edge.SourceSymbolId);
            Assert.Equal(addId, edge.TargetSymbolId);
            Assert.Equal("calls-v2", edge.ExtractorVersion);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task GetOutgoingEdges_FromComputeReturnsCallsAndConstructs()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        var snapshotId = await IndexTwoClassSolutionAsync();
        var computeId = ResolveSymbolId(snapshotId, "global::TestProject.Service.Compute");

        using var store = OpenStore(DbPath);
        try
        {
            var outgoing = store.GetOutgoingEdges(snapshotId, computeId);
            Assert.Contains(outgoing, e =>
                e.Kind == "Calls" &&
                e.TargetSymbolId == ResolveSymbolId(snapshotId, "global::TestProject.Calculator.Add"));
            Assert.Contains(outgoing, e =>
                e.Kind == "Constructs" &&
                e.TargetSymbolId == ResolveSymbolId(snapshotId, "global::TestProject.Calculator"));
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task SearchSymbols_FindsCalculatorWithCorrectId()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        var snapshotId = await IndexTwoClassSolutionAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var results = store.SearchSymbols("Calculator", snapshotId);
            var match = Assert.Single(results, r => r.FullyQualifiedName == "global::TestProject.Calculator");
            Assert.Equal(ResolveSymbolId(snapshotId, "global::TestProject.Calculator"), match.SymbolId);
            Assert.Equal("Type", match.Kind);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task BlindProject_RecordsIncompletenessAndIndexesNoSymbols()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("Readable",
            new Dictionary<string, string>
            {
                ["Marker.cs"] = """
                    namespace Readable;

                    public class Marker
                    {
                    }
                    """,
            });

        // No corlib: System.Object cannot bind, so the gate classifies the
        // project as blind and skips it instead of publishing an empty graph.
        CreateProject("Blind",
            new Dictionary<string, string>
            {
                ["BlindMarker.cs"] = """
                    namespace Blind;

                    public class BlindMarker
                    {
                    }
                    """,
            },
            msbuildProperties: new Dictionary<string, string>
            {
                ["DisableImplicitFrameworkReferences"] = "true",
            });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            var incompleteness = store.GetBindingIncompleteness(snapshotId);
            var blindRecords = incompleteness
                .Where(r => r.ProjectName == "Blind" && r.Reason == "project_unreadable")
                .ToList();
            Assert.NotEmpty(blindRecords);
            Assert.Contains(blindRecords, r => r.DocumentPath != null && r.DocumentPath.EndsWith("BlindMarker.cs", StringComparison.Ordinal));

            // The blind project contributed no symbols to the snapshot.
            foreach (var id in store.GetSymbolIdsInSnapshot(snapshotId))
            {
                var info = store.GetSymbolInfo(id, snapshotId);
                Assert.NotEqual("global::Blind.BlindMarker", info?.FullyQualifiedName);
            }
        }
        finally
        {
            store.Close();
        }
    }
}
