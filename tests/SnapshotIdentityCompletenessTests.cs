using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Phase-A identity-completeness tests: configuration-only changes that must
///     force a rebuild but do not on main. Both fail on main because
///     <see cref="SnapshotIdentityInput" /> hashes only workspace id, document
///     hashes, target frameworks, project references, SDK/compiler/extractor
///     versions, and skipped adapters — not metadata references (A3) or
///     compilation options (A4) — and <see cref="WorkspaceFreshness" /> has no
///     comparators for either.
/// </summary>
public sealed class SnapshotIdentityCompletenessTests : IntegrationTestBase
{
    [SkippableFact]
    public async Task PackageReferenceChange_ForcesRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        var source = new Dictionary<string, string>
        {
            ["Svc.cs"] = """
                         namespace P;

                         public class Svc
                         {
                             public int Value() => 42;
                         }
                         """
        };

        CreateProject("P", source, []);
        await RestoreSolutionAsync();

        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        var snapshotV1 = await RunFullIndexAsync(DbPath);

        // Capture the V1 manifest now: after the second index writes V2, the
        // latest-snapshot lookup below would return V2's manifest and the
        // comparison would be current-vs-itself.
        SnapshotRow storedV1;
        using (var store = OpenStore(DbPath))
        {
            storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                       ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
        }

        // Rewrites the .csproj in place with a package reference. Written
        // directly rather than via CreateProject, whose slnx append would add
        // a duplicate project entry and make dotnet restore fail with MSB4025.
        WriteFile("P", "P.csproj", """
                                   <Project Sdk="Microsoft.NET.Sdk">
                                     <PropertyGroup>
                                       <TargetFramework>net10.0</TargetFramework>
                                       <ImplicitUsings>enable</ImplicitUsings>
                                       <Nullable>enable</Nullable>
                                     </PropertyGroup>
                                     <ItemGroup>
                                       <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                                     </ItemGroup>
                                   </Project>
                                   """);
        await RestoreSolutionAsync();

        var workspaceInfoV2 = await LoadWorkspaceInfoAsync();

        // Assertion 1: the identity must change. Fails on main: a
        // <PackageReference> change touches no hashed field, so
        // SnapshotIdentity.Create returns the same id for both.
        Assert.NotEqual(
            SnapshotIdentity.Create(workspaceInfoV1, new HashSet<string>()),
            SnapshotIdentity.Create(workspaceInfoV2, new HashSet<string>()));

        // Assertion 2: a second full index into the same db must write a new
        // snapshot. Fails on main: ResolveExistingSnapshot returns Reuse, so
        // "no new snapshot written" (IndexRunner).
        var snapshotV2 = await RunFullIndexNoDeleteAsync(DbPath);
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(2, store.GetSnapshotIds(workspaceInfoV1.Id.Value).Count);
            Assert.NotEqual(snapshotV1, snapshotV2);
        }

        // Assertion 3: the full-rebuild freshness gate must report a mismatch.
        // Fails on main: no metadata-reference comparator exists in
        // WorkspaceFreshness.GetFullRebuildMismatches.
        var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
            workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
        Assert.NotEmpty(mismatches);
    }

    [SkippableFact]
    public async Task DefineConstantsChange_ForcesRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        var source = new Dictionary<string, string>
        {
            ["Svc.cs"] = """
                         namespace P;

                         public class Svc
                         {
                         #if FEATURE
                             public int Extra() => 1;
                         #endif
                             public int Base() => 0;
                         }
                         """
        };

        CreateProject("P", source);
        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        var snapshotV1 = await RunFullIndexAsync(DbPath);

        // Capture the V1 manifest now: after the second index writes V2, the
        // latest-snapshot lookup below would return V2's manifest and the
        // comparison would be current-vs-itself.
        SnapshotRow storedV1;
        using (var store = OpenStore(DbPath))
        {
            storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                       ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
        }

        // Rewrites the .csproj in place to flip <DefineConstants>. Written
        // directly rather than via CreateProject, whose slnx append would add
        // a duplicate project entry and break solution loading.
        WriteFile("P", "P.csproj", """
                                   <Project Sdk="Microsoft.NET.Sdk">
                                     <PropertyGroup>
                                       <TargetFramework>net10.0</TargetFramework>
                                       <ImplicitUsings>enable</ImplicitUsings>
                                       <Nullable>enable</Nullable>
                                       <DefineConstants>FEATURE</DefineConstants>
                                     </PropertyGroup>
                                   </Project>
                                   """);
        var workspaceInfoV2 = await LoadWorkspaceInfoAsync();

        // Assertion 1: the identity must change. Fails on main: flipping
        // <DefineConstants> changes ParseOptions.PreprocessorSymbolNames, which
        // the identity payload does not capture, while the document bytes stay
        // byte-identical (only the #if branch taken changes).
        Assert.NotEqual(
            SnapshotIdentity.Create(workspaceInfoV1, new HashSet<string>()),
            SnapshotIdentity.Create(workspaceInfoV2, new HashSet<string>()));

        // Assertion 2: a second full index into the same db must write a new
        // snapshot. Fails on main for the same reason as assertion 1.
        var snapshotV2 = await RunFullIndexNoDeleteAsync(DbPath);
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(2, store.GetSnapshotIds(workspaceInfoV1.Id.Value).Count);
            Assert.NotEqual(snapshotV1, snapshotV2);
        }

        // Assertion 3: the full-rebuild freshness gate must report a mismatch.
        // Expected to fail on main: no compilation-options comparator exists
        // until Phase B adds CheckCompilationOptions — this is one of the
        // three things B closes.
        var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
            workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
        Assert.NotEmpty(mismatches);
    }

    [SkippableFact]
    public async Task ProjectReferenceChange_ForcesRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("Q", new Dictionary<string, string>
        {
            ["Q.cs"] = """
                       namespace Q;

                       public static class QHelper
                       {
                           public static int Twice(int v) => v * 2;
                       }
                       """
        });
        CreateProject("P", new Dictionary<string, string>
        {
            ["Svc.cs"] = """
                         namespace P;

                         public class Svc
                         {
                             public int Value() => 42;
                         }
                         """
        }, projectReferences: ["Q"]);
        await RestoreSolutionAsync();

        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        _ = await RunFullIndexAsync(DbPath);

        // Rewrites P.csproj in place to drop the project reference. Written
        // directly rather than via CreateProject, whose slnx append would add
        // a duplicate project entry and break solution loading.
        WriteFile("P", "P.csproj", """
                                   <Project Sdk="Microsoft.NET.Sdk">
                                     <PropertyGroup>
                                       <TargetFramework>net10.0</TargetFramework>
                                       <ImplicitUsings>enable</ImplicitUsings>
                                       <Nullable>enable</Nullable>
                                     </PropertyGroup>
                                   </Project>
                                   """);
        await RestoreSolutionAsync();

        var workspaceInfoV2 = await LoadWorkspaceInfoAsync();

        // Assertion 1: the identity must change (project graph is part of the
        // deterministic payload). Pins the existing correct gate.
        Assert.NotEqual(
            SnapshotIdentity.Create(workspaceInfoV1, new HashSet<string>()),
            SnapshotIdentity.Create(workspaceInfoV2, new HashSet<string>()));

        // Assertion 2: the full-rebuild freshness gate must report a mismatch.
        // Pins the existing CheckProjectGraph comparator.
        using (var store = OpenStore(DbPath))
        {
            var storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                           ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
            var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
                workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
            Assert.Contains(mismatches, m => m.Kind == MismatchKind.ProjectReferenceChanged);
        }
    }

    [SkippableFact]
    public async Task ExtractorVersionBump_ForcesRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("P", new Dictionary<string, string>
        {
            ["Svc.cs"] = """
                         namespace P;

                         public class Svc
                         {
                             public int Value() => 42;
                         }
                         """
        });
        await RestoreSolutionAsync();

        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        var snapshotV1 = await RunFullIndexAsync(DbPath);

        // Simulates a tool upgrade between runs: the persisted manifest's
        // extractor version is rewritten while the workspace stays identical.
        await using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE snapshots SET extractor_version = @newVersion WHERE snapshot_id = @sid;";
            command.Parameters.AddWithValue("@newVersion", "9.9.9-bump");
            command.Parameters.AddWithValue("@sid", snapshotV1);
            command.ExecuteNonQuery();
        }

        // The full-rebuild freshness gate must report a mismatch even though
        // no document, TFM, reference, or option changed. Pins the existing
        // CheckExtractorVersion comparator.
        using (var store = OpenStore(DbPath))
        {
            var storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                           ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
            var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
                workspaceInfoV1, SnapshotManifest.FromStorageManifest(storedV1));
            Assert.Contains(mismatches, m => m.Kind == MismatchKind.VersionChanged);
        }
    }

    [SkippableFact]
    public async Task Force_RebuildsIdenticalWorkspace()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("P", new Dictionary<string, string>
        {
            ["Svc.cs"] = """
                         namespace P;

                         public class Svc
                         {
                             public int Value() => 42;
                         }
                         """
        });
        await RestoreSolutionAsync();

        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        var snapshotV1 = await RunFullIndexAsync(DbPath);

        // A second full index over the identical workspace must reuse the
        // deterministic snapshot id and write nothing new.
        var snapshotV2 = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.Equal(snapshotV1, snapshotV2);

        DateTime? builtBeforeForce;
        using (var store = OpenStore(DbPath))
        {
            builtBeforeForce = store.LoadLatestSnapshot()?.CreatedAtUtc;
        }

        // --force must run re-extraction anyway while keeping the same
        // deterministic id (identical workspace => identical id).
        await Task.Delay(100);
        var snapshotV3 = await RunFullIndexNoDeleteAsync(DbPath, true);
        Assert.Equal(snapshotV1, snapshotV3);

        using (var store = OpenStore(DbPath))
        {
            // The forced rebuild replaced the snapshot in place: no duplicate
            // id rows, and the row was re-written (built_at advanced).
            Assert.Single(store.GetSnapshotIds(workspaceInfoV1.Id.Value));
            var latest = store.LoadLatestSnapshot()
                         ?? throw new InvalidOperationException("No snapshot after forced rebuild.");
            Assert.True(latest.CreatedAtUtc > builtBeforeForce,
                $"Expected the forced rebuild to re-write the snapshot row (built_at {builtBeforeForce:o} -> {latest.CreatedAtUtc:o}).");
        }
    }

    [SkippableFact]
    public async Task Freshness_StatVsHash_TouchWithoutEdit_IsFreshUnderHashMode()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("P", new Dictionary<string, string>
        {
            ["Svc.cs"] = """
                         namespace P;

                         public class Svc
                         {
                             public int Value() => 42;
                         }
                         """
        });
        await RestoreSolutionAsync();

        var snapshotV1 = await RunFullIndexAsync(DbPath);

        var svcPath = Path.Combine(TestDir, "src", "P", "Svc.cs");
        File.SetLastWriteTimeUtc(svcPath, DateTime.UtcNow.AddSeconds(5));

        // Stat-only mode sees the touched mtime and reports stale; hash mode
        // re-hashes the unchanged bytes and reports fresh. Pins the existing
        // CheckFreshnessCheap behavior.
        using (var store = OpenStore(DbPath))
        {
            var statStamp = WorkspaceFreshness.CheckFreshnessCheap(store, store, snapshotV1, FreshnessMode.Auto);
            Assert.Equal("stale", statStamp.State);

            var hashStamp = WorkspaceFreshness.CheckFreshnessCheap(store, store, snapshotV1, FreshnessMode.Hash);
            Assert.Equal("fresh", hashStamp.State);
        }
    }

    private async Task<WorkspaceInfo> LoadWorkspaceInfoAsync()
    {
        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(SolutionPath);
        return new WorkspaceInfo(solution, TestDir);
    }
}