using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Workspace;

internal sealed class MemberEdgeExtractionContext(
    Compilation compilation,
    IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
    IReadOnlySet<DocumentId> generatedDocuments,
    string snapshotId,
    string gitRoot,
    IReadOnlySet<string>? scopeDocuments = null,
    BindingIncompletenessCollector? incompleteness = null,
    Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null)
    : ExtractionContextBase(
        compilation,
        snapshotId,
        gitRoot,
        scopeDocuments,
        incompleteness,
        semanticModelCache,
        documentVersions.Keys.Select(static id => id.ToString()),
        generatedDocuments.Select(static id => id.ToString()))
{
    private List<(IMethodSymbol Method, CSharpSyntaxNode Syntax)>? _methodDeclarations;

    internal bool IsInScope(SyntaxTree? syntaxTree) => ExtractionUtils.IsInScope(ScopeDocuments, syntaxTree);

    internal bool IsMemberInScope(ISymbol member)
    {
        if (ScopeDocuments == null)
            return true;

        var syntaxRefs = member.DeclaringSyntaxReferences;
        if (syntaxRefs.IsEmpty)
        {
            // Compiler-synthesized members (implicit constructors, auto-property
            // backing fields) have no declaring syntax of their own : fall back to
            // the containing type's scope.
            var containingType = member.ContainingType;
            if (containingType == null)
                return true;
            return containingType.DeclaringSyntaxReferences.Any(syntaxRef => IsInScope(syntaxRef.SyntaxTree));
        }

        return syntaxRefs.Any(syntaxRef => IsInScope(syntaxRef.SyntaxTree));
    }

    internal IEnumerable<INamedTypeSymbol> GetAllNamedTypes()
    {
        return ExtractionUtils.GetAllNamedTypes(Compilation.Assembly.GlobalNamespace);
    }

    internal static SyntaxNode? GetMethodBody(CSharpSyntaxNode node)
    {
        return ExtractionUtils.GetMethodBody(node);
    }

    internal IReadOnlyList<(IMethodSymbol Method, CSharpSyntaxNode Syntax)> EnumerateMethodDeclarations()
    {
        if (_methodDeclarations != null)
            return _methodDeclarations;
        var result = ExtractionUtils.EnumerateMethodDeclarations(GetAllNamedTypes(), IsInScope).ToList();
        _methodDeclarations = result;
        return result;
    }


    internal EdgeRecord MakeEdge(string sourceId, string targetId, string kind, string extractorVersion, (string? path, int? sl, int? sc, int? el, int? ec)? location)
    {
        var sourceDocumentPath = location?.path;
        var isSourceGenerated = IsGeneratedDocument(sourceDocumentPath);

        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = Provenance.CompilerProved,
            SnapshotId = SnapshotId,
            ExtractorVersion = extractorVersion,
            SourceDocumentPath = sourceDocumentPath,
            SourceStartLine = location?.sl,
            SourceStartColumn = location?.sc,
            SourceEndLine = location?.el,
            SourceEndColumn = location?.ec,
            IsCrossGenerated = isSourceGenerated
        };
    }

    private bool IsGeneratedDocument(string? documentPath)
    {
        return LocationResolver.IsGenerated(documentPath);
    }

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)?
        GetMemberSourceLocation(ISymbol member)
    {
        var result = LocationResolver.Resolve(member);
        if (result.path == null && result.sl == null)
            return null;
        return result;
    }
}

internal interface IMemberEdgeExtractor
{
    List<EdgeRecord> Extract();
}