using Lurp.Adapters;
using Lurp.Shared;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using System.Text;
using DocumentId = Lurp.Workspace.DocumentId;

namespace Lurp.Storage.Tests;

public partial class MigrationRunnerTests
{
    public class ExtractorRegistryTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public ExtractorRegistryTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_extreg_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();
            _store = store;
            return store;
        }

        [Fact]
        public void UpsertExtractors_PopulatesTable()
        {
            var store = CreateStore();
            store.UpsertExtractors(ExtractorRegistry.All);

            // Verify the table is non-empty
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM extractors";
            var count = (long)cmd.ExecuteScalar()!;
            Assert.True(count > 0, "extractors table should have at least one row after UpsertExtractors");

            // Verify every registered extractor is present
            foreach (var (name, version, _) in ExtractorRegistry.All)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM extractors WHERE name = @name AND version = @version";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@version", version);
                var rowCount = (long)cmd.ExecuteScalar()!;
                Assert.Equal(1, rowCount);
            }

            store.Close();
        }

        [Fact]
        public void UpsertExtractors_IsIdempotent()
        {
            var store = CreateStore();

            // First call
            store.UpsertExtractors(ExtractorRegistry.All);
            var countAfterFirst = GetExtractorCount();

            // Second call : should not duplicate
            store.UpsertExtractors(ExtractorRegistry.All);
            var countAfterSecond = GetExtractorCount();

            Assert.Equal(countAfterFirst, countAfterSecond);
            store.Close();

            long GetExtractorCount()
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM extractors";
                return (long)cmd.ExecuteScalar()!;
            }
        }

        [Fact]
        public void UpsertExtractors_AllVersionsMatchRegistryConstants()
        {
            // Every registered version string should match an ExtractorConstants or VersionConstants value,
            // or one of the adapter version strings. This test is the build-time guard that the registry
            // stays in sync with the code.

            // Collect all known version strings that can appear in edges.extractor_version
            var known = new HashSet<string>
            {
                // ExtractorConstants
                ExtractorConstants.DeclaresExtractor,
                ExtractorConstants.CallsExtractor,
                ExtractorConstants.ConstructsExtractor,
                ExtractorConstants.OverridesExtractor,
                ExtractorConstants.HidesExtractor,
                ExtractorConstants.ReadsWritesExtractor,
                ExtractorConstants.ReturnsExtractor,
                ExtractorConstants.ThrowsExtractor,
                ExtractorConstants.ParameterDependenciesExtractor,
                ExtractorConstants.ReflectionExtractor,
                ExtractorConstants.StaticallyCallsExtractor,
                ExtractorConstants.PolymorphismExtractor,
                ExtractorConstants.DependencyInjectionExtractor,
                // VersionConstants
                VersionConstants.ExtractorVersion,
                // Adapter versions (hard-coded strings that appear in edge records)
                "aspnetcore-v1",
                "mediatr-v1",
                "efcore-v1",
                "serialization-v1",
                "test-v3",
            };

            var registryVersions = new HashSet<string>(ExtractorRegistry.All.Select(e => e.Version));

            foreach (var knownVersion in known)
            {
                Assert.True(registryVersions.Contains(knownVersion),
                    $"Registry is missing version '{knownVersion}'. Add it to ExtractorRegistry.All.");
            }
        }

        [Fact]
        public void EdgeExtractorVersions_AreCoveredByRegistry()
        {
            // Simulate: write edges with known extractor versions, upsert registry,
            // then verify every extractor_version in edges has a matching row in extractors.
            var store = CreateStore();
            var snapshotId = "snap-extreg-001";

            // Create edges using every known extractor version
            var edges = ExtractorRegistry.All.Select(e => new EdgeRecord
            {
                SourceSymbolId = "T:Src|asm1",
                TargetSymbolId = "T:Tgt|asm1",
                Kind = "References",
                Provenance = "compiler_proved",
                SnapshotId = snapshotId,
                ExtractorVersion = e.Version,
            }).ToList();

            // Also include an edge with "1.3.0" (VersionConstants.ExtractorVersion) to cover the Structural extractor
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = "T:Src|asm1",
                TargetSymbolId = "T:Tgt2|asm1",
                Kind = "References",
                Provenance = "compiler_proved",
                SnapshotId = snapshotId,
                ExtractorVersion = VersionConstants.ExtractorVersion,
            });

            store.SaveEdges(snapshotId, edges);
            store.UpsertExtractors(ExtractorRegistry.All);

            // The acceptance query from the task:
            // SELECT DISTINCT extractor_version FROM edges
            // must be a subset of extractors.version (or name||version)
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT e.extractor_version
                FROM edges e
                WHERE e.extractor_version NOT IN (SELECT version FROM extractors)
                  AND e.snapshot_id = @snapshotId
            ";
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            using var reader = cmd.ExecuteReader();
            var uncovered = new List<string>();
            while (reader.Read())
                uncovered.Add(reader.GetString(0));

            Assert.Empty(uncovered);
            store.Close();
        }

        [Fact]
        public void HasStaleExtractorVersions_DetectsVersionBump()
        {
            // Regression for T3 Finding 2: UpsertExtractors must prune superseded
            // (name, version) rows, otherwise old versions never leave the
            // extractors table and staleness can never be detected.
            var store = CreateStore();
            var snapshotId = "snap-extreg-bump";

            store.UpsertExtractors([("Calls", "calls-v1", "desc")]);

            store.SaveEdges(snapshotId, [new EdgeRecord
            {
                SourceSymbolId = "T:Src|asm1",
                TargetSymbolId = "T:Tgt|asm1",
                Kind = "Calls",
                Provenance = "compiler_proved",
                SnapshotId = snapshotId,
                ExtractorVersion = "calls-v1",
            }]);

            Assert.False(store.HasStaleExtractorVersions(snapshotId),
                "Edges should not be stale immediately after being written with the current version.");

            // Simulate a version bump: the extractor registry now reports calls-v2.
            store.UpsertExtractors([("Calls", "calls-v2", "desc")]);

            Assert.True(store.HasStaleExtractorVersions(snapshotId),
                "Edges written with a superseded extractor version must be detected as stale after a version bump.");

            store.Close();
        }
    }
}
