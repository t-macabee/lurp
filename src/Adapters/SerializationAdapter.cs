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

    private const string DataContractDataMember = "System.Runtime.Serialization.DataMemberAttribute";
    private const string DataContractIgnoreDataMember = "System.Runtime.Serialization.IgnoreDataMemberAttribute";

    private const int JsonIgnoreConditionAlways = 1;

    public string Name => "Serialization";
    public string Version => "serialization-v2";
    public string Description => "Serialization contracts (System.Text.Json, Newtonsoft.Json, DataContract)";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var edges = new List<EdgeRecord>();
        var annotations = new List<AnnotationRecord>();

        foreach (var type in context.GetAllNamedTypes())
        {
            if (!context.IsSymbolInScope(type))
                continue;

            ExtractJsonSerializable(type, context, edges);

            foreach (var member in type.GetMembers())
            {
                if (member is not (IPropertySymbol or IFieldSymbol { IsImplicitlyDeclared: false }))
                    continue;

                if (!context.IsSymbolInScope(member))
                    continue;

                ExtractMemberContracts(member, context, annotations);
            }
        }

        return new AdapterExtractionResult(edges, annotations);
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

    private static void ExtractMemberContracts(
        ISymbol member,
        AdapterExtractionContext context,
        List<AnnotationRecord> annotations)
    {
        var memberId = context.MakeSymbolId(member);
        if (memberId == null)
            return;

        (int Rank, string Value) stj = default;
        (int Rank, string Value) newtonsoft = default;
        (int Rank, string Value) dataContract = default;

        foreach (var attr in member.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass == null)
                continue;

            if (FrameworkTypes.Is(attrClass, StjJsonIgnore))
            {
                var condition = GetNamedInt(attr, "Condition") ?? JsonIgnoreConditionAlways;
                var candidate = condition == JsonIgnoreConditionAlways ? (3, "ignored") : (1, "included");
                stj = PickHigher(stj, candidate);
            }
            else if (FrameworkTypes.Is(attrClass, StjJsonPropertyName))
            {
                if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string name)
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
                var name = GetNamedString(attr, "Name");
                var candidate = name != null ? (2, $"name={name}") : (1, "included");
                dataContract = PickHigher(dataContract, candidate);
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
