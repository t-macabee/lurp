using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

internal enum ReflectionSinkTargetKind
{
    Member,
    Type,
    NestedType
}

internal readonly record struct ReflectionSinkPattern(string Pattern, ReflectionSinkTargetKind TargetKind, INamedTypeSymbol? KnownType);

internal static class ReflectionSinkPatterns
{
    internal static ReflectionSinkPattern? Resolve(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return null;

        var memberName = memberAccess.Name.Identifier.Text;

        switch (memberName)
        {
            case "GetType" when IsTypeGetType(invocation, semanticModel):
                return new ReflectionSinkPattern("Type.GetType", ReflectionSinkTargetKind.Type, null);
            case "GetType":
            case "GetExportedTypes":
                var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression);
                if (receiverType.Type?.ToDisplayString() is "System.Reflection.Assembly" or "System.Type")
                    return new ReflectionSinkPattern(
                        memberName == "GetExportedTypes" ? "Assembly.GetExportedTypes" : "Assembly.GetType",
                        ReflectionSinkTargetKind.Type,
                        null);
                return null;
            case "CreateInstance":
                var createReceiver = semanticModel.GetSymbolInfo(memberAccess.Expression);
                if (createReceiver.Symbol is INamedTypeSymbol namedType && namedType.ToDisplayString() == "System.Activator")
                    return new ReflectionSinkPattern("Activator.CreateInstance", ReflectionSinkTargetKind.Type, null);
                return null;
            case "MakeGenericType":
                return new ReflectionSinkPattern("MakeGenericType", ReflectionSinkTargetKind.Type, null);
            case "MakeGenericMethod":
                return new ReflectionSinkPattern("MakeGenericMethod", ReflectionSinkTargetKind.Type, null);
            case "GetMethod":
            case "GetProperty":
            case "GetField":
            case "GetMember":
            case "GetEvent":
            case "InvokeMember":
                return IsSystemTypeReceiver(memberAccess, semanticModel)
                    ? new ReflectionSinkPattern($"Type.{memberName}", ReflectionSinkTargetKind.Member, GetKnownTypeOperand(memberAccess.Expression, semanticModel))
                    : null;
            case "GetNestedType":
                return IsSystemTypeReceiver(memberAccess, semanticModel)
                    ? new ReflectionSinkPattern("Type.GetNestedType", ReflectionSinkTargetKind.NestedType, GetKnownTypeOperand(memberAccess.Expression, semanticModel))
                    : null;
            default:
                return null;
        }
    }

    private static bool IsSystemTypeReceiver(MemberAccessExpressionSyntax memberAccess, SemanticModel semanticModel)
    {
        return semanticModel.GetTypeInfo(memberAccess.Expression).Type?.ToDisplayString() == "System.Type";
    }

    private static INamedTypeSymbol? GetKnownTypeOperand(ExpressionSyntax receiver, SemanticModel semanticModel)
    {
        if (receiver is not TypeOfExpressionSyntax typeOfExpression)
            return null;

        return semanticModel.GetTypeInfo(typeOfExpression.Type).Type as INamedTypeSymbol;
    }

    private static bool IsTypeGetType(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression);
        if (symbolInfo.Symbol is INamedTypeSymbol namedType && namedType.ToDisplayString() == "System.Type") return true;

        if (memberAccess.Expression is IdentifierNameSyntax id && id.Identifier.Text == "Type") return true;

        return false;
    }
}
