using Lurp.Adapters;
using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public static class CompilationFactExtractor
{
    public sealed record ExtractionResult(List<SymbolDeclaration> Declarations, List<EdgeRecord> Edges, List<DiagnosticRecord> Diagnostics);

    public static ExtractionResult ExtractAll(Compilation compilation, WorkspaceInfo workspaceInfo, string snapshotId, string projectName, IReadOnlySet<string>? skipAdapters = null, Action<string>? logWarning = null, Action<string>? logError = null)
    {
        var symbolExtractor = new SymbolExtractor(compilation, workspaceInfo.DocumentContents, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId);

        var declarations = symbolExtractor.ExtractAll();
        var edges = symbolExtractor.ExtractEdges();


        var memberEdgeExtractor = new MemberEdgeExtractor(compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId);

        edges.AddRange(memberEdgeExtractor.ExtractAll());


        var polyExtractor = new PolymorphismExtractor(compilation, snapshotId);

        edges.AddRange(polyExtractor.ExtractAll());

        try
        {
            var reflectionExtractor = new ReflectionExtractor(compilation, snapshotId);
            edges.AddRange(reflectionExtractor.Extract());
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"Reflection extraction failed: {ex.Message}");
        }

        var adapters = AdapterRegistry.GetAdapters(skipAdapters);

        foreach (var adapter in adapters)
        {
            try
            {
                edges.AddRange(adapter.Extract(compilation, snapshotId));
            }
            catch (Exception ex)
            {
                logError?.Invoke($"Adapter '{adapter.Name}' failed: {ex.Message}");
            }
        }

        var diagnostics = CompilationHelper.GetDiagnostics(projectName, compilation);

        return new ExtractionResult(declarations, edges, diagnostics);
    }
}
