using Lurp.Workspace;
using Microsoft.CodeAnalysis;

namespace Lurp.Tests;

/// <summary>
/// Contract tests for <see cref="WorkspaceLoader" /> load-diagnostic reporting:
/// failure-level workspace diagnostics are surfaced on stderr with a count line,
/// warning-level diagnostics are not, and a clean load stays silent.
/// </summary>
public sealed class WorkspaceLoaderDiagnosticsTests
{
    [Fact]
    public async Task LoadAsync_ReportsFailureDiagnosticsAndSkipsWarnings()
    {
        var sink = new CapturingOutputSink();
        var diagnostics = new[]
        {
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, "warning that must not be printed"),
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "design-time build failed")
        };

        using var workspace = new AdhocWorkspace();
        using var loader = CreateLoader(workspace, sink, diagnostics);
        await loader.LoadAsync("unused.sln", CancellationToken.None);

        Assert.Contains(sink.ErrorLines, l => l.Contains("workspace load reported 1 failure-level diagnostic(s):"));
        Assert.Contains(sink.ErrorLines, l => l.Contains("  WARNING: design-time build failed"));
        Assert.DoesNotContain(sink.ErrorLines, l => l.Contains("warning that must not be printed"));
    }

    [Fact]
    public async Task LoadAsync_WarningOnlyDiagnostics_WritesNothingToErrorOutput()
    {
        var sink = new CapturingOutputSink();
        var diagnostics = new[]
        {
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, "normal load noise")
        };

        using var workspace = new AdhocWorkspace();
        using var loader = CreateLoader(workspace, sink, diagnostics);
        await loader.LoadAsync("unused.sln", CancellationToken.None);

        Assert.Empty(sink.ErrorLines);
    }

    private static WorkspaceLoader CreateLoader(AdhocWorkspace workspace, CapturingOutputSink sink, IReadOnlyList<WorkspaceDiagnostic> diagnostics)
    {
        return new WorkspaceLoader(
            (_, _) => Task.FromResult(workspace.CurrentSolution),
            sink,
            diagnostics);
    }
}
