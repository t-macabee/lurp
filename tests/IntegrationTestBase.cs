using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;
using System.Text;

namespace Lurp.Tests;

/// <summary>
///     Pattern A test infrastructure: on-disk MSBuild solution indexed through the
///     full pipeline (IndexRunner full / IncrementalIndexer incremental).
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    public readonly string DbPath;
    public readonly string SolutionPath;
    public readonly string TestDir;

    static IntegrationTestBase()
    {
        if (!MSBuildLocator.IsRegistered)
            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch
            {
            }
    }

    protected IntegrationTestBase()
    {
        TestDir = Path.Combine(Path.GetTempPath(), $"lurp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TestDir);
        SolutionPath = Path.Combine(TestDir, "Test.slnx");
        DbPath = Path.Combine(TestDir, "index.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TestDir))
                Directory.Delete(TestDir, true);
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes one .csproj + source files and appends the project to the .slnx.</summary>
    public void CreateProject(
        string projectName,
        IReadOnlyDictionary<string, string> sourceFiles,
        string[]? packageReferences = null,
        string[]? projectReferences = null,
        string[]? frameworkReferences = null,
        IReadOnlyDictionary<string, string>? msbuildProperties = null,
        string targetFramework = "net10.0")
    {
        var projDir = Path.Combine(TestDir, "src", projectName);
        Directory.CreateDirectory(projDir);

        var sb = new StringBuilder();
        sb.AppendLine(@"<Project Sdk=""Microsoft.NET.Sdk"">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{targetFramework}</TargetFramework>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        if (msbuildProperties is { Count: > 0 })
            foreach (var (name, value) in msbuildProperties)
                sb.AppendLine($"    <{name}>{value}</{name}>");
        sb.AppendLine("  </PropertyGroup>");
        if (packageReferences is { Length: > 0 })
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var package in packageReferences)
            {
                var (name, version) = SplitPackage(package);
                sb.AppendLine(version == null
                    ? $"    <PackageReference Include=\"{name}\" />"
                    : $"    <PackageReference Include=\"{name}\" Version=\"{version}\" />");
            }

            sb.AppendLine("  </ItemGroup>");
        }

        if (frameworkReferences is { Length: > 0 })
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var reference in frameworkReferences)
                sb.AppendLine($"    <FrameworkReference Include=\"{reference}\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        if (projectReferences is { Length: > 0 })
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var reference in projectReferences)
                sb.AppendLine($"    <ProjectReference Include=\"..\\{reference}\\{reference}.csproj\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine("</Project>");
        File.WriteAllText(Path.Combine(projDir, $"{projectName}.csproj"), sb.ToString());

        foreach (var (fileName, content) in sourceFiles)
            File.WriteAllText(Path.Combine(projDir, fileName), content);

        var slnxContent = File.Exists(SolutionPath) ? File.ReadAllText(SolutionPath) : "<Solution>\n</Solution>";
        slnxContent = slnxContent.Replace("</Solution>",
            $"  <Folder Name=\"/src/{projectName}/\">\n    <Project Path=\"src/{projectName}/{projectName}.csproj\" />\n  </Folder>\n</Solution>");
        File.WriteAllText(SolutionPath, slnxContent);
    }

    private static (string Name, string? Version) SplitPackage(string package)
    {
        var at = package.IndexOf('@');
        return at < 0
            ? (package, null)
            : (package[..at], package[(at + 1)..]);
    }

    /// <summary>Overwrites a source file under the project's directory.</summary>
    public void WriteFile(string projectName, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(TestDir, "src", projectName, fileName), content);
    }

    /// <summary>Deletes a source file under the project's directory.</summary>
    public void DeleteFile(string projectName, string fileName)
    {
        File.Delete(Path.Combine(TestDir, "src", projectName, fileName));
    }

    public static SqliteIndexStore OpenStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open();
        store.RunMigrations();
        return store;
    }

    /// <summary>
    ///     Runs <c>dotnet restore</c> on the temp solution so package references
    ///     resolve inside MSBuildWorkspace (which never restores by itself).
    /// </summary>
    public async Task RestoreSolutionAsync()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "restore", SolutionPath, "--nologo" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start dotnet restore.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);

        Assert.True(process.ExitCode == 0,
            $"dotnet restore failed (exit {process.ExitCode}).\n{await stderr}");
    }

    public async Task<string> RunFullIndexAsync(string dbPath)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        return await RunFullIndexNoDeleteAsync(dbPath);
    }

    /// <summary>
    ///     Runs a full index into an existing database without deleting it first,
    ///     so deterministic-identity reuse detection (a second index over an
    ///     identical workspace writing no new snapshot) can be exercised across
    ///     two full runs. <paramref name="force" /> requests <c>--force</c>
    ///     semantics: re-extract even when the identical snapshot already exists.
    /// </summary>
    public async Task<string> RunFullIndexNoDeleteAsync(string dbPath, bool force = false)
    {
        using var store = OpenStore(dbPath);

        await IndexRunner.RunAsync(
            store, SolutionPath, TestDir,
            [], null, "full",
            false, null, false, force, default);

        var snapshot = store.LoadLatestSnapshot()
                       ?? throw new InvalidOperationException($"No snapshot found in {dbPath} after full index.");
        return snapshot.SnapshotId;
    }

    public async Task<string> RunIncrementalIndexAsync()
    {
        using var store = OpenStore(DbPath);

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(SolutionPath);
        var workspaceInfo = new WorkspaceInfo(solution, TestDir);

        var previousManifest = store.LoadLatestSnapshot(workspaceInfo.Id.Value)
                               ?? throw new InvalidOperationException(
                                   "No previous snapshot found. Cannot run incremental index.");

        var incrementalIndexer = new IncrementalIndexer(store, TestDir, [], null);

        var result = await incrementalIndexer.RunIncrementalAsync(solution, workspaceInfo, previousManifest);

        store.PruneOldSnapshots(3);
        return result.NewSnapshotId;
    }

    /// <summary>Resolves a symbol's snapshot symbol_id by exact FQN match.</summary>
    public string ResolveSymbolId(string snapshotId, string fqn)
    {
        using var store = OpenStore(DbPath);
        try
        {
            foreach (var id in store.GetSymbolIdsInSnapshot(snapshotId))
            {
                var info = store.GetSymbolInfo(id, snapshotId);
                if (info?.FullyQualifiedName == fqn)
                    return id;
            }
        }
        finally
        {
            store.Close();
        }

        throw new InvalidOperationException($"No symbol with FQN '{fqn}' found in snapshot {snapshotId}.");
    }

    /// <summary>All edges of a snapshot matching the given kind/provenance.</summary>
    public List<EdgeRecord> QueryEdges(string snapshotId, string kind, string provenance)
    {
        using var store = OpenStore(DbPath);
        try
        {
            return store.GetEdges(snapshotId)
                .Where(e => e.Kind == kind && e.Provenance == provenance)
                .ToList();
        }
        finally
        {
            store.Close();
        }
    }
}