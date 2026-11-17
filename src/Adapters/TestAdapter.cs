using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class TestAdapter : IFrameworkAdapter
{
    private const string AdapterVersion = "test-v4";

    public string Name => "Test";
    public string Version => AdapterVersion;
    public string Description => "Production-to-test tested-by edges";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var edges = new List<EdgeRecord>();

        if (!IsTestProject(context.Compilation))
            return edges;

        var bodyInputs = context.Inputs with { ScopeDocuments = null, Incompleteness = null };
        var memberCtx = new MemberEdgeExtractionContext(bodyInputs);
        var targetSymbolsById = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        var bodyEdges = new OperationShapeExtractor(memberCtx).ExtractBodies(targetSymbolsById);

        var edgesByOwner = new Dictionary<string, List<(string TargetId, string Kind)>>(StringComparer.Ordinal);
        foreach (var edge in bodyEdges)
        {
            if (edge.Kind is not (nameof(EdgeKind.Calls) or nameof(EdgeKind.Constructs) or
                nameof(EdgeKind.Reads) or nameof(EdgeKind.Writes) or nameof(EdgeKind.References)))
                continue;

            if (!edgesByOwner.TryGetValue(edge.SourceSymbolId, out var list))
            {
                list = [];
                edgesByOwner[edge.SourceSymbolId] = list;
            }

            list.Add((edge.TargetSymbolId, edge.Kind));
        }

        var asyncLifetimeInit = context.Compilation
            .GetTypeByMetadataName("Xunit.IAsyncLifetime")
            ?.GetMembers("InitializeAsync")
            .OfType<IMethodSymbol>()
            .FirstOrDefault();

        foreach (var type in context.GetAllNamedTypes())
        {
            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol method || !IsTestMethod(method))
                    continue;

                if (!IsMethodInScope(method, context))
                    continue;

                var testMethodId = context.MakeSymbolId(method);
                if (testMethodId == null)
                    continue;

                var location = context.LocationResolver.Resolve(method);
                CollectTestedByForMethod(
                    method,
                    type,
                    testMethodId,
                    location,
                    asyncLifetimeInit,
                    edgesByOwner,
                    targetSymbolsById,
                    context,
                    edges);
            }
        }

        return edges;
    }

    private static bool IsTestProject(Compilation compilation)
    {
        return TestFrameworks.IsTestProjectReferenceSet(compilation.ReferencedAssemblyNames.Select(r => r.Name));
    }

    private static bool IsTestMethod(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsAbstract)
            return false;

        return HasFrameworkAttribute(method, TestFrameworks.TestAttributeMetadataNames);
    }

    private static bool IsSetupMethod(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsAbstract)
            return false;

        return HasFrameworkAttribute(method, TestFrameworks.SetupAttributeMetadataNames);
    }

    private static bool HasFrameworkAttribute(IMethodSymbol method, string[] metadataNames)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass == null)
                continue;

            foreach (var metadataName in metadataNames)
            {
                if (FrameworkTypes.DerivesFrom(attr.AttributeClass, metadataName))
                    return true;
            }
        }

        return false;
    }

    private static bool IsMethodInScope(IMethodSymbol method, AdapterExtractionContext context)
    {
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is MethodDeclarationSyntax methodSyntax &&
                context.IsInScope(methodSyntax.SyntaxTree))
                return true;
        }

        return false;
    }

    private static bool IsInTestAssembly(ISymbol symbol, string testAssemblyIdentity)
    {
        var assembly = symbol.ContainingAssembly;
        return assembly != null && assembly.Identity.GetDisplayName() == testAssemblyIdentity;
    }

    private static void CollectTestedByForMethod(
        IMethodSymbol testMethod,
        INamedTypeSymbol testType,
        string testMethodId,
        (string? path, int? sl, int? sc, int? el, int? ec) location,
        IMethodSymbol? asyncLifetimeInit,
        Dictionary<string, List<(string TargetId, string Kind)>> edgesByOwner,
        Dictionary<string, ISymbol> targetSymbolsById,
        AdapterExtractionContext context,
        List<EdgeRecord> edges)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(ISymbol Symbol, bool ExpandAccessors)>();
        var accessorsExpanded = new HashSet<string>(StringComparer.Ordinal);
        var emittedProductionIds = new HashSet<string>(StringComparer.Ordinal);

        void EnqueueSymbol(ISymbol? symbol, bool expandAccessors = true)
        {
            if (symbol == null)
                return;

            var id = context.MakeSymbolId(symbol);
            if (id == null)
                return;

            var needBody = visited.Add(id);
            var needAccessors = expandAccessors && symbol is IPropertySymbol or IEventSymbol && accessorsExpanded.Add(id);
            if (needBody || needAccessors)
                queue.Enqueue((symbol, needAccessors));
        }

        void EnqueueInstanceInitializers(INamedTypeSymbol startType)
        {
            for (var cur = startType; cur != null && IsInTestAssembly(cur, context.AssemblyIdentity); cur = cur.BaseType)
            {
                foreach (var member in cur.GetMembers())
                {
                    if (member is (IFieldSymbol or IPropertySymbol) && !member.IsStatic)
                        EnqueueSymbol(member, expandAccessors: false);
                }
            }
        }

        EnqueueSymbol(testMethod);

        for (var cur = testType; cur != null && IsInTestAssembly(cur, context.AssemblyIdentity); cur = cur.BaseType)
        {
            foreach (var member in cur.GetMembers())
            {
                switch (member)
                {
                    case IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: false, IsStatic: false } ctor:
                        EnqueueSymbol(ctor);
                        break;
                    case IMethodSymbol m when IsSetupMethod(m):
                        EnqueueSymbol(m);
                        break;
                    case (IFieldSymbol or IPropertySymbol) when !member.IsStatic:
                        EnqueueSymbol(member, expandAccessors: false);
                        break;
                }
            }

            if (asyncLifetimeInit != null &&
                cur.FindImplementationForInterfaceMember(asyncLifetimeInit) is IMethodSymbol initImpl &&
                IsInTestAssembly(initImpl, context.AssemblyIdentity))
            {
                EnqueueSymbol(initImpl);
            }
        }

        while (queue.Count > 0)
        {
            var (current, expandAccessors) = queue.Dequeue();
            var currentId = context.MakeSymbolId(current);

            if (currentId != null && edgesByOwner.TryGetValue(currentId, out var outgoing))
            {
                foreach (var (targetId, kind) in outgoing)
                {
                    if (!targetSymbolsById.TryGetValue(targetId, out var targetSymbol))
                        continue;

                    if (IsInTestAssembly(targetSymbol, context.AssemblyIdentity))
                    {
                        EnqueueSymbol(targetSymbol.OriginalDefinition);
                        if (kind == nameof(EdgeKind.Constructs) && targetSymbol is INamedTypeSymbol constructedType)
                            EnqueueInstanceInitializers(constructedType);
                    }
                    else
                    {
                        var productionType = targetSymbol switch
                        {
                            INamespaceSymbol or ITypeParameterSymbol => null,
                            INamedTypeSymbol named => named.OriginalDefinition,
                            _ => targetSymbol.ContainingType?.OriginalDefinition
                        };

                        if (productionType == null || IsInTestAssembly(productionType, context.AssemblyIdentity))
                            continue;

                        var productionId = context.MakeSymbolId(productionType);
                        if (productionId != null && emittedProductionIds.Add(productionId))
                        {
                            edges.Add(context.MakeEdge(productionId, testMethodId,
                                nameof(EdgeKind.TestedBy), Provenance.FrameworkDerived, AdapterVersion, location));
                        }
                    }
                }
            }

            if (expandAccessors)
            {
                switch (current)
                {
                    case IPropertySymbol prop:
                        EnqueueSymbol(prop.GetMethod);
                        EnqueueSymbol(prop.SetMethod);
                        break;
                    case IEventSymbol ev:
                        EnqueueSymbol(ev.AddMethod);
                        EnqueueSymbol(ev.RemoveMethod);
                        break;
                }
            }
        }
    }
}
