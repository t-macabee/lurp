using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Tests;

/// <summary>
/// Phase-A identity-completeness tests: configuration-only changes that must
/// force a rebuild but do not on main. Both fail on main because
/// <see cref="SnapshotIdentityInput"/> hashes only workspace id, document
/// hashes, target frameworks, project references, SDK/compiler/extractor
/// versions, and skipped adapters — not metadata references (A3) or
/// compilation options (A4) — and <see cref="WorkspaceFreshness"/> has no
/// comparators for either.
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
                """,
        };

        CreateProject("P", source, packageReferences: []);
        await RestoreSolutionAsync();

        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        var snapshotV1 = await RunFullIndexAsync(DbPath);

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
        using (var store = OpenStore(DbPath))
        {
            var storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
            var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
                workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
            Assert.NotEmpty(mismatches);
        }
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
                """,
        };

        CreateProject("P", source);
        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        var snapshotV1 = await RunFullIndexAsync(DbPath);

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
        using (var store = OpenStore(DbPath))
        {
            var storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
            var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
                workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
            Assert.NotEmpty(mismatches);
        }
    }

    private async Task<WorkspaceInfo> LoadWorkspaceInfoAsync()
    {
        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(SolutionPath);
        return new WorkspaceInfo(solution, TestDir);
    }
}
