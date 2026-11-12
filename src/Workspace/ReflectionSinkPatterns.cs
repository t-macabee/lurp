using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Lurp.Workspace;

/// <summary>The kind of declared symbol that the name argument of a sink binds (D21, D143).</summary>
internal enum NameTargetKind
{
    /// <summary>The sink has no name argument; it is a runtime-unknown signal only.</summary>
    None,
    /// <summary>A type by its namespace-qualified metadata name (D144).</summary>
    Type,
    /// <summary>An interface by its simple or namespace-qualified metadata name.</summary>
    Interface,
    /// <summary>A nested type by its simple metadata name.</summary>
    NestedType,
    Method,
    Property,
    Field,
    Event,
    /// <summary>A property or a field.</summary>
    PropertyOrField,
    /// <summary>A property, a field or a method.</summary>
    PropertyFieldOrMethod,
    /// <summary>Any member, nested types included.</summary>
    AnyMember
}

/// <summary>Where a sink looks for its name (D36, D143).</summary>
internal enum NameScope
{
    /// <summary>Every declared type or member: the call names no type.</summary>
    Global,
    /// <summary>The known type and its base types.</summary>
    Hierarchy,
    /// <summary>The known type only.</summary>
    Declared,
    /// <summary>The interfaces of the known type.</summary>
    Interfaces
}

/// <summary>One runtime name-binding API: the parameter that carries the name, and what the name binds.</summary>
internal sealed record ReflectionSink(string Pattern, NameTargetKind TargetKind, NameScope Scope, string? NameParameter);

/// <summary>A sink call: the API, the argument that carries the name, the type it is scoped to, and its case rule.</summary>
internal readonly record struct ReflectionSinkCall(
    ReflectionSink Sink,
    SyntaxNode? NameArgument,
    INamedTypeSymbol? KnownType,
    bool IgnoreCase,
    bool DeclaredOnly);

/// <summary>
///     D36, D143: the runtime name-binding APIs, identified by the bound method (containing type
///     metadata name and method name), never by the name in the syntax. Each entry gives the member
///     kind of its name (D21) and its scope. A call to one of them is a reflection sink: it gets a
///     <c>ReflectionTargetUnknown</c> fact, and its literal name argument can be a name candidate.
/// </summary>
internal static class ReflectionSinkPatterns
{
    private const string BindingFlagsType = "System.Reflection.BindingFlags";
    private const int IgnoreCaseFlag = 1;
    private const int DeclaredOnlyFlag = 2;

    private static readonly Dictionary<(string Type, string Method), ReflectionSink> Sinks = new()
    {
        [("System.Type", "GetType")] = new("Type.GetType", NameTargetKind.Type, NameScope.Global, "typeName"),
        [("System.Type", "GetMethod")] = new("Type.GetMethod", NameTargetKind.Method, NameScope.Hierarchy, "name"),
        [("System.Type", "GetProperty")] = new("Type.GetProperty", NameTargetKind.Property, NameScope.Hierarchy, "name"),
        [("System.Type", "GetField")] = new("Type.GetField", NameTargetKind.Field, NameScope.Hierarchy, "name"),
        [("System.Type", "GetEvent")] = new("Type.GetEvent", NameTargetKind.Event, NameScope.Hierarchy, "name"),
        [("System.Type", "GetMember")] = new("Type.GetMember", NameTargetKind.AnyMember, NameScope.Hierarchy, "name"),
        [("System.Type", "InvokeMember")] = new("Type.InvokeMember", NameTargetKind.AnyMember, NameScope.Hierarchy, "name"),
        [("System.Type", "GetNestedType")] = new("Type.GetNestedType", NameTargetKind.NestedType, NameScope.Declared, "name"),
        [("System.Type", "GetInterface")] = new("Type.GetInterface", NameTargetKind.Interface, NameScope.Interfaces, "name"),
        [("System.Type", "MakeGenericType")] = new("Type.MakeGenericType", NameTargetKind.None, NameScope.Global, null),
        [("System.Reflection.MethodInfo", "MakeGenericMethod")] = new("MethodInfo.MakeGenericMethod", NameTargetKind.None, NameScope.Global, null),
        [("System.Reflection.Assembly", "GetType")] = new("Assembly.GetType", NameTargetKind.Type, NameScope.Global, "name"),
        [("System.Reflection.Assembly", "GetExportedTypes")] = new("Assembly.GetExportedTypes", NameTargetKind.None, NameScope.Global, null),
        [("System.Activator", "CreateInstance")] = new("Activator.CreateInstance", NameTargetKind.Type, NameScope.Global, "typeName"),
        [("System.Reflection.RuntimeReflectionExtensions", "GetRuntimeMethod")] = new("RuntimeReflectionExtensions.GetRuntimeMethod", NameTargetKind.Method, NameScope.Hierarchy, "name"),
        [("System.Reflection.RuntimeReflectionExtensions", "GetRuntimeProperty")] = new("RuntimeReflectionExtensions.GetRuntimeProperty", NameTargetKind.Property, NameScope.Hierarchy, "name"),
        [("System.Reflection.RuntimeReflectionExtensions", "GetRuntimeField")] = new("RuntimeReflectionExtensions.GetRuntimeField", NameTargetKind.Field, NameScope.Hierarchy, "name"),
        [("System.Reflection.RuntimeReflectionExtensions", "GetRuntimeEvent")] = new("RuntimeReflectionExtensions.GetRuntimeEvent", NameTargetKind.Event, NameScope.Hierarchy, "name"),
        [("System.Reflection.TypeInfo", "GetDeclaredMethod")] = new("TypeInfo.GetDeclaredMethod", NameTargetKind.Method, NameScope.Declared, "name"),
        [("System.Reflection.TypeInfo", "GetDeclaredMethods")] = new("TypeInfo.GetDeclaredMethods", NameTargetKind.Method, NameScope.Declared, "name"),
        [("System.Reflection.TypeInfo", "GetDeclaredProperty")] = new("TypeInfo.GetDeclaredProperty", NameTargetKind.Property, NameScope.Declared, "name"),
        [("System.Reflection.TypeInfo", "GetDeclaredField")] = new("TypeInfo.GetDeclaredField", NameTargetKind.Field, NameScope.Declared, "name"),
        [("System.Reflection.TypeInfo", "GetDeclaredEvent")] = new("TypeInfo.GetDeclaredEvent", NameTargetKind.Event, NameScope.Declared, "name"),
        [("System.Reflection.TypeInfo", "GetDeclaredNestedType")] = new("TypeInfo.GetDeclaredNestedType", NameTargetKind.NestedType, NameScope.Declared, "name"),
    };

    /// <summary>
    ///     The sink that <paramref name="invocation" /> calls, or null when it calls no runtime
    ///     name-binding API (for example a user method named <c>GetType</c>, R6 of S01-F35).
    /// </summary>
    internal static ReflectionSinkCall? Resolve(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (semanticModel.GetOperation(invocation) is not IInvocationOperation operation)
            return null;

        var method = operation.TargetMethod.ReducedFrom ?? operation.TargetMethod;
        method = method.OriginalDefinition;
        if (method.ContainingType is not { } containingType ||
            !Sinks.TryGetValue((containingType.ToDisplayString(), method.Name), out var sink))
            return null;

        SyntaxNode? nameArgument = null;
        var ignoreCase = false;
        var declaredOnly = false;
        INamedTypeSymbol? knownType = KnownType(operation.Instance);
        foreach (var argument in operation.Arguments)
        {
            if (argument.Parameter is not { } parameter)
                continue;

            if (parameter.Name == sink.NameParameter && parameter.Type.SpecialType == SpecialType.System_String)
                nameArgument = argument.Syntax;
            else if (parameter.Ordinal == 0 && method.IsExtensionMethod)
                knownType = KnownType(argument.Value);
            else if (parameter.Type.ToDisplayString() == BindingFlagsType && argument.Value.ConstantValue is { HasValue: true, Value: int flags })
            {
                ignoreCase |= (flags & IgnoreCaseFlag) != 0;
                declaredOnly |= (flags & DeclaredOnlyFlag) != 0;
            }
            else if (parameter.Name == "ignoreCase" && argument.Value.ConstantValue is { HasValue: true, Value: true })
                ignoreCase = true;
        }

        return new ReflectionSinkCall(sink, nameArgument, knownType, ignoreCase, declaredOnly);
    }

    /// <summary>The type a receiver names at compile time: <c>typeof(X)</c>, also through <c>GetTypeInfo()</c>.</summary>
    private static INamedTypeSymbol? KnownType(IOperation? receiver)
    {
        while (receiver is IConversionOperation conversion)
            receiver = conversion.Operand;

        return receiver switch
        {
            ITypeOfOperation typeOf => typeOf.TypeOperand as INamedTypeSymbol,
            IInvocationOperation { TargetMethod.Name: "GetTypeInfo" } getTypeInfo
                when getTypeInfo.TargetMethod.ContainingType?.ToDisplayString() == "System.Reflection.IntrospectionExtensions"
                => KnownType(getTypeInfo.Arguments.FirstOrDefault()?.Value),
            _ => null
        };
    }
}
