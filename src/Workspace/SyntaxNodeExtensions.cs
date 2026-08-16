using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

internal static class SyntaxNodeExtensions
{
    internal static bool IsWriteContext(this SyntaxNode node)
    {
        return node.Parent switch
        {
            AssignmentExpressionSyntax assign => assign.Left == node,
            PrefixUnaryExpressionSyntax preUnary
                when preUnary.IsKind(SyntaxKind.PreIncrementExpression) || preUnary.IsKind(SyntaxKind.PreDecrementExpression)
                => preUnary.Operand == node,
            PostfixUnaryExpressionSyntax postUnary
                when postUnary.IsKind(SyntaxKind.PostIncrementExpression) || postUnary.IsKind(SyntaxKind.PostDecrementExpression)
                => postUnary.Operand == node,
            ArgumentSyntax arg
                when arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)
                => true,
            _ => false
        };
    }
}