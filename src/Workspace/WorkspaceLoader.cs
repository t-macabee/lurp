using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

namespace Lurp.Workspace;

/// <summary>
///     Owns workspace acquisition for indexing: MSBuild registration, workspace
///     creation, <c>OpenSolutionAsync</c>, language-version recovery, and workspace
///     disposal. <see cref="IndexRunner" /> consumes only the recovered
///     <see cref="LoadedSolution" />; extraction (full or incremental) never sees the
///     raw workspace output.
/// </summary>
internal sealed class WorkspaceLoader : IDisposable
{
    private readonly Func<string, CancellationToken, Task<Solution>> _openSolutionAsync;
    private MSBuildWorkspace? _workspace;

    public WorkspaceLoader()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            var instances = MSBuildLocator.RegisterDefaults();

            Console.WriteLine($"MSBuild: {instances?.MSBuildPath ?? "default"}");
        }

        _openSolutionAsync = OpenWithWorkspaceAsync;
    }

    /// <summary>
    ///     Test seam: substitutes the solution opener so recovery ordering can be
    ///     verified deterministically without MSBuild. No workspace is created.
    /// </summary>
    internal WorkspaceLoader(Func<string, CancellationToken, Task<Solution>> openSolutionAsync)
    {
        _openSolutionAsync = openSolutionAsync ?? throw new ArgumentNullException(nameof(openSolutionAsync));
    }

    public void Dispose()
    {
        _workspace?.Dispose();
    }

    /// <summary>
    ///     Open <paramref name="solutionPath" /> and apply language-version recovery
    ///     to the result before it is returned. The stopwatch and console output
    ///     mirror the previous inline behavior: timing covers workspace creation
    ///     and <c>OpenSolutionAsync</c>, and "Loading solution... done (N
    ///     projects)." is written before recovery lines.
    /// </summary>
    public async Task<LoadedSolution> LoadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        Console.Write("Loading solution... ");

        var solution = await _openSolutionAsync(solutionPath, cancellationToken);

        Console.WriteLine($"done ({solution.Projects.Count()} projects).");
        sw.Stop();

        // Restore compiler fidelity: MSBuildWorkspace can silently fall back to
        // C# 7.3 parse options when a project fails to evaluate. Derive each
        // affected project's effective language version from its own inputs
        // (explicit LangVersion, or the SDK-style default) so modern C# binds.
        var recovered = LanguageVersionRecovery.Apply(solution);

        return new LoadedSolution(recovered, sw.ElapsedMilliseconds);
    }

    private async Task<Solution> OpenWithWorkspaceAsync(string solutionPath, CancellationToken cancellationToken)
    {
        _workspace = MSBuildWorkspace.Create();
        return await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
    }
}

/// <summary>The opened and language-version-recovered solution, ready for extraction.</summary>
internal sealed record LoadedSolution(Solution Solution, long LoadElapsedMilliseconds);