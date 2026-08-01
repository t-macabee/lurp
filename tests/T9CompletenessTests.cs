using System.Text.Json;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

public sealed class T9CompletenessTests
{
    [Fact]
    public void SnapshotManifest_Completeness_RoundTrips_AndNullIsOmitted()
    {
        var testDir = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(testDir, "index.db");
            var completePath = Path.Combine(testDir, "complete.json");
            var complete = new SnapshotCompleteness
            {
                GeneratedTreesIncluded = false,
                ActiveTfms = new Dictionary<string, string>
                {
                    ["App"] = "net10.0",
                    ["Tests"] = "net10.0",
                },
                SkippedAdapters = ["MediatR", "Test"],
                ExtractorVersion = "extractor-test-v1",
            };

            using (var store = OpenStore(dbPath))
            {
                var manifest = CreateManifest(testDir, complete);
                manifest.Save(store, jsonExportPath: completePath);
            }

            var loaded = SnapshotManifest.Load(completePath);
            Assert.NotNull(loaded.Completeness);
            Assert.False(loaded.Completeness!.GeneratedTreesIncluded);
            Assert.Equal("net10.0", loaded.Completeness.ActiveTfms["App"]);
            Assert.Equal(["MediatR", "Test"], loaded.Completeness.SkippedAdapters);
            Assert.Equal("extractor-test-v1", loaded.Completeness.ExtractorVersion);

            var legacyPath = Path.Combine(testDir, "legacy.json");
            using (var store = OpenStore(dbPath))
            {
                var legacyManifest = CreateManifest(testDir, completeness: null);
                legacyManifest.Save(store, jsonExportPath: legacyPath);
            }

            var legacyJson = File.ReadAllText(legacyPath);
            Assert.DoesNotContain("\"completeness\"", legacyJson, StringComparison.Ordinal);
            Assert.Null(SnapshotManifest.Load(legacyPath).Completeness);
        }
        finally
        {
            DeleteTempDirectory(testDir);
        }
    }

    [Fact]
    public async Task StatusJson_IncludesLatestSnapshotManifestCompleteness()
    {
        var testDir = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(testDir, "index.db");
            var workspaceId = WorkspaceId.Create(testDir, Path.Combine(testDir, "Sample.slnx"));
            var snapshotId = SnapshotId.New().ToString();

            using (var store = OpenStore(dbPath))
            {
                store.SaveSnapshot(new SnapshotRow
                {
                    SnapshotId = snapshotId,
                    WorkspaceId = workspaceId.Value,
                    GitRoot = workspaceId.GitRoot,
                    SolutionPath = workspaceId.SolutionPath,
                    SdkVersion = "10.0.100",
                    CompilerVersion = "5.6.0",
                    CreatedAtUtc = DateTime.UtcNow,
                    DatabaseSchemaVersion = VersionConstants.DatabaseSchemaVersion,
                    OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
                    ExtractorVersion = "extractor-test-v2",
                    ToolVersion = "tool-test-v2",
                    Projects =
                    [
                        new ProjectRow { Name = "App", TargetFramework = "net10.0" },
                    ],
                    SkippedAdapters = ["EF Core"],
                });
                store.MarkSnapshotComplete(snapshotId);
            }

            var originalOut = Console.Out;
            using var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            try
            {
                await Lurp.Handlers.StatusHandler.Run(
                [
                    $"--output-dir={testDir}",
                    "--json",
                ]);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            using var document = JsonDocument.Parse(capturedOut.ToString());
            var manifest = document.RootElement.GetProperty("manifest");
            var completeness = manifest.GetProperty("completeness");

            Assert.False(completeness.GetProperty("generated_trees_included").GetBoolean());
            Assert.Equal("net10.0", completeness.GetProperty("active_tfms").GetProperty("App").GetString());
            Assert.Equal("EF Core", completeness.GetProperty("skipped_adapters")[0].GetString());
            Assert.Equal("extractor-test-v2", completeness.GetProperty("extractor_version").GetString());
        }
        finally
        {
            DeleteTempDirectory(testDir);
        }
    }

    private static SnapshotManifest CreateManifest(string testDir, SnapshotCompleteness? completeness)
    {
        var workspaceId = WorkspaceId.Create(testDir, Path.Combine(testDir, "Sample.slnx"));
        return new SnapshotManifest
        {
            SnapshotId = SnapshotId.New(),
            WorkspaceId = workspaceId,
            BuiltAtUtc = DateTime.UtcNow,
            DatabaseSchemaVersion = VersionConstants.DatabaseSchemaVersion,
            OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
            ExtractorVersion = "extractor-test-v1",
            ToolVersion = "tool-test-v1",
            TargetFrameworks = new Dictionary<string, string>
            {
                ["App"] = "net10.0",
            },
            ProjectGraph = new Dictionary<string, string[]>
            {
                ["App"] = [],
            },
            Completeness = completeness,
        };
    }

    private static SqliteIndexStore OpenStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open();
        store.RunMigrations();
        return store;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lurp_t9_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
