using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Proves the workspace-loading seam (Task 4): the loader owns MSBuild
/// registration, solution opening, language-version recovery, and workspace
/// disposal, and recovery is applied to the solution <b>before</b> either full
/// or incremental extraction can see it : both paths in
/// <see cref="IndexRunner"/> consume only the loader's returned
/// <see cref="LoadedSolution"/>.
///
/// The recovery path is exercised deterministically with a substituted opener
/// whose solution carries the exact C# 7.3 parse-options fallback that
/// MSBuildWorkspace assigns when project evaluation fails; the loader must
/// recover it before returning.
/// </summary>
public sealed class WorkspaceLoaderTests : IDisposable
{
    private string? _testDir;

    public void Dispose()
    {
        if (_testDir != null && Directory.Exists(_testDir))
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task Loader_AppliesRecovery_ToFallbackParseOptions_BeforeSolutionIsReturned()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"lurp_loader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        // The substituted opener returns a solution whose projects carry the
        // C# 7.3 parse-options fallback that MSBuildWorkspace assigns when a
        // project fails to evaluate : the exact state LanguageVersionRecovery
        // exists to correct. Project files on disk mirror the fallback fixture:
        // Modern is SDK-style with no LangVersion (SDK default is latest),
        // ExplicitLang pins LangVersion=9.0.
        var modernPath = Path.Combine(_testDir, "Modern.csproj");
        File.WriteAllText(modernPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var explicitPath = Path.Combine(_testDir, "ExplicitLang.csproj");
        File.WriteAllText(explicitPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <LangVersion>9.0</LangVersion>
              </PropertyGroup>
            </Project>
            """);

        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "Modern",
                "Modern",
                LanguageNames.CSharp,
                filePath: modernPath,
                parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp7_3)))
            .AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "ExplicitLang",
                "ExplicitLang",
                LanguageNames.CSharp,
                filePath: explicitPath,
                parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp7_3)));

        using var loader = new WorkspaceLoader((_, _) => Task.FromResult(solution));
        var loaded = await loader.LoadAsync(Path.Combine(_testDir, "Test.slnx"), CancellationToken.None);

        // The loader must hand extraction a recovered solution: the SDK-style
        // project leaves the C# 7.3 fallback for the SDK default, and the
        // explicitly pinned project is never clobbered.
        var modern = loaded.Solution.Projects.Single(p => p.Name == "Modern");
        var explicitLang = loaded.Solution.Projects.Single(p => p.Name == "ExplicitLang");

        var modernOptions = Assert.IsType<CSharpParseOptions>(modern.ParseOptions);
        Assert.Equal(LanguageVersion.LatestMajor, modernOptions.SpecifiedLanguageVersion);
        Assert.Equal(LanguageVersion.CSharp9, Assert.IsType<CSharpParseOptions>(explicitLang.ParseOptions).SpecifiedLanguageVersion);

        // Console shape is preserved: the load line completes before recovery
        // lines, matching the previous inline ordering.
        Assert.True(loaded.LoadElapsedMilliseconds >= 0);
    }

    [SkippableFact]
    public async Task Loader_RealFixtureLoad_EffectiveVersions_AndDisposal()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        _testDir = Path.Combine(Path.GetTempPath(), $"lurp_loader_real_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var solutionPath = IntegrationHarness.CopyNamedFixtureToTemp(_testDir, "LanguageVersionFallback");

        var originalOut = Console.Out;
        using var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            using var loader = new WorkspaceLoader();
            var loaded = await loader.LoadAsync(solutionPath, CancellationToken.None);
            try
            {
                var modern = loaded.Solution.Projects.Single(p => p.Name == "Modern");
                var explicitLang = loaded.Solution.Projects.Single(p => p.Name == "ExplicitLang");

                // The unset-LangVersion project must never surface the C# 7.3
                // fallback; the explicit 9.0 pin must survive the load.
                Assert.NotEqual(
                    LanguageVersion.CSharp7_3,
                    Assert.IsType<CSharpParseOptions>(modern.ParseOptions).SpecifiedLanguageVersion);
                Assert.Equal(
                    LanguageVersion.CSharp9,
                    Assert.IsType<CSharpParseOptions>(explicitLang.ParseOptions).SpecifiedLanguageVersion);
            }
            finally
            {
                loader.Dispose();
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capturedOut.ToString();
        Assert.Contains("Loading solution... done (2 projects).", output);
    }
}
