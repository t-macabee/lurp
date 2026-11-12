using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

/// <summary>
///     The implicit calls of a collection expression that Roslyn binds but does not expose
///     (S01-F10): the enumeration of a spread element and the <c>Add</c> call for each element.
///     Each is bound by the compiler in a speculative copy of the source at the same position,
///     so the C# lookup rules decide the target, not Lurp.
/// </summary>
internal static class SpeculativePatterns
{
    private const string LoopVariable = "__lurpItem";

    /// <summary>
    ///     The foreach pattern of a spread operand (C#: a spread element is enumerated like
    ///     <c>foreach</c>). Inside a body the operand is bound in a speculative <c>foreach</c>
    ///     statement; in a field or property initializer, in a lambda body of a speculative
    ///     initializer. Null when neither binds.
    /// </summary>
    internal static ForEachStatementInfo? ForEach(SemanticModel model, ExpressionSyntax operand)
    {
        var loop = SyntaxFactory.ForEachStatement(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(LoopVariable),
            SyntaxFactory.ParenthesizedExpression(operand.WithoutTrivia()),
            SyntaxFactory.Block());
        if (model.TryGetSpeculativeSemanticModel(operand.SpanStart, loop, out var statementModel))
            return statementModel.GetForEachStatementInfo(loop);

        var initializer = SyntaxFactory.EqualsValueClause(
            SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName("global::System.Action"),
                SyntaxFactory.ParenthesizedExpression(
                    SyntaxFactory.ParenthesizedLambdaExpression(SyntaxFactory.Block(loop)))));
        if (!model.TryGetSpeculativeSemanticModel(operand.SpanStart, initializer, out var initializerModel))
            return null;

        var speculativeLoop = initializer.DescendantNodes().OfType<ForEachStatementSyntax>().First();
        return initializerModel.GetForEachStatementInfo(speculativeLoop);
    }

    /// <summary>
    ///     The <c>Add</c> method that a collection expression of <paramref name="collectionType" />
    ///     calls with <paramref name="argument" /> (C#: a type with an applicable <c>Add</c> is built
    ///     by its constructor and one <c>Add</c> call per element), bound as
    ///     <c>((T)default!).Add(argument)</c> at <paramref name="position" />.
    /// </summary>
    internal static IMethodSymbol? AddMethod(SemanticModel model, int position, ITypeSymbol collectionType, ExpressionSyntax argument)
    {
        var receiver = SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(collectionType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression,
                    SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))));
        var call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, SyntaxFactory.IdentifierName("Add")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(argument.WithoutTrivia()))));
        return model.GetSpeculativeSymbolInfo(position, call, SpeculativeBindingOption.BindAsExpression).Symbol as IMethodSymbol;
    }

    /// <summary>A typed placeholder for one item of a spread element: <c>default(T)!</c>.</summary>
    internal static ExpressionSyntax ItemOf(ITypeSymbol elementType)
    {
        return SyntaxFactory.PostfixUnaryExpression(
            SyntaxKind.SuppressNullableWarningExpression,
            SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))));
    }
}
