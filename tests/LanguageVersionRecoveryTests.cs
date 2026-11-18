using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;

namespace Lurp.Tests;

/// <summary>
///     Pins <see cref="LanguageVersionRecovery" />: when MSBuildWorkspace cannot
///     evaluate a project it silently falls back to C# 7.3 parse options. A
///     project that evaluated cleanly is never changed. For a project whose
///     evaluation failed, recovery derives the effective language version from
///     its own inputs: an explicit <c>&lt;LangVersion&gt;</c> wins, an SDK-style
///     project uses its target-framework default (or
///     <see cref="LanguageVersion.LatestMajor" /> when no target framework is
///     declared), a legacy (non-SDK) project stays at 7.3, and a project already
///     resolved to another version is left untouched.
/// </summary>
public sealed class LanguageVersionRecoveryTests : IntegrationTestBase
{
    private const string ModernSource = """
                                        namespace Modern;

                                        public sealed record Widget(int Id)
                                        {
                                            public string Render() => new StringBuilder().Append(Id).ToString();
                                        }

                                        public static class Consumer
                                        {
                                            public static string Use(Widget widget) => widget.Render();
                                        }
                                        """;

    /// <summary>
    ///     Case 1: the fallback only happens inside MSBuild, so this case goes
    ///     through <see cref="WorkspaceLoader" /> (the product's real MSBuild
    ///     path). The project omits <c>&lt;TargetFramework&gt;</c>, which makes
    ///     MSBuildWorkspace fail evaluation and fall back to C# 7.3; recovery must
    ///     restore the SDK default so the modern syntax is parsed, not rejected.
    /// </summary>
    [Fact]
    public async Task MsBuildFallback_SdkProjectWithoutTargetFramework_RecoversToLatestMajorAndParsesModernSyntax()
    {
        CreateProject("Modern",
            new Dictionary<string, string>
            {
                ["GlobalUsings.cs"] = "global using System.Text;\n",
                ["Widget.cs"] = ModernSource
            },
            targetFramework: null);

        var sink = new CapturingOutputSink();
        using var loader = new WorkspaceLoader(sink);
        var loaded = await loader.LoadAsync(SolutionPath, TestDir, CancellationToken.None);

        var project = Assert.Single(loaded.Solution.Projects);
        var parseOptions = Assert.IsType<CSharpParseOptions>(project.ParseOptions);
        Assert.True(parseOptions.SpecifiedLanguageVersion == LanguageVersion.LatestMajor,
            $"Expected the SDK default {LanguageVersion.LatestMajor} after recovery, got {parseOptions.SpecifiedLanguageVersion}. Recovery output: {sink.Output}");

        Assert.Contains("[language-version recovery] Modern:", sink.Output.ToString(), StringComparison.Ordinal);

        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);

        var cs8370 = compilation.GetDiagnostics().Where(d => d.Id == "CS8370").ToList();
        Assert.True(cs8370.Count == 0,
            $"Expected no CS8370 (language-version) diagnostics after recovery, got {cs8370.Count}: {Describe(cs8370)}");

        foreach (var tree in compilation.SyntaxTrees)
        {
            var syntaxErrors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(syntaxErrors.Count == 0,
                $"Expected '{Path.GetFileName(tree.FilePath)}' to parse without syntax errors after recovery, got {syntaxErrors.Count}: {Describe(syntaxErrors)}");
        }

        var nodes = compilation.SyntaxTrees.SelectMany(t => t.GetRoot().DescendantNodes()).ToList();
        Assert.True(nodes.OfType<FileScopedNamespaceDeclarationSyntax>().Any(),
            "Fixture sanity check failed: no file-scoped namespace declaration was parsed.");
        Assert.True(nodes.OfType<RecordDeclarationSyntax>().Any(),
            "Fixture sanity check failed: no record declaration was parsed.");
        Assert.True(nodes.OfType<UsingDirectiveSyntax>().Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)),
            "Fixture sanity check failed: no global using directive was parsed.");

        Assert.True(WorkspaceLoadGate.Classify(compilation) == CompilationReadability.Blind,
            "Expected the reference-less fallback project to be classified as blind; the index gate would otherwise publish an empty graph as a proved absence.");
    }

    [Fact]
    public async Task MsBuildLoad_SdkNetStandard20ProjectWithoutLangVersion_StaysAtCSharp73AndReportsCS8370()
    {
        const string source = """
                              namespace Sample
                              {
                                  public class Switcher
                                  {
                                      public int F(int x) => x switch { 1 => 2, _ => 0 };
                                  }
                              }
                              """;
        CreateProject("NetStd20",
            new Dictionary<string, string> { ["Switcher.cs"] = source },
            targetFramework: "netstandard2.0");
        RestoreSolution();

        var sink = new CapturingOutputSink();
        using var loader = new WorkspaceLoader(sink);
        var loaded = await loader.LoadAsync(SolutionPath, TestDir, CancellationToken.None);

        var project = Assert.Single(loaded.Solution.Projects);
        var parseOptions = Assert.IsType<CSharpParseOptions>(project.ParseOptions);
        Assert.Equal(LanguageVersion.CSharp7_3, parseOptions.SpecifiedLanguageVersion);
        Assert.DoesNotContain("[language-version recovery]", sink.Output.ToString(), StringComparison.Ordinal);

        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "CS8370");
    }

    [Fact]
    public async Task MsBuildLoad_TargetFrameworkInDirectoryBuildProps_StaysAtCSharp73()
    {
        const string source = """
                              namespace Sample
                              {
                                  public class Switcher
                                  {
                                      public int F(int x) => x switch { 1 => 2, _ => 0 };
                                  }
                              }
                              """;
        CreateProject("NetStdProps",
            new Dictionary<string, string> { ["Switcher.cs"] = source },
            targetFramework: null);
        File.WriteAllText(
            Path.Combine(TestDir, "src", "NetStdProps", "NetStdProps.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        File.WriteAllText(
            Path.Combine(TestDir, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        RestoreSolution();

        var sink = new CapturingOutputSink();
        using var loader = new WorkspaceLoader(sink);
        var loaded = await loader.LoadAsync(SolutionPath, TestDir, CancellationToken.None);

        var project = Assert.Single(loaded.Solution.Projects);
        var parseOptions = Assert.IsType<CSharpParseOptions>(project.ParseOptions);
        Assert.Equal(LanguageVersion.CSharp7_3, parseOptions.SpecifiedLanguageVersion);
        Assert.DoesNotContain("[language-version recovery]", sink.Output.ToString(), StringComparison.Ordinal);

        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "CS8370");
    }

    [Fact]
    public void Apply_EvaluatedSdkProjectAtCSharp73_WithoutLoadFailure_IsReturnedUnchanged()
    {
        var projectPath = WriteProjectFile("Evaluated73", """
                                                          <Project Sdk="Microsoft.NET.Sdk">
                                                            <PropertyGroup>
                                                              <TargetFramework>net8.0</TargetFramework>
                                                            </PropertyGroup>
                                                          </Project>
                                                          """);
        var solution = CreateSolutionWithFallbackProject(projectPath);

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, TestDir, [], sink);

        Assert.Same(solution, recovered);
        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.Equal(LanguageVersion.CSharp7_3, parseOptions.SpecifiedLanguageVersion);
        Assert.Equal(string.Empty, sink.Output.ToString());
    }

    [Fact]
    public void Apply_FailureForSimilarlyNamedProject_DoesNotUpgradeCleanProject()
    {
        var projectPath = WriteProjectFile("App", """
                                                  <Project Sdk="Microsoft.NET.Sdk">
                                                    <PropertyGroup>
                                                      <TargetFramework>net8.0</TargetFramework>
                                                    </PropertyGroup>
                                                  </Project>
                                                  """);
        var otherProjectPath = WriteProjectFile("MyApp", """
                                                         <Project Sdk="Microsoft.NET.Sdk">
                                                           <PropertyGroup>
                                                             <TargetFramework>net8.0</TargetFramework>
                                                           </PropertyGroup>
                                                         </Project>
                                                         """);
        var solution = CreateSolutionWithFallbackProject(projectPath, projectName: "App");
        var diagnostics = new[] { new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, $"Failed to evaluate {otherProjectPath}") };

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, TestDir, diagnostics, sink);

        Assert.Same(solution, recovered);
        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.Equal(LanguageVersion.CSharp7_3, parseOptions.SpecifiedLanguageVersion);
        Assert.Equal(string.Empty, sink.Output.ToString());
    }

    [Fact]
    public void Apply_ProjectDiagnosticMatchingProjectId_RecoversWithoutPathInMessage()
    {
        var projectPath = WriteProjectFile("ById", """
                                                   <Project Sdk="Microsoft.NET.Sdk">
                                                     <PropertyGroup>
                                                       <TargetFramework>net8.0</TargetFramework>
                                                     </PropertyGroup>
                                                   </Project>
                                                   """);
        var solution = CreateSolutionWithFallbackProject(projectPath, projectName: "ById");
        var projectId = Assert.Single(solution.Projects).Id;
        var diagnostics = new WorkspaceDiagnostic[]
        {
            new ProjectDiagnostic(WorkspaceDiagnosticKind.Failure, "MSBuild evaluation failed", projectId)
        };

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, TestDir, diagnostics, sink);

        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.Equal(LanguageVersion.CSharp12, parseOptions.SpecifiedLanguageVersion);
        Assert.Contains("[language-version recovery] ById:", sink.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("net10.0", LanguageVersion.CSharp14)]
    [InlineData("net10.0-windows", LanguageVersion.CSharp14)]
    [InlineData("net9.0", LanguageVersion.CSharp13)]
    [InlineData("net8.0", LanguageVersion.CSharp12)]
    [InlineData("net7.0", LanguageVersion.CSharp11)]
    [InlineData("net6.0", LanguageVersion.CSharp10)]
    [InlineData("net5.0", LanguageVersion.CSharp9)]
    [InlineData("netcoreapp3.1", LanguageVersion.CSharp8)]
    [InlineData("netstandard2.1", LanguageVersion.CSharp8)]
    [InlineData("netstandard2.0", LanguageVersion.CSharp7_3)]
    [InlineData("net472", LanguageVersion.CSharp7_3)]
    public void Apply_FailedEvaluationSdkProject_UsesTargetFrameworkDefault(string tfm, LanguageVersion expected)
    {
        var projectPath = WriteProjectFile($"Tfm_{tfm.Replace('.', '_').Replace('-', '_')}", $"""
                                                                                              <Project Sdk="Microsoft.NET.Sdk">
                                                                                                <PropertyGroup>
                                                                                                  <TargetFramework>{tfm}</TargetFramework>
                                                                                                </PropertyGroup>
                                                                                              </Project>
                                                                                              """);
        var solution = CreateSolutionWithFallbackProject(projectPath);
        var diagnostics = new[] { new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, $"Failed to evaluate {projectPath}") };

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, TestDir, diagnostics, sink);

        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.Equal(expected, parseOptions.SpecifiedLanguageVersion);
        if (expected == LanguageVersion.CSharp7_3)
        {
            Assert.Same(solution, recovered);
            Assert.Equal(string.Empty, sink.Output.ToString());
        }
        else
        {
            Assert.Contains("[language-version recovery] Fixture:", sink.Output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Apply_FailedEvaluationSdkProject_UsesTargetFrameworkFromDirectoryBuildProps()
    {
        var propsRoot = Path.Combine(TestDir, "props-case");
        var projectDir = Path.Combine(propsRoot, "App");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(propsRoot, "Directory.Build.props"), """
                                                                           <Project>
                                                                             <PropertyGroup>
                                                                               <TargetFramework>net8.0</TargetFramework>
                                                                             </PropertyGroup>
                                                                           </Project>
                                                                           """);
        var projectPath = Path.Combine(projectDir, "App.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");

        var solution = CreateSolutionWithFallbackProject(projectPath);
        var diagnostics = new[] { new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, $"Failed to evaluate {projectPath}") };

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, propsRoot, diagnostics, sink);

        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.Equal(LanguageVersion.CSharp12, parseOptions.SpecifiedLanguageVersion);
        Assert.Contains("[language-version recovery] Fixture:", sink.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_FailedEvaluationMultiTargetSdkProject_UsesProjectNameSuffix()
    {
        var projectPath = WriteProjectFile("Multi", """
                                                    <Project Sdk="Microsoft.NET.Sdk">
                                                      <PropertyGroup>
                                                        <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
                                                      </PropertyGroup>
                                                    </Project>
                                                    """);
        var solution = CreateSolutionWithFallbackProject(projectPath, projectName: "Multi(net8.0)");
        var diagnostics = new[] { new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, $"Failed to evaluate {projectPath}") };

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, TestDir, diagnostics, sink);

        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.Equal(LanguageVersion.CSharp12, parseOptions.SpecifiedLanguageVersion);
        Assert.Contains("[language-version recovery] Multi(net8.0):", sink.Output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Case 2: an explicit <c>&lt;LangVersion&gt;</c> is authoritative after a failed evaluation.</summary>
    [Fact]
    public void Apply_ExplicitLangVersion_IsAuthoritative()
    {
        var projectPath = WriteProjectFile("ExplicitLang", """
                                                           <Project Sdk="Microsoft.NET.Sdk">
                                                             <PropertyGroup>
                                                               <TargetFramework>net10.0</TargetFramework>
                                                               <LangVersion>9</LangVersion>
                                                             </PropertyGroup>
                                                           </Project>
                                                           """);
        var solution = CreateSolutionWithFallbackProject(projectPath);
        var diagnostics = new[] { new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, $"Failed to evaluate {projectPath}") };

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, TestDir, diagnostics, sink);

        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.True(parseOptions.SpecifiedLanguageVersion == LanguageVersion.CSharp9,
            $"Expected the explicit LangVersion=9 to be authoritative, got {parseOptions.SpecifiedLanguageVersion}. Recovery output: {sink.Output}");
        Assert.Contains("[language-version recovery] Fixture:", sink.Output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Case 3: a legacy (non-SDK) project's correct default is C# 7.3, so it
    ///     must be left alone.
    /// </summary>
    [Fact]
    public void Apply_LegacyProjectWithoutSdk_StaysAtCSharp73()
    {
        var projectPath = WriteProjectFile("Legacy", """
                                                    <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                                                      <PropertyGroup>
                                                        <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                                                      </PropertyGroup>
                                                    </Project>
                                                    """);
        var solution = CreateSolutionWithFallbackProject(projectPath);

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, TestDir, [], sink);

        Assert.Same(solution, recovered);
        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.True(parseOptions.SpecifiedLanguageVersion == LanguageVersion.CSharp7_3,
            $"Expected the legacy project to stay at {LanguageVersion.CSharp7_3}, got {parseOptions.SpecifiedLanguageVersion}.");
        Assert.Equal(string.Empty, sink.Output.ToString());
    }

    /// <summary>
    ///     Case 4: a project whose parse options MSBuild already resolved to
    ///     something other than the C# 7.3 fallback is untouched.
    /// </summary>
    [Fact]
    public void Apply_HealthySdkProject_IsReturnedUnchanged()
    {
        var projectPath = WriteProjectFile("Healthy", """
                                                     <Project Sdk="Microsoft.NET.Sdk">
                                                       <PropertyGroup>
                                                         <TargetFramework>net10.0</TargetFramework>
                                                       </PropertyGroup>
                                                     </Project>
                                                     """);
        var solution = CreateSolutionWithFallbackProject(projectPath, LanguageVersion.Latest);
        var originalParseOptions = solution.Projects.Single().ParseOptions;

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, TestDir, [], sink);

        Assert.Same(solution, recovered);
        Assert.Same(originalParseOptions, recovered.Projects.Single().ParseOptions);
        Assert.Equal(string.Empty, sink.Output.ToString());
    }

    /// <summary>
    ///     Builds a single-project solution in the state MSBuildWorkspace produces
    ///     when it cannot evaluate a project: C# 7.3 parse options on a project that
    ///     points at a real project file on disk.
    /// </summary>
    private static Solution CreateSolutionWithFallbackProject(
        string projectFilePath,
        LanguageVersion languageVersion = LanguageVersion.CSharp7_3,
        string projectName = "Fixture")
    {
        using var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            projectName,
            projectName,
            LanguageNames.CSharp,
            filePath: projectFilePath,
            parseOptions: new CSharpParseOptions(languageVersion));
        return workspace.CurrentSolution.AddProject(projectInfo);
    }

    private string WriteProjectFile(string projectName, string content)
    {
        var projectDir = Path.Combine(TestDir, "raw", projectName);
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, $"{projectName}.csproj");
        File.WriteAllText(projectPath, content);
        return projectPath;
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics)
    {
        return string.Join("; ",
            diagnostics.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}")));
    }
}
