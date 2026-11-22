using System.Text.Json;

namespace Lurp.Storage;

public static class EdgeMerge
{
    /// <summary>
    ///     Combines records of each edge key. Keeps all evidence and the union of the payloads.
    ///     Projects the fields and shown sites of the highest provenance (D209).
    /// </summary>
    public static List<EdgeRecord> CollapseBatch(IEnumerable<EdgeRecord> edges)
    {
        var builders = new Dictionary<(string source, string target, string kind), BatchEdgeBuilder>();

        foreach (var edge in edges)
        {
            var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
            if (builders.TryGetValue(key, out var builder))
                builder.Add(edge);
            else
                builders[key] = new BatchEdgeBuilder(edge);
        }

        var result = new List<EdgeRecord>(builders.Count);
        foreach (var builder in builders.Values)
            result.Add(builder.Build());
        return result;
    }

    private static bool TryGetPrimarySite(EdgeRecord edge, out EdgeSite site)
    {
        if (edge.SourceDocumentPath != null &&
            edge.SourceStartLine.HasValue &&
            edge.SourceStartColumn.HasValue &&
            edge.SourceEndLine.HasValue &&
            edge.SourceEndColumn.HasValue)
        {
            site = new EdgeSite(
                edge.SourceDocumentPath,
                edge.SourceStartLine.Value,
                edge.SourceStartColumn.Value,
                edge.SourceEndLine.Value,
                edge.SourceEndColumn.Value);
            return true;
        }

        site = default!;
        return false;
    }

    internal static int CompareSites(EdgeSite a, EdgeSite b)
    {
        var cmp = StringComparer.Ordinal.Compare(a.DocumentPath, b.DocumentPath);
        if (cmp != 0) return cmp;
        cmp = a.StartLine.CompareTo(b.StartLine);
        if (cmp != 0) return cmp;
        cmp = a.StartColumn.CompareTo(b.StartColumn);
        if (cmp != 0) return cmp;
        cmp = a.EndLine.CompareTo(b.EndLine);
        return cmp != 0 ? cmp : a.EndColumn.CompareTo(b.EndColumn);
    }

    private static int CompareEvidence(EdgeSiteEvidence a, EdgeSiteEvidence b)
    {
        var cmp = ProvenanceRank(b.Provenance).CompareTo(ProvenanceRank(a.Provenance));
        if (cmp != 0) return cmp;
        cmp = CompareSites(a.Site, b.Site);
        if (cmp != 0) return cmp;
        cmp = string.CompareOrdinal(a.Provenance, b.Provenance);
        if (cmp != 0) return cmp;
        cmp = string.CompareOrdinal(a.ExtractorVersion, b.ExtractorVersion);
        return cmp != 0 ? cmp : a.IsCrossGenerated.CompareTo(b.IsCrossGenerated);
    }

    private sealed class BatchEdgeBuilder
    {
        private readonly EdgeRecord _first;
        private readonly List<EdgeSiteEvidence> _evidence = [];
        private EdgeRecord? _unlocated;
        private string? _typeArguments;
        private bool _typeArgumentsNormalized;
        private string? _constraints;
        private bool _constraintsNormalized;

        public BatchEdgeBuilder(EdgeRecord first)
        {
            _first = first;
            _typeArguments = first.TypeArgumentsJson;
            _constraints = first.ReceiverTypeConstraintsJson;
            AddEvidence(first);
        }

        public void Add(EdgeRecord edge)
        {
            _typeArguments = MergePayload(_typeArguments, edge.TypeArgumentsJson, ref _typeArgumentsNormalized);
            _constraints = MergePayload(_constraints, edge.ReceiverTypeConstraintsJson, ref _constraintsNormalized);
            AddEvidence(edge);
        }

        public EdgeRecord Build()
        {
            if (_evidence.Count > 1)
            {
                _evidence.Sort(CompareEvidence);
                var count = 1;
                for (var i = 1; i < _evidence.Count; i++)
                {
                    var current = _evidence[i];
                    var previous = _evidence[count - 1];
                    if (current.Site != previous.Site || current.Provenance != previous.Provenance)
                        _evidence[count++] = current;
                }
                _evidence.RemoveRange(count, _evidence.Count - count);
            }

            if (_unlocated != null && (_evidence.Count == 0 ||
                ProvenanceRank(_unlocated.Provenance) > ProvenanceRank(_evidence[0].Provenance)))
                return _unlocated.WithSites([], _typeArguments, _constraints, _evidence, null);

            var winner = _evidence[0];
            var sites = _evidence.Count == 1
                ? [winner.Site]
                : _evidence.Where(e => e.Provenance == winner.Provenance).Select(e => e.Site).ToArray();
            return _first.WithSites(sites, _typeArguments, _constraints, _evidence, winner);
        }

        private void AddEvidence(EdgeRecord edge)
        {
            if (edge.Evidence.Count > 0)
                _evidence.AddRange(edge.Evidence);
            else if (edge.Sites.Count > 0)
            {
                foreach (var site in edge.Sites)
                    AddSite(edge, site);
            }
            else if (TryGetPrimarySite(edge, out var site))
            {
                AddSite(edge, site);
                return;
            }

            if (edge.Sites.Count == 0 &&
                (_unlocated == null || ProvenanceRank(edge.Provenance) > ProvenanceRank(_unlocated.Provenance)))
                _unlocated = edge;
        }

        private void AddSite(EdgeRecord edge, EdgeSite site) =>
            _evidence.Add(new EdgeSiteEvidence(site, edge.Provenance ?? "", edge.ExtractorVersion ?? "", edge.IsCrossGenerated));

        private static string? MergePayload(string? left, string? right, ref bool leftNormalized)
        {
            if (left == null && right == null)
            {
                leftNormalized = true;
                return null;
            }

            if (right == null && leftNormalized)
                return left;

            leftNormalized = true;
            return MergeTypeArguments(left, right);
        }
    }

    public static string? MergeTypeArguments(string? leftJson, string? rightJson)
    {
        var variants = DeserializeTypeArguments(leftJson);
        foreach (var variant in DeserializeTypeArguments(rightJson).Where(variant => !variants.Any(existing => existing.SequenceEqual(variant, StringComparer.Ordinal))))
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

    public static int ProvenanceRank(string? provenance)
    {
        return provenance switch
        {
            Provenance.CompilerProved => 6,
            Provenance.FrameworkDerived => 5,
            Provenance.Possible => 3,
            Provenance.Convention => 2,
            Provenance.NameCandidate => 1,
            Provenance.RuntimeUnknown => 0,
            _ => -1
        };
    }
}
