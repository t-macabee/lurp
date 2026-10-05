using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Tests;

/// <summary>
///     Audit B1 Oracle B: Roslyn's own <see cref="SymbolFinder.FindCallersAsync" />
///     is the caller oracle. For every method-like member the extractor models —
///     ordinary methods, constructors (including primary constructors),
///     user-defined operators, conversions, and property/event accessors — each
///     caller Roslyn reports must appear in extraction as a Calls, Constructs or
///     MethodGroupRef relation: recall must be 100% for these compiler-proved
///     relations. Constructor targets match Calls to the constructor itself or
///     Constructs to its containing type; accessor targets are mapped to the
///     owner ids the extractor uses (a getter/setter to its property, checked
///     against the extractor's Reads/Writes), and event accessors are excluded
///     with the boundary id that documents the unmodeled relation. The test also
///     prints the precision of those relations and the first 20 extracted
///     Calls/MethodGroupRef edges to checked targets that Roslyn did not report
///     (precision is informational).
/// </summary>
/// <remarks>
///     Caller identity is normalized to the extraction owner: an auto-property
///     backing field maps to its property, a property maps to its accessors, and
///     a local function maps to its containing method. Static-abstract dispatch
///     (a generic method constrained to a static abstract interface member) is a
///     dispatch relation, not a direct call, and is reported as a skipped target.
/// </remarks>
[Trait("Category", "Slow")]
[Collection("GroundTruth")]
public sealed class GroundTruthOracleBTests(GroundTruthFixture callShapes, CrossProjectGroundTruthFixture crossProject)
{
    private static readonly MethodKind[] TargetKinds =
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

    // Target kinds Lurp deliberately does not model as edges. Event accessor
    // calls are the one case here: an event subscription is modeled as a
    // MethodGroupRef to the subscribed handler (B1/Q6), never as an edge to the
    // event or its add/remove accessors. 'event_subscriptions' is a retired id:
    // it was registered in DeclaredBoundaries at HEAD and removed from Known in
    // the Phase 7 cleanup; naming it here keeps the skip visible instead of
    // turning it into a recall failure.
    private static readonly (MethodKind Kind, string BoundaryId)[] ExcludedTargetKinds =
    [
        (MethodKind.EventAdd, "event_subscriptions"),
        (MethodKind.EventRemove, "event_subscriptions")
    ];

    // Static-abstract dispatch is a dispatch relation, not a direct call: the
    // constrained call site is extracted as Calls to the interface member, and
    // the implementing type is not. Skipped targets name this boundary id.
    private const string StaticAbstractDispatchBoundaryId = "static_abstract_dispatch";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySymbolFinderCaller_IsExtracted(bool useCrossProject)
    {
        GroundTruthFixtureBase fixture = useCrossProject ? crossProject : callShapes;
        var solution = fixture.Solution ?? throw new InvalidOperationException("Solution not loaded.");
        var edges = fixture.Facts
            .Where(fact => fact.SourceDeclared && fact.TargetDeclared)
            .Select(fact => (fact.Source, fact.Kind, fact.Target))
            .ToHashSet();

        var targets = new List<Target>();
        var skippedStaticAbstract = new List<IMethodSymbol>();
        var excludedTargets = new List<string>();
        var skippedImplicit = 0;
        var skippedNoId = 0;

        foreach (var (_, compilation) in fixture.Compilations)
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
                        excludedTargets.Add($"{method.MethodKind} " +
                                            $"{OperationShapeOracle.NormalizedDocId(method) ?? method.ToDisplayString()} -> {excludedBoundary}");
                        continue;
                    }

                    if (method.MethodKind == MethodKind.Ordinary && IsStaticAbstractImplementation(method))
                    {
                        skippedStaticAbstract.Add(method);
                        continue;
                    }

                    if (OperationShapeOracle.NormalizedDocId(member) is not { Length: > 0 } id)
                    {
                        skippedNoId++;
                        continue;
                    }

                    targets.Add(new Target(
                        id,
                        method,
                        method.MethodKind == MethodKind.Constructor
                            ? OperationShapeOracle.NormalizedDocId(method.ContainingType)
                            : null,
                        method.AssociatedSymbol is { } owner ? OperationShapeOracle.NormalizedDocId(owner) : null));
                }
            }
        }

        Assert.NotEmpty(targets);

        var checkedPairs = 0;
        var matchedPairs = 0;
        var misses = new List<string>();
        var symbolFinderPairs = new HashSet<(string Caller, string Target)>();
        var evaluated = new Dictionary<(string Caller, string Target), bool>();
        var targetCounts = new Dictionary<MethodKind, int>();
        var missCounts = new Dictionary<MethodKind, int>();

        foreach (var target in targets)
        {
            targetCounts[target.Symbol.MethodKind] = targetCounts.GetValueOrDefault(target.Symbol.MethodKind) + 1;
            var callers = await SymbolFinder.FindCallersAsync(target.Symbol, solution);
            foreach (var caller in callers)
            {
                var candidates = CallerOwnerIds(caller.CallingSymbol).Distinct().ToList();
                var matched = false;
                foreach (var callerId in candidates)
                {
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
                    var isMatch = Matches(edges, callerId, target);
                    evaluated[key] = isMatch;
                    if (!isMatch)
                        continue;

                    matchedPairs++;
                    matched = true;
                    break;
                }

                if (matched)
                    continue;

                var lines = string.Join("|", caller.Locations.Select(location => location.GetLineSpan().StartLinePosition.Line + 1));
                misses.Add($"{target.Symbol.MethodKind} {string.Join(" OR ", candidates)} -[{lines}]-> {target.Id}");
                missCounts[target.Symbol.MethodKind] = missCounts.GetValueOrDefault(target.Symbol.MethodKind) + 1;
            }
        }

        // Fix (5): precision is the share of extracted Calls/MethodGroupRef edges
        // to checked targets that SymbolFinder did report as callers; the rest are
        // printed so an oracle/extractor divergence is visible. Not asserted.
        var checkedTargetIds = targets.Select(static target => target.Id).ToHashSet(StringComparer.Ordinal);
        var extras = edges
            .Where(edge => (edge.Kind == nameof(EdgeKind.Calls) || edge.Kind == nameof(EdgeKind.MethodGroupRef)) &&
                           checkedTargetIds.Contains(edge.Target) &&
                           !symbolFinderPairs.Contains((edge.Source, edge.Target)))
            .OrderBy(static edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(static edge => edge.Target, StringComparer.Ordinal)
            .ThenBy(static edge => edge.Kind, StringComparer.Ordinal)
            .ToList();
        var precision = matchedPairs + extras.Count == 0
            ? 1.0
            : (double)matchedPairs / (matchedPairs + extras.Count);
        Console.WriteLine(
            $"Oracle B precision: {precision:P2} ({matchedPairs} matched SymbolFinder pairs, {extras.Count} " +
            $"extracted Calls/MethodGroupRef edge(s) to checked targets that SymbolFinder did not report).");
        foreach (var edge in extras.Take(20))
            Console.WriteLine($"  extra edge: {edge.Source} -[{edge.Kind}]-> {edge.Target}");

        var kindSummary = string.Join(", ", TargetKinds.Select(kind =>
            $"{kind}={targetCounts.GetValueOrDefault(kind)} target(s)/{missCounts.GetValueOrDefault(kind)} miss(es)"));
        Console.WriteLine(
            $"Oracle B recall: checked {checkedPairs} caller pair(s) against {targets.Count} target(s) ({kindSummary}); " +
            $"skipped {skippedImplicit} implicit member(s), {skippedNoId} without doc id, " +
            $"{skippedStaticAbstract.Count} static-abstract dispatch target(s).");
        foreach (var method in skippedStaticAbstract)
            Console.WriteLine($"  skipped static-abstract: {OperationShapeOracle.NormalizedDocId(method) ?? method.ToDisplayString()} " +
                              $"(registered boundary '{StaticAbstractDispatchBoundaryId}')");
        foreach (var entry in ExcludedTargetKinds)
            Console.WriteLine($"Oracle B exclusion: {entry.Kind} targets are not modeled as edges; " +
                              $"registered boundary '{entry.BoundaryId}'.");
        foreach (var excluded in excludedTargets)
            Console.WriteLine($"  excluded target: {excluded}");

        Assert.True(checkedPairs > 0, "SymbolFinder returned no caller pairs; the oracle did not run.");
        Assert.True(misses.Count == 0,
            $"{misses.Count} SymbolFinder caller(s) missing from extraction (checked {checkedPairs} pairs):{Environment.NewLine}" +
            string.Join(Environment.NewLine, misses.Take(60)));
    }

    private static bool Matches(HashSet<(string Source, string Kind, string Target)> edges, string callerId, Target target)
    {
        // The task's accepted relations: Calls and MethodGroupRef to the target
        // itself; a constructor may also be reached through a Constructs edge to
        // its containing type (object creation targets the type, not the ctor).
        if (edges.Contains((callerId, nameof(EdgeKind.Calls), target.Id)) ||
            edges.Contains((callerId, nameof(EdgeKind.MethodGroupRef), target.Id)))
            return true;

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

    private static string? ExcludedTargetBoundary(MethodKind kind)
    {
        foreach (var entry in ExcludedTargetKinds)
        {
            if (entry.Kind == kind)
                return entry.BoundaryId;
        }

        return null;
    }

    private sealed record Target(string Id, IMethodSymbol Symbol, string? ContainingTypeId, string? OwnerId);

    private static IEnumerable<string> CallerOwnerIds(ISymbol caller)
    {
        if (OperationShapeOracle.NormalizedDocId(caller) is { Length: > 0 } direct)
            yield return direct;

        switch (caller)
        {
            case IFieldSymbol { AssociatedSymbol: { } associated }:
                if (OperationShapeOracle.NormalizedDocId(associated) is { Length: > 0 } propertyId)
                    yield return propertyId;
                break;
            case IPropertySymbol property:
                if (property.GetMethod != null && OperationShapeOracle.NormalizedDocId(property.GetMethod) is { Length: > 0 } getterId)
                    yield return getterId;
                if (property.SetMethod != null && OperationShapeOracle.NormalizedDocId(property.SetMethod) is { Length: > 0 } setterId)
                    yield return setterId;
                break;
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction, ContainingSymbol: { } container }:
                if (OperationShapeOracle.NormalizedDocId(container) is { Length: > 0 } containerId)
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
