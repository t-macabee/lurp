using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class SymbolExtractor
{
    private readonly SymbolExtractionContext _context;

    internal SymbolExtractor(ProjectExtractionInputs inputs, WorkspaceInfo workspaceInfo, string targetFramework)
    {
        _context = new SymbolExtractionContext(inputs, workspaceInfo.DocumentContents, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, targetFramework);
    }

    public List<SymbolDeclaration> ExtractAll()
    {
        return new SymbolDeclarationExtractor(_context).ExtractAll();
    }

    public List<EdgeRecord> ExtractEdges()
    {
        return new SymbolStructuralEdgeExtractor(_context).ExtractEdges();
    }
}