using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lurp;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

public sealed class TempEdgeInspect : IAsyncLifetime, IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _solutionPath;

    public TempEdgeInspect()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"lurp_temp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");
        _solutionPath = Path.Combine(_testDir, "TestSolution.slnx");
    }

    public Task InitializeAsync()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try { MSBuildLocator.RegisterDefaults(); } catch { }
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task InspectImplicitConstructorEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered);

        var projectDirectory = Path.Combine(_testDir, "src", "TestProject");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "TestProject.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Model.cs"), """
            namespace TestProject;
            public class Model { }
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Consumer.cs"), """
            namespace TestProject;
            public class Consumer
            {
                public Model Create() => new Model();
            }
            """);
        File.WriteAllText(_solutionPath, """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/TestProject/TestProject.csproj" />
              </Folder>
            </Solution>
            """);

        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(_solutionPath);
        var workspaceInfo = new WorkspaceInfo(solution, _testDir);
        var snapshotId = SnapshotIdentity.Create(workspaceInfo, new HashSet<string>());
        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
        var snapshotIdStr = snapshotId.ToString();
        manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath: null);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;
            var result = CompilationFactExtractor.ExtractAll(
                compilation!, workspaceInfo, snapshotIdStr, project.Name,
                new CompilationFactExtractor.ExtractionOptions(new HashSet<string>()));
            store.SaveDeclarations(snapshotIdStr, result.Declarations);
            store.SaveEdges(snapshotIdStr, EdgeDedup.Deduplicate(result.Edges));
            store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
            store.SaveBindingIncompleteness(snapshotIdStr, result.BindingIncompleteness);
        }

        Console.WriteLine($"DB: {_dbPath}");
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT source_symbol_id, target_symbol_id, kind, source_document_path
            FROM edges
            WHERE snapshot_id = @snapshotId
            ORDER BY kind, source_symbol_id, target_symbol_id;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotIdStr);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            Console.WriteLine($"{reader.GetString(0)} --{reader.GetString(2)}--> {reader.GetString(1)} | path={reader[3]}");
        }
    }
}
