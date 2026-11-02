using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;

namespace Lurp.Tests;

/// <summary>
///     Pins <see cref="LanguageVersionRecovery" />: when MSBuildWorkspace cannot
///     evaluate a project it silently falls back to C# 7.3 parse options. The
///     recovery derives each affected project's effective language version from
///     the project file instead: an explicit <c>&lt;LangVersion&gt;</c> wins, an
///     SDK-style project with no <c>&lt;LangVersion&gt;</c> uses
///     <see cref="LanguageVersion.LatestMajor" />, a legacy (non-SDK) project
///     stays at 7.3, and a project already resolved to another version is left
///     untouched.
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
    /// <remarks>
    ///     A project that fails evaluation never receives a reference set, and
    ///     <see cref="WorkspaceLoadGate" /> then refuses to extract a graph from it
    ///     (asserted below). Recovery changes the language version only; it cannot
    ///     invent the metadata references MSBuild never resolved. The graph-level
    ///     consequence of these features binding (a <c>Calls</c> edge into the
    ///     record member) therefore belongs to a project MSBuild can evaluate, and
    ///     is covered by the general edge suites. What recovery is responsible for
    ///     here is that the C# 7.3 fallback no longer rejects the source.
    /// </remarks>
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
        var loaded = await loader.LoadAsync(SolutionPath, CancellationToken.None);

        var project = Assert.Single(loaded.Solution.Projects);
        var parseOptions = Assert.IsType<CSharpParseOptions>(project.ParseOptions);
        Assert.True(parseOptions.SpecifiedLanguageVersion == LanguageVersion.LatestMajor,
            $"Expected the SDK default {LanguageVersion.LatestMajor} after recovery, got {parseOptions.SpecifiedLanguageVersion}. Recovery output: {sink.Output}");

        Assert.Contains("[language-version recovery] Modern:", sink.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("SDK-style project with unset LangVersion", sink.Output.ToString(), StringComparison.Ordinal);

        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);

        // The C# 7.3 fallback rejects both the global using directive (CS8370) and
        // the file-scoped namespace (syntax errors), so an empty error set is only
        // reachable when recovery actually changed the parse options.
        var cs8370 = compilation.GetDiagnostics().Where(d => d.Id == "CS8370").ToList();
        Assert.True(cs8370.Count == 0,
            $"Expected no CS8370 (language-version) diagnostics after recovery, got {cs8370.Count}: {Describe(cs8370)}");

        foreach (var tree in compilation.SyntaxTrees)
        {
            var syntaxErrors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(syntaxErrors.Count == 0,
                $"Expected '{Path.GetFileName(tree.FilePath)}' to parse without syntax errors after recovery, got {syntaxErrors.Count}: {Describe(syntaxErrors)}");
        }

        // Fixture sanity: the sources really use the features the C# 7.3 fallback
        // would reject, so the clean parse above is evidence of recovery, not of a
        // source file that never needed it.
        var nodes = compilation.SyntaxTrees.SelectMany(t => t.GetRoot().DescendantNodes()).ToList();
        Assert.True(nodes.OfType<FileScopedNamespaceDeclarationSyntax>().Any(),
            "Fixture sanity check failed: no file-scoped namespace declaration was parsed.");
        Assert.True(nodes.OfType<RecordDeclarationSyntax>().Any(),
            "Fixture sanity check failed: no record declaration was parsed.");
        Assert.True(nodes.OfType<UsingDirectiveSyntax>().Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)),
            "Fixture sanity check failed: no global using directive was parsed.");

        // Declared boundary: a project whose evaluation failed has no metadata
        // references, so indexing reports it as unreadable instead of publishing an
        // empty call graph. Recovery restores the language version only.
        Assert.True(WorkspaceLoadGate.Classify(compilation) == CompilationReadability.Blind,
            "Expected the reference-less fallback project to be classified as blind; the index gate would otherwise publish an empty graph as a proved absence.");
    }

    /// <summary>Case 2: an explicit <c>&lt;LangVersion&gt;</c> is authoritative.</summary>
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

        var sink = new CapturingOutputSink();
        var recovered = LanguageVersionRecovery.Apply(solution, sink);

        var parseOptions = Assert.IsType<CSharpParseOptions>(Assert.Single(recovered.Projects).ParseOptions);
        Assert.True(parseOptions.SpecifiedLanguageVersion == LanguageVersion.CSharp9,
            $"Expected the explicit LangVersion=9 to be authoritative, got {parseOptions.SpecifiedLanguageVersion}. Recovery output: {sink.Output}");
        Assert.Contains("explicit LangVersion=9", sink.Output.ToString(), StringComparison.Ordinal);
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
        var recovered = LanguageVersionRecovery.Apply(solution, sink);

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
        var recovered = LanguageVersionRecovery.Apply(solution, sink);

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
        LanguageVersion languageVersion = LanguageVersion.CSharp7_3)
    {
        using var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "Fixture",
            "Fixture",
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
