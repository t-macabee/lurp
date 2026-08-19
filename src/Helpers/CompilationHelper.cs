using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Lurp.Helpers;

internal static class CompilationHelper
{
    public static async IAsyncEnumerable<(Project Project, Compilation Compilation)> GetAllAsync(Solution solution, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken)
                .ConfigureAwait(false);
            if (compilation == null)
                throw new InvalidOperationException($"Compilation loader: GetCompilationAsync returned null for project '{project.Name}' during full extraction.");
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
                Message = diag.GetMessage(),
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
                    Message = diag.GetMessage(),
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
