using System.Text;
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
        var sink = new CapturingSink();
        var diagnostics = new[]
        {
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, "warning that must not be printed"),
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "design-time build failed")
        };

        using var workspace = new AdhocWorkspace();
        using var loader = CreateLoader(workspace, sink, diagnostics);
        await loader.LoadAsync("unused.sln", CancellationToken.None);

        Assert.Contains("workspace load reported 1 failure-level diagnostic(s):", sink.ErrorOutput.ToString());
        Assert.Contains("  WARNING: design-time build failed", sink.ErrorOutput.ToString());
        Assert.DoesNotContain("warning that must not be printed", sink.ErrorOutput.ToString());
    }

    [Fact]
    public async Task LoadAsync_WarningOnlyDiagnostics_WritesNothingToErrorOutput()
    {
        var sink = new CapturingSink();
        var diagnostics = new[]
        {
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, "normal load noise")
        };

        using var workspace = new AdhocWorkspace();
        using var loader = CreateLoader(workspace, sink, diagnostics);
        await loader.LoadAsync("unused.sln", CancellationToken.None);

        Assert.Equal("", sink.ErrorOutput.ToString());
    }

    private static WorkspaceLoader CreateLoader(AdhocWorkspace workspace, CapturingSink sink, IReadOnlyList<WorkspaceDiagnostic> diagnostics)
    {
        return new WorkspaceLoader(
            (_, _) => Task.FromResult(workspace.CurrentSolution),
            sink,
            diagnostics);
    }

    private sealed class CapturingSink : IOutputSink
    {
        public StringBuilder Output { get; } = new();

        public StringBuilder ErrorOutput { get; } = new();

        public void Write(string message)
        {
            Output.Append(message);
        }

        public void WriteLine(string message = "")
        {
            Output.AppendLine(message);
        }

        public void WriteErrorLine(string message = "")
        {
            ErrorOutput.AppendLine(message);
        }
    }
}
