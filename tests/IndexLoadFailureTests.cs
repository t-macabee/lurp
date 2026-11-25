using System.Reflection;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class IndexLoadFailureTests : IntegrationTestBase
{
    private const string MissingProjectPath = "src/Missing/Missing.csproj";

    [Theory]
    [InlineData(".sln")]
    [InlineData(".slnx")]
    public void AllDeclaredCSharpProjectsMissing_RefusesToComplete(string extension)
    {
        var solutionPath = Path.ChangeExtension(SolutionPath, extension);
        File.WriteAllText(solutionPath, extension == ".slnx"
            ? $"""<Solution><Project Path="{MissingProjectPath}" /></Solution>"""
            : $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Missing", "{{MissingProjectPath.Replace('/', '\\')}}", "{6B5AA3F2-F944-4B7F-9857-864E86DD691C}"
                EndProject
                Global
                EndGlobal
                """);

        try
        {
            var (exitCode, _, stderr) = RunCli("index", solutionPath);
            using var store = OpenStore(DbPath);

            Assert.Equal(2, exitCode);
            Assert.Contains(MissingProjectPath, stderr);
            Assert.Null(store.GetLatestSnapshotId());
            Assert.Equal("workspace_unreadable", store.GetLatestSnapshotFailure()?.ReasonCode);
        }
        finally
        {
            TestDatabase.DeleteSolutionCache(solutionPath);
        }
    }

    [Fact]
    public async Task MissingProject_IsRecordedAndChangesFreshnessAndSnapshotIdentity()
    {
        CreateProject("Present", new Dictionary<string, string> { ["C.cs"] = "public class C {}" });
        var cleanSolution = File.ReadAllText(SolutionPath);
        File.WriteAllText(SolutionPath, cleanSolution.Replace("</Solution>",
            $"""<Project Path="{MissingProjectPath}" /></Solution>""", StringComparison.Ordinal));

        var partialId = await RunFullIndexAsync(DbPath);
        using (var store = OpenStore(DbPath))
        {
            var unreadable = Assert.Single(store.GetBindingIncompleteness(partialId),
                row => row.Reason == BindingIncompletenessReason.ProjectUnreadable);
            Assert.Equal(MissingProjectPath, unreadable.ProjectName);
            Assert.Null(unreadable.DocumentPath);
            var snapshot = store.LoadSnapshot(partialId)!;
            Assert.Equal([MissingProjectPath], snapshot.UnloadedProjects);
            Assert.Single(snapshot.Projects);
        }

        File.WriteAllText(SolutionPath, cleanSolution);
        var output = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run(
            [$"--solution={SolutionPath}", $"--output-dir={TestDir}", "--json"], sink));
        Assert.Null(output.Failure);
        using var status = JsonDocument.Parse(output.Stdout);
        Assert.False(status.RootElement.GetProperty("is_fresh").GetBoolean());
        Assert.Contains(MissingProjectPath, output.Stdout);

        await using (var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={TestDir}"]))
        {
            var tool = new StatusTool(session);
            using var mcpStatus = JsonDocument.Parse(await tool.LurpStatus(sections: "manifest", full: true));
            var freshness = mcpStatus.RootElement.GetProperty("freshness");
            Assert.False(freshness.GetProperty("is_fresh").GetBoolean());
            Assert.Contains(MissingProjectPath, freshness.GetProperty("mismatches").GetRawText());
        }

        var completeId = await RunFullIndexNoDeleteAsync(DbPath);
        using var completed = OpenStore(DbPath);
        Assert.NotEqual(partialId, completeId);
        Assert.DoesNotContain(completed.GetBindingIncompleteness(completeId),
            row => row.Reason == BindingIncompletenessReason.ProjectUnreadable);
    }

    [Theory]
    [InlineData(".sln", "invalid solution")]
    [InlineData(".slnx", "<NotASolution />")]
    public async Task MalformedSolution_IndexAndStatusExitTwo(string extension, string content)
    {
        CreateProject("Present", new Dictionary<string, string> { ["C.cs"] = "public class C {}" });
        await RunFullIndexAsync(DbPath);
        var malformed = Path.Combine(TestDir, "Malformed" + extension);
        File.WriteAllText(malformed, content);

        try
        {
            var index = RunCli("index", malformed);
            var status = RunCli("status", malformed);
            Assert.Equal((2, 2), (index.ExitCode, status.ExitCode));
            Assert.Contains(malformed, index.Stderr);
            Assert.Contains(malformed, status.Stderr);
        }
        finally
        {
            TestDatabase.DeleteSolutionCache(malformed);
        }
    }

    [Fact]
    public void EmptySolution_RemainsReadable()
    {
        File.WriteAllText(SolutionPath, "<Solution />");

        var (exitCode, _, stderr) = RunCli("index", SolutionPath);

        Assert.True(exitCode == 0, stderr);
        using var store = OpenStore(DbPath);
        Assert.NotNull(store.GetLatestSnapshotId());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Slow")]
    public async Task ProjectWriteLock_FailsTheRunAsStorageFailure(bool afterFirstProject)
    {
        CreateProject("First", new Dictionary<string, string> { ["C.cs"] = "public class First {}" });
        if (afterFirstProject)
            CreateProject("Second", new Dictionary<string, string> { ["C.cs"] = "public class Second {}" });

        var sink = new ProjectWriteLockSink(DbPath, afterFirstProject ? "Second" : "First");
        using var store = OpenStore(DbPath);
        var failure = await Record.ExceptionAsync(() => IndexRunner.RunAsync(
            store, SolutionPath, [], "full", output: sink));
        Assert.NotNull(sink.Release);
        await sink.Release;

        var sqlite = Assert.IsType<SqliteException>(failure);
        Assert.Equal(5, sqlite.SqliteErrorCode);
        Assert.Null(store.GetLatestSnapshotId());
        Assert.Equal("full_index_failure", store.GetLatestSnapshotFailure()?.ReasonCode);
    }

    [Fact]
    public async Task DatabaseAtPageLimit_DoesNotPublishPartialSnapshot()
    {
        CreateProject("First", new Dictionary<string, string> { ["C.cs"] = "public class First {}" });
        CreateProject("Second", new Dictionary<string, string>
        {
            ["C.cs"] = string.Join(Environment.NewLine, Enumerable.Range(0, 500).Select(i => $"public class Second{i} {{}}"))
        });

        using var store = OpenStore(DbPath);
        // max_page_count applies to the native connection that performs the write.
        var field = typeof(SqliteIndexStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var connection = Assert.IsType<SqliteConnection>(field.GetValue(store));
        var sink = new PageLimitSink(connection);
        var failure = await Record.ExceptionAsync(() => IndexRunner.RunAsync(
            store, SolutionPath, [], "full", output: sink));

        Assert.True(sink.LimitApplied);
        var failureText = failure?.Message ?? string.Join(Environment.NewLine, sink.Errors);
        Assert.Contains("database or disk is full", failureText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(failure);
        Assert.Null(store.GetLatestSnapshotId());
        Assert.Equal("full_index_failure", store.GetLatestSnapshotFailure()?.ReasonCode);
    }

    private sealed class PageLimitSink(SqliteConnection connection) : IOutputSink
    {
        public bool LimitApplied { get; private set; }
        public List<string> Errors { get; } = [];

        public void Write(string message)
        {
            if (message != "  [Second] ")
                return;

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA page_count";
            var pages = (long)command.ExecuteScalar()!;
            command.CommandText = FormattableString.Invariant($"PRAGMA max_page_count={pages + 2}");
            Assert.Equal(pages + 2, command.ExecuteScalar());
            LimitApplied = true;
        }

        public void WriteLine(string message = "") { }

        public void WriteErrorLine(string message = "")
        {
            Errors.Add(message);
            if (!message.StartsWith("FAILED:", StringComparison.Ordinal))
                return;

            // After observing the baseline swallow SQLITE_FULL, let it finish the run.
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA max_page_count=2147483646";
            command.ExecuteNonQuery();
        }
    }

    private (int ExitCode, string Stdout, string Stderr) RunCli(string mode, string solutionPath)
    {
        var args = LurpTool.Argv([$"--mode={mode}", $"--solution={solutionPath}", $"--output-dir={TestDir}"]);
        return TestProcess.Run(args[0], args[1..], TimeSpan.FromMinutes(2));
    }

    private sealed class ProjectWriteLockSink(string dbPath, string projectName) : IOutputSink
    {
        public Task? Release { get; private set; }

        public void Write(string message)
        {
            if (message != $"  [{projectName}] " || Release != null)
                return;

            var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            connection.Open();
            var transaction = connection.BeginTransaction();
            Release = ReleaseAsync(connection, transaction);
        }

        private static async Task ReleaseAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            using (connection)
            using (transaction)
                await Task.Delay(TimeSpan.FromSeconds(35));
        }

        public void WriteLine(string message = "") { }
        public void WriteErrorLine(string message = "") { }
    }
}
