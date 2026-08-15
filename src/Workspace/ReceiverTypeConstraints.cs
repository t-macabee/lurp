using Microsoft.CodeAnalysis;
using System.Text.Json;

namespace Lurp.Workspace;

internal static class ReceiverTypeConstraints
{
    internal static string? FromReceiverType(ITypeSymbol? receiverType, MemberEdgeExtractionContext context)
    {
        if (receiverType is null or IDynamicTypeSymbol)
            return null;

        if (receiverType is ITypeParameterSymbol typeParameter)
        {
            // Persist only constraints whose complete assignability test can be
            // replayed from the stored type graph. Special constraints need facts
            // that the graph does not currently retain, so omitting candidates is
            // safer than admitting an incompatible implementation.
            if (typeParameter.HasReferenceTypeConstraint ||
                typeParameter.HasValueTypeConstraint ||
                typeParameter.HasUnmanagedTypeConstraint ||
                typeParameter.HasConstructorConstraint ||
                typeParameter.HasNotNullConstraint ||
                typeParameter.ConstraintTypes.IsEmpty)
                return null;

            var constraintIds = new List<string>();
            foreach (var constraint in typeParameter.ConstraintTypes)
            {
                var constraintId = context.MakeSymbolId(constraint);
                if (constraintId == null)
                    return null;
                constraintIds.Add(constraintId);
            }

            return Serialize([constraintIds]);
        }

        var receiverTypeId = context.MakeSymbolId(receiverType);
        return receiverTypeId == null ? null : Serialize([[receiverTypeId]]);
    }

    internal static string? Merge(string? leftJson, string? rightJson)
    {
        var alternatives = Deserialize(leftJson);
        foreach (var alternative in Deserialize(rightJson))
            if (!alternatives.Any(existing => existing.SequenceEqual(alternative, StringComparer.Ordinal)))
                alternatives.Add(alternative);

        return alternatives.Count == 0 ? null : Serialize(alternatives);
    }

    internal static List<List<string>> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<List<string>>>(json)?
                .Where(static alternative => alternative.Count > 0 && alternative.All(static id => !string.IsNullOrWhiteSpace(id)))
                .Select(static alternative => alternative.Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal).ToList())
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string SerializeForTests(params string[] requiredTypeIds)
    {
        return Serialize([requiredTypeIds.ToList()]);
    }

    private static string Serialize(IEnumerable<IEnumerable<string>> alternatives)
    {
        var canonical = alternatives
            .Select(static alternative => alternative.Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal).ToList())
            .Where(static alternative => alternative.Count > 0)
            .OrderBy(static alternative => string.Join("\n", alternative), StringComparer.Ordinal)
            .ToList();
        return JsonSerializer.Serialize(canonical);
    }

    internal static EdgeRecord WithMergedConstraints(EdgeRecord edge, string? additionalConstraintsJson)
    {
        var merged = Merge(edge.ReceiverTypeConstraintsJson, additionalConstraintsJson);
        if (string.Equals(merged, edge.ReceiverTypeConstraintsJson, StringComparison.Ordinal))
            return edge;

        return new EdgeRecord
        {
            SourceSymbolId = edge.SourceSymbolId,
            TargetSymbolId = edge.TargetSymbolId,
            Kind = edge.Kind,
            Provenance = edge.Provenance,
            SnapshotId = edge.SnapshotId,
            ExtractorVersion = edge.ExtractorVersion,
            SourceDocumentPath = edge.SourceDocumentPath,
            SourceStartLine = edge.SourceStartLine,
            SourceStartColumn = edge.SourceStartColumn,
            SourceEndLine = edge.SourceEndLine,
            SourceEndColumn = edge.SourceEndColumn,
            IsCrossGenerated = edge.IsCrossGenerated,
            TypeArgumentsJson = edge.TypeArgumentsJson,
            ReceiverTypeConstraintsJson = merged,
            SourceNodeKind = edge.SourceNodeKind,
            TargetNodeKind = edge.TargetNodeKind
        };
    }
}