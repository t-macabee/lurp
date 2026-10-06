using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Parity.Shared;

/// <summary>
///     Audit B1 Oracle B, shared: Roslyn's own <see cref="SymbolFinder.FindCallersAsync" />
///     is the caller oracle. For a <see cref="Solution"/>, the extracted edge set
///     and a symbol-to-node-id resolver, it enumerates every method-like member the
///     extractor models, asks Roslyn for its callers, normalizes each caller to the
///     extraction owner, filters the excluded/boundary kinds, and compares the result
///     with the edge set. The result carries the expected and missing callers per
///     target, the accepted edge kinds an unmatched caller lacks, the precision
///     extras, and the skip/exclusion counts. It uses no xUnit types.
/// </summary>
public static class SymbolFinderOracle
{
    /// <summary>
    ///     The target method kinds the extractor models: ordinary methods,
    ///     constructors (including primary constructors), user-defined operators,
    ///     conversions, and property/event accessors.
    /// </summary>
    public static IReadOnlyList<MethodKind> TargetKinds { get; } =
    [
        MethodKind.Ordinary,
        MethodKind.Constructor,
        MethodKind.UserDefinedOperator,
        MethodKind.Conversion,
        MethodKind.PropertyGet,
        MethodKind.PropertySet,
        MethodKind.EventAdd,
        MethodKind.EventRemove
    ];

    /// <summary>
    ///     Target kinds Lurp deliberately does not model as edges. Event accessor
    ///     calls are the one case here: an event subscription is modeled as a
    ///     MethodGroupRef to the subscribed handler (B1/Q6), never as an edge to the
    ///     event or its add/remove accessors. 'event_subscriptions' is a retired id:
    ///     it was registered in DeclaredBoundaries at HEAD and removed from Known in
    ///     the Phase 7 cleanup; naming it here keeps the skip visible instead of
    ///     turning it into a recall failure.
    /// </summary>
    public static IReadOnlyList<(MethodKind Kind, string BoundaryId)> ExcludedTargetKinds { get; } =
    [
        (MethodKind.EventAdd, "event_subscriptions"),
        (MethodKind.EventRemove, "event_subscriptions")
    ];

    /// <summary>
    ///     Static-abstract dispatch is a dispatch relation, not a direct call: the
    ///     constrained call site is extracted as Calls to the interface member, and
    ///     the implementing type is not. Skipped targets name this boundary id.
    /// </summary>
    public const string StaticAbstractDispatchBoundaryId = "static_abstract_dispatch";

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
        var excludedTargets = new List<SymbolFinderExcludedTarget>();
        var skippedStaticAbstract = new List<string>();
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
                    if (method.IsImplicitlyDeclared)
                    {
                        skippedImplicit++;
                        continue;
                    }

                    if (!TargetKinds.Contains(method.MethodKind))
                        continue;

                    if (ExcludedTargetBoundary(method.MethodKind) is { } excludedBoundary)
                    {
                        excludedTargets.Add(new SymbolFinderExcludedTarget(
                            method.MethodKind,
                            resolveNodeId(method) ?? method.ToDisplayString(),
                            excludedBoundary));
                        continue;
                    }

                    if (method.MethodKind == MethodKind.Ordinary && IsStaticAbstractImplementation(method))
                    {
                        skippedStaticAbstract.Add(resolveNodeId(method) ?? method.ToDisplayString());
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
                        method.MethodKind == MethodKind.Constructor
                            ? resolveNodeId(method.ContainingType)
                            : null,
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
            var callers = await SymbolFinder.FindCallersAsync(target.Symbol, solution, cancellationToken).ConfigureAwait(false);
            var expectedCallers = new HashSet<string>(StringComparer.Ordinal);
            var missingCallers = new List<SymbolFinderMissingCaller>();

            foreach (var caller in callers)
            {
                var candidates = CallerOwnerIds(caller.CallingSymbol, resolveNodeId).Distinct().ToList();

                // Per-caller external-dispatch rule: a caller whose called symbol
                // is outside the solution cannot have an edge (the extractor
                // filters external targets), so it is set aside rather than
                // swallowed by a target-level skip. An in-solution called symbol
                // outside the dispatch family is recorded but still checked, so a
                // missing MayDispatchTo edge stays a miss.
                var (expected, reason) = ClassifyCaller(caller, family, compilationByTree, resolveNodeId);
                if (!expected)
                {
                    setAsideCallers.Add(new SymbolFinderSetAsideCaller(
                        target.Id,
                        target.Symbol.MethodKind,
                        candidates,
                        reason!,
                        CallerLines(caller)));
                    if (reason == BindsExternalReason)
                        continue;
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
                    var isMatch = Matches(edgeSet, callerId, target, family);
                    evaluated[key] = isMatch;
                    if (!isMatch)
                        continue;

                    matchedPairs++;
                    matched = true;
                    break;
                }

                if (matched)
                    continue;

                missingCallers.Add(new SymbolFinderMissingCaller(
                    target.Id,
                    target.Symbol.MethodKind,
                    candidates,
                    AcceptedEdgeKinds(target),
                    CallerLines(caller)));
                missCounts[target.Symbol.MethodKind] = missCounts.GetValueOrDefault(target.Symbol.MethodKind) + 1;
            }

            targetResults.Add(new SymbolFinderTargetResult(
                target.Id,
                target.Symbol.MethodKind,
                [.. expectedCallers.OrderBy(static id => id, StringComparer.Ordinal)],
                missingCallers));
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
                skippedStaticAbstract,
                excludedTargets,
                targetCounts,
                missCounts,
                precision,
                setAsideCallers));
    }

    private static bool Matches(
        HashSet<(string Source, string Kind, string Target)> edges,
        string callerId,
        Target target,
        HashSet<string> family)
    {
        // The accepted relations: Calls and MethodGroupRef to the target itself or
        // to any member of the target's dispatch family (the target plus every id
        // reachable through MayDispatchTo edges). This mirrors Roslyn reporting a
        // caller of an interface/abstract member as a caller of its implementation
        // or override, and the reverse.
        foreach (var familyId in family)
        {
            if (edges.Contains((callerId, nameof(EdgeKind.Calls), familyId)) ||
                edges.Contains((callerId, nameof(EdgeKind.MethodGroupRef), familyId)))
                return true;
        }

        // A constructor may also be reached through a Constructs edge to its
        // containing type (object creation targets the type, not the ctor).
        if (target is { Symbol.MethodKind: MethodKind.Constructor, ContainingTypeId: { } containingTypeId } &&
            edges.Contains((callerId, nameof(EdgeKind.Constructs), containingTypeId)))
            return true;

        // Accessor targets are not edge endpoints in extraction; the property is.
        // A getter is the compiler-proved read of its property, a setter the
        // compiler-proved write; a compound access emits both.
        if (target.OwnerId is { } ownerId)
        {
            if (target.Symbol.MethodKind == MethodKind.PropertyGet &&
                edges.Contains((callerId, nameof(EdgeKind.Reads), ownerId)))
                return true;
            if (target.Symbol.MethodKind == MethodKind.PropertySet &&
                edges.Contains((callerId, nameof(EdgeKind.Writes), ownerId)))
                return true;
        }

        return false;
    }

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

    /// <summary>The edge kinds <see cref="Matches" /> accepts for a target.</summary>
    private static List<string> AcceptedEdgeKinds(Target target)
    {
        var kinds = new List<string> { nameof(EdgeKind.Calls), nameof(EdgeKind.MethodGroupRef) };
        if (target is { Symbol.MethodKind: MethodKind.Constructor, ContainingTypeId: not null })
            kinds.Add(nameof(EdgeKind.Constructs));
        if (target.OwnerId is not null)
        {
            if (target.Symbol.MethodKind == MethodKind.PropertyGet)
                kinds.Add(nameof(EdgeKind.Reads));
            if (target.Symbol.MethodKind == MethodKind.PropertySet)
                kinds.Add(nameof(EdgeKind.Writes));
        }

        return kinds;
    }

    private static string? ExcludedTargetBoundary(MethodKind kind)
    {
        foreach (var entry in ExcludedTargetKinds)
        {
            if (entry.Kind == kind)
                return entry.BoundaryId;
        }

        return null;
    }

    /// <summary>
    ///     Classifies a caller by the symbol Roslyn binds for it
    ///     (<see cref="SymbolCallerInfo.CalledSymbol" />). <c>Expected</c> is true
    ///     when the called symbol resolves to the target's dispatch family.
    ///     Otherwise <c>Reason</c> is <see cref="BindsExternalReason" /> when the
    ///     called symbol is outside the solution, or <see cref="BindsOtherReason" />
    ///     when it is an in-solution symbol outside the family.
    /// </summary>
    private static (bool Expected, string? Reason) ClassifyCaller(
        SymbolCallerInfo caller,
        HashSet<string> family,
        IReadOnlyDictionary<SyntaxTree, Compilation> compilationByTree,
        Func<ISymbol, string?> resolveNodeId)
    {
        var called = caller.CalledSymbol;
        if (resolveNodeId(called) is { Length: > 0 } calledId && family.Contains(calledId))
            return (true, null);

        return (false, IsInSolution(called, compilationByTree) ? BindsOtherReason : BindsExternalReason);
    }

    /// <summary>
    ///     True when the symbol is declared in a source tree the solution's
    ///     compilations carry. A metadata view of a referenced project is not in
    ///     the set, so classification never depends on assembly object identity.
    /// </summary>
    private static bool IsInSolution(
        ISymbol symbol,
        IReadOnlyDictionary<SyntaxTree, Compilation> compilationByTree)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource && location.SourceTree is { } tree && compilationByTree.ContainsKey(tree))
                return true;
        }

        return false;
    }

    private static List<int> CallerLines(SymbolCallerInfo caller) =>
        [.. caller.Locations.Select(location => location.GetLineSpan().StartLinePosition.Line + 1)];

    private sealed record Target(string Id, IMethodSymbol Symbol, string? ContainingTypeId, string? OwnerId);

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
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction, ContainingSymbol: { } container }:
                if (resolveNodeId(container) is { Length: > 0 } containerId)
                    yield return containerId;
                break;
        }
    }

    private static bool IsStaticAbstractImplementation(IMethodSymbol method)
    {
        if (!method.IsStatic || method.ContainingType.TypeKind != TypeKind.Class)
            return false;
        return method.ContainingType.AllInterfaces
            .SelectMany(interfaceType => interfaceType.GetMembers(method.Name).OfType<IMethodSymbol>())
            .Any(candidate => candidate.IsStatic && candidate.IsAbstract);
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

/// <summary>A target kind Oracle B excludes from the edge comparison.</summary>
public sealed record SymbolFinderExcludedTarget(MethodKind Kind, string DisplayId, string BoundaryId);

/// <summary>
///     A caller Roslyn reported for a target whose called symbol is not the
///     target or a member of its dispatch family: the candidate owner ids the
///     caller normalizes to, the reason it was set aside
///     (<see cref="SymbolFinderOracle.BindsExternalReason" /> or
///     <see cref="SymbolFinderOracle.BindsOtherReason" />), and its source lines.
/// </summary>
public sealed record SymbolFinderSetAsideCaller(
    string TargetId,
    MethodKind TargetKind,
    IReadOnlyList<string> CallerIds,
    string Reason,
    IReadOnlyList<int> Lines);

/// <summary>Aggregate Oracle B counts, skips, exclusions, and precision.</summary>
public sealed record SymbolFinderOracleSummary(
    int CheckedPairs,
    int MatchedPairs,
    int SkippedImplicit,
    int SkippedNoId,
    IReadOnlyList<string> SkippedStaticAbstract,
    IReadOnlyList<SymbolFinderExcludedTarget> ExcludedTargets,
    IReadOnlyDictionary<MethodKind, int> TargetCounts,
    IReadOnlyDictionary<MethodKind, int> MissCounts,
    double Precision,
    IReadOnlyList<SymbolFinderSetAsideCaller> SetAsideCallers);

/// <summary>The structured Oracle B result over one <see cref="Solution" />.</summary>
public sealed record SymbolFinderOracleResult(
    IReadOnlyList<SymbolFinderTargetResult> Targets,
    IReadOnlyList<SymbolFinderEdge> Extras,
    SymbolFinderOracleSummary Summary);
