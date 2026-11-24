using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Parity.Shared;

/// <summary>
///     Oracle B compares Roslyn callers with the extracted edges. It uses SymbolFinder
///     callers for methods and semantic event assignments for event accessors.
///     It follows dispatch edges to accept callers through an interface or base member.
///     It uses the resource type to resolve disposal at a using site. It sets aside
///     dynamic calls and callers that bind outside the solution.
/// </summary>
public static class SymbolFinderOracle
{
    /// <summary>
    ///     The target method kinds the extractor models: ordinary methods,
    ///     constructors, user-defined operators, conversions, explicit interface
    ///     implementations, and property and event accessors.
    /// </summary>
    public static IReadOnlyList<MethodKind> TargetKinds { get; } =
    [
        MethodKind.Ordinary,
        MethodKind.ExplicitInterfaceImplementation,
        MethodKind.Constructor,
        MethodKind.UserDefinedOperator,
        MethodKind.Conversion,
        MethodKind.PropertyGet,
        MethodKind.PropertySet,
        MethodKind.EventAdd,
        MethodKind.EventRemove
    ];

    public static bool IsTarget(IMethodSymbol method) =>
        TargetKinds.Contains(method.MethodKind) &&
        (!method.IsImplicitlyDeclared ||
         (IsEventAccessor(method) && method.AssociatedSymbol is IEventSymbol { DeclaringSyntaxReferences.Length: > 0 }));

    private static bool IsEventAccessor(IMethodSymbol method) =>
        method.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove;

    /// <summary>A caller set aside because the runtime selects the target.</summary>
    public const string DynamicBindingReason = "dynamic_binding";

    /// <summary>
    ///     A caller set aside because its called symbol is outside the solution.
    ///     The extractor deletes edges to external symbols
    ///     (<c>filtered_external</c>), so no Calls/MayDispatchTo edge can exist and
    ///     the caller is not a miss.
    /// </summary>
    public const string BindsExternalReason = "binds_external";

    /// <summary>
    ///     A caller recorded because its called symbol is an in-solution symbol that
    ///     is neither the target nor a member of the target's dispatch family. It
    ///     is still checked: without a persisted edge that matches it, it stays a
    ///     miss, so a missing MayDispatchTo edge is not hidden.
    /// </summary>
    public const string BindsOtherReason = "binds_other";

    /// <summary>
    ///     Runs Oracle B over <paramref name="solution" />. <paramref name="edges" />
    ///     is the extracted edge set as (source node id, edge kind, target node id);
    ///     <paramref name="resolveNodeId" /> maps a Roslyn symbol to the node id the
    ///     extractor persists for it, or null when it has none. When
    ///     <paramref name="targetIds" /> is not null, only targets whose resolved id
    ///     is in that set are evaluated; every other target is skipped, so a caller
    ///     checks a sample without paying for the whole solution. The skipped
    ///     targets are still enumerated and counted as before.
    /// </summary>
    public static async Task<SymbolFinderOracleResult> CompareAsync(
        Solution solution,
        IEnumerable<(string Source, string Kind, string Target)> edges,
        Func<ISymbol, string?> resolveNodeId,
        IReadOnlySet<string>? targetIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(resolveNodeId);

        var edgeSet = edges as HashSet<(string Source, string Kind, string Target)>
                      ?? new HashSet<(string Source, string Kind, string Target)>(edges);

        // Roslyn reports a caller of an interface/abstract member also as a caller
        // of its implementation/override, and the reverse. Lurp models the direct
        // call as Calls to the bound member plus a separate MayDispatchTo edge
        // between the interface or root virtual member and its implementation or
        // override. The adjacency map makes the oracle accept a caller whose Calls
        // edge lands on any member of the target's dispatch family.
        var dispatchAdjacency = BuildDispatchAdjacency(edgeSet);

        var targets = new List<Target>();
        var setAsideCallers = new List<SymbolFinderSetAsideCaller>();
        var skippedImplicit = 0;
        var skippedNoId = 0;

        var compilations = new List<Compilation>();
        var compilationByTree = new Dictionary<SyntaxTree, Compilation>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                continue;

            compilations.Add(compilation);
            foreach (var tree in compilation.SyntaxTrees)
                compilationByTree[tree] = compilation;
        }

        foreach (var compilation in compilations)
        {
            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
            {
                foreach (var member in type.GetMembers())
                {
                    if (member is not IMethodSymbol method)
                        continue;
                    if (!IsTarget(method))
                    {
                        if (method.IsImplicitlyDeclared)
                            skippedImplicit++;
                        continue;
                    }

                    if (resolveNodeId(member) is not { Length: > 0 } id)
                    {
                        skippedNoId++;
                        continue;
                    }

                    targets.Add(new Target(
                        id,
                        method,
                        method.AssociatedSymbol is { } owner ? resolveNodeId(owner) : null));
                }
            }
        }

        var checkedPairs = 0;
        var matchedPairs = 0;
        var symbolFinderPairs = new HashSet<(string Caller, string Target)>();
        var evaluated = new Dictionary<(string Caller, string Target), bool>();
        var targetCounts = new Dictionary<MethodKind, int>();
        var missCounts = new Dictionary<MethodKind, int>();
        var targetResults = new List<SymbolFinderTargetResult>(targets.Count);

        foreach (var target in targets)
        {
            if (targetIds is not null && !targetIds.Contains(target.Id))
                continue;

            targetCounts[target.Symbol.MethodKind] = targetCounts.GetValueOrDefault(target.Symbol.MethodKind) + 1;
            var family = DispatchFamily(target.Id, dispatchAdjacency);
            var ownerFamily = target.OwnerId is null ? null : DispatchFamily(target.OwnerId, dispatchAdjacency);
            var expectedCallers = new HashSet<string>(StringComparer.Ordinal);
            var missingCallers = new List<SymbolFinderMissingCaller>();

            var relations = AcceptedRelations(target.Symbol.MethodKind);
            var relationNames = relations.Select(static relation => relation.Kind).ToArray();
            if (IsEventAccessor(target.Symbol))
            {
                var callers = await FindEventCallersAsync(target.Symbol, solution, compilationByTree, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var (owner, locations) in callers)
                    CheckCaller(owner, locations, reason: null);
            }
            else
            {
                var callers = await SymbolFinder.FindCallersAsync(target.Symbol, solution, cancellationToken).ConfigureAwait(false);
                foreach (var caller in callers)
                {
                    // SymbolFinder can group a static call and a dynamic call under the same caller.
                    // Classify each site so a dynamic site cannot hide a missing edge at a static site.
                    var sites = caller.Locations.GroupBy(location =>
                        ClassifyCaller(caller.CalledSymbol, location, family, compilationByTree, resolveNodeId, cancellationToken));
                    foreach (var site in sites)
                        CheckCaller(caller.CallingSymbol, site, site.Key);
                }
            }

            targetResults.Add(new SymbolFinderTargetResult(
                target.Id,
                target.Symbol.MethodKind,
                [.. expectedCallers.OrderBy(static id => id, StringComparer.Ordinal)],
                missingCallers));

            void CheckCaller(ISymbol caller, IEnumerable<Location> locations, string? reason)
            {
                var candidates = CallerOwnerIds(caller, resolveNodeId).Distinct(StringComparer.Ordinal).ToList();
                var lines = locations.Select(location => location.GetLineSpan().StartLinePosition.Line + 1).ToArray();
                if (reason is not null)
                {
                    setAsideCallers.Add(new SymbolFinderSetAsideCaller(
                        target.Id, target.Symbol.MethodKind, candidates, reason, lines));
                    if (reason is BindsExternalReason or DynamicBindingReason)
                        return;
                }

                var matched = false;
                foreach (var callerId in candidates)
                {
                    expectedCallers.Add(callerId);
                    var key = (callerId, target.Id);
                    symbolFinderPairs.Add(key);

                    if (evaluated.TryGetValue(key, out var alreadyMatched))
                    {
                        if (alreadyMatched)
                        {
                            matched = true;
                            break;
                        }

                        continue;
                    }

                    checkedPairs++;
                    var isMatch = Matches(edgeSet, callerId, ownerFamily, family, relations);
                    evaluated[key] = isMatch;
                    if (!isMatch)
                        continue;

                    matchedPairs++;
                    matched = true;
                    break;
                }

                if (matched)
                    return;

                missingCallers.Add(new SymbolFinderMissingCaller(
                    target.Id,
                    target.Symbol.MethodKind,
                    candidates,
                    relationNames,
                    lines));
                missCounts[target.Symbol.MethodKind] = missCounts.GetValueOrDefault(target.Symbol.MethodKind) + 1;
            }
        }

        // Fix (5): precision is the share of extracted Calls/MethodGroupRef edges
        // to checked targets that SymbolFinder did report as callers; the rest are
        // reported so an oracle/extractor divergence is visible. Not asserted here.
        var checkedTargetIds = targetResults.Select(static target => target.TargetId).ToHashSet(StringComparer.Ordinal);
        var extras = edgeSet
            .Where(edge => (edge.Kind == nameof(EdgeKind.Calls) || edge.Kind == nameof(EdgeKind.MethodGroupRef)) &&
                           checkedTargetIds.Contains(edge.Target) &&
                           !symbolFinderPairs.Contains((edge.Source, edge.Target)))
            .OrderBy(static edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(static edge => edge.Target, StringComparer.Ordinal)
            .ThenBy(static edge => edge.Kind, StringComparer.Ordinal)
            .Select(static edge => new SymbolFinderEdge(edge.Source, edge.Kind, edge.Target))
            .ToList();
        var precision = matchedPairs + extras.Count == 0
            ? 1.0
            : (double)matchedPairs / (matchedPairs + extras.Count);

        return new SymbolFinderOracleResult(
            targetResults,
            extras,
            new SymbolFinderOracleSummary(
                checkedPairs,
                matchedPairs,
                skippedImplicit,
                skippedNoId,
                targetCounts,
                missCounts,
                precision,
                setAsideCallers));
    }

    private static bool Matches(
        HashSet<(string Source, string Kind, string Target)> edges,
        string callerId,
        HashSet<string>? ownerFamily,
        HashSet<string> family,
        Relation[] relations)
    {
        foreach (var relation in relations)
        {
            var targets = relation.OnOwner ? ownerFamily : family;
            if (targets is not null && targets.Any(id => edges.Contains((callerId, relation.Kind, id))))
                return true;
        }

        return false;
    }

    private static readonly Relation[] MethodRelations =
        [new(nameof(EdgeKind.Calls), OnOwner: false), new(nameof(EdgeKind.MethodGroupRef), OnOwner: false)];

    private static readonly Relation[] ReadRelations = [.. MethodRelations, new(nameof(EdgeKind.Reads), OnOwner: true)];
    private static readonly Relation[] WriteRelations = [.. MethodRelations, new(nameof(EdgeKind.Writes), OnOwner: true)];

    private static Relation[] AcceptedRelations(MethodKind kind) => kind switch
    {
        MethodKind.PropertyGet => ReadRelations,
        MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove => WriteRelations,
        _ => MethodRelations
    };

    private readonly record struct Relation(string Kind, bool OnOwner);

    /// <summary>
    ///     The undirected adjacency map over <see cref="EdgeKind.MayDispatchTo" />
    ///     edges: each node maps to the set of nodes linked to it in either
    ///     direction. Ids compare with <see cref="StringComparer.Ordinal" />.
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildDispatchAdjacency(
        IEnumerable<(string Source, string Kind, string Target)> edges)
    {
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (edge.Kind != nameof(EdgeKind.MayDispatchTo))
                continue;

            AddNeighbour(adjacency, edge.Source, edge.Target);
            AddNeighbour(adjacency, edge.Target, edge.Source);
        }

        return adjacency;
    }

    private static void AddNeighbour(Dictionary<string, HashSet<string>> adjacency, string from, string to)
    {
        if (!adjacency.TryGetValue(from, out var neighbours))
        {
            neighbours = new HashSet<string>(StringComparer.Ordinal);
            adjacency[from] = neighbours;
        }

        neighbours.Add(to);
    }

    /// <summary>
    ///     The dispatch family of <paramref name="targetId" />: the id itself plus
    ///     every id reachable through the <see cref="EdgeKind.MayDispatchTo" />
    ///     adjacency map, computed as an iterative transitive closure so a cycle
    ///     cannot loop.
    /// </summary>
    private static HashSet<string> DispatchFamily(
        string targetId, Dictionary<string, HashSet<string>> dispatchAdjacency)
    {
        var family = new HashSet<string>(StringComparer.Ordinal) { targetId };
        var pending = new Stack<string>();
        pending.Push(targetId);
        while (pending.Count > 0)
        {
            if (!dispatchAdjacency.TryGetValue(pending.Pop(), out var neighbours))
                continue;

            foreach (var neighbour in neighbours)
            {
                if (family.Add(neighbour))
                    pending.Push(neighbour);
            }
        }

        return family;
    }

    private static async Task<Dictionary<ISymbol, List<Location>>> FindEventCallersAsync(
        IMethodSymbol accessor,
        Solution solution,
        Dictionary<SyntaxTree, Compilation> compilationByTree,
        CancellationToken cancellationToken)
    {
        var eventSymbol = (IEventSymbol)accessor.AssociatedSymbol!;
        var references = await SymbolFinder.FindReferencesAsync(eventSymbol, solution, cancellationToken).ConfigureAwait(false);
        var callers = new Dictionary<ISymbol, List<Location>>(SymbolEqualityComparer.Default);
        var adds = accessor.MethodKind == MethodKind.EventAdd;

        foreach (var reference in references.SelectMany(static item => item.Locations))
        {
            var location = reference.Location;
            if (location.SourceTree is not { } tree || !compilationByTree.TryGetValue(tree, out var compilation))
                continue;

            var model = compilation.GetSemanticModel(tree);
            var node = tree.GetRoot(cancellationToken).FindNode(location.SourceSpan, getInnermostNodeForTie: true);
            var assignment = node.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
            if (assignment is null ||
                model.GetOperation(assignment, cancellationToken) is not IEventAssignmentOperation assignmentOp ||
                assignmentOp.Adds != adds ||
                assignmentOp.EventReference is not IEventReferenceOperation eventRef ||
                !eventRef.Syntax.Span.Contains(location.SourceSpan) ||
                !SameEvent(eventSymbol, eventRef.Event))
                continue;

            var owner = model.GetEnclosingSymbol(location.SourceSpan.Start, cancellationToken);
            while (owner is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
                owner = owner.ContainingSymbol;
            if (owner is null)
                continue;

            if (!callers.TryGetValue(owner, out var locations))
                callers[owner] = locations = [];
            locations.Add(location);
        }

        return callers;

        static bool SameEvent(IEventSymbol expected, IEventSymbol actual)
        {
            expected = expected.OriginalDefinition;
            actual = actual.OriginalDefinition;
            // A reference from a different project can represent the same event as metadata.
            return SymbolEqualityComparer.Default.Equals(expected, actual) ||
                   (expected.ContainingAssembly.Identity.Equals(actual.ContainingAssembly.Identity) &&
                    expected.GetDocumentationCommentId() is { } id && id == actual.GetDocumentationCommentId());
        }
    }

    private static string? ClassifyCaller(
        ISymbol called,
        Location location,
        HashSet<string> family,
        Dictionary<SyntaxTree, Compilation> compilationByTree,
        Func<ISymbol, string?> resolveNodeId,
        CancellationToken cancellationToken)
    {
        if (location.SourceTree is { } tree && compilationByTree.TryGetValue(tree, out var compilation))
        {
            var root = tree.GetRoot(cancellationToken);
            var token = root.FindToken(location.SourceSpan.Start);
            var model = compilation.GetSemanticModel(tree);
            if (token.IsKind(SyntaxKind.UsingKeyword) && token.Span == location.SourceSpan)
            {
                var disposal = model.GetOperation(token.Parent!, cancellationToken);
                var targets = disposal switch
                {
                    IUsingOperation operation => DisposeTargets(operation.Resources, operation.IsAsynchronous),
                    IUsingDeclarationOperation operation => DisposeTargets(operation.DeclarationGroup, operation.IsAsynchronous),
                    _ => null
                };
                if (targets is not null)
                {
                    var inSolution = false;
                    foreach (var target in targets)
                    {
                        if (FamilyContains(target))
                            return null;
                        inSolution |= IsInSolution(target, compilationByTree);
                    }

                    return inSolution ? BindsOtherReason : BindsExternalReason;
                }

                IEnumerable<IMethodSymbol> DisposeTargets(IOperation resources, bool isAsync) =>
                    ResourceTypes(resources)
                        .Select(type => DisposeTarget(type, isAsync, compilation, called as IMethodSymbol))
                        .OfType<IMethodSymbol>();
            }

            var node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);
            foreach (var ancestor in node.AncestorsAndSelf())
            {
                // A site inside an operand of the dynamic operation binds statically.
                if (model.GetOperation(ancestor, cancellationToken) is
                    (IDynamicInvocationOperation or IDynamicObjectCreationOperation or
                     IDynamicIndexerAccessOperation or IDynamicMemberReferenceOperation) and var dynamicOperation &&
                    !dynamicOperation.ChildOperations.Any(child => child.Syntax.Span.Contains(location.SourceSpan)))
                    return DynamicBindingReason;
                if (ancestor is StatementSyntax)
                    break;
            }
        }

        if (FamilyContains(called))
            return null;
        return IsInSolution(called, compilationByTree) ? BindsOtherReason : BindsExternalReason;

        bool FamilyContains(ISymbol symbol) => resolveNodeId(symbol) is { Length: > 0 } id && family.Contains(id);
    }

    private static IEnumerable<ITypeSymbol> ResourceTypes(IOperation resources)
    {
        if (resources is IVariableDeclarationGroupOperation group)
        {
            foreach (var declaration in group.Declarations)
            foreach (var declarator in declaration.Declarators)
                yield return declarator.Symbol.Type;
        }
        else if (resources.Type is { } type)
        {
            yield return type;
        }
    }

    private static IMethodSymbol? DisposeTarget(ITypeSymbol type, bool isAsync, Compilation compilation, IMethodSymbol? called)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        if (type.IsRefLikeType)
            return called;

        var interfaceType = isAsync
            ? compilation.GetTypeByMetadataName("System.IAsyncDisposable")
            : compilation.GetSpecialType(SpecialType.System_IDisposable);
        var member = interfaceType?.GetMembers(isAsync ? "DisposeAsync" : "Dispose").OfType<IMethodSymbol>().FirstOrDefault();
        if (member is null)
            return null;

        var implementingType = type switch
        {
            ITypeParameterSymbol parameter => EffectiveBaseClass(parameter),
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => null,
            INamedTypeSymbol named => named,
            _ => null
        };

        return implementingType?.FindImplementationForInterfaceMember(member) as IMethodSymbol ?? member;
    }

    private static INamedTypeSymbol? EffectiveBaseClass(ITypeParameterSymbol parameter)
    {
        var seen = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeParameterSymbol>([parameter]);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current))
                continue;

            foreach (var constraint in current.ConstraintTypes)
            {
                if (constraint is INamedTypeSymbol { TypeKind: TypeKind.Class } named)
                    return named;
                if (constraint is ITypeParameterSymbol next)
                    pending.Push(next);
            }
        }

        return null;
    }

    /// <summary>
    ///     True when the symbol is declared in a source tree the solution's
    ///     compilations carry. A metadata view of a referenced project is not in
    ///     the set, so classification never depends on assembly object identity.
    /// </summary>
    private static bool IsInSolution(
        ISymbol symbol,
        Dictionary<SyntaxTree, Compilation> compilationByTree)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource && location.SourceTree is { } tree && compilationByTree.ContainsKey(tree))
                return true;
        }

        return false;
    }

    private sealed record Target(string Id, IMethodSymbol Symbol, string? OwnerId);

    private static IEnumerable<string> CallerOwnerIds(ISymbol caller, Func<ISymbol, string?> resolveNodeId)
    {
        if (resolveNodeId(caller) is { Length: > 0 } direct)
            yield return direct;

        switch (caller)
        {
            case IFieldSymbol { AssociatedSymbol: { } associated }:
                if (resolveNodeId(associated) is { Length: > 0 } propertyId)
                    yield return propertyId;
                break;
            case IPropertySymbol property:
                if (property.GetMethod != null && resolveNodeId(property.GetMethod) is { Length: > 0 } getterId)
                    yield return getterId;
                if (property.SetMethod != null && resolveNodeId(property.SetMethod) is { Length: > 0 } setterId)
                    yield return setterId;
                break;
            // D8: an event accessor body is owned by the accessor (S01-F12).
            case IEventSymbol eventSymbol:
                if (eventSymbol.AddMethod != null && resolveNodeId(eventSymbol.AddMethod) is { Length: > 0 } adderId)
                    yield return adderId;
                if (eventSymbol.RemoveMethod != null && resolveNodeId(eventSymbol.RemoveMethod) is { Length: > 0 } removerId)
                    yield return removerId;
                break;
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction, ContainingSymbol: { } container }:
                if (resolveNodeId(container) is { Length: > 0 } containerId)
                    yield return containerId;
                break;
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in Descendants(type))
                yield return nested;
        }

        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in AllTypes(child))
                yield return type;

        static IEnumerable<INamedTypeSymbol> Descendants(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in Descendants(nested))
                    yield return deeper;
            }
        }
    }
}

/// <summary>Per-target Oracle B result: the expected callers and the unmatched ones.</summary>
public sealed record SymbolFinderTargetResult(
    string TargetId,
    MethodKind TargetKind,
    IReadOnlyList<string> ExpectedCallers,
    IReadOnlyList<SymbolFinderMissingCaller> MissingCallers);

/// <summary>
///     A Roslyn caller Oracle B did not find as an edge: the candidate owner ids the
///     caller normalizes to, the accepted edge kinds none of which matched, and the
///     caller's source lines.
/// </summary>
public sealed record SymbolFinderMissingCaller(
    string TargetId,
    MethodKind TargetKind,
    IReadOnlyList<string> CallerIds,
    IReadOnlyList<string> MissingEdgeKinds,
    IReadOnlyList<int> Lines);

/// <summary>An extracted edge: <see cref="Source" /> -[<see cref="Kind" />]-&gt; <see cref="Target" />.</summary>
public sealed record SymbolFinderEdge(string Source, string Kind, string Target);

/// <summary>
///     A caller Roslyn reported for a target whose called symbol is not the
///     target or a member of its dispatch family: the candidate owner ids the
///     caller normalizes to, the reason it was set aside
///     and its source lines. The reason describes an external target, a dynamic
///     binding, or an in-solution target outside the dispatch family.
/// </summary>
public sealed record SymbolFinderSetAsideCaller(
    string TargetId,
    MethodKind TargetKind,
    IReadOnlyList<string> CallerIds,
    string Reason,
    IReadOnlyList<int> Lines);

/// <summary>Aggregate Oracle B counts, skips, and precision.</summary>
public sealed record SymbolFinderOracleSummary(
    int CheckedPairs,
    int MatchedPairs,
    int SkippedImplicit,
    int SkippedNoId,
    IReadOnlyDictionary<MethodKind, int> TargetCounts,
    IReadOnlyDictionary<MethodKind, int> MissCounts,
    double Precision,
    IReadOnlyList<SymbolFinderSetAsideCaller> SetAsideCallers);

/// <summary>The structured Oracle B result over one <see cref="Solution" />.</summary>
public sealed record SymbolFinderOracleResult(
    IReadOnlyList<SymbolFinderTargetResult> Targets,
    IReadOnlyList<SymbolFinderEdge> Extras,
    SymbolFinderOracleSummary Summary);
