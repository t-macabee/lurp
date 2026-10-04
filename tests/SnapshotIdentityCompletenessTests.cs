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
    public async Task CompileRemoveForOneTfm_ForcesRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("P",
            new Dictionary<string, string>
            {
                ["X.cs"] = """
                           namespace P;

                           public static class X
                           {
                           #if NET9_0
                               public static int OnlyNet9() => 9;
                           #endif
                           }
                           """
            },
            targetFramework: "net9.0;net10.0");
        await RestoreSolutionAsync();

        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        var snapshotV1 = await RunFullIndexAsync(DbPath);
        Assert.Contains("M:P.X.OnlyNet9", SymbolDocCommentIds(DbPath, snapshotV1));

        // Rewrites P.csproj in place to drop X.cs from the net9.0 compilation.
        // No document content changes, so before the fix the project → document
        // membership was not hashed anywhere.
        WriteFile("P", "P.csproj", """
                                   <Project Sdk="Microsoft.NET.Sdk">
                                     <PropertyGroup>
                                       <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
                                       <ImplicitUsings>enable</ImplicitUsings>
                                       <Nullable>enable</Nullable>
                                     </PropertyGroup>
                                     <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
                                       <Compile Remove="X.cs" />
                                     </ItemGroup>
                                   </Project>
                                   """);
        await RestoreSolutionAsync();

        var workspaceInfoV2 = await LoadWorkspaceInfoAsync();

        // Assertion 1: the identity must change. Fails before the fix: membership
        // is not part of the payload and the document bytes are identical.
        Assert.NotEqual(
            SnapshotIdentity.Create(workspaceInfoV1, new HashSet<string>()),
            SnapshotIdentity.Create(workspaceInfoV2, new HashSet<string>()));

        // Assertion 2: the full-rebuild freshness gate must report a mismatch.
        using (var store = OpenStore(DbPath))
        {
            var storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                           ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
            var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
                workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
            Assert.Contains(mismatches, m => m.Kind == MismatchKind.ProjectDocumentsChanged);
        }

        // Assertion 3: a full index over the same database must write the new
        // workspace and drop the net9.0-only member.
        var snapshotV2 = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.NotEqual(snapshotV1, snapshotV2);
        Assert.DoesNotContain("M:P.X.OnlyNet9", SymbolDocCommentIds(DbPath, snapshotV2));
    }

    [SkippableFact]
    public async Task LinkedFileAddedToSecondProject_ForcesRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("A", new Dictionary<string, string>
        {
            ["Shared.cs"] = """
                            namespace A;

                            public static class Shared
                            {
                                public static int Foo() => 1;
                            }
                            """
        });
        CreateProject("B", new Dictionary<string, string>
        {
            ["B.cs"] = """
                       namespace B;

                       public static class BType
                       {
                       }
                       """
        });
        await RestoreSolutionAsync();

        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        var snapshotV1 = await RunFullIndexAsync(DbPath);

        // Rewrites B.csproj in place to compile A's file through a link. The
        // file bytes do not change; only the project → document membership does.
        WriteFile("B", "B.csproj", """
                                   <Project Sdk="Microsoft.NET.Sdk">
                                     <PropertyGroup>
                                       <TargetFramework>net10.0</TargetFramework>
                                       <ImplicitUsings>enable</ImplicitUsings>
                                       <Nullable>enable</Nullable>
                                     </PropertyGroup>
                                     <ItemGroup>
                                       <Compile Include="..\A\Shared.cs" Link="Shared.cs" />
                                     </ItemGroup>
                                   </Project>
                                   """);
        await RestoreSolutionAsync();

        var workspaceInfoV2 = await LoadWorkspaceInfoAsync();

        // Assertion 1: the identity must change. Fails before the fix: the
        // linked file's bytes and every hashed option are unchanged.
        Assert.NotEqual(
            SnapshotIdentity.Create(workspaceInfoV1, new HashSet<string>()),
            SnapshotIdentity.Create(workspaceInfoV2, new HashSet<string>()));

        // Assertion 2: the full-rebuild freshness gate must report a mismatch.
        using (var store = OpenStore(DbPath))
        {
            var storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                           ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
            var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
                workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
            Assert.Contains(mismatches, m => m.Kind == MismatchKind.ProjectDocumentsChanged);
        }

        // Assertion 3: the rebuild must write a new snapshot instead of reusing
        // the stale identity.
        var snapshotV2 = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.NotEqual(snapshotV1, snapshotV2);
    }

    [SkippableFact]
    public async Task UnchangedMultiTargetProject_SecondIndex_ReportsNoChangesAndKeepsSnapshotId()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("P",
            new Dictionary<string, string>
            {
                ["X.cs"] = """
                           namespace P;

                           public static class X
                           {
                               public static int Value() => 42;
                           }
                           """
            },
            targetFramework: "net9.0;net10.0");
        await RestoreSolutionAsync();

        var snapshotV1 = await RunFullIndexAsync(DbPath);

        // A second full index over the same multi-target project. The
        // compilation-options fingerprint must hash exactly the documents
        // BuildDocumentMap keeps, not the generated obj/ documents it filters
        // out, so the identity must not change when no source changed.
        var sink = new CapturingSink();
        using var store = OpenStore(DbPath);
        await IndexRunner.RunAsync(
            store, SolutionPath,
            [], null, "full",
            false, sink, false, false, CancellationToken.None);

        Assert.Contains("Identical complete snapshot", sink.Output);

        var latest = store.LoadLatestSnapshot()
                     ?? throw new InvalidOperationException("No snapshot after the second index.");
        Assert.Equal(snapshotV1, latest.SnapshotId);
        Assert.Single(store.GetSnapshotIds(latest.WorkspaceId));
    }

    [SkippableFact]
    public async Task AddedFile_StaysIncremental()
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
        _ = await RunFullIndexAsync(DbPath);

        WriteFile("P", "Extra.cs", """
                                    namespace P;

                                    public static class Extra
                                    {
                                        public static int Value() => 1;
                                    }
                                    """);

        var workspaceInfoV2 = await LoadWorkspaceInfoAsync();

        // A plain file add must stay on the incremental path: the new path is
        // absent from the stored document set, so the membership gate is silent.
        using var store = OpenStore(DbPath);
        var storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                       ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
        var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
            workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
        Assert.DoesNotContain(mismatches, m => m.Kind == MismatchKind.ProjectDocumentsChanged);
    }

    [SkippableFact]
    public async Task DeletedFile_StaysIncremental()
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
                         """,
            ["Extra.cs"] = """
                           namespace P;

                           public static class Extra
                           {
                               public static int Value() => 1;
                           }
                           """
        });
        await RestoreSolutionAsync();

        var workspaceInfoV1 = await LoadWorkspaceInfoAsync();
        _ = await RunFullIndexAsync(DbPath);

        DeleteFile("P", "Extra.cs");

        var workspaceInfoV2 = await LoadWorkspaceInfoAsync();

        // A plain file delete must stay on the incremental path: the removed
        // path is absent from the current document set, so the gate is silent.
        using var store = OpenStore(DbPath);
        var storedV1 = store.LoadLatestSnapshot(workspaceInfoV1.Id.Value)
                       ?? throw new InvalidOperationException("No stored V1 snapshot manifest.");
        var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
            workspaceInfoV2, SnapshotManifest.FromStorageManifest(storedV1));
        Assert.DoesNotContain(mismatches, m => m.Kind == MismatchKind.ProjectDocumentsChanged);
    }

    [SkippableFact]
    public async Task NullStoredDocumentPaths_TreatedAsUnknown_NoMembershipMismatch()
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

        var workspaceInfo = await LoadWorkspaceInfoAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        // Simulates a pre-032 snapshot: the column exists but holds null, which
        // means "unknown", so the membership comparator must skip the project.
        await using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE projects SET document_paths_json = NULL WHERE snapshot_id = @sid;";
            command.Parameters.AddWithValue("@sid", snapshotId);
            command.ExecuteNonQuery();
        }

        using var store = OpenStore(DbPath);
        var stored = store.LoadLatestSnapshot(workspaceInfo.Id.Value)
                     ?? throw new InvalidOperationException("No stored snapshot manifest.");
        var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(
            workspaceInfo, SnapshotManifest.FromStorageManifest(stored));
        Assert.DoesNotContain(mismatches, m => m.Kind == MismatchKind.ProjectDocumentsChanged);
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

    private static HashSet<string> SymbolDocCommentIds(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.doc_comment_id
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private async Task<WorkspaceInfo> LoadWorkspaceInfoAsync()
    {
        using var workspace = MSBuildWorkspace.Create(LurpCache.CreateWorkspaceGlobalProperties(SolutionPath));
        var solution = await workspace.OpenSolutionAsync(SolutionPath);
        return new WorkspaceInfo(solution, TestDir);
    }

    private sealed class CapturingSink : IOutputSink
    {
        private readonly List<string> _lines = [];

        public string Output => string.Join("\n", _lines);

        public void Write(string message)
        {
            _lines.Add(message);
        }

        public void WriteLine(string message = "")
        {
            _lines.Add(message);
        }

        public void WriteErrorLine(string message = "")
        {
            _lines.Add(message);
        }
    }
}