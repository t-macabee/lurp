using Lurp.Handlers;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
/// Contract tests for the CLI index path's per-solution and per-database run
/// locks: a held lock blocks a second run before any database file is created,
/// and a finished run (successful or failed) releases both locks.
/// </summary>
public sealed class IndexRunLockTests : IntegrationTestBase
{
    private const string SourceA = "namespace LockA { public class A { public void Foo() {} } }";

    [Fact]
    public async Task HeldSolutionLock_BlocksRunOnSameSolutionAndDifferentDatabase()
    {
        CreateProject("LockA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        var otherOut = Path.Combine(TestDir, "other-out");
        var otherDb = Path.Combine(otherOut, "index.db");

        var key = IndexRunLock.NormalizeKey(Path.GetFullPath(SolutionPath));
        using (HoldLock(IndexRunLock.GetLockFilePath(key)))
        {
            var ex = await Record.ExceptionAsync(() => IndexHandler.Run(
                [$"--solution={SolutionPath}", $"--output-dir={otherOut}"], CancellationToken.None));

            var cli = Assert.IsType<CliExitException>(ex);
            Assert.Contains("another Lurp index run is using solution", cli.Message);
            Assert.False(File.Exists(otherDb));
        }

        var after = await Record.ExceptionAsync(() => IndexHandler.Run(
            [$"--solution={SolutionPath}", $"--output-dir={otherOut}"], CancellationToken.None));
        Assert.Null(after);
        Assert.True(File.Exists(otherDb));
    }

    [Fact]
    public async Task HeldDatabaseLock_BlocksRunOnDifferentSolutionAndSameDatabase()
    {
        CreateProject("LockA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        var otherSolution = CreateSecondSolution();
        var outDir = Path.Combine(TestDir, "shared-out");
        var dbPath = Path.Combine(outDir, "index.db");

        var key = IndexRunLock.NormalizeKey(dbPath);
        using (HoldLock(IndexRunLock.GetLockFilePath(key)))
        {
            var ex = await Record.ExceptionAsync(() => IndexHandler.Run(
                [$"--solution={otherSolution}", $"--output-dir={outDir}"], CancellationToken.None));

            var cli = Assert.IsType<CliExitException>(ex);
            Assert.Contains("another Lurp index run is using database", cli.Message);
            Assert.False(File.Exists(dbPath));
        }

        var after = await Record.ExceptionAsync(() => IndexHandler.Run(
            [$"--solution={otherSolution}", $"--output-dir={outDir}"], CancellationToken.None));
        Assert.Null(after);
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public async Task AfterSuccessfulRun_BothLocksAreReleased()
    {
        CreateProject("LockA", new Dictionary<string, string> { ["A.cs"] = SourceA });
        var outDir = Path.Combine(TestDir, "success-out");
        var dbPath = Path.Combine(outDir, "index.db");

        var ex = await Record.ExceptionAsync(() => IndexHandler.Run(
            [$"--solution={SolutionPath}", $"--output-dir={outDir}"], CancellationToken.None));
        Assert.Null(ex);

        var solutionKey = IndexRunLock.NormalizeKey(Path.GetFullPath(SolutionPath));
        using var solutionLock = HoldLock(IndexRunLock.GetLockFilePath(solutionKey));
        var databaseKey = IndexRunLock.NormalizeKey(dbPath);
        using var databaseLock = HoldLock(IndexRunLock.GetLockFilePath(databaseKey));
    }

    private string CreateSecondSolution()
    {
        var projectDir = Path.Combine(TestDir, "src", "LockB");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "LockB.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDir, "B.cs"), "namespace LockB { public class B { public void Foo() {} } }");

        var solutionPath = Path.Combine(TestDir, "Other.slnx");
        File.WriteAllText(solutionPath, """
            <Solution>
              <Project Path="src/LockB/LockB.csproj" />
            </Solution>
            """);
        return solutionPath;
    }

    private static FileStream HoldLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
}
