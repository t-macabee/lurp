using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Tests;

/// <summary>
/// Contract tests for MCP index tool run locking: a held solution or database lock
/// prevents the MCP index run from starting, and both locks are held for the entire
/// background run then released on completion.
/// </summary>
public sealed class ManagedIndexToolLockTests : IntegrationTestBase
{
    private const string SourceA = "namespace LockMcpA { public class A { public void Foo() {} } }";

    [Fact]
    public async Task HeldSolutionLock_FailsMcpIndexCallAndDoesNotStartRun()
    {
        CreateProject("LockMcpA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        await RunIndexingSuccessfully();

        var key = IndexRunLock.NormalizeKey(Path.GetFullPath(SolutionPath));
        using (HoldLock(IndexRunLock.GetLockFilePath(key)))
        {
            var indexState = new McpIndexSessionState();
            var sessionContext = CreateSessionContext();
            var tool = new IndexTool(sessionContext, indexState);

            var ex = await Record.ExceptionAsync(() =>
                Task.FromResult(tool.LurpIndex(
                    solution: SolutionPath,
                    strategy: "incremental",
                    cancellationToken: CancellationToken.None)));

            var mcpEx = Assert.IsType<McpProtocolException>(ex);
            Assert.Contains("another Lurp index run is using solution", mcpEx.Message);
            Assert.Equal(McpErrorCode.InvalidParams, mcpEx.ErrorCode);

            // Verify _indexState is not Running.
            Assert.Null(indexState.Current);
        }
    }

    [Fact]
    public async Task HeldDatabaseLock_FailsMcpIndexCallAndDoesNotStartRun()
    {
        CreateProject("LockMcpA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        await RunIndexingSuccessfully();

        var key = IndexRunLock.NormalizeKey(DbPath);
        using (HoldLock(IndexRunLock.GetLockFilePath(key)))
        {
            var indexState = new McpIndexSessionState();
            var sessionContext = CreateSessionContext();
            var tool = new IndexTool(sessionContext, indexState);

            var ex = await Record.ExceptionAsync(() =>
                Task.FromResult(tool.LurpIndex(
                    solution: SolutionPath,
                    strategy: "incremental",
                    cancellationToken: CancellationToken.None)));

            var mcpEx = Assert.IsType<McpProtocolException>(ex);
            Assert.Contains("another Lurp index run is using database", mcpEx.Message);
            Assert.Equal(McpErrorCode.InvalidParams, mcpEx.ErrorCode);

            // Verify _indexState is not Running.
            Assert.Null(indexState.Current);
        }
    }

    [Fact]
    public async Task AlreadyRunning_RefusesMcpIndexCall_AndBothLocksCanBeTakenAgain()
    {
        CreateProject("LockMcpA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        await RunIndexingSuccessfully();

        var indexState = new McpIndexSessionState();
        var sessionContext = CreateSessionContext();
        var tool = new IndexTool(sessionContext, indexState);

        // An operation is already active for this session, so the single-flight guard refuses the call.
        using var runningCts = new CancellationTokenSource();
        Assert.NotNull(indexState.TryStart("already-running", runningCts));

        var ex = await Record.ExceptionAsync(() =>
            Task.FromResult(tool.LurpIndex(
                solution: SolutionPath,
                strategy: "incremental",
                cancellationToken: CancellationToken.None)));

        var mcpEx = Assert.IsType<McpProtocolException>(ex);
        Assert.Contains("already running", mcpEx.Message);
        Assert.Equal(McpErrorCode.InvalidParams, mcpEx.ErrorCode);

        // The refusal must not hold either run lock: both can be taken again.
        var solutionKey = IndexRunLock.NormalizeKey(Path.GetFullPath(SolutionPath));
        using var solutionLock = HoldLock(IndexRunLock.GetLockFilePath(solutionKey));
        var databaseKey = IndexRunLock.NormalizeKey(DbPath);
        using var databaseLock = HoldLock(IndexRunLock.GetLockFilePath(databaseKey));

        // The refused call must not dispose the already-running operation's CTS.
        Assert.False(runningCts.IsCancellationRequested);
        Assert.Null(Record.Exception(() => runningCts.Token));
    }

    [Fact]
    public async Task AfterSuccessfulMcpRun_BothLocksAreReleased()
    {
        CreateProject("LockMcpA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        await RunIndexingSuccessfully();

        var indexState = new McpIndexSessionState();
        var sessionContext = CreateSessionContext();
        var tool = new IndexTool(sessionContext, indexState);

        var response = tool.LurpIndex(
            solution: SolutionPath,
            strategy: "incremental",
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(response);
        var operationId = json.RootElement.GetProperty("operation_id").GetString()!;

        // Wait for the background task to complete.
        var current = indexState.Current;
        Assert.NotNull(current);
        if (current.BackgroundTask != null)
            await current.BackgroundTask;

        // Verify the run completed successfully.
        var snapshot = indexState.Snapshot(operationId);
        Assert.NotNull(snapshot);
        Assert.Equal("completed", snapshot!.Status);

        // Now both locks should be available.
        var solutionKey = IndexRunLock.NormalizeKey(Path.GetFullPath(SolutionPath));
        using var solutionLock = HoldLock(IndexRunLock.GetLockFilePath(solutionKey));
        var databaseKey = IndexRunLock.NormalizeKey(DbPath);
        using var databaseLock = HoldLock(IndexRunLock.GetLockFilePath(databaseKey));
    }

    [Fact]
    public async Task AfterSuccessfulMcpRun_OperationCtsIsDisposed()
    {
        CreateProject("LockMcpA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        await RunIndexingSuccessfully();

        var indexState = new McpIndexSessionState();
        var sessionContext = CreateSessionContext();
        var tool = new IndexTool(sessionContext, indexState);

        tool.LurpIndex(
            solution: SolutionPath,
            strategy: "incremental",
            cancellationToken: CancellationToken.None);

        var current = indexState.Current;
        Assert.NotNull(current);
        if (current.BackgroundTask != null)
            await current.BackgroundTask;

        // Once the background task owns the CTS it disposes it in its finally.
        Assert.Throws<ObjectDisposedException>(() => current.CancellationTokenSource.Token);
    }

    [Fact]
    public async Task StoreFactoryThrow_FailsOperationAndReleasesLocksAndCts()
    {
        CreateProject("LockMcpA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        await RunIndexingSuccessfully();

        var indexState = new McpIndexSessionState();
        var sessionContext = CreateSessionContext();
        var tool = new IndexTool(sessionContext, indexState,
            _ => throw new InvalidOperationException("store factory probe"));

        var response = tool.LurpIndex(
            solution: SolutionPath,
            strategy: "incremental",
            cancellationToken: CancellationToken.None);

        var json = JsonDocument.Parse(response);
        var operationId = json.RootElement.GetProperty("operation_id").GetString()!;

        var current = indexState.Current;
        Assert.NotNull(current);
        if (current.BackgroundTask != null)
            await current.BackgroundTask;

        var snapshot = indexState.Snapshot(operationId);
        Assert.NotNull(snapshot);
        Assert.Equal("failed", snapshot!.Status);
        Assert.Contains("store factory probe", snapshot.ErrorMessage);

        var solutionKey = IndexRunLock.NormalizeKey(Path.GetFullPath(SolutionPath));
        using var solutionLock = HoldLock(IndexRunLock.GetLockFilePath(solutionKey));
        var databaseKey = IndexRunLock.NormalizeKey(DbPath);
        using var databaseLock = HoldLock(IndexRunLock.GetLockFilePath(databaseKey));

        Assert.Throws<ObjectDisposedException>(() => current.CancellationTokenSource.Token);
    }

    private async Task RunIndexingSuccessfully()
    {
        var ex = await Record.ExceptionAsync(() => IndexHandler.Run(
            [$"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}"],
            CancellationToken.None));
        Assert.Null(ex);
    }

    private McpSessionContext CreateSessionContext()
    {
        return McpSessionContext.Create([$"--output-dir={Path.GetDirectoryName(DbPath)!}", $"--solution={SolutionPath}"]);
    }

    private static FileStream HoldLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
}
