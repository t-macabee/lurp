using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class IncrementalNoOpPrecheckTests : IntegrationTestBase
{
    private const string Project = "Core";

    private const string WidgetSource = """
                                        namespace Core;

                                        public class Widget
                                        {
                                            public int Value { get; set; }
                                        }
                                        """;

    private void Seed()
    {
        CreateProject(Project, new Dictionary<string, string> { ["Widget.cs"] = WidgetSource });
    }

    private Task<string> RunIncrementalAsync(bool force = false) => RunIndexAsync("incremental", force);

    private async Task<string> RunIndexAsync(string strategy, bool force = false, string? solutionPath = null)
    {
        using var store = OpenStore(DbPath);
        var sink = new CapturingOutputSink();
        await IndexRunner.RunAsync(store, solutionPath ?? SolutionPath, [], strategy, false, sink, false, force, CancellationToken.None);
        return sink.Output.ToString();
    }

    [Fact]
    public async Task UnchangedWorkspace_SkipsWorkspaceLoad()
    {
        Seed();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var output = await RunIncrementalAsync();

        Assert.Contains("Workspace load skipped: stored snapshot is still current.", output);
        Assert.DoesNotContain("Loading solution...", output);
        Assert.Contains("documents_changed_this_run:          0", output);
        Assert.Contains($"Incremental index complete. Snapshot: {snapshotId}", output);
    }

    [Fact]
    public async Task EditedDocument_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        WriteFile(Project, "Widget.cs", WidgetSource + Environment.NewLine + "public class Extra { }");
        Touch(Path.Combine(TestDir, "src", Project, "Widget.cs"));

        var output = await RunIncrementalAsync();

        Assert.DoesNotContain("Workspace load skipped", output);
        Assert.Contains("Loading solution...", output);
    }

    [Fact]
    public async Task IncrementalRun_PrintsTheSummaryOnce()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        WriteFile(Project, "Widget.cs", WidgetSource + Environment.NewLine + "public class Extra { }");
        Touch(Path.Combine(TestDir, "src", Project, "Widget.cs"));

        var output = await RunIncrementalAsync();

        Assert.Equal(1, CountOccurrences(output, "Incremental index complete"));
        Assert.Contains("documents_in_snapshot:", output);
        Assert.Contains("projects_reextracted_this_run:", output);
        Assert.True(
            CountOccurrences(output, "edges_dropped_reason_other") <= 1,
            "the orphan-drop warning must print at most once");
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public async Task NewSourceFile_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        File.WriteAllText(Path.Combine(TestDir, "src", Project, "Added.cs"), "namespace Core; public class Added { }");

        var output = await RunIncrementalAsync();

        Assert.DoesNotContain("Workspace load skipped", output);
        Assert.Contains("Loading solution...", output);
    }

    [Fact]
    public async Task TouchedProjectFile_KeepsTheSkip()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        Touch(Path.Combine(TestDir, "src", Project, $"{Project}.csproj"));

        var output = await RunIncrementalAsync();

        Assert.Contains("Workspace load skipped", output);
        Assert.DoesNotContain("Loading solution...", output);
    }

    [Fact]
    public async Task RemovedBuildProps_RebuildsWithoutConditionalSymbol()
    {
        CreateProject(Project, new Dictionary<string, string>
        {
            ["Widget.cs"] = WidgetSource + "\n#if FEATURE\npublic class ConditionalWidget { }\n#endif\n"
        });
        var propsPath = Path.Combine(TestDir, "Directory.Build.props");
        File.WriteAllText(propsPath, """
            <Project><PropertyGroup><DefineConstants>$(DefineConstants);FEATURE</DefineConstants></PropertyGroup></Project>
            """);
        var originalId = await RunFullIndexAsync(DbPath);

        File.Delete(propsPath);
        var output = await RunIncrementalAsync();

        Assert.Contains("Full rebuild required", output);
        using var store = OpenStore(DbPath);
        Assert.Contains(store.SearchSymbolsPage("ConditionalWidget", originalId, 10, false, null, null).Items,
            item => item.DocCommentId == "T:Core.ConditionalWidget");
        var workspaceId = WorkspaceId.Create(TestDir, SolutionPath);
        var selectedId = store.GetSelectedLatestSnapshotId(workspaceId.Value);
        Assert.NotNull(selectedId);
        Assert.NotEqual(originalId, selectedId);
        Assert.DoesNotContain(store.SearchSymbolsPage("ConditionalWidget", selectedId, 10, false, null, null).Items,
            item => item.DocCommentId == "T:Core.ConditionalWidget");
    }

    [Fact]
    public async Task DeletedProject_DoesNotSkipAndRecordsUnloadedProject()
    {
        Seed();
        CreateProject("Keep", new Dictionary<string, string> { ["Keep.cs"] = "public class Keep { }" });
        await RunFullIndexAsync(DbPath);

        File.Delete(Path.Combine(TestDir, "src", Project, $"{Project}.csproj"));
        var output = await RunIncrementalAsync();

        Assert.Contains("Loading solution...", output);
        using var store = OpenStore(DbPath);
        var workspaceId = WorkspaceId.Create(TestDir, SolutionPath);
        var snapshot = store.LoadSelectedLatestSnapshot(workspaceId.Value);
        Assert.NotNull(snapshot);
        Assert.Contains($"src/{Project}/{Project}.csproj", SnapshotManifest.FromStorageManifest(snapshot).UnloadedProjects);
    }

    [Fact]
    public async Task BackdatedBuildProps_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        var propsPath = Path.Combine(TestDir, "Directory.Build.props");
        File.WriteAllText(propsPath, "<Project />");
        File.SetLastWriteTimeUtc(propsPath, DateTime.UtcNow.AddDays(-1));

        var output = await RunIncrementalAsync();

        Assert.Contains("Loading solution...", output);
        Assert.Contains("Workspace load skipped", await RunIncrementalAsync());
    }

    [Fact]
    public async Task BackdatedAssetsFile_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);
        var objDir = Path.Combine(TestDir, "src", Project, "obj");
        Directory.CreateDirectory(objDir);
        var assetsPath = Path.Combine(objDir, "project.assets.json");
        File.WriteAllText(assetsPath, "{}");
        File.SetLastWriteTimeUtc(assetsPath, DateTime.UtcNow.AddDays(-1));

        Assert.Contains("Loading solution...", await RunIncrementalAsync());
    }

    [Theory]
    [InlineData("incremental")]
    [InlineData("full")]
    public async Task ProjectComment_RefreshesInputsWhenTheSnapshotIsKept(string strategy)
    {
        Seed();
        var originalId = await RunFullIndexAsync(DbPath);

        var projectPath = Path.Combine(TestDir, "src", Project, $"{Project}.csproj");
        File.AppendAllText(projectPath, "<!-- comment -->");
        var output = await RunIndexAsync(strategy);

        Assert.Contains("Loading solution...", output);
        using var store = OpenStore(DbPath);
        var workspaceId = WorkspaceId.Create(TestDir, SolutionPath);
        Assert.Equal(originalId, store.GetSelectedLatestSnapshotId(workspaceId.Value));
        Assert.Contains("Workspace load skipped", await RunIncrementalAsync());
    }

    [Fact]
    public async Task SameIdFullRebuild_PromotesCapturedBuildInputs()
    {
        Seed();
        var originalId = await RunFullIndexAsync(DbPath);
        var projectPath = Path.Combine(TestDir, "src", Project, $"{Project}.csproj");
        File.AppendAllText(projectPath, "<!-- comment -->");

        await RunIndexAsync("full", force: true);
        var output = await RunIncrementalAsync();

        Assert.Contains("Workspace load skipped", output);
        Assert.Contains($"Incremental index complete. Snapshot: {originalId}", output);
    }

    [Fact]
    public async Task ParentBuildProps_DoesNotSkipWorkspaceLoad()
    {
        var solutionDir = Path.Combine(TestDir, "nested");
        WriteSolution(solutionDir, Project, new Dictionary<string, string> { ["Widget.cs"] = WidgetSource });
        var solutionPath = Path.Combine(solutionDir, "Test.slnx");
        var propsPath = Path.Combine(TestDir, "Directory.Build.props");
        File.WriteAllText(propsPath, "<Project />");
        try
        {
            await RunIndexAsync("full", solutionPath: solutionPath);
            File.AppendAllText(propsPath, "<!-- parent change -->");

            var output = await RunIndexAsync("incremental", solutionPath: solutionPath);

            Assert.Contains("Loading solution...", output);
        }
        finally
        {
            TestDatabase.DeleteSolutionCache(solutionPath);
        }
    }

    [SkippableFact]
    public async Task UnreadableParentDirectory_KeepsTheSkip()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix directory permissions.");

        var solutionDir = Path.Combine(TestDir, "nested");
        WriteSolution(solutionDir, Project, new Dictionary<string, string> { ["Widget.cs"] = WidgetSource });
        var solutionPath = Path.Combine(solutionDir, "Test.slnx");
        await RunIndexAsync("full", solutionPath: solutionPath);

        var originalMode = UnixFileMode.None;
        if (!OperatingSystem.IsWindows())
        {
            originalMode = File.GetUnixFileMode(TestDir);
            File.SetUnixFileMode(TestDir, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            try
            {
                _ = Directory.EnumerateFiles(TestDir).Any();
                Skip.If(true, "The test process can list an unreadable directory (for example, it runs as root).");
            }
            catch (UnauthorizedAccessException)
            {
            }

            Assert.Contains("Workspace load skipped", await RunIndexAsync("incremental", solutionPath: solutionPath));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(TestDir, originalMode);
            TestDatabase.DeleteSolutionCache(solutionPath);
        }
    }

    [SkippableFact]
    public async Task DanglingParentNuGetConfig_KeepsTheSkip()
    {
        Skip.If(OperatingSystem.IsWindows(), "Linux only (symlinks).");

        var solutionDir = Path.Combine(TestDir, "nested");
        WriteSolution(solutionDir, Project, new Dictionary<string, string> { ["Widget.cs"] = WidgetSource });
        var solutionPath = Path.Combine(solutionDir, "Test.slnx");
        var linkPath = Path.Combine(TestDir, "nuget.config");
        File.CreateSymbolicLink(linkPath, Path.Combine(TestDir, "missing.config"));
        try
        {
            await RunIndexAsync("full", solutionPath: solutionPath);

            Assert.Contains("Workspace load skipped", await RunIndexAsync("incremental", solutionPath: solutionPath));
        }
        finally
        {
            File.Delete(linkPath);
            TestDatabase.DeleteSolutionCache(solutionPath);
        }
    }

    [Fact]
    public async Task ExternalProject_DoesNotSkipAfterBackdatedProjectChange()
    {
        Seed();
        var solutionDir = Path.Combine(TestDir, "nested");
        Directory.CreateDirectory(solutionDir);
        var solutionPath = Path.Combine(solutionDir, "External.slnx");
        File.WriteAllText(solutionPath, $"""
            <Solution><Project Path="../src/{Project}/{Project}.csproj" /></Solution>
            """);
        try
        {
            await RunIndexAsync("full", solutionPath: solutionPath);
            Assert.Contains("Workspace load skipped", await RunIndexAsync("incremental", solutionPath: solutionPath));

            var projectPath = Path.Combine(TestDir, "src", Project, $"{Project}.csproj");
            File.AppendAllText(projectPath, "<!-- external change -->");
            File.SetLastWriteTimeUtc(projectPath, DateTime.UtcNow.AddDays(-1));

            Assert.Contains("Loading solution...", await RunIndexAsync("incremental", solutionPath: solutionPath));
            Assert.Contains("Workspace load skipped", await RunIndexAsync("incremental", solutionPath: solutionPath));
        }
        finally
        {
            TestDatabase.DeleteSolutionCache(solutionPath);
        }
    }

    [Fact]
    public async Task MigratedSnapshot_LoadsOnceToCaptureBuildInputs()
    {
        Seed();
        var snapshotId = await RunFullIndexAsync(DbPath);
        using (var connection = TestDatabase.Open(DbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TABLE IF EXISTS snapshot_build_inputs;
                DROP TABLE snapshot_excluded_symbols;
                ALTER TABLE snapshots DROP COLUMN analyzers_skipped;
                ALTER TABLE projects DROP COLUMN excluded_documents_fingerprint;
                DELETE FROM schema_metadata WHERE version > 52;
                UPDATE snapshots SET database_schema_version = 52;
                """;
            command.ExecuteNonQuery();
        }

        var firstRun = await RunIncrementalAsync();
        Assert.Contains("Loading solution...", firstRun);
        Assert.Contains($"Incremental index complete. Snapshot: {snapshotId}", firstRun);
        Assert.Contains("Workspace load skipped", await RunIncrementalAsync());
    }

    [Fact]
    public async Task Force_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        var output = await RunIncrementalAsync(force: true);

        Assert.DoesNotContain("Workspace load skipped", output);
        Assert.Contains("Loading solution...", output);
    }

    [Fact]
    public async Task TouchedGitIgnore_KeepsTheSkip()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        File.WriteAllText(Path.Combine(TestDir, ".gitignore"), "src/Core/Widget.cs\n");
        Touch(Path.Combine(TestDir, ".gitignore"));

        var output = await RunIncrementalAsync();

        Assert.Contains("Workspace load skipped: stored snapshot is still current.", output);
        Assert.DoesNotContain("Loading solution...", output);
    }

    [Fact]
    public async Task Cheap_UntrackedFileOlderThanTheSelection_IsFreshAndKeepsTheShortcut()
    {
        Seed();
        var projPath = Path.Combine(TestDir, "src", Project, $"{Project}.csproj");
        var csproj = File.ReadAllText(projPath).Replace(
            "</Project>",
            "  <ItemGroup>\n    <Compile Remove=\"Excluded/**\" />\n  </ItemGroup>\n</Project>",
            StringComparison.Ordinal);
        File.WriteAllText(projPath, csproj);

        var excludedDir = Path.Combine(TestDir, "src", Project, "Excluded");
        Directory.CreateDirectory(excludedDir);
        var oldFilePath = Path.Combine(excludedDir, "Old.cs");
        File.WriteAllText(oldFilePath, "namespace Core; class Old { }\n");
        var oldTime = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(oldFilePath, oldTime);
        Directory.SetLastWriteTimeUtc(excludedDir, oldTime);

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, snapshotId, FreshnessMode.Auto);
        Assert.Equal("fresh", stamp.State);
        Assert.Equal(0, stamp.ChangedDocumentCount);

        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(SolutionPath))!;
        var workspaceId = WorkspaceId.Create(gitRoot, Path.GetFullPath(SolutionPath));
        var shortcut = WorkspaceFreshness.CanSkipIncrementalIndex(store, workspaceId, gitRoot);
        Assert.NotNull(shortcut);
    }

    [Fact]
    public async Task StaleExtractorVersion_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE edges SET extractor_version = 'reflection-v1';";
            Assert.True(command.ExecuteNonQuery() > 0);
        }

        var output = await RunIncrementalAsync();

        Assert.DoesNotContain("Workspace load skipped", output);
        Assert.Contains("Full rebuild required", output);
        Assert.DoesNotContain("Full rebuild required: Full rebuild required:", output);
    }

    [Fact]
    public async Task ChangedContentWithPreservedMtime_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        WriteFile(Project, "Widget.cs", WidgetSource + Environment.NewLine + "public class Extra { }");
        File.SetLastWriteTimeUtc(Path.Combine(TestDir, "src", Project, "Widget.cs"), DateTime.UtcNow.AddYears(-1));

        var output = await RunIncrementalAsync();

        Assert.DoesNotContain("Workspace load skipped", output);
        Assert.Contains("Loading solution...", output);
    }

    [Fact]
    public async Task PrecheckWorkspaceId_MatchesWorkspaceInfoIdAfterRealLoad()
    {
        Seed();
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(SolutionPath))!;
        var precheckWorkspaceId = WorkspaceId.Create(gitRoot, Path.GetFullPath(SolutionPath));

        using var loader = new WorkspaceLoader();
        var (solution, _) = await loader.LoadAsync(SolutionPath, gitRoot, CancellationToken.None);
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        Assert.Equal(workspaceInfo.Id.Value, precheckWorkspaceId.Value);
    }

    [Fact]
    public async Task NewGitIgnoredCompileItem_DisablesTheSkip()
    {
        File.WriteAllText(Path.Combine(TestDir, ".gitignore"), "Late.cs\n");
        Seed();
        await RunFullIndexAsync(DbPath);

        WriteFile(Project, "Late.cs", "namespace Core; public class Late { }\n");

        using var store = OpenStore(DbPath);
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(SolutionPath))!;
        var workspaceId = WorkspaceId.Create(gitRoot, Path.GetFullPath(SolutionPath));
        var result = WorkspaceFreshness.CanSkipIncrementalIndex(store, workspaceId, gitRoot);

        Assert.Null(result);
    }
}
