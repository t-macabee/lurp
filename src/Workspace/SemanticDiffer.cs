using Lurp.Storage;
using System.Text.Json;

#if CODE_ANALYSIS
using System.Diagnostics.CodeAnalysis;
#endif

namespace Lurp.Workspace
{

#if CODE_ANALYSIS
    [SuppressMessage("NDepend", "ND1000", Justification = "Full/scoped semantic diff engine: symbol/edge diffing, rename detection, and the contract-driven metadata comparison table (task 10). Task 7 already consolidated the two ComputeDiff overloads into one internal path, removing ~70 duplicated lines; remaining size reflects the diff domain (rename heuristics, 17-key metadata table, edge diff), not duplication. Review trigger: re-split if a new diff concern (e.g. a second comparison table) is added without a natural seam, or if size grows materially beyond this remediation's baseline.")]
#endif
    public class SemanticDiffer
    {
        private enum MetadataComparisonKind { String, Array, Scalar }

        private readonly record struct MetadataComparisonEntry(
            string Key,
            string ChangeType,
            MetadataComparisonKind Kind);

        private static readonly MetadataComparisonEntry[] MetadataComparisons =
        [
            new("accessibility", ChangeType.AccessibilityChanged, MetadataComparisonKind.String),
            new("signature", ChangeType.SignatureChanged, MetadataComparisonKind.String),
            new("base_type", ChangeType.BaseTypeChanged, MetadataComparisonKind.String),
            new("interfaces", ChangeType.InterfacesChanged, MetadataComparisonKind.Array),
            new("isRecord", ChangeType.RecordChanged, MetadataComparisonKind.Scalar),
            new("typeKind", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isAbstract", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isVirtual", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isOverride", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isStatic", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isAsync", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isExtensionMethod", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isReadOnly", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isWriteOnly", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isConst", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("isVolatile", ChangeType.MetadataChanged, MetadataComparisonKind.Scalar),
            new("attributes", ChangeType.AttributeChanged, MetadataComparisonKind.Array)
        ];

        // Intentionally excluded from comparison (captured by signature):
        // - returnType: included in signature for methods, properties, events
        // - arity: generic type parameter count included in signature
        private readonly ISnapshotStore _snapshotStore;
        private readonly IDeclarationStore _declarationStore;
        private readonly IEdgeStore _edgeStore;

        public SemanticDiffer(ISnapshotStore snapshotStore, IDeclarationStore declarationStore, IEdgeStore edgeStore)
        {
            _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
            _declarationStore = declarationStore ?? throw new ArgumentNullException(nameof(declarationStore));
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

            DetectRenames(changes, fromSnapshotId, toSnapshotId);

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

            var fromInfo = _declarationStore.GetSymbolInfo(symbolId, fromSnapshotId);
            var toInfo = _declarationStore.GetSymbolInfo(symbolId, toSnapshotId);

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
                var fromSimple = GetSimpleName(fromInfo.FullyQualifiedName);
                var toSimple = GetSimpleName(toInfo.FullyQualifiedName);
                var fromContainer = GetContainer(fromInfo.FullyQualifiedName);
                var toContainer = GetContainer(toInfo.FullyQualifiedName);

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

        private void DetectRenames(List<SemanticChange> changes, string fromSnapshotId, string toSnapshotId)
        {
            var removed = changes.Where(c => c.ChangeType == ChangeType.SymbolRemoved).ToList();
            var added = changes.Where(c => c.ChangeType == ChangeType.SymbolAdded).ToList();

            if (removed.Count == 0 || added.Count == 0)
                return;

            var removedBySimpleName = new Dictionary<string, List<(SemanticChange Change, IndexedSymbolInfo? Info)>>(StringComparer.Ordinal);
            foreach (var change in removed)
            {
                var info = _declarationStore.GetSymbolInfo(change.SymbolId, fromSnapshotId);
                var simpleName = GetSimpleName(info?.FullyQualifiedName);
                if (string.IsNullOrEmpty(simpleName))
                    continue;

                if (!removedBySimpleName.TryGetValue(simpleName, out var list))
                {
                    list = [];
                    removedBySimpleName[simpleName] = list;
                }
                list.Add((change, info));
            }

            var addedBySimpleName = new Dictionary<string, List<(SemanticChange Change, IndexedSymbolInfo? Info)>>(StringComparer.Ordinal);
            foreach (var change in added)
            {
                var info = _declarationStore.GetSymbolInfo(change.SymbolId, toSnapshotId);
                var simpleName = GetSimpleName(info?.FullyQualifiedName);
                if (string.IsNullOrEmpty(simpleName))
                    continue;

                if (!addedBySimpleName.TryGetValue(simpleName, out var list))
                {
                    list = [];
                    addedBySimpleName[simpleName] = list;
                }
                list.Add((change, info));
            }

            var matchedRemoved = new HashSet<string>();
            var matchedAdded = new HashSet<string>();

            foreach (var (simpleName, removedList) in removedBySimpleName)
            {
                if (!addedBySimpleName.TryGetValue(simpleName, out var addedList))
                    continue;

                foreach (var removedEntry in removedList)
                {
                    if (removedEntry.Info == null || matchedRemoved.Contains(removedEntry.Change.SymbolId))
                        continue;

                    foreach (var addedEntry in addedList)
                    {
                        if (addedEntry.Info == null || matchedAdded.Contains(addedEntry.Change.SymbolId))
                            continue;

                        if (removedEntry.Info.Kind == addedEntry.Info.Kind)
                        {
                            changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolRenamed,
                                removedEntry.Change.SymbolId,
                                new { before = removedEntry.Info.FullyQualifiedName, after = addedEntry.Info.FullyQualifiedName }));
                            matchedRemoved.Add(removedEntry.Change.SymbolId);
                            matchedAdded.Add(addedEntry.Change.SymbolId);
                            break;
                        }
                    }
                }
            }

            changes.RemoveAll(c =>
                (c.ChangeType == ChangeType.SymbolRemoved && matchedRemoved.Contains(c.SymbolId)) ||
                (c.ChangeType == ChangeType.SymbolAdded && matchedAdded.Contains(c.SymbolId)));
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

            var fromSig = _declarationStore.GetSymbolSource(symbolId, fromSnapshotId, ViewKind.Signature);
            var toSig = _declarationStore.GetSymbolSource(symbolId, toSnapshotId, ViewKind.Signature);

            if (fromSig == null || toSig == null)
            {
                if (fromSig == null && toSig == null)
                    return (changes, 0);

                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.ComparisonUnavailable, symbolId,
                    new { reason = $"Source comparison unavailable: from_signature={(fromSig == null ? "missing" : "present")}, to_signature={(toSig == null ? "missing" : "present")}" }));
                return (changes, 1);
            }

            var fromBody = _declarationStore.GetSymbolSource(symbolId, fromSnapshotId, ViewKind.Body);
            var toBody = _declarationStore.GetSymbolSource(symbolId, toSnapshotId, ViewKind.Body);

            if (fromSig == toSig)
            {
                if (fromBody != toBody)
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.BodyOnlyChanged, symbolId, new { note = "signature unchanged, body differs" }));
                }
            }

            return (changes, 0);
        }

        private static string GetSimpleName(string? fqn)
        {
            if (string.IsNullOrEmpty(fqn)) return string.Empty;
            var idx = fqn.LastIndexOf('.');
            return idx < 0 ? fqn : fqn.Substring(idx + 1);
        }

        private static string GetContainer(string? fqn)
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
