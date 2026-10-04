using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Lurp.Helpers;

internal static class CompilationHelper
{
    public static async IAsyncEnumerable<(Project Project, Compilation Compilation)> GetAllAsync(
        Solution solution,
        Action<string>? logWarning = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken)
                .ConfigureAwait(false);
            if (compilation == null)
            {
                // MSBuildWorkspace loads a project shell for a language it does not
                // support (VB/F#) but returns no compilation for it. That skip is a
                // declared boundary (non_csharp_projects), not a full-index failure.
                logWarning?.Invoke($"Project '{project.Name}' ({project.Language}) has no C# compilation and is skipped. " +
                                   "Non-C# projects are a declared boundary (non_csharp_projects).");
                continue;
            }

            yield return (project, compilation);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public static List<DiagnosticRecord> GetDiagnostics(string projectName, Compilation compilation)
    {
        var results = new List<DiagnosticRecord>();

        var diagnostics = compilation.GetDiagnostics();
        foreach (var diag in diagnostics)
        {
            var loc = diag.Location;
            int? startLine = null, startColumn = null, endLine = null, endColumn = null;
            string? documentPath = null;

            if (loc is { IsInSource: true, SourceTree: not null })
            {
                var span = loc.GetLineSpan();
                documentPath = loc.SourceTree.FilePath;
                startLine = span.StartLinePosition.Line;
                startColumn = span.StartLinePosition.Character;
                endLine = span.EndLinePosition.Line;
                endColumn = span.EndLinePosition.Character;
            }

            results.Add(new DiagnosticRecord
            {
                ProjectName = projectName,
                DocumentPath = documentPath,
                Severity = diag.Severity.ToString(),
                Id = diag.Id,
                Message = diag.GetMessage(CultureInfo.InvariantCulture),
                StartLine = startLine,
                StartColumn = startColumn,
                EndLine = endLine,
                EndColumn = endColumn
            });
        }

        return results;
    }

    public static List<DiagnosticRecord> GetDiagnostics(Project project, Compilation compilation)
    {
        // Start with compiler diagnostics (covers CS8019/CS8933 etc.)
        var results = GetDiagnostics(project.Name, compilation);

        if (project.AnalyzerReferences.Count == 0)
            return results;

        try
        {
            var analyzers = project.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzers(project.Language))
                .ToImmutableArray();
            if (analyzers.Length == 0)
                return results;

            var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
            var analyzerDiags = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
            foreach (var diag in analyzerDiags)
            {
                var loc = diag.Location;
                int? startLine = null, startColumn = null, endLine = null, endColumn = null;
                string? documentPath = null;

                if (loc is { IsInSource: true, SourceTree: not null })
                {
                    var span = loc.GetLineSpan();
                    documentPath = loc.SourceTree.FilePath;
                    startLine = span.StartLinePosition.Line;
                    startColumn = span.StartLinePosition.Character;
                    endLine = span.EndLinePosition.Line;
                    endColumn = span.EndLinePosition.Character;
                }

                results.Add(new DiagnosticRecord
                {
                    ProjectName = project.Name,
                    DocumentPath = documentPath,
                    Severity = diag.Severity.ToString(),
                    Id = diag.Id,
                    Message = diag.GetMessage(CultureInfo.InvariantCulture),
                    StartLine = startLine,
                    StartColumn = startColumn,
                    EndLine = endLine,
                    EndColumn = endColumn
                });
            }
        }
        catch
        {
            // Analyzer execution failed — return compiler diagnostics only.
            // This keeps indexing resilient to a poisoned analyzer assembly.
        }

        return results;
    }
}
