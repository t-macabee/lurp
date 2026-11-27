using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Lurp.Adapters;
using Lurp.Handlers;
using Lurp.Helpers;
using Lurp.Parity.Shared;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class IncrementalPerformanceTests : IntegrationTestBase
{
    [Fact]
    public async Task TestAdapter_IncrementalBodyEdit_ExtractsOnlyReachedBodies_AndMatchesFullTestedBy()
    {
        var prodSource = new StringBuilder();
        prodSource.AppendLine("namespace Prod;");
        prodSource.AppendLine("public class ProdDirect { public int Value() => 1; }");
        prodSource.AppendLine("public class ProdHelper { public int Value() => 2; }");
        prodSource.AppendLine("public class ProdFixture { public int Value() => 3; }");
        prodSource.AppendLine("public class ProdDataSource { public int Value() => 4; }");
        prodSource.AppendLine("public class ProdLambda { public int Value() => 5; }");
        prodSource.AppendLine("public class ProdUnreachableHelper { public int Value() => 6; }");
        for (var i = 1; i <= 50; i++)
            prodSource.AppendLine(CultureInfo.InvariantCulture, $"public class ProdUntouched{i:D2} {{ public int Value() => {i}; }}");

        CreateProject("Prod", new Dictionary<string, string>
        {
            ["Models.cs"] = prodSource.ToString()
        });

        var testFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SharedHelpers.cs"] = """
                namespace Checks;
                public static class SharedHelpers
                {
                    public static int ReachProd() => new Prod.ProdHelper().Value();
                }
                """,
            ["UnreachableHelpers.cs"] = """
                namespace Checks;
                public static class UnreachableHelpers
                {
                    public static int UnreachableHelper() => new Prod.ProdUnreachableHelper().Value();
                }
                """,
            ["SharedFixture.cs"] = """
                namespace Checks;
                public sealed class SharedFixture
                {
                    public SharedFixture() { _ = new Prod.ProdFixture().Value(); }
                }
                """,
            ["SharedDataSource.cs"] = """
                using System.Collections.Generic;
                namespace Checks;
                public static class SharedDataSource
                {
                    public static IEnumerable<object[]> Cases()
                    {
                        yield return new object[] { new Prod.ProdDataSource().Value() };
                    }
                }
                """,
            ["EditedTests.cs"] = """
                using System;
                using Xunit;
                namespace Checks;
                public sealed class EditedTests : IClassFixture<SharedFixture>
                {
                    [Theory]
                    [MemberData(nameof(SharedDataSource.Cases), MemberType = typeof(SharedDataSource))]
                    public void VerifyAll(int _)
                    {
                        Func<int> lambda = () => new Prod.ProdLambda().Value();
                        _ = new Prod.ProdDirect().Value() + SharedHelpers.ReachProd() + lambda();
                    }
                }
                """
        };

        for (var i = 1; i <= 50; i++)
        {
            testFiles[$"UntouchedTest{i:D2}.cs"] = $$"""
                using Xunit;
                namespace Checks;
                public sealed class UntouchedTest{{i:D2}}
                {
                    [Fact]
                    public void Check{{i:D2}}()
                    {
                        _ = new Prod.ProdUntouched{{i:D2}}().Value();
                    }
                }
                """;
        }

        CreateProject("Checks", testFiles, ["xunit@2.9.3"], projectReferences: ["Prod"]);
        RestoreSolution();

        await RunFullIndexAsync(DbPath);

        // Body-only edit in EditedTests.cs
        WriteFile("Checks", "EditedTests.cs", """
            using System;
            using Xunit;
            namespace Checks;
            public sealed class EditedTests : IClassFixture<SharedFixture>
            {
                [Theory]
                [MemberData(nameof(SharedDataSource.Cases), MemberType = typeof(SharedDataSource))]
                public void VerifyAll(int _)
                {
                    Func<int> lambda = () => new Prod.ProdLambda().Value();
                    var sum = new Prod.ProdDirect().Value() + SharedHelpers.ReachProd() + lambda();
                    _ = sum + 1;
                }
            }
            """);

        var extractedBodyPaths = new ConcurrentBag<string>();
        string incrementalSnapshot;
        TestAdapter.BodyRootObserver.Value = path => extractedBodyPaths.Add(path);
        try
        {
            (incrementalSnapshot, _) = await RunIndexAsync();
        }
        finally
        {
            TestAdapter.BodyRootObserver.Value = null;
        }

        var fullDbPath = Path.Combine(TestDir, "full-parity.db");
        var fullSnapshot = await RunFullIndexAsync(fullDbPath);

        Assert.Equal(fullSnapshot, incrementalSnapshot);
        var diffs = SnapshotComparison.Compare(DbPath, incrementalSnapshot, fullDbPath, fullSnapshot);
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));

        // Reached files MUST be extracted, while none of the 50 untouched test files may be extracted.
        Assert.Contains(extractedBodyPaths, p => p.EndsWith("/EditedTests.cs", StringComparison.Ordinal));
        Assert.Contains(extractedBodyPaths, p => p.EndsWith("/SharedHelpers.cs", StringComparison.Ordinal));
        Assert.Contains(extractedBodyPaths, p => p.EndsWith("/SharedFixture.cs", StringComparison.Ordinal));
        Assert.Contains(extractedBodyPaths, p => p.EndsWith("/SharedDataSource.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(extractedBodyPaths, p => p.EndsWith("/UnreachableHelpers.cs", StringComparison.Ordinal));
        for (var i = 1; i <= 50; i++)
        {
            var suffix = $"/UntouchedTest{i:D2}.cs";
            Assert.DoesNotContain(extractedBodyPaths, p => p.EndsWith(suffix, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BodyOnlyEdit_InMiddleProjectOfChain_DoesNotExtractLastProject_AndMatchesFull(int scenario)
    {
        CreateProject("ProjA", new Dictionary<string, string>
        {
            ["BaseService.cs"] = """
                namespace ProjA;
                public class BaseService
                {
                    public int Compute(int x) => x + 1;
                }
                """
        });

        CreateProject("ProjB", new Dictionary<string, string>
        {
            ["UsedByC.cs"] = """
                namespace ProjB;
                public class UsedByC
                {
                    public int GetValue() => 10;
                }
                """,
            ["MiddleService.cs"] = """
                namespace ProjB;
                public class MiddleService
                {
                    public int Run(int x)
                    {
                        return new ProjA.BaseService().Compute(x);
                    }
                }
                """
        }, projectReferences: ["ProjA"]);

        CreateProject("ProjC", new Dictionary<string, string>
        {
            ["LeafService.cs"] = """
                namespace ProjC;
                public class LeafService
                {
                    public int Execute()
                    {
                        int unused = 42;
                        return new ProjB.UsedByC().GetValue() + GeneratedHelper.Value();
                    }
                }
                """
        }, projectReferences: ["ProjB"]);

        var projectDir = Path.Combine(TestDir, "src", "ProjC");
        Directory.CreateDirectory(Path.Combine(projectDir, "obj"));
        File.WriteAllText(Path.Combine(projectDir, "obj", "Generated.cs"),
            "namespace ProjC; public static class GeneratedHelper { public static int Value() => 1; }");
        var projectPath = Path.Combine(projectDir, "ProjC.csproj");
        File.WriteAllText(projectPath, File.ReadAllText(projectPath).Replace("</Project>",
            "<ItemGroup><Compile Include=\"obj/Generated.cs\" /></ItemGroup></Project>", StringComparison.Ordinal));

        var previousSnapshot = await RunFullIndexAsync(DbPath);
        if (scenario == 1)
            File.AppendAllText(Path.Combine(projectDir, "obj", "Generated.cs"), "\npublic class ExtraGenerated {}");
        if (scenario == 2)
        {
            using var connection = TestDatabase.Open(DbPath);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE projects SET excluded_documents_fingerprint = NULL WHERE snapshot_id = @id; DELETE FROM snapshot_excluded_symbols WHERE snapshot_id = @id;";
            command.Parameters.AddWithValue("@id", previousSnapshot);
            command.ExecuteNonQuery();
        }

        // Body-only edit in ProjB/MiddleService.cs (not referenced by ProjC).
        WriteFile("ProjB", "MiddleService.cs", """
            namespace ProjB;
            public class MiddleService
            {
                public int Run(int x)
                {
                    var y = new ProjA.BaseService().Compute(x);
                    return y + 2;
                }
            }
            """);

        var requested = new ConcurrentBag<string>();
        string incrementalSnapshot;
        string output;
        CompilationHelper.CompilationRequestObserver.Value = requested.Add;
        try
        {
            (incrementalSnapshot, output) = await RunIndexAsync();
        }
        finally
        {
            CompilationHelper.CompilationRequestObserver.Value = null;
        }

        // ProjC must not be loaded or re-extracted.
        Assert.DoesNotContain("[ProjC]", output, StringComparison.Ordinal);
        if (scenario == 0)
            Assert.DoesNotContain("ProjC", requested);
        else
            Assert.Contains("ProjC", requested);

        using (var store = OpenStore(DbPath))
        {
            var excluded = store.LoadExcludedSymbolsByProject(incrementalSnapshot);
            Assert.Contains(excluded["ProjC"], id => id.StartsWith("M:ProjC.GeneratedHelper.Value", StringComparison.Ordinal));
            if (scenario == 1)
                Assert.Contains(excluded["ProjC"], id => id.StartsWith("T:ProjC.ExtraGenerated", StringComparison.Ordinal));
        }

        var fullDbPath = Path.Combine(TestDir, "full-parity.db");
        var fullSnapshot = await RunFullIndexAsync(fullDbPath);

        Assert.Equal(fullSnapshot, incrementalSnapshot);
        var diffs = SnapshotComparison.Compare(DbPath, incrementalSnapshot, fullDbPath, fullSnapshot);
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [Fact]
    public async Task SkipAnalyzers_ProducesDistinctSnapshotId_ReportsInStatusAndDiagnostics_AndDoesNotMixOnIncremental()
    {
        CreateProject("AnalyzerApp", new Dictionary<string, string>
        {
            ["Service.cs"] = """
                namespace AnalyzerApp;
                public class Service
                {
                    private int ComputeConstant() => 42;
                    public int Run() => ComputeConstant();
                }
                """
        }, msbuildProperties: new Dictionary<string, string>
        {
            ["AnalysisMode"] = "All",
            ["EnableNETAnalyzers"] = "true",
            ["AnalysisLevel"] = "latest"
        });

        // 1. Index with --skip-analyzers
        var skipResult = await CapturingOutputSink.CaptureAsync(sink =>
            IndexHandler.Run([$"--solution={SolutionPath}", $"--output-dir={TestDir}", "--strategy=full", "--skip-analyzers"], sink));
        Assert.Null(skipResult.Failure);

        string skippedSnapshotId;
        using (var store = OpenStore(DbPath))
        {
            skippedSnapshotId = store.LoadLatestSnapshot()!.SnapshotId;
            var diags = store.GetDiagnostics(skippedSnapshotId);
            Assert.DoesNotContain(diags, d => d.Id == "CA1822");
        }

        // Check diagnostics & status report analyzers_skipped: true
        var diagSkip = CapturingOutputSink.Capture(sink =>
            DiagnosticsHandler.Run([$"--output-dir={TestDir}", "--output=json", "--severity=all"], sink));
        using (var diagDoc = JsonDocument.Parse(diagSkip.Stdout))
        {
            Assert.True(diagDoc.RootElement.GetProperty("analyzers_skipped").GetBoolean());
        }

        var statusSkip = await CapturingOutputSink.CaptureAsync(sink =>
            StatusHandler.Run([$"--output-dir={TestDir}", "--json"], sink));
        using (var statusDoc = JsonDocument.Parse(statusSkip.Stdout))
        {
            Assert.True(statusDoc.RootElement.GetProperty("manifest")
                .GetProperty("completeness")
                .GetProperty("analyzers_skipped").GetBoolean());
        }

        // 2. Unflagged incremental run afterwards (even without file changes) must NOT reuse the skipped snapshot
        var fullResult = await CapturingOutputSink.CaptureAsync(sink =>
            IndexHandler.Run([$"--solution={SolutionPath}", $"--output-dir={TestDir}"], sink));
        Assert.Null(fullResult.Failure);

        string unflaggedSnapshotId;
        using (var store = OpenStore(DbPath))
        {
            unflaggedSnapshotId = store.LoadLatestSnapshot()!.SnapshotId;
            Assert.NotEqual(skippedSnapshotId, unflaggedSnapshotId);
            var diags = store.GetDiagnostics(unflaggedSnapshotId);
            Assert.Contains(diags, d => d.Id == "CA1822");
        }

        var diagFull = CapturingOutputSink.Capture(sink =>
            DiagnosticsHandler.Run([$"--output-dir={TestDir}", "--output=json", "--severity=all"], sink));
        using (var diagDoc = JsonDocument.Parse(diagFull.Stdout))
        {
            Assert.False(diagDoc.RootElement.GetProperty("analyzers_skipped").GetBoolean());
        }

        // 3. Incremental run with --skip-analyzers after a body edit must rebuild without mixing analyzer diagnostics
        WriteFile("AnalyzerApp", "Service.cs", """
            namespace AnalyzerApp;
            public class Service
            {
                private int ComputeConstant() => 43;
                public int Run() => ComputeConstant();
            }
            """);

        var reSkipResult = await CapturingOutputSink.CaptureAsync(sink =>
            IndexHandler.Run([$"--solution={SolutionPath}", $"--output-dir={TestDir}", "--skip-analyzers"], sink));
        Assert.Null(reSkipResult.Failure);
        Assert.Contains("Analyzer mode changed", reSkipResult.Stdout, StringComparison.Ordinal);

        using (var store = OpenStore(DbPath))
        {
            var latest = store.LoadLatestSnapshot()!;
            Assert.True(latest.AnalyzersSkipped);
            Assert.DoesNotContain(store.GetDiagnostics(latest.SnapshotId), d => d.Id == "CA1822");
        }
    }
}
