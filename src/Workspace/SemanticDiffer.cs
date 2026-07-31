using Lurp.Storage;
using System.Text.Json;

namespace Lurp.Workspace
{

    public class SemanticDiffer
    {
        private enum MetadataComparisonKind { String, Array, Scalar }

        private readonly record struct MetadataComparisonEntry(
            string Key,
            string ChangeType,
            MetadataComparisonKind Kind);

        private static readonly MetadataComparisonEntry[] MetadataComparisons =
        [
            new(SymbolMetadataKeys.Accessibility, ChangeType.AccessibilityChanged, MetadataComparisonKind.String),
            new(SymbolMetadataKeys.Signature, ChangeType.SignatureChanged, MetadataComparisonKind.String),
            new(SymbolMetadataKeys.BaseType, ChangeType.BaseTypeChanged, MetadataComparisonKind.String),
            new(SymbolMetadataKeys.Interfaces, ChangeType.InterfacesChanged, MetadataComparisonKind.Array),
            new(SymbolMetadataKeys.IsRecord, ChangeType.RecordChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.TypeKind, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsAbstract, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsVirtual, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsOverride, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsStatic, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsAsync, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsExtensionMethod, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsReadOnly, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsWriteOnly, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsConst, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.IsVolatile, ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new(SymbolMetadataKeys.Attributes, ChangeType.AttributeChanged, MetadataComparisonKind.Array)
        ];

        // Intentionally excluded from comparison (captured by signature):
        // - returnType: included in signature for methods, properties, events
        // - arity: generic type parameter count included in signature
        private readonly ISnapshotStore _snapshotStore;
        private readonly ISemanticDiffReadStore _readStore;
        private readonly IEdgeStore _edgeStore;

        public SemanticDiffer(ISnapshotStore snapshotStore, ISemanticDiffReadStore readStore, IEdgeStore edgeStore)
        {
            _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
            _readStore = readStore ?? throw new ArgumentNullException(nameof(readStore));
            _edgeStore = edgeStore ?? throw new ArgumentNullException(nameof(edgeStore));
        }

        public (List<SemanticChange> Changes, int SkippedComparisons) ComputeDiff(string fromSnapshotId, string toSnapshotId)
        {
            return ComputeDiffInternal(fromSnapshotId, toSnapshotId, changedSymbolIds: null);
        }

        public (List<SemanticChange> Changes, int SkippedComparisons) ComputeDiff(string fromSnapshotId, string toSnapshotId, HashSet<string> changedPaths, HashSet<string> changedSymbolIds)
        {
            if (changedSymbolIds.Count == 0)
                return ([], 0);

            return ComputeDiffInternal(fromSnapshotId, toSnapshotId, changedSymbolIds);
        }

        private (List<SemanticChange> Changes, int SkippedComparisons) ComputeDiffInternal(string fromSnapshotId, string toSnapshotId, HashSet<string>? changedSymbolIds)
        {
            var changes = new List<SemanticChange>();
            int skippedComparisons = 0;

            var fromSymbols = GetSymbolIdsInSnapshot(fromSnapshotId);
            var toSymbols = GetSymbolIdsInSnapshot(toSnapshotId);

            var fromSet = new HashSet<string>(fromSymbols);
            var toSet = new HashSet<string>(toSymbols);

            foreach (var symbolId in toSymbols)
            {
                if (changedSymbolIds != null && !changedSymbolIds.Contains(symbolId))
                    continue;
                if (!fromSet.Contains(symbolId))
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolAdded, symbolId, new { symbol_id = symbolId }));
            }

            foreach (var symbolId in fromSymbols)
            {
                if (changedSymbolIds != null && !changedSymbolIds.Contains(symbolId))
                    continue;
                if (!toSet.Contains(symbolId))
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolRemoved, symbolId, new { symbol_id = symbolId }));
            }

            var common = fromSet.Intersect(toSet);
            if (changedSymbolIds != null)
                common = common.Where(id => changedSymbolIds.Contains(id));
            var commonList = common.ToList();

            foreach (var symbolId in commonList)
            {
                var (symbolChanges, symbolSkipped) = ComputeSymbolDiff(symbolId, fromSnapshotId, toSnapshotId);
                changes.AddRange(symbolChanges);
                skippedComparisons += symbolSkipped;
            }

            MatchTransitions(changes, fromSnapshotId, toSnapshotId);

            var fromEdges = _edgeStore.GetEdges(fromSnapshotId);
            var toEdges = _edgeStore.GetEdges(toSnapshotId);

            if (changedSymbolIds != null)
            {
                fromEdges = fromEdges.Where(e => changedSymbolIds.Contains(e.SourceSymbolId) || changedSymbolIds.Contains(e.TargetSymbolId)).ToList();
                toEdges = toEdges.Where(e => changedSymbolIds.Contains(e.SourceSymbolId) || changedSymbolIds.Contains(e.TargetSymbolId)).ToList();
            }

            DiffEdges(fromEdges, toEdges, fromSnapshotId, toSnapshotId, changes);

            return (changes, skippedComparisons);
        }

        private (List<SemanticChange> Changes, int SkippedComparisons) ComputeSymbolDiff(string symbolId, string fromSnapshotId, string toSnapshotId)
        {
            var changes = new List<SemanticChange>();
            int skippedComparisons = 0;

            var fromInfo = _readStore.GetSymbolInfo(symbolId, fromSnapshotId);
            var toInfo = _readStore.GetSymbolInfo(symbolId, toSnapshotId);

            if (fromInfo == null || toInfo == null)
            {
                skippedComparisons++;
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.ComparisonUnavailable, symbolId,
                    new { reason = $"Symbol info missing: from={(fromInfo == null ? "missing" : "present")}, to={(toInfo == null ? "missing" : "present")}" }));
                return (changes, skippedComparisons);
            }

            if (!string.Equals(fromInfo.FullyQualifiedName, toInfo.FullyQualifiedName, StringComparison.Ordinal) &&
                fromInfo.SymbolId.DocCommentId == toInfo.SymbolId.DocCommentId)
            {
                var fromSimple = GetSimpleNameFromFqn(fromInfo.FullyQualifiedName);
                var toSimple = GetSimpleNameFromFqn(toInfo.FullyQualifiedName);
                var fromContainer = GetContainerFromFqn(fromInfo.FullyQualifiedName);
                var toContainer = GetContainerFromFqn(toInfo.FullyQualifiedName);

                if (fromSimple != toSimple)
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolRenamed, symbolId, new { before = fromInfo.FullyQualifiedName, after = toInfo.FullyQualifiedName }));

                if (fromContainer != toContainer)
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolMoved, symbolId, new { before = fromContainer, after = toContainer }));
            }

            var metaChanges = CompareMetadata(symbolId, fromInfo.MetadataJson, toInfo.MetadataJson, fromSnapshotId, toSnapshotId);
            changes.AddRange(metaChanges);

            var (sourceChanges, sourceSkipped) = CompareSource(symbolId, fromSnapshotId, toSnapshotId);
            changes.AddRange(sourceChanges);
            skippedComparisons += sourceSkipped;

            return (changes, skippedComparisons);
        }

        private void MatchTransitions(List<SemanticChange> changes, string fromSnapshotId, string toSnapshotId)
        {
            var removedIds = changes
                .Where(c => c.ChangeType == ChangeType.SymbolRemoved)
                .Select(c => c.SymbolId)
                .ToList();
            var addedIds = changes
                .Where(c => c.ChangeType == ChangeType.SymbolAdded)
                .Select(c => c.SymbolId)
                .ToList();

            if (removedIds.Count == 0 || addedIds.Count == 0)
                return;

            var removedCandidates = _readStore.LoadTransitionCandidates(fromSnapshotId, removedIds);
            var addedCandidates = _readStore.LoadTransitionCandidates(toSnapshotId, addedIds);

            var resolution = SymbolTransitionMatcher.MatchTransitions(removedCandidates, addedCandidates);

            foreach (var transition in resolution.Transitions)
            {
                var detail = new
                {
                    previous_symbol_id = transition.PreviousSymbolId,
                    current_symbol_id = transition.CurrentSymbolId,
                    previous_fqn = transition.PreviousFullyQualifiedName,
                    current_fqn = transition.CurrentFullyQualifiedName,
                    transition_kind = transition.Kind.ToString()
                };

                switch (transition.Kind)
                {
                    case SymbolTransitionKind.Rename:
                        changes.Add(MakeChange(fromSnapshotId, toSnapshotId,
                            ChangeType.SymbolRenamed, transition.CurrentSymbolId, detail));
                        break;
                    case SymbolTransitionKind.Move:
                        changes.Add(MakeChange(fromSnapshotId, toSnapshotId,
                            ChangeType.SymbolMoved, transition.CurrentSymbolId, detail));
                        break;
                    case SymbolTransitionKind.RenameAndMove:
                        changes.Add(MakeChange(fromSnapshotId, toSnapshotId,
                            ChangeType.SymbolRenamed, transition.CurrentSymbolId, detail));
                        changes.Add(MakeChange(fromSnapshotId, toSnapshotId,
                            ChangeType.SymbolMoved, transition.CurrentSymbolId, detail));
                        break;
                }
            }

            changes.RemoveAll(c =>
                (c.ChangeType == ChangeType.SymbolRemoved && resolution.ConsumedRemovedIds.Contains(c.SymbolId)) ||
                (c.ChangeType == ChangeType.SymbolAdded && resolution.ConsumedAddedIds.Contains(c.SymbolId)));
        }

        private void DiffEdges(List<EdgeRecord> fromEdges, List<EdgeRecord> toEdges, string fromSnapshotId, string toSnapshotId, List<SemanticChange> changes)
        {
            var fromEdgeSet = new HashSet<(string source, string target, string kind)>(fromEdges.Select(e => (e.SourceSymbolId, e.TargetSymbolId, e.Kind)));
            var toEdgeSet = new HashSet<(string source, string target, string kind)>(toEdges.Select(e => (e.SourceSymbolId, e.TargetSymbolId, e.Kind)));

            foreach (var edge in toEdges)
            {
                var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
                if (!fromEdgeSet.Contains(key))
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.EdgeAdded, edge.SourceSymbolId, new { source = edge.SourceSymbolId, target = edge.TargetSymbolId, kind = edge.Kind }));
                }
            }

            foreach (var edge in fromEdges)
            {
                var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
                if (!toEdgeSet.Contains(key))
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.EdgeRemoved, edge.SourceSymbolId, new { source = edge.SourceSymbolId, target = edge.TargetSymbolId, kind = edge.Kind }));
                }
            }
        }

        private List<string> GetSymbolIdsInSnapshot(string snapshotId)
        {
            return _snapshotStore.GetSymbolIdsInSnapshot(snapshotId);
        }

        private List<SemanticChange> CompareMetadata(string symbolId, string? fromJson, string? toJson, string fromSnapshotId, string toSnapshotId)
        {
            var changes = new List<SemanticChange>();

            var fromMeta = string.IsNullOrEmpty(fromJson)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fromJson) ?? [];

            var toMeta = string.IsNullOrEmpty(toJson)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toJson) ?? [];

            foreach (var entry in MetadataComparisons)
            {
                switch (entry.Kind)
                {
                    case MetadataComparisonKind.String:
                        CompareStringMetadata(entry.Key, entry.ChangeType);
                        break;
                    case MetadataComparisonKind.Array:
                        CompareArrayMetadata(entry.Key, entry.ChangeType);
                        break;
                    case MetadataComparisonKind.Scalar:
                        CompareScalarMetadata(entry.Key, entry.ChangeType);
                        break;
                }
            }

            return changes;

            void CompareStringMetadata(string key, string changeType)
            {
                var from = GetMetaString(fromMeta, key);
                var to = GetMetaString(toMeta, key);
                if (from != null && to != null && from != to)
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, changeType, symbolId, new { before = from, after = to }));
                }
            }

            void CompareArrayMetadata(string key, string changeType)
            {
                var from = GetMetaArray(fromMeta, key);
                var to = GetMetaArray(toMeta, key);
                if (from != null && to != null && !from.SequenceEqual(to))
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, changeType, symbolId, new { before = from, after = to }));
                }
            }

            void CompareScalarMetadata(string key, string changeType)
            {
                if (!fromMeta.TryGetValue(key, out var before) || !toMeta.TryGetValue(key, out var after) ||
                    before.GetRawText() == after.GetRawText())
                {
                    return;
                }

                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, changeType, symbolId,
                    new { field = key, before, after }));
            }
        }

        private (List<SemanticChange> Changes, int Skipped) CompareSource(string symbolId, string fromSnapshotId, string toSnapshotId)
        {
            var changes = new List<SemanticChange>();

            var fromSig = _readStore.GetSymbolSource(symbolId, fromSnapshotId, ViewKind.Signature);
            var toSig = _readStore.GetSymbolSource(symbolId, toSnapshotId, ViewKind.Signature);

            if (fromSig == null || toSig == null)
            {
                if (fromSig == null && toSig == null)
                    return (changes, 0);

                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.ComparisonUnavailable, symbolId,
                    new { reason = $"Source comparison unavailable: from_signature={(fromSig == null ? "missing" : "present")}, to_signature={(toSig == null ? "missing" : "present")}" }));
                return (changes, 1);
            }

            var fromBody = _readStore.GetSymbolSource(symbolId, fromSnapshotId, ViewKind.Body);
            var toBody = _readStore.GetSymbolSource(symbolId, toSnapshotId, ViewKind.Body);

            if (fromSig == toSig)
            {
                if (fromBody != toBody)
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.BodyOnlyChanged, symbolId, new { note = "signature unchanged, body differs" }));
                }
            }

            return (changes, 0);
        }

        private static string GetSimpleNameFromFqn(string? fqn)
        {
            if (string.IsNullOrEmpty(fqn)) return string.Empty;
            var idx = fqn.LastIndexOf('.');
            return idx < 0 ? fqn : fqn.Substring(idx + 1);
        }

        private static string GetContainerFromFqn(string? fqn)
        {
            if (string.IsNullOrEmpty(fqn)) return string.Empty;
            var idx = fqn.LastIndexOf('.');
            return idx < 0 ? string.Empty : fqn.Substring(0, idx);
        }

        private static string? GetMetaString(Dictionary<string, JsonElement> meta, string key)
        {
            if (meta.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
            return null;
        }

        private static List<string>? GetMetaArray(Dictionary<string, JsonElement> meta, string key)
        {
            if (meta.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                return el.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
            return null;
        }

        private static SemanticChange MakeChange(string? fromSnapshotId, string? toSnapshotId, string changeType, string symbolId, object? detail)
        {
            return new SemanticChange
            {
                ChangeId = Guid.NewGuid().ToString("N"),
                FromSnapshotId = fromSnapshotId ?? string.Empty,
                ToSnapshotId = toSnapshotId ?? string.Empty,
                ChangeType = changeType,
                SymbolId = symbolId,
                DetailJson = detail != null ? JsonSerializer.Serialize(detail) : null,
                CreatedAtUtc = DateTime.UtcNow
            };
        }
    }
}
