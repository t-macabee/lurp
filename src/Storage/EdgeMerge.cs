using System.Text.Json;

namespace Lurp.Storage;

public static class EdgeMerge
{
    /// <summary>
    ///     Collapses a batch to one edge for each (source, target, kind). The edge with the
    ///     higher provenance rank wins (the first one on a tie); the type-argument and the
    ///     receiver-constraint variants of every edge of the key are kept (<see cref="Merge" />),
    ///     and <see cref="EdgeRecord.Sites" /> keeps the ordered sites of the winning rank (D155.4).
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

    /// <summary>
    ///     The row of <paramref name="winner" /> with the union of the type-argument variants and
    ///     the union of the receiver-constraint variants of both edges, and sites per D155.4
    ///     (equal rank adds sites; lower rank adds none).
    /// </summary>
    public static EdgeRecord Merge(EdgeRecord winner, EdgeRecord other)
    {
        var typeArguments = MergeTypeArguments(winner.TypeArgumentsJson, other.TypeArgumentsJson);
        var constraints = MergeReceiverTypeConstraints(winner.ReceiverTypeConstraintsJson, other.ReceiverTypeConstraintsJson);
        var winnerSites = NormalizeSites(GetSites(winner));
        var mergedSites = ProvenanceRank(winner.Provenance) == ProvenanceRank(other.Provenance)
            ? NormalizeSites(winnerSites.Concat(GetSites(other)))
            : winnerSites;

        return winner.WithSites(mergedSites, typeArguments, constraints);
    }

    internal static IReadOnlyList<EdgeSite> GetSites(EdgeRecord edge)
    {
        if (edge.Sites.Count > 0)
            return edge.Sites;

        if (TryGetPrimarySite(edge, out var site))
            return [site];

        return [];
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

    internal static IReadOnlyList<EdgeSite> NormalizeSites(IEnumerable<EdgeSite> sites)
    {
        if (sites is IReadOnlyList<EdgeSite> readOnly)
        {
            if (readOnly.Count == 0)
                return [];
            if (readOnly.Count == 1)
                return [readOnly[0]];
        }

        var list = sites.Distinct().ToList();
        list.Sort(CompareSites);
        return list;
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

    private sealed class BatchEdgeBuilder
    {
        private EdgeRecord _winner;
        private int _winnerRank;
        private string? _typeArguments;
        private bool _typeArgumentsNormalized;
        private string? _constraints;
        private bool _constraintsNormalized;
        private readonly List<EdgeSite> _sites = [];

        public BatchEdgeBuilder(EdgeRecord first)
        {
            _winner = first;
            _winnerRank = ProvenanceRank(first.Provenance);
            _typeArguments = first.TypeArgumentsJson;
            _constraints = first.ReceiverTypeConstraintsJson;
            AddSites(first);
        }

        public void Add(EdgeRecord edge)
        {
            var edgeRank = ProvenanceRank(edge.Provenance);
            if (edgeRank > _winnerRank)
            {
                var taNormalized = false;
                _typeArguments = MergePayload(edge.TypeArgumentsJson, _typeArguments, ref taNormalized);
                _typeArgumentsNormalized = taNormalized;

                var rcNormalized = false;
                _constraints = MergePayload(edge.ReceiverTypeConstraintsJson, _constraints, ref rcNormalized);
                _constraintsNormalized = rcNormalized;

                _winner = edge;
                _winnerRank = edgeRank;
                _sites.Clear();
                AddSites(edge);
            }
            else
            {
                _typeArguments = MergePayload(_typeArguments, edge.TypeArgumentsJson, ref _typeArgumentsNormalized);
                _constraints = MergePayload(_constraints, edge.ReceiverTypeConstraintsJson, ref _constraintsNormalized);
                if (edgeRank == _winnerRank)
                    AddSites(edge);
            }
        }

        public EdgeRecord Build() =>
            _winner.WithSites(NormalizeSites(_sites), _typeArguments, _constraints);

        private void AddSites(EdgeRecord edge)
        {
            if (edge.Sites.Count > 0)
                _sites.AddRange(edge.Sites);
            else if (TryGetPrimarySite(edge, out var site))
                _sites.Add(site);
        }

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