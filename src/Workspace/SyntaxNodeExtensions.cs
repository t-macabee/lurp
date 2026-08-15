using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

internal static class SyntaxNodeExtensions
{
    internal static bool IsWriteContext(this SyntaxNode node)
    {
        if (node.Parent is AssignmentExpressionSyntax assign)
            return assign.Left == node;

        if (node.Parent is PrefixUnaryExpressionSyntax preUnary &&
            (preUnary.IsKind(SyntaxKind.PreIncrementExpression) ||
             preUnary.IsKind(SyntaxKind.PreDecrementExpression)))
            return preUnary.Operand == node;

        if (node.Parent is PostfixUnaryExpressionSyntax postUnary &&
            (postUnary.IsKind(SyntaxKind.PostIncrementExpression) ||
             postUnary.IsKind(SyntaxKind.PostDecrementExpression)))
            return postUnary.Operand == node;

        if (node.Parent is ArgumentSyntax arg &&
            (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
             arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)))
            return true;

        return false;
    }
}