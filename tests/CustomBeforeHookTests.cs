using System.Text;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Tests;

/// <summary>
///     B14 regression guard (proved in R45, fixed in R46). Lurp redirects each
///     project's design-time intermediates into the cache by chaining the solution's
///     own <c>CustomBeforeMicrosoftCommonTargets</c> hook instead of replacing it. The
///     control workspace (no Lurp properties) shows that the fixture's hook takes
///     effect and that its intermediates otherwise land in the target tree; the Lurp
///     workspace shows that the hook still runs, that the intermediates move into the
///     cache, and that a project assigning the hook in its own body is reported.
/// </summary>
[Trait("Category", "Slow")]
public sealed class CustomBeforeHookTests
{
    [SkippableFact]
    public async Task LurpRedirect_KeepsTheSolutionsOwnCustomBeforeHook()
    {
        Skip.If(!IntegrationHarness.TryRegisterMSBuild(), "MSBuild is not available on this system.");

        var scenario = await RunScenarioAsync(HookPlacement.DirectoryBuildProps);

        AssertControlHookApplied(scenario.Control);

        Assert.True(scenario.Lurp.AlwaysThere,
            $"Lurp workspace: Class1.cs is not in the compilation. {StopRuleValues(scenario)}");
        var lurpFalse = FalseProbes(scenario.Lurp, includeAlwaysThere: false);
        Assert.True(lurpFalse.Count == 0,
            "B14: the solution's own CustomBeforeMicrosoftCommonTargets hook has no effect under Lurp's redirect. "
            + $"False: {string.Join(", ", lurpFalse)}.\nLurp diagnostics:\n{FormatDiagnostics(scenario.Lurp)}");

        Assert.True(IsUnder(scenario.Control.AssemblyPath, Path.Combine(scenario.FixtureRoot, "Fx", "obj")),
            $"Without Lurp the intermediates must land in the target tree. {StopRuleValues(scenario)}");
        Assert.True(IsUnder(scenario.Lurp.AssemblyPath, scenario.CacheObjDir),
            $"Lurp must redirect the intermediates into the cache. {StopRuleValues(scenario)}");
        Assert.Empty(scenario.Lurp.RedirectWarnings);
    }

    [SkippableFact]
    public async Task LurpRedirect_AppliesWhenTheSolutionHasNoHook()
    {
        Skip.If(!IntegrationHarness.TryRegisterMSBuild(), "MSBuild is not available on this system.");

        var scenario = await RunScenarioAsync(HookPlacement.None);

        Assert.True(scenario.Control.AlwaysThere && scenario.Lurp.AlwaysThere,
            $"Class1.cs is not in both compilations. {StopRuleValues(scenario)}");

        AssertNoHookProbes(scenario.Control, "control");
        AssertNoHookProbes(scenario.Lurp, "Lurp");

        Assert.True(IsUnder(scenario.Control.AssemblyPath, Path.Combine(scenario.FixtureRoot, "Fx", "obj")),
            $"Without Lurp the intermediates must land in the target tree. {StopRuleValues(scenario)}");
        Assert.True(IsUnder(scenario.Lurp.AssemblyPath, scenario.CacheObjDir),
            $"Lurp must redirect the intermediates into the cache. {StopRuleValues(scenario)}");
        Assert.Empty(scenario.Lurp.RedirectWarnings);
    }

    [SkippableFact]
    public async Task LurpRedirect_ReportsAProjectThatAssignsTheHookInItsBody()
    {
        Skip.If(!IntegrationHarness.TryRegisterMSBuild(), "MSBuild is not available on this system.");

        var scenario = await RunScenarioAsync(HookPlacement.ProjectBody);

        AssertControlHookApplied(scenario.Control);

        Assert.True(scenario.Lurp.AlwaysThere,
            $"Lurp workspace: Class1.cs is not in the compilation. {StopRuleValues(scenario)}");
        var lurpFalse = FalseProbes(scenario.Lurp, includeAlwaysThere: false);
        Assert.True(lurpFalse.Count == 0,
            "the solution's own hook must still run when the project assigns the property in its body. "
            + $"False: {string.Join(", ", lurpFalse)}.\nLurp diagnostics:\n{FormatDiagnostics(scenario.Lurp)}");

        Assert.False(IsUnder(scenario.Lurp.AssemblyPath, scenario.CacheObjDir),
            $"the project's own assignment must win and move its intermediates back into the tree. {StopRuleValues(scenario)}");
        Assert.Single(scenario.Lurp.RedirectWarnings);
        Assert.Contains("Project 'Fx'", scenario.Lurp.RedirectWarnings[0]);
    }

    private enum HookPlacement
    {
        DirectoryBuildProps,
        ProjectBody,
        None
    }

    private static async Task<Scenario> RunScenarioAsync(HookPlacement placement)
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"lurp-b14-{Guid.NewGuid():N}");
        var solutionPath = Path.Combine(testDir, "Fx.slnx");
        var cacheDir = LurpCache.ResolveSolutionCacheDir(solutionPath);

        try
        {
            WriteFixture(testDir, placement);
            GroundTruthFixture.RestoreSolution(solutionPath);

            var control = await ProbeControlAsync(solutionPath);
            var sink = new CapturingOutputSink();
            var lurp = await ProbeLurpAsync(solutionPath, sink);

            return new Scenario(control, lurp, testDir, Path.Combine(cacheDir, "obj"));
        }
        finally
        {
            TestDatabase.DeleteDirectory(cacheDir);
            TestDatabase.DeleteDirectory(testDir);
        }
    }

    private static async Task<Probe> ProbeControlAsync(string solutionPath)
    {
        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        return await BuildProbeAsync(solution, workspace.Diagnostics.Select(d => d.Message).ToList(), []);
    }

    private static async Task<Probe> ProbeLurpAsync(string solutionPath, CapturingOutputSink sink)
    {
        using var loader = new WorkspaceLoader(sink);
        var loaded = await loader.LoadAsync(solutionPath, CancellationToken.None);
        var redirectWarnings = sink.ErrorLines
            .Where(line => line.Contains("intermediate_redirect_not_applied", StringComparison.Ordinal))
            .ToList();
        return await BuildProbeAsync(loaded.Solution, [], redirectWarnings);
    }

    private static async Task<Probe> BuildProbeAsync(Solution solution, IReadOnlyList<string> diagnostics, IReadOnlyList<string> redirectWarnings)
    {
        var project = solution.Projects.Single();
        var compilation = await project.GetCompilationAsync()
            ?? throw new InvalidOperationException("The fixture project has no compilation.");
        var fxClass = compilation.GetTypeByMetadataName("Fx.Class1");

        return new Probe(
            DefineSymbol: ((CSharpParseOptions)project.ParseOptions!).PreprocessorSymbolNames.Contains("LURP_HOOK"),
            AlwaysThere: fxClass?.GetMembers("AlwaysThere").Length > 0,
            HookedMethod: fxClass?.GetMembers("HookedMethod").Length > 0,
            HookAddedType: compilation.GetTypeByMetadataName("Fx.HookAdded") != null,
            AssemblyPath: project.CompilationOutputInfo.AssemblyPath,
            OutputFilePath: project.OutputFilePath,
            RedirectWarnings: redirectWarnings,
            Diagnostics: diagnostics);
    }

    private static bool IsUnder(string? path, string directory)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        var prefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertControlHookApplied(Probe control)
    {
        var controlFalse = FalseProbes(control, includeAlwaysThere: true);
        Assert.True(controlFalse.Count == 0,
            "fixture invalid: the solution's own hook has no effect without Lurp's properties. "
            + $"False: {string.Join(", ", controlFalse)}.\nControl diagnostics:\n{FormatDiagnostics(control)}");
    }

    private static void AssertNoHookProbes(Probe probe, string label)
    {
        Assert.True(
            !probe.DefineSymbol && !probe.HookedMethod && !probe.HookAddedType,
            $"the fixture has no hook, so nothing may leak into the {label} compilation. "
            + $"DefineSymbol={probe.DefineSymbol}, HookedMethod={probe.HookedMethod}, HookAddedType={probe.HookAddedType}.");
    }

    private static string StopRuleValues(Scenario scenario) =>
        "CompilationOutputInfo.AssemblyPath / OutputFilePath -> "
        + $"control: '{scenario.Control.AssemblyPath}' / '{scenario.Control.OutputFilePath}'; "
        + $"Lurp: '{scenario.Lurp.AssemblyPath}' / '{scenario.Lurp.OutputFilePath}'.";

    private static void WriteFixture(string root, HookPlacement placement)
    {
        WriteFile(root, "Fx.slnx",
            "<Solution><Project Path=\"Fx/Fx.csproj\" /></Solution>");
        if (placement == HookPlacement.DirectoryBuildProps)
        {
            WriteFile(root, "Directory.Build.props",
                "<Project>",
                "  <PropertyGroup>",
                @"    <CustomBeforeMicrosoftCommonTargets>$(MSBuildThisFileDirectory)hooks\solution-hook.targets</CustomBeforeMicrosoftCommonTargets>",
                "  </PropertyGroup>",
                "</Project>");
        }

        if (placement != HookPlacement.None)
        {
            WriteFile(root, @"hooks\solution-hook.targets",
                "<Project>",
                "  <PropertyGroup>",
                "    <DefineConstants>$(DefineConstants);LURP_HOOK</DefineConstants>",
                "  </PropertyGroup>",
                "  <ItemGroup>",
                @"    <Compile Include=""$(MSBuildThisFileDirectory)HookAdded.cs"" />",
                "  </ItemGroup>",
                "</Project>");
            WriteFile(root, @"hooks\HookAdded.cs",
                "namespace Fx;",
                "",
                "public static class HookAdded { }");
        }

        WriteFile(root, @"Fx\Fx.csproj", ProjectXml(placement));
        WriteFile(root, @"Fx\Class1.cs",
            "namespace Fx;",
            "",
            "public static class Class1",
            "{",
            "    public static void AlwaysThere() { }",
            "",
            "#if LURP_HOOK",
            "    public static void HookedMethod() { }",
            "#endif",
            "}");
    }

    private static string[] ProjectXml(HookPlacement placement)
    {
        var lines = new List<string>
        {
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "  <PropertyGroup>",
            "    <TargetFramework>net10.0</TargetFramework>",
            "    <Nullable>enable</Nullable>",
            "    <ImplicitUsings>enable</ImplicitUsings>"
        };
        if (placement == HookPlacement.ProjectBody)
        {
            lines.Add(@"    <CustomBeforeMicrosoftCommonTargets>$(MSBuildProjectDirectory)\..\hooks\solution-hook.targets</CustomBeforeMicrosoftCommonTargets>");
        }

        lines.Add("  </PropertyGroup>");
        lines.Add("</Project>");
        return lines.ToArray();
    }

    private static void WriteFile(string root, string relativePath, params string[] lines)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    private static List<string> FalseProbes(Probe probe, bool includeAlwaysThere)
    {
        var names = new List<string>();
        if (includeAlwaysThere && !probe.AlwaysThere) names.Add("AlwaysThere");
        if (!probe.DefineSymbol) names.Add("DefineSymbol");
        if (!probe.HookedMethod) names.Add("HookedMethod");
        if (!probe.HookAddedType) names.Add("HookAddedType");
        return names;
    }

    private static string FormatDiagnostics(Probe probe) =>
        probe.Diagnostics.Count == 0 ? "  (none)" : string.Join("\n", probe.Diagnostics.Select(m => "  " + m));

    private sealed record Scenario(
        Probe Control,
        Probe Lurp,
        string FixtureRoot,
        string CacheObjDir);

    private sealed record Probe(
        bool DefineSymbol,
        bool AlwaysThere,
        bool HookedMethod,
        bool HookAddedType,
        string? AssemblyPath,
        string? OutputFilePath,
        IReadOnlyList<string> RedirectWarnings,
        IReadOnlyList<string> Diagnostics);
}
