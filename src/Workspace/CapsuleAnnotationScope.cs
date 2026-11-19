namespace Lurp.Workspace;

internal sealed class CapsuleAnnotationScope
{
    private const string MediatRPatternKind = "unmodeled_mediatr_pattern";

    private readonly IReadOnlyCollection<string> _containingTypes;
    private readonly bool _hasEffectiveHandles;
    private readonly CancellationToken _cancellationToken;
    private readonly List<AnnotationRecord> _constraints = [];
    private readonly List<AnnotationRecord> _patterns = [];

    internal CapsuleAnnotationScope(IReadOnlyCollection<string> containingTypes, bool hasEffectiveHandles,
        IEnumerable<AnnotationRecord> annotations, CancellationToken cancellationToken = default)
    {
        _containingTypes = containingTypes;
        _hasEffectiveHandles = hasEffectiveHandles;
        _cancellationToken = cancellationToken;
        foreach (var annotation in annotations)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (annotation.Kind == MediatRPatternKind)
                _patterns.Add(annotation);
            else if (IsConstraintKind(annotation.Kind))
                _constraints.Add(annotation);
        }
    }

    internal static CapsuleAnnotationScope Read(ContextTierContext context)
    {
        context.ThrowIfCancellationRequested();
        var rows = context.EdgeStore.GetAnnotations(context.SnapshotId);
        if (rows.Count == 0)
            return new CapsuleAnnotationScope([], false, []);

        var hasEffectiveHandles = context.EffectiveSymbolIds.Any(id =>
            context.GetIncomingEdges(id).Any(IsHandles) || context.GetOutgoingEdges(id).Any(IsHandles));
        return new CapsuleAnnotationScope(FindContainingTypes(context), hasEffectiveHandles, rows, context.CancellationToken);
    }

    internal void AddConstraints(ContextCapsule capsule)
    {
        if (_constraints.Count == 0)
            return;
        var scope = EmittedSymbols(capsule, out _);
        foreach (var annotation in _constraints)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (scope.Contains(annotation.SymbolId))
                capsule.Constraints.Add(new CapsuleConstraint(annotation.Value, "annotation", annotation.Kind, annotation.SymbolId));
        }
    }

    internal void RetainConstraints(ContextCapsule capsule)
    {
        if (capsule.Constraints.Count == 0)
            return;
        var scope = EmittedSymbols(capsule, out _);
        capsule.Constraints.RemoveAll(constraint => constraint.SymbolId != null && !scope.Contains(constraint.SymbolId));
    }

    internal IEnumerable<AnnotationRecord> MediatRPatterns(ContextCapsule capsule)
    {
        if (_patterns.Count == 0)
            return [];
        var scope = EmittedSymbols(capsule, out var hasEmittedHandles);
        var hasHandles = _hasEffectiveHandles || hasEmittedHandles;
        return _patterns.Where(annotation => scope.Contains(annotation.SymbolId) ||
            (hasHandles && IsWrapper(annotation.Value)));
    }

    private HashSet<string> EmittedSymbols(ContextCapsule capsule, out bool hasHandles)
    {
        var ids = new HashSet<string>(_containingTypes, StringComparer.Ordinal) { capsule.Anchor.SymbolId };
        hasHandles = false;
        foreach (var item in ContextAssembler.EnumerateTierItems(capsule).Concat(capsule.AffectedPublicSurfaces))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ids.Add(item.SymbolId);
            hasHandles |= item.EdgeKind == nameof(EdgeKind.Handles);
        }

        foreach (var path in capsule.IncomingPaths.Concat(capsule.OutgoingPaths))
            foreach (var hop in path.Hops)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ids.Add(hop.SourceSymbolId);
                ids.Add(hop.TargetSymbolId);
            }
        return ids;
    }

    private static HashSet<string> FindContainingTypes(ContextTierContext context)
    {
        var anchor = context.SymbolId.Value;
        var visited = new HashSet<string>(StringComparer.Ordinal) { anchor };
        var queue = new Queue<string>();
        queue.Enqueue(anchor);
        while (queue.TryDequeue(out var id))
        {
            var kind = SymbolId.DocCommentIdOf(id) is ['T', ':', ..]
                ? nameof(EdgeKind.Contains)
                : nameof(EdgeKind.Declares);
            foreach (var edge in context.GetIncomingEdges(id))
            {
                context.ThrowIfCancellationRequested();
                if (edge.Kind == kind && SymbolId.DocCommentIdOf(edge.SourceSymbolId) is ['T', ':', ..] &&
                    visited.Add(edge.SourceSymbolId))
                    queue.Enqueue(edge.SourceSymbolId);
            }
        }
        visited.Remove(anchor);
        return visited;
    }

    private static bool IsHandles(EdgeRecord edge) => edge.Kind == nameof(EdgeKind.Handles);

    private static bool IsWrapper(string pattern) =>
        pattern is "IPipelineBehavior" or "IRequestPreProcessor" or "IRequestPostProcessor" or "IRequestExceptionHandler";

    private static bool IsConstraintKind(string kind) =>
        kind.Equals("constraint", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("invariant", StringComparison.OrdinalIgnoreCase) ||
        kind.EndsWith("_constraint", StringComparison.OrdinalIgnoreCase) ||
        kind.EndsWith("_invariant", StringComparison.OrdinalIgnoreCase);
}
