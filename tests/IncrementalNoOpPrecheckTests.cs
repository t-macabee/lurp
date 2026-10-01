using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using System.Text;

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

    private async Task<string> RunIncrementalAsync(bool force = false)
    {
        using var store = OpenStore(DbPath);
        var sink = new CapturingSink();
        await IndexRunner.RunAsync(store, SolutionPath, [], null, "incremental", false, sink, false, force, CancellationToken.None);
        return sink.Output.ToString();
    }

    private static void Touch(string fullPath)
    {
        File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddSeconds(10));
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
    public async Task TouchedProjectFile_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        Touch(Path.Combine(TestDir, "src", Project, $"{Project}.csproj"));

        var output = await RunIncrementalAsync();

        Assert.DoesNotContain("Workspace load skipped", output);
        Assert.Contains("Loading solution...", output);
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
    public async Task TouchedGitIgnore_DoesNotSkipWorkspaceLoad()
    {
        Seed();
        await RunFullIndexAsync(DbPath);

        File.WriteAllText(Path.Combine(TestDir, ".gitignore"), "src/Core/Widget.cs\n");
        Touch(Path.Combine(TestDir, ".gitignore"));

        var output = await RunIncrementalAsync();

        Assert.DoesNotContain("Workspace load skipped", output);
        Assert.Contains("Loading solution...", output);
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
    public async Task PrecheckWorkspaceId_MatchesWorkspaceInfoIdAfterRealLoad()
    {
        Seed();
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(SolutionPath))!;
        var precheckWorkspaceId = WorkspaceId.Create(gitRoot, Path.GetFullPath(SolutionPath));

        using var loader = new WorkspaceLoader();
        var (solution, _) = await loader.LoadAsync(SolutionPath, CancellationToken.None);
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        Assert.Equal(workspaceInfo.Id.Value, precheckWorkspaceId.Value);
    }

    private sealed class CapturingSink : IOutputSink
    {
        public StringBuilder Output { get; } = new();

        public void Write(string message)
        {
            Output.Append(message);
        }

        public void WriteLine(string message = "")
        {
            Output.AppendLine(message);
        }

        public void WriteErrorLine(string message = "")
        {
            Output.AppendLine(message);
        }
    }
}
