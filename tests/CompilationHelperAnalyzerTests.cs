using System.Collections.Immutable;
using Lurp.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Lurp.Tests;

public sealed class CompilationHelperAnalyzerTests
{
    [Fact]
    public async Task GetDiagnostics_AnalyzerReferenceThrows_KeepsCompilerDiagnosticsAndWarns()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "Poisoned", "Poisoned", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                analyzerReferences: [new ThrowingAnalyzerReference()]))
            .AddDocument(DocumentId.CreateNewId(projectId), "C.cs", SourceText.From("class C { void M() { int unused; } }"));
        var project = solution.GetProject(projectId)!;
        var compilation = (await project.GetCompilationAsync())!;
        var warnings = new List<string>();

        var diagnostics = CompilationHelper.GetDiagnostics(project, compilation, warnings.Add);

        Assert.Contains(diagnostics, d => d.Id == "CS0168");
        var warning = Assert.Single(warnings);
        Assert.Contains("Poisoned", warning);
        Assert.Contains("poisoned analyzer", warning);
    }

    private sealed class ThrowingAnalyzerReference : AnalyzerReference
    {
        public override string? FullPath => null;
        public override object Id => "throwing";
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => throw new InvalidOperationException("poisoned analyzer");
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => throw new InvalidOperationException("poisoned analyzer");
    }
}
