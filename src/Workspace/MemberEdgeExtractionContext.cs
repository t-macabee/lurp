using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Workspace;

internal sealed class MemberEdgeExtractionContext(ProjectExtractionInputs inputs) : ExtractionContextBase(inputs)
{
    private List<(IMethodSymbol Method, CSharpSyntaxNode Syntax)>? _methodDeclarations;

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

    internal IReadOnlyList<(IMethodSymbol Method, CSharpSyntaxNode Syntax)> EnumerateMethodDeclarations()
    {
        if (_methodDeclarations != null)
            return _methodDeclarations;
        var result = ExtractionUtils.EnumerateMethodDeclarations(GetAllNamedTypes(), IsInScope).ToList();
        _methodDeclarations = result;
        return result;
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