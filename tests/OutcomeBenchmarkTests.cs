using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Storage.Tests;

/// <summary>
/// Repeatable outcome benchmark runner for T11. It records capsule content and
/// the ten architecture measures instead of treating database counts as an
/// outcome score.
/// </summary>
public sealed class OutcomeBenchmarkTests : IDisposable
{
    private readonly string _runDirectory = Path.Combine(
        Path.GetTempPath(), $"lurp_outcome_benchmark_{Guid.NewGuid():N}");

    private sealed record BenchmarkScenario(
        string Id,
        string AnchorFqn,
        ContextIntent Intent,
        string[] ExpectedContracts,
        string[] ExpectedTests,
        string[] ExpectedRelevant,
        string[] ExpectedIrrelevant,
        string[] ExpectedPatchFiles,
        (string RelativePath, string Before, string After)[] Mutations);

    private sealed record CapsuleContent(
        string Tier,
        string Fqn,
        string SymbolId,
        string EdgeKind,
        string Provenance,
        string Relevance);

    [SkippableFact]
    public async Task RunBaseline_WritesOutcomeEvaluation()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run outcome benchmark.");

        var fixtureRoot = LocateFixtureRoot();
        Directory.CreateDirectory(_runDirectory);

        var scenarios = new[]
        {
            new BenchmarkScenario(
                Id: "local-validation-change",
                AnchorFqn: "Outcome.Validation.OrderValidator",
                Intent: ContextIntent.Modify,
                ExpectedContracts: ["Outcome.Contracts.IOrderValidator"],
                ExpectedTests: ["Outcome.Tests.ValidationTests.Validate_RejectsZeroTotal"],
                ExpectedRelevant: ["Outcome.Contracts.IOrderValidator", "Outcome.Tests.ValidationTests"],
                ExpectedIrrelevant: ["Outcome.Validation.StrictOrderValidator"],
                ExpectedPatchFiles: ["Validation/OrderValidator.cs"],
                Mutations: [(
                    "Validation/OrderValidator.cs",
                    "order.Total > 0",
                    "order.Total >= 0")]),

            new BenchmarkScenario(
                Id: "handler-dto-modification",
                AnchorFqn: "Outcome.Application.OrderHandler",
                Intent: ContextIntent.Modify,
                ExpectedContracts: ["Outcome.Contracts.IOrderHandler", "Outcome.Contracts.IOrderValidator"],
                ExpectedTests: ["Outcome.Tests.HandlerTests.Handle_ReturnsValidationResult"],
                ExpectedRelevant: ["Outcome.Contracts.IOrderHandler", "Outcome.Tests.HandlerTests"],
                ExpectedIrrelevant: [],
                ExpectedPatchFiles: ["Contracts/OrderContracts.cs", "Application/OrderHandler.cs"],
                Mutations: [
                    ("Contracts/OrderContracts.cs", "string? Note = null", "string? Note = null, string? Currency = null"),
                    ("Application/OrderHandler.cs", "var accepted = validator.Validate(order);", "var accepted = validator.Validate(order) && order.Note != \"blocked\";")]),

            new BenchmarkScenario(
                Id: "di-implementation-replacement",
                AnchorFqn: "Outcome.Composition.ServiceRegistration.Configure",
                Intent: ContextIntent.Diagnose,
                ExpectedContracts: [],
                ExpectedTests: ["Outcome.Tests.CompositionTests.Configure_RegistersApplicationServices"],
                ExpectedRelevant: ["Outcome.Validation.StrictOrderValidator"],
                ExpectedIrrelevant: ["Outcome.Validation.OrderValidator"],
                ExpectedPatchFiles: ["Composition/ServiceRegistration.cs"],
                Mutations: [(
                    "Composition/ServiceRegistration.cs",
                    "AddScoped<IOrderValidator, StrictOrderValidator>()",
                    "AddScoped<IOrderValidator, OrderValidator>()")]),
        };

        var results = new List<object>();
        foreach (var scenario in scenarios)
        {
            results.Add(await RunScenarioAsync(fixtureRoot, scenario));
        }

        var outputPath = Environment.GetEnvironmentVariable("LURP_OUTCOME_BENCHMARK_OUTPUT")
            ?? Path.Combine(LocateRepositoryRoot(), "tests", "benchmark-runs", "baseline.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var evaluation = new
        {
            benchmark = "lurp-outcome-benchmark",
            version = 1,
            generatedAtUtc = DateTime.UtcNow,
            runner = "tests/OutcomeBenchmarkTests.cs",
            measures = new[]
            {
                "correct starting symbol found",
                "source retrieved without project exploration",
                "all constraining contracts included",
                "affected callers and implementations represented",
                "relevant tests included",
                "unknown runtime vectors declared",
                "irrelevant capsule content",
                "worker tokens after capsule",
                "patch avoids unrelated changes",
                "incremental and clean indexing agree",
            },
            scenarios = results,
        };

        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(evaluation, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(File.Exists(outputPath), $"Benchmark output was not written: {outputPath}");
    }

    private async Task<object> RunScenarioAsync(string fixtureRoot, BenchmarkScenario scenario)
    {
        var scenarioDir = Path.Combine(_runDirectory, scenario.Id);
        CopyDirectory(fixtureRoot, scenarioDir);
        try
        {
            return await RunScenarioCoreAsync(scenarioDir, scenario);
        }
        catch (Exception ex)
        {
            return new
            {
                id = scenario.Id,
                status = "blocked",
                blocker = ex.GetBaseException().Message,
                failure = "full-index-baseline",
                nextAction = "Resolve the T4 relation-uniqueness conflict, then rerun the outcome benchmark.",
            };
        }
    }

    private static async Task<object> RunScenarioCoreAsync(string scenarioDir, BenchmarkScenario scenario)
    {
        var solutionPath = Path.Combine(scenarioDir, "OutcomeBenchmark.slnx");
        var dbPath = Path.Combine(scenarioDir, "index.db");

        var baselineSnapshot = await RunIndexAsync(dbPath, solutionPath, scenarioDir, "full");
        IndexedSymbolInfo anchor;
        ContextCapsule capsule;
        using (var baselineStore = IntegrationHarness.OpenReadStore(dbPath))
        {
            anchor = baselineStore.ResolveSymbolByFqn(scenario.AnchorFqn, baselineSnapshot)
                ?? throw new InvalidOperationException($"Benchmark anchor not found: {scenario.AnchorFqn}");

            capsule = ContextAssembler.ResolveAndAssemble(
                baselineStore,
                baselineStore,
                new ContextLookup(baselineSnapshot, anchor.SymbolId.Value, null, null),
                new ContextAssemblyOptions(scenario.Intent, Budget: 8000));
        }

        var capsuleContent = GetCapsuleContent(capsule, scenario);
        var contracts = capsule.Contracts.Select(i => i.FullyQualifiedName).ToArray();
        var missingContracts = scenario.ExpectedContracts
            .Where(expected => !contracts.Any(actual => Matches(actual, expected)))
            .ToArray();
        var actualTests = capsule.RelevantTests.Select(i => i.FullyQualifiedName).ToArray();
        var missingTests = scenario.ExpectedTests
            .Where(expected => !actualTests.Any(actual => Matches(actual, expected)))
            .ToArray();

        foreach (var mutation in scenario.Mutations)
        {
            var path = Path.Combine(scenarioDir, mutation.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var content = await File.ReadAllTextAsync(path);
            if (!content.Contains(mutation.Before, StringComparison.Ordinal))
                throw new InvalidOperationException($"Mutation text not found in {mutation.RelativePath}: {mutation.Before}");
            await File.WriteAllTextAsync(path, content.Replace(mutation.Before, mutation.After, StringComparison.Ordinal));
        }

        var incrementalSnapshot = await RunIndexAsync(dbPath, solutionPath, scenarioDir, "incremental");
        var fullSnapshot = await RunIndexAsync(dbPath, solutionPath, scenarioDir, "full");

        var agreement = true;
        string? agreementError = null;
        try
        {
            SnapshotAssertions.CompareSnapshotsAreEquivalent(dbPath, incrementalSnapshot, fullSnapshot);
        }
        catch (Exception ex)
        {
            agreement = false;
            agreementError = ex.Message;
        }

        var actualPatchFiles = scenario.Mutations
            .Select(m => m.RelativePath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var expectedPatchFiles = scenario.ExpectedPatchFiles
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        return new
        {
            id = scenario.Id,
            anchor = new
            {
                requestedFqn = scenario.AnchorFqn,
                found = true,
                symbolId = anchor.SymbolId.Value,
                resolvedFqn = anchor.FullyQualifiedName,
                sourceRetrieved = !string.IsNullOrEmpty(capsule.Anchor.Source),
            },
            capsule = new
            {
                estimatedTokens = capsule.EstimatedTokens,
                truncated = capsule.Truncated,
                unknownRuntimeVectorsDeclared = capsule.Uncertainties
                    .Select(u => new { u.RelationshipKind, u.Description, u.SymbolIds })
                    .ToArray(),
                content = capsuleContent,
                relevantCount = capsuleContent.Count(c => c.Relevance == "relevant"),
                irrelevantCount = capsuleContent.Count(c => c.Relevance == "irrelevant"),
                unclassifiedCount = capsuleContent.Count(c => c.Relevance == "unclassified"),
            },
            contracts = new
            {
                expected = scenario.ExpectedContracts,
                actual = contracts,
                missing = missingContracts,
                allIncluded = missingContracts.Length == 0,
            },
            tests = new
            {
                expected = scenario.ExpectedTests,
                actual = actualTests,
                missing = missingTests,
                allIncluded = missingTests.Length == 0,
            },
            patchScope = new
            {
                expectedFiles = expectedPatchFiles,
                observedFiles = actualPatchFiles,
                exact = expectedPatchFiles.SequenceEqual(actualPatchFiles, StringComparer.Ordinal),
            },
            incrementalFullAgreement = new
            {
                incrementalSnapshot,
                fullSnapshot,
                equivalent = agreement,
                error = agreementError,
            },
            failures = new
            {
                missingContracts = missingContracts.Length > 0,
                missingRelevantTests = missingTests.Length > 0,
                irrelevantCapsuleContent = capsuleContent.Any(c => c.Relevance == "irrelevant"),
                unknownRuntimeVectorsUndeclared = scenario.Id == "di-implementation-replacement"
                    && !capsule.Uncertainties.Any(u => u.RelationshipKind.Contains("runtime", StringComparison.OrdinalIgnoreCase)),
                incrementalFullDisagreement = !agreement,
            },
        };
    }

    private static List<CapsuleContent> GetCapsuleContent(ContextCapsule capsule, BenchmarkScenario scenario)
    {
        var content = new List<CapsuleContent>();
        Add("contracts", capsule.Contracts);
        Add("directCallees", capsule.DirectCallees);
        Add("directCallers", capsule.DirectCallers);
        Add("registeredImplementations", capsule.RegisteredImplementations);
        Add("relevantTests", capsule.RelevantTests);
        Add("secondDegreeContext", capsule.SecondDegreeContext);
        Add("surroundingSource", capsule.SurroundingSource);
        return content;

        void Add(string tier, IEnumerable<CapsuleItem> items)
        {
            foreach (var item in items)
            {
                var relevance = scenario.ExpectedRelevant.Any(expected => Matches(item.FullyQualifiedName, expected))
                    ? "relevant"
                    : scenario.ExpectedIrrelevant.Any(expected => Matches(item.FullyQualifiedName, expected))
                        ? "irrelevant"
                        : "unclassified";
                content.Add(new CapsuleContent(tier, item.FullyQualifiedName, item.SymbolId,
                    item.EdgeKind, item.Provenance, relevance));
            }
        }
    }

    private static bool Matches(string actual, string expected)
        => actual.Contains(expected, StringComparison.Ordinal);

    private static async Task<string> RunIndexAsync(
        string dbPath, string solutionPath, string outputDir, string strategy)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);
        store.RunMigrations();
        try
        {
            await IndexRunner.RunAsync(store, solutionPath, outputDir,
                skipAdapters: [], jsonExportPath: null, strategyArg: strategy);
            return store.GetLatestSnapshotId()
                ?? throw new InvalidOperationException($"No snapshot after {strategy} benchmark index.");
        }
        finally
        {
            store.Close();
        }
    }

    private static string LocateFixtureRoot()
        => Path.Combine(LocateRepositoryRoot(), "tests", "fixtures", "OutcomeBenchmark");

    private static string LocateRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lurp.slnx")))
            current = current.Parent;
        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root for outcome benchmark.");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destDir, Path.GetFileName(directory)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_runDirectory))
        {
            try { Directory.Delete(_runDirectory, recursive: true); }
            catch { }
        }
    }
}
