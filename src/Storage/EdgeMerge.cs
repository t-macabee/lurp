using System.Text.Json;

namespace Lurp.Storage;

public static class EdgeMerge
{
    public static List<EdgeRecord> CollapseBatch(IEnumerable<EdgeRecord> edges)
    {
        var best = new Dictionary<(string source, string target, string kind), EdgeRecord>();

        foreach (var edge in edges)
        {
            var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
            if (best.TryGetValue(key, out var existing))
            {
                if (ProvenanceRank(edge.Provenance) > ProvenanceRank(existing.Provenance))
                    best[key] = WithMergedTypeArguments(edge, existing.TypeArgumentsJson);
                else
                    best[key] = WithMergedTypeArguments(existing, edge.TypeArgumentsJson);
            }
            else
            {
                best[key] = edge;
            }
        }

        return [.. best.Values];
    }

    public static EdgeRecord WithMergedTypeArguments(EdgeRecord edge, string? additionalTypeArgumentsJson)
    {
        var merged = MergeTypeArguments(edge.TypeArgumentsJson, additionalTypeArgumentsJson);
        if (string.Equals(merged, edge.TypeArgumentsJson, StringComparison.Ordinal))
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
            TypeArgumentsJson = merged,
            ReceiverTypeConstraintsJson = edge.ReceiverTypeConstraintsJson,
            SourceNodeKind = edge.SourceNodeKind,
            TargetNodeKind = edge.TargetNodeKind
        };
    }

    public static string? MergeTypeArguments(string? leftJson, string? rightJson)
    {
        var variants = DeserializeTypeArguments(leftJson);
        foreach (var variant in DeserializeTypeArguments(rightJson))
            if (!variants.Any(existing => existing.SequenceEqual(variant, StringComparer.Ordinal)))
                variants.Add(variant);

        return variants.Count == 0 ? null : SerializeTypeArguments(variants);
    }

    // Receiver-type-constraint JSON uses the same canonical List<List<string>>
    // encoding as type-argument JSON, so the union algorithm is identical. This
    // lives in Storage (not Workspace's ReceiverTypeConstraints) so EdgeOperationsStore
    // can union-merge without a dependency on Microsoft.CodeAnalysis.
    public static string? MergeReceiverTypeConstraints(string? leftJson, string? rightJson)
    {
        return MergeTypeArguments(leftJson, rightJson);
    }

    public static List<List<string>> DeserializeTypeArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return [];

            if (root[0].ValueKind == JsonValueKind.Array)
            {
                var result = new List<List<string>>();
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Array)
                        continue;
                    var variant = element.EnumerateArray()
                        .Select(static e => e.GetString())
                        .Where(static s => s != null && !string.IsNullOrWhiteSpace(s))
                        .Cast<string>()
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static s => s, StringComparer.Ordinal)
                        .ToList();
                    if (variant.Count > 0)
                        result.Add(variant);
                }

                return result;
            }

            {
                var variant = root.EnumerateArray()
                    .Select(static e => e.GetString())
                    .Where(static s => s != null && !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static s => s, StringComparer.Ordinal)
                    .ToList();
                return variant.Count == 0 ? [] : [variant];
            }
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeTypeArguments(IEnumerable<IEnumerable<string>> variants)
    {
        var canonical = variants
            .Select(static variant => variant.Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal).ToList())
            .Where(static variant => variant.Count > 0)
            .OrderBy(static variant => string.Join("\n", variant), StringComparer.Ordinal)
            .ToList();
        return JsonSerializer.Serialize(canonical);
    }

    public static int ProvenanceRank(string provenance)
    {
        return provenance switch
        {
            Provenance.CompilerProved => 6,
            Provenance.FrameworkDerived => 5,
            Provenance.GlobalImplementationRelation => 4,
            Provenance.Possible => 3,
            Provenance.Convention => 2,
            Provenance.NameCandidate => 1,
            Provenance.RuntimeUnknown => 0,
            _ => -1
        };
    }
}