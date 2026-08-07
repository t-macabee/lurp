using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Workspace;

internal static class EdgeDedup
{
    public static List<EdgeRecord> Deduplicate(IEnumerable<EdgeRecord> edges)
    {
        var best = new Dictionary<(string source, string target, string kind), EdgeRecord>();

        foreach (var edge in edges)
        {
            var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
            if (best.TryGetValue(key, out var existing))
            {
                if (ProvenanceRank(edge.Provenance) > ProvenanceRank(existing.Provenance))
                {
                    best[key] = WithMergedTypeArguments(edge, existing.TypeArgumentsJson);
                }
                else
                {
                    best[key] = WithMergedTypeArguments(existing, edge.TypeArgumentsJson);
                }
            }
            else
            {
                best[key] = edge;
            }
        }

        return new List<EdgeRecord>(best.Values);
    }

    internal static EdgeRecord WithMergedTypeArguments(EdgeRecord edge, string? additionalTypeArgumentsJson)
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
            TargetNodeKind = edge.TargetNodeKind,
        };
    }

    internal static string? MergeTypeArguments(string? leftJson, string? rightJson)
    {
        var variants = DeserializeTypeArguments(leftJson);
        foreach (var variant in DeserializeTypeArguments(rightJson))
        {
            if (!variants.Any(existing => existing.SequenceEqual(variant, StringComparer.Ordinal)))
                variants.Add(variant);
        }

        return variants.Count == 0 ? null : SerializeTypeArguments(variants);
    }

    internal static List<List<string>> DeserializeTypeArguments(string? json)
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
            else
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

    private static string SerializeTypeArguments(IEnumerable<IEnumerable<string>> variants)
    {
        var canonical = variants
            .Select(static variant => variant.Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal).ToList())
            .Where(static variant => variant.Count > 0)
            .OrderBy(static variant => string.Join("\n", variant), StringComparer.Ordinal)
            .ToList();
        return JsonSerializer.Serialize(canonical);
    }

    /// <summary>
    /// Returns the rank for a provenance string. Higher = stronger evidence.
    /// Unknown provenance values rank below all canonical values so they are
    /// never selected as "best" over a known value during dedup.
    /// </summary>
    internal static int ProvenanceRank(string provenance) => provenance switch
    {
        Provenance.CompilerProved => 6,
        Provenance.FrameworkDerived => 5,
        Provenance.GlobalImplementationRelation => 4,
        Provenance.Possible => 3,
        Provenance.Convention => 2,
        Provenance.NameCandidate => 1,
        Provenance.RuntimeUnknown => 0,
        _ => -1,
    };

    /// <summary>
    /// Composes the effective claim provenance for a caller reached through an
    /// interface/abstract dispatch path (one or more Calls hops plus a
    /// MayDispatchTo edge).
    ///
    /// This is not the strongest : or weakest : edge in the path. The
    /// structural edges may each be individually compiler-proved while the
    /// projected runtime-target claim remains "possible", because the compiler
    /// establishes that an implementation exists, not that this call site
    /// selects it at runtime. The only stronger outcome is actual framework
    /// participation: when a framework, registration, routing, or DI-derived
    /// edge (framework_derived) takes part in the composed path.
    /// </summary>
    internal static string ComposeDispatchClaimProvenance(IEnumerable<string> pathProvenances, string dispatchProvenance)
    {
        if (dispatchProvenance == Provenance.FrameworkDerived
            || pathProvenances.Any(provenance => provenance == Provenance.FrameworkDerived))
        {
            return Provenance.FrameworkDerived;
        }
        return Provenance.Possible;
    }
}
