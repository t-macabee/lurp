using System.Diagnostics;
using System.Security;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class PathOwnershipWindowsTests : IntegrationTestBase
{
    [Fact]
    public void Root_project_does_not_own_a_foreign_drive_path()
    {
        var facts = ProjectFacts.FromRows([new ProjectRow { Name = "Root", ProjectPath = "Root.csproj" }]);
        Assert.Empty(facts.GetDocumentOwners("X:/Shared/Linked.cs"));
    }

    [Fact]
    public async Task Linked_file_outline_and_navigation_prefer_physical_project()
    {
        CreateProject("Shared", new Dictionary<string, string> { ["Common.cs"] = "namespace Example; public class Common { }" });
        CreateProject("App", new Dictionary<string, string>());
        var app = Path.Combine(TestDir, "src", "App", "App.csproj");
        File.WriteAllText(app, File.ReadAllText(app).Replace("</Project>", "<ItemGroup><Compile Include=\"../Shared/Common.cs\" Link=\"Common.cs\" /></ItemGroup></Project>"));
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        const string path = "src/Shared/Common.cs";
        var outline = Assert.Single(store.GetDeclarationsOutline(path, snapshot, false, 100, null)!.Items, x => x.FullyQualifiedName == "global::Example.Common");
        var navigation = store.NavigateToLocation(path, 1, snapshot);
        Assert.NotNull(navigation);
        Assert.Equal("Shared", new System.Reflection.AssemblyName(SymbolId.Parse(outline.SymbolId).AssemblyIdentity).Name);
        Assert.Equal("Shared", new System.Reflection.AssemblyName(SymbolId.Parse(navigation.SymbolId).AssemblyIdentity).Name);
    }

    [SkippableFact]
    public async Task Linked_file_on_second_drive_has_no_physical_owner()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Requires Windows subst and drive-relative path behavior.");
        var external = Path.Combine(Path.GetTempPath(), "lurp-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var drive = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => $"{(char)c}:")
            .First(d => !Directory.Exists(d + "\\"));
        var mounted = false;
        try
        {
            RunSubst(drive, external);
            mounted = true;
            Directory.CreateDirectory(Path.Combine(external, "Shared"));
            File.WriteAllText(Path.Combine(external, "Shared", "Linked.cs"), "public class Linked { }");
            var linkedPath = drive + "/Shared/Linked.cs";
            File.WriteAllText(Path.Combine(TestDir, "Root.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"{SecurityElement.Escape(linkedPath)}\" Link=\"Linked.cs\" /></ItemGroup></Project>");
            File.WriteAllText(SolutionPath, "<Solution><Project Path=\"Root.csproj\" /></Solution>");
            RestoreSolution();
            var snapshot = await RunFullIndexAsync(DbPath);
            using var connection = OpenDbConnection();
            var facts = ProjectFacts.Load(connection, snapshot);
            var relative = PathNormalizer.ToGitRelativeFromNormalizedRoot(linkedPath, PathNormalizer.NormalizeRoot(TestDir));
            Assert.Equal(linkedPath, relative, ignoreCase: true);
            Assert.Empty(facts.GetDocumentOwners(relative));
        }
        finally
        {
            if (mounted) RunSubst(drive, "/d");
            TestDatabase.DeleteDirectory(external);
        }
    }

    [SkippableFact]
    public async Task Solution_project_directory_with_different_case_loads()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Requires Windows case-insensitive filesystem behavior.");
        CreateProject("ActualCase", new Dictionary<string, string> { ["C.cs"] = "public class C { }" });
        File.WriteAllText(SolutionPath, File.ReadAllText(SolutionPath).Replace("src/ActualCase/ActualCase.csproj", "src/actualcase/ActualCase.csproj"));
        RestoreSolution();
        using var loader = new WorkspaceLoader(new CapturingOutputSink());
        var loaded = await loader.LoadAsync(SolutionPath, TestDir, CancellationToken.None);
        Assert.Single(loaded.Solution.Projects);
        Assert.Empty(loaded.UnloadedProjects);
    }

    private static void RunSubst(string drive, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("subst")
        {
            ArgumentList = { drive, target }, UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
        })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"subst {drive} {target} failed: {error}");
    }
}
