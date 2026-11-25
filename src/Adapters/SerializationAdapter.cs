using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class SerializationAdapter : IFrameworkAdapter
{
    private const string StjJsonPropertyName = "System.Text.Json.Serialization.JsonPropertyNameAttribute";
    private const string StjJsonIgnore = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string StjJsonInclude = "System.Text.Json.Serialization.JsonIncludeAttribute";
    private const string StjJsonSerializable = "System.Text.Json.Serialization.JsonSerializableAttribute";

    private const string NewtonsoftJsonProperty = "Newtonsoft.Json.JsonPropertyAttribute";
    private const string NewtonsoftJsonIgnore = "Newtonsoft.Json.JsonIgnoreAttribute";
    private const string NewtonsoftOnError = "Newtonsoft.Json.Serialization.OnErrorAttribute";

    private static readonly string[] StjCallbackInterfaces =
    [
        "System.Text.Json.Serialization.IJsonOnSerializing",
        "System.Text.Json.Serialization.IJsonOnSerialized",
        "System.Text.Json.Serialization.IJsonOnDeserializing",
        "System.Text.Json.Serialization.IJsonOnDeserialized"
    ];

    private const string SerializationContract = "serialization_contract";

    private const string DataContractAttribute = "System.Runtime.Serialization.DataContractAttribute";
    private const string DataContractDataMember = "System.Runtime.Serialization.DataMemberAttribute";
    private const string DataContractIgnoreDataMember = "System.Runtime.Serialization.IgnoreDataMemberAttribute";

    private const int JsonIgnoreConditionAlways = 1;

    public string Name => "Serialization";
    public string Version => "serialization-v3";
    public string Description => "Serialization contracts (System.Text.Json, Newtonsoft.Json, DataContract)";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var edges = new List<EdgeRecord>();
        var annotations = new List<AnnotationRecord>();
        var callbackInterfaces = StjCallbackInterfaces
            .Select(context.Compilation.GetTypeByMetadataName)
            .OfType<INamedTypeSymbol>()
            .ToList();

        foreach (var type in context.GetAllNamedTypes())
        {
            if (!context.IsSymbolInScope(type))
                continue;

            ExtractJsonSerializable(type, context, edges);
            ExtractInterfaceCallbacks(type, callbackInterfaces, context, annotations);

            var hasDataContract = HasAttribute(type, DataContractAttribute);
            foreach (var member in type.GetMembers())
            {
                if (!context.IsSymbolInScope(member))
                    continue;

                switch (member)
                {
                    case IPropertySymbol or IFieldSymbol { IsImplicitlyDeclared: false }:
                        ExtractMemberContracts(member, hasDataContract, context, annotations);
                        break;
                    case IMethodSymbol { IsImplicitlyDeclared: false } method:
                        ExtractMethodContract(method, context, annotations);
                        break;
                }
            }
        }

        return new AdapterExtractionResult(edges, Deduplicate(annotations));
    }

    /// <summary>
    ///     System.Text.Json calls these interfaces' methods by their interface member, so the
    ///     implementation can be implicit, explicit or inherited. It is resolved through the
    ///     type's interface map, which also gives the document that retirement must follow.
    /// </summary>
    private static void ExtractInterfaceCallbacks(
        INamedTypeSymbol type,
        List<INamedTypeSymbol> callbackInterfaces,
        AdapterExtractionContext context,
        List<AnnotationRecord> annotations)
    {
        foreach (var callbackInterface in callbackInterfaces)
        {
            foreach (var member in callbackInterface.GetMembers())
            {
                if (member is not IMethodSymbol interfaceMethod)
                    continue;

                if (type.FindImplementationForInterfaceMember(interfaceMethod) is not IMethodSymbol implementation)
                    continue;

                var definition = implementation.OriginalDefinition;
                if (definition.IsStatic || definition.IsGenericMethod || !context.IsSolutionSymbol(definition))
                    continue;

                if (definition.ContainingType is not { } declaringType)
                    continue;

                // An implicit or explicit implementation belongs to the document of the method.
                // An inherited method whose base does not implement the interface belongs to the
                // derived type's document; otherwise the base's own walk emits it.
                string? documentPath;
                if (SymbolEqualityComparer.Default.Equals(declaringType, type.OriginalDefinition))
                    documentPath = context.LocationResolver.Resolve(definition).path;
                else if (!declaringType.AllInterfaces.Any(i =>
                             SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, callbackInterface.OriginalDefinition)))
                    documentPath = context.LocationResolver.Resolve(type).path;
                else
                    continue;

                if (documentPath == null || context.MakeSymbolId(definition) is not { } id)
                    continue;

                annotations.Add(new AnnotationRecord(id, SerializationContract, "included", documentPath));
            }
        }
    }

    /// <summary>
    ///     The annotations unique index keys on the document too. Two derived types that inherit
    ///     one callback must keep one row per derived document, so the key has the same shape.
    /// </summary>
    private static List<AnnotationRecord> Deduplicate(List<AnnotationRecord> annotations)
    {
        var seen = new HashSet<(string Symbol, string Kind, string Value, string Document)>();
        var results = new List<AnnotationRecord>(annotations.Count);
        foreach (var annotation in annotations)
        {
            if (annotation.DocumentPath == null ||
                seen.Add((annotation.SymbolId, annotation.Kind, annotation.Value, annotation.DocumentPath)))
                results.Add(annotation);
        }

        return results;
    }

    private void ExtractJsonSerializable(INamedTypeSymbol type, AdapterExtractionContext context, List<EdgeRecord> edges)
    {
        var sourceId = context.MakeSymbolId(type);
        if (sourceId == null)
            return;

        foreach (var attr in type.GetAttributes())
        {
            if (!FrameworkTypes.Is(attr.AttributeClass, StjJsonSerializable))
                continue;

            if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Value is not ITypeSymbol rootType)
                continue;

            var location = attr.ApplicationSyntaxReference != null
                ? context.LocationResolver.Resolve(attr.ApplicationSyntaxReference.GetSyntax().GetLocation())
                : context.LocationResolver.Resolve(type);

            var targets = new List<INamedTypeSymbol>();
            CollectNamedTypes(rootType, targets);

            foreach (var target in targets)
            {
                var targetId = context.MakeSymbolId(target);
                if (targetId != null)
                {
                    edges.Add(context.MakeEdge(sourceId, targetId, nameof(EdgeKind.References),
                        Provenance.FrameworkDerived, Version, location));
                }
            }
        }
    }

    private static void CollectNamedTypes(ITypeSymbol type, List<INamedTypeSymbol> results)
    {
        switch (type)
        {
            case INamedTypeSymbol named:
                results.Add(named.OriginalDefinition);
                foreach (var typeArg in named.TypeArguments)
                    CollectNamedTypes(typeArg, results);
                break;
            case IArrayTypeSymbol array:
                CollectNamedTypes(array.ElementType, results);
                break;
        }
    }

    private static void ExtractMethodContract(
        IMethodSymbol method, AdapterExtractionContext context, List<AnnotationRecord> annotations)
    {
        var included = method.MethodKind switch
        {
            MethodKind.Constructor => HasAttribute(method, "System.Text.Json.Serialization.JsonConstructorAttribute") ||
                                      HasAttribute(method, "Newtonsoft.Json.JsonConstructorAttribute"),
            MethodKind.Ordinary => !method.IsStatic && !method.IsGenericMethod &&
                                   (HasAttribute(method, "System.Runtime.Serialization.OnSerializingAttribute") ||
                                    HasAttribute(method, "System.Runtime.Serialization.OnSerializedAttribute") ||
                                    HasAttribute(method, "System.Runtime.Serialization.OnDeserializingAttribute") ||
                                    HasAttribute(method, "System.Runtime.Serialization.OnDeserializedAttribute") ||
                                    HasAttribute(method, NewtonsoftOnError)),
            _ => false
        };

        if (included && context.MakeSymbolId(method) is { } id)
            annotations.Add(new AnnotationRecord(id, SerializationContract, "included",
                context.LocationResolver.Resolve(method).path));
    }

    private static void ExtractMemberContracts(
        ISymbol member,
        bool hasDataContract,
        AdapterExtractionContext context,
        List<AnnotationRecord> annotations)
    {
        var memberId = context.MakeSymbolId(member);
        if (memberId == null)
            return;

        var attributes = member.GetAttributes();
        var stjVisible = member.DeclaredAccessibility == Accessibility.Public ||
                         attributes.Any(static a => a.AttributeClass != null && FrameworkTypes.Is(a.AttributeClass, StjJsonInclude));

        (int Rank, string Value) stj = default;
        (int Rank, string Value) newtonsoft = default;
        (int Rank, string Value) dataContract = default;

        foreach (var attr in attributes)
        {
            var attrClass = attr.AttributeClass;
            if (attrClass == null)
                continue;

            if (FrameworkTypes.Is(attrClass, StjJsonIgnore))
            {
                var condition = GetNamedInt(attr, "Condition") ?? JsonIgnoreConditionAlways;
                if (condition == JsonIgnoreConditionAlways)
                    stj = PickHigher(stj, (3, "ignored"));
                else if (stjVisible)
                    stj = PickHigher(stj, (1, "included"));
            }
            else if (FrameworkTypes.Is(attrClass, StjJsonPropertyName))
            {
                if (stjVisible && attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string name)
                    stj = PickHigher(stj, (2, $"name={name}"));
            }
            else if (FrameworkTypes.Is(attrClass, StjJsonInclude))
            {
                stj = PickHigher(stj, (1, "included"));
            }
            else if (FrameworkTypes.Is(attrClass, NewtonsoftJsonIgnore))
            {
                newtonsoft = PickHigher(newtonsoft, (3, "ignored"));
            }
            else if (FrameworkTypes.Is(attrClass, NewtonsoftJsonProperty))
            {
                var name = (attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string : null)
                           ?? GetNamedString(attr, "PropertyName");
                var candidate = name != null ? (2, $"name={name}") : (1, "included");
                newtonsoft = PickHigher(newtonsoft, candidate);
            }
            else if (FrameworkTypes.Is(attrClass, DataContractIgnoreDataMember))
            {
                dataContract = PickHigher(dataContract, (3, "ignored"));
            }
            else if (FrameworkTypes.Is(attrClass, DataContractDataMember))
            {
                if (hasDataContract)
                {
                    var name = GetNamedString(attr, "Name");
                    var candidate = name != null ? (2, $"name={name}") : (1, "included");
                    dataContract = PickHigher(dataContract, candidate);
                }
            }
        }

        if (stj.Rank == 0 && newtonsoft.Rank == 0 && dataContract.Rank == 0)
            return;

        var distinctValues = new HashSet<string>(StringComparer.Ordinal);
        if (stj.Rank > 0 && stj.Value != null) distinctValues.Add(stj.Value);
        if (newtonsoft.Rank > 0 && newtonsoft.Value != null) distinctValues.Add(newtonsoft.Value);
        if (dataContract.Rank > 0 && dataContract.Value != null) distinctValues.Add(dataContract.Value);

        var docPath = context.LocationResolver.Resolve(member).path;
        foreach (var value in distinctValues)
            annotations.Add(new AnnotationRecord(memberId, "serialization_contract", value, docPath));
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass != null && FrameworkTypes.Is(attr.AttributeClass, metadataName))
                return true;
        }
        return false;
    }

    private static (int Rank, string Value) PickHigher((int Rank, string Value) current, (int Rank, string Value) candidate)
    {
        return candidate.Rank > current.Rank ? candidate : current;
    }

    private static string? GetNamedString(AttributeData attr, string name)
    {
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == name && named.Value.Value is string s)
                return s;
        }

        return null;
    }

    private static int? GetNamedInt(AttributeData attr, string name)
    {
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == name && named.Value.Value is int i)
                return i;
        }

        return null;
    }
}
