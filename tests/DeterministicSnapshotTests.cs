using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Deterministic snapshot identity: identical indexed state must produce an
/// identical <see cref="SnapshotId"/>, and different semantic index inputs
/// must produce different ids. A complete snapshot with the deterministic id
/// must be reused instead of duplicated, and an incomplete or failed snapshot
/// with the same id must not block a retry.
/// </summary>
public sealed class DeterministicSnapshotTests : IDisposable
{
    public void Dispose()
    {
        DeleteTempDirectory(_tempDir);
    }

    private string? _tempDir;

    [Fact]
    public void DeterministicSnapshot_SameCanonicalInput_ProducesSameId()
    {
        var input = BuildIdentityInput();
        var reordered = input with
        {
            DocumentHashes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Z.cs"] = "z-hash",
                ["src/App/Widget.cs"] = "abc123",
            },
            TargetFrameworks = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Library"] = "net10.0",
                ["App"] = "net10.0",
            },
            ProjectGraph = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["Library"] = [],
                ["App"] = ["Library", "Zebra"],
            },
            SkipAdapters = new HashSet<string>(StringComparer.Ordinal) { "Zebra", "MediatR" },
        };
        var sorted = reordered with
        {
            DocumentHashes = reordered.DocumentHashes
                .OrderByDescending(entry => entry.Key, StringComparer.Ordinal)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            TargetFrameworks = reordered.TargetFrameworks
                .OrderByDescending(entry => entry.Key, StringComparer.Ordinal)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            ProjectGraph = reordered.ProjectGraph
                .OrderByDescending(entry => entry.Key, StringComparer.Ordinal)
                .ToDictionary(
                    entry => entry.Key,
                    entry => (IReadOnlyCollection<string>)entry.Value.OrderByDescending(value => value, StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal),
            SkipAdapters = new HashSet<string>(reordered.SkipAdapters.OrderByDescending(value => value, StringComparer.Ordinal), StringComparer.Ordinal),
        };

        var first = SnapshotIdentity.Create(input);
        var second = SnapshotIdentity.Create(input);

        Assert.Equal(first, second);
        Assert.Equal(first.ToString(), second.ToString());
        Assert.Equal(SnapshotIdentity.Create(reordered), SnapshotIdentity.Create(sorted));
    }

    [Fact]
    public void DeterministicSnapshot_DocumentContentChange_ProducesDifferentId()
    {
        var before = BuildIdentityInput();
        var after = before with
        {
            DocumentHashes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/App/Widget.cs"] = "changed-content-hash",
            },
        };

        Assert.NotEqual(SnapshotIdentity.Create(before), SnapshotIdentity.Create(after));
    }

    [Fact]
    public void DeterministicSnapshot_DocumentPathChange_ProducesDifferentId()
    {
        var before = BuildIdentityInput();
        var after = before with
        {
            DocumentHashes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/App/MovedWidget.cs"] = "abc123",
            },
        };

        Assert.NotEqual(SnapshotIdentity.Create(before), SnapshotIdentity.Create(after));
    }

    [Fact]
    public void DeterministicSnapshot_SemanticConfigurationChange_ProducesDifferentId()
    {
        var before = BuildIdentityInput();

        var changedTfm = before with
        {
            TargetFrameworks = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["App"] = "net8.0",
            },
        };
        Assert.NotEqual(SnapshotIdentity.Create(before), SnapshotIdentity.Create(changedTfm));

        var changedExtractor = before with { ExtractorVersion = "extractor-v2" };
        Assert.NotEqual(SnapshotIdentity.Create(before), SnapshotIdentity.Create(changedExtractor));

        var changedSkippedAdapters = before with
        {
            SkipAdapters = new HashSet<string>(StringComparer.Ordinal) { "EF Core" },
        };
        Assert.NotEqual(SnapshotIdentity.Create(before), SnapshotIdentity.Create(changedSkippedAdapters));

        Assert.NotEqual(SnapshotIdentity.Create(before), SnapshotIdentity.Create(before with { SdkVersion = "10.0.101" }));
        Assert.NotEqual(SnapshotIdentity.Create(before), SnapshotIdentity.Create(before with { CompilerVersion = "5.0.1" }));
        Assert.NotEqual(SnapshotIdentity.Create(before), SnapshotIdentity.Create(before with
        {
            ProjectGraph = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["App"] = [],
            },
        }));
    }

    [SkippableFact]
    public async Task DeterministicSnapshot_RepeatedFullIndex_ReusesCompleteSnapshot()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        var (dbPath, solutionPath, testDir) = SetupFixture();

        var firstId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, testDir);

        var originalOut = Console.Out;
        using var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        string secondId;
        try
        {
            secondId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, testDir);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(firstId, secondId);
        Assert.Contains("already exists", capturedOut.ToString(), StringComparison.Ordinal);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM snapshots;";
            Assert.Equal(1L, countCmd.ExecuteScalar());

            using var statusCmd = conn.CreateCommand();
            statusCmd.CommandText = "SELECT status FROM snapshots;";
            Assert.Equal("complete", statusCmd.ExecuteScalar());
        }
    }

    [SkippableFact]
    public async Task DeterministicSnapshot_UnchangedIncrementalIndex_ReturnsExistingSnapshotId()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        var (dbPath, solutionPath, testDir) = SetupFixture();

        var firstId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, testDir);
        var incrementalId = await IntegrationHarness.RunIncrementalIndexAsync(dbPath, solutionPath, testDir);

        Assert.Equal(firstId, incrementalId);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM snapshots;";
        Assert.Equal(1L, countCmd.ExecuteScalar());
    }

    [SkippableFact]
    public async Task DeterministicSnapshot_FailedSnapshot_DoesNotBlockRetry()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        var (dbPath, solutionPath, testDir) = SetupFixture();

        var firstId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, testDir);

        SnapshotRow manifest;
        using (var store = IntegrationHarness.OpenReadStore(dbPath))
        {
            manifest = store.LoadLatestSnapshot()
                ?? throw new InvalidOperationException("The completed snapshot was not found.");
        }

        // Seed a separate database with the same deterministic identity in a
        // failed state. This exercises retry cleanup without ever changing the
        // completed snapshot in the original database.
        var retryDbPath = Path.Combine(testDir, "retry.db");
        using (var store = IntegrationHarness.OpenReadStore(retryDbPath))
        {
            store.SaveSnapshot(manifest);
            store.MarkSnapshotFailed(firstId, "full_index_failure", "simulated failure");
        }

        using (var conn = new SqliteConnection($"Data Source={retryDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status FROM snapshots;";
            Assert.Equal("failed", cmd.ExecuteScalar());
        }

        var retriedId = await IntegrationHarness.RunFullIndexAsync(retryDbPath, solutionPath, testDir);

        Assert.Equal(firstId, retriedId);

        using (var conn = new SqliteConnection($"Data Source={retryDbPath}"))
        {
            conn.Open();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM snapshots;";
            Assert.Equal(1L, countCmd.ExecuteScalar());

            using var statusCmd = conn.CreateCommand();
            statusCmd.CommandText = "SELECT status FROM snapshots;";
            Assert.Equal("complete", statusCmd.ExecuteScalar());
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static SnapshotIdentityInput BuildIdentityInput()
        => new(
            WorkspaceId: WorkspaceId.Create(@"C:\repo\lurp", @"C:\repo\lurp\App.slnx"),
            DocumentHashes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/App/Widget.cs"] = "abc123",
            },
            TargetFrameworks: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["App"] = "net10.0",
            },
            ProjectGraph: new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["App"] = ["Library"],
            },
            SdkVersion: "10.0.100",
            CompilerVersion: "5.0.0",
            ExtractorVersion: "extractor-v1",
            SkipAdapters: new HashSet<string>(StringComparer.Ordinal) { "MediatR" });

    private (string DbPath, string SolutionPath, string TestDir) SetupFixture()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_deterministic_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempDir);
        var dbPath = Path.Combine(_tempDir, "index.db");
        var solutionPath = IntegrationHarness.CopyFixtureToTemp(_tempDir);
        return (dbPath, solutionPath, _tempDir);
    }

    private static void DeleteTempDirectory(string? path)
    {
        if (path == null || !Directory.Exists(path))
            return;

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
