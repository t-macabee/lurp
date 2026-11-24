using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class TestAdapter : IFrameworkAdapter
{
    private const string AdapterVersion = "test-v5";

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

        var types = context.GetAllNamedTypes().ToList();
        var collectionDefinitions = types
            .SelectMany(type => type.GetAttributes()
                .Where(attr => FrameworkTypes.DerivesFrom(attr.AttributeClass, "Xunit.CollectionDefinitionAttribute"))
                .Select(attr => (Name: ReadAttributeName(attr) ?? type.Name, Type: type)))
            .ToLookup(entry => entry.Name, entry => entry.Type, StringComparer.Ordinal);

        foreach (var type in types)
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
                    collectionDefinitions,
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

    private static IEnumerable<(INamedTypeSymbol Provider, string? Member)> DataSourcesFor(IMethodSymbol method)
    {
        foreach (var attr in FrameworkTypes.GetAttributesWithInheritance(method))
        {
            var attrType = attr.AttributeClass;
            if (FrameworkTypes.DerivesFrom(attrType, "Xunit.ClassDataAttribute") ||
                FrameworkTypes.DerivesFrom(attrType, "Xunit.ClassDataAttribute`1"))
            {
                if (ReadAttributeType(attr) is { } provider)
                    yield return (provider, null);
                continue;
            }

            var nunitSource = FrameworkTypes.DerivesFrom(attrType, "NUnit.Framework.TestCaseSourceAttribute");
            if (nunitSource || FrameworkTypes.DerivesFrom(attrType, "Xunit.MemberDataAttribute") ||
                FrameworkTypes.DerivesFrom(attrType, "Microsoft.VisualStudio.TestTools.UnitTesting.DynamicDataAttribute"))
            {
                var provider = ReadAttributeType(attr);
                if (ReadAttributeName(attr) is { } name)
                    yield return (provider ?? method.ContainingType, name);
                else if (nunitSource && provider != null)
                    yield return (provider, null);
            }
        }
    }

    private static INamedTypeSymbol? ReadAttributeType(AttributeData attr)
    {
        foreach (var argument in attr.NamedArguments)
        {
            if (argument.Key == "MemberType" && argument.Value.Value is INamedTypeSymbol memberType)
                return memberType;
        }

        foreach (var argument in attr.ConstructorArguments)
        {
            if (argument.Kind == TypedConstantKind.Type && argument.Value is INamedTypeSymbol provider)
                return provider;
        }

        return attr.AttributeClass?.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
    }

    private static string? ReadAttributeName(AttributeData attr)
    {
        foreach (var argument in attr.ConstructorArguments)
        {
            if (argument.Kind == TypedConstantKind.Primitive && argument.Value is string name)
                return name;
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> FixturesFor(
        INamedTypeSymbol testType, ILookup<string, INamedTypeSymbol> collectionDefinitions)
    {
        foreach (var fixture in FixtureTypes(testType, "Xunit.IClassFixture`1"))
            yield return fixture;

        foreach (var attr in FrameworkTypes.GetAttributesWithInheritance(testType))
        {
            if (!FrameworkTypes.DerivesFrom(attr.AttributeClass, "Xunit.CollectionAttribute") &&
                !FrameworkTypes.DerivesFrom(attr.AttributeClass, "Xunit.CollectionAttribute`1"))
                continue;

            IEnumerable<INamedTypeSymbol> collections = ReadAttributeType(attr) is { } type
                ? [type]
                : ReadAttributeName(attr) is { } name ? collectionDefinitions[name] : [];
            foreach (var collection in collections)
            foreach (var fixture in FixtureTypes(collection, "Xunit.ICollectionFixture`1"))
                yield return fixture;
        }
    }

    private static IEnumerable<INamedTypeSymbol> FixtureTypes(INamedTypeSymbol owner, string interfaceName)
    {
        foreach (var iface in owner.AllInterfaces)
        {
            if (FrameworkTypes.Is(iface, interfaceName) && iface.TypeArguments is [INamedTypeSymbol fixture])
                yield return fixture;
        }
    }

    private static void CollectTestedByForMethod(
        IMethodSymbol testMethod,
        INamedTypeSymbol testType,
        string testMethodId,
        (string? path, int? sl, int? sc, int? el, int? ec) location,
        ILookup<string, INamedTypeSymbol> collectionDefinitions,
        Dictionary<string, List<(string TargetId, string Kind)>> edgesByOwner,
        Dictionary<string, ISymbol> targetSymbolsById,
        AdapterExtractionContext context,
        List<EdgeRecord> edges)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(ISymbol Symbol, bool ExpandAccessors)>();
        var accessorsExpanded = new HashSet<string>(StringComparer.Ordinal);
        var emittedProductionIds = new HashSet<string>(StringComparer.Ordinal);
        var setupDependencies = new HashSet<string>(StringComparer.Ordinal);

        void RecordSetupDependency(ISymbol symbol)
        {
            // Reflected setup has no call site. Give the reverse document closure a path
            // from a fixture or data provider edit to the test fact's evidence document.
            if (IsInTestAssembly(symbol, context.AssemblyIdentity) &&
                context.MakeSymbolId(symbol) is { } id && setupDependencies.Add(id))
            {
                edges.Add(context.MakeEdge(testMethodId, id, nameof(EdgeKind.References),
                    Provenance.FrameworkDerived, AdapterVersion, location));
            }
        }

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

        void EnqueueSetup(INamedTypeSymbol startType, bool includeFrameworkSetup)
        {
            for (var cur = startType; cur != null && IsInTestAssembly(cur, context.AssemblyIdentity); cur = cur.BaseType)
            {
                foreach (var member in cur.GetMembers())
                {
                    switch (member)
                    {
                        case IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: false, IsStatic: false }:
                            EnqueueSymbol(member);
                            break;
                        case IMethodSymbol m when includeFrameworkSetup && IsSetupMethod(m):
                            EnqueueSymbol(m);
                            break;
                        case IFieldSymbol or IPropertySymbol when !member.IsStatic:
                            EnqueueSymbol(member, expandAccessors: false);
                            break;
                    }
                }

                EnqueueInterfaceMethods(cur, "InitializeAsync", "Xunit.IAsyncLifetime");
            }
        }

        void EnqueueInterfaceMethods(INamedTypeSymbol type, string name, string interfaceName)
        {
            foreach (var iface in type.AllInterfaces.Where(i => FrameworkTypes.Is(i, interfaceName)))
            foreach (var member in iface.GetMembers(name))
                EnqueueSymbol(type.FindImplementationForInterfaceMember(member));
        }

        EnqueueSymbol(testMethod);
        EnqueueSetup(testType, includeFrameworkSetup: true);

        foreach (var fixture in FixturesFor(testType, collectionDefinitions))
        {
            RecordSetupDependency(fixture);
            EnqueueSetup(fixture, includeFrameworkSetup: false);
        }

        foreach (var (provider, memberName) in DataSourcesFor(testMethod))
        {
            if (!IsInTestAssembly(provider, context.AssemblyIdentity))
                continue;

            if (memberName != null)
            {
                for (var current = provider; current != null && IsInTestAssembly(current, context.AssemblyIdentity); current = current.BaseType)
                {
                    foreach (var member in current.GetMembers(memberName).Where(m => m.IsStatic))
                    {
                        RecordSetupDependency(member);
                        EnqueueSymbol(member);
                    }
                }
            }
            else
            {
                RecordSetupDependency(provider);
                EnqueueSetup(provider, includeFrameworkSetup: false);
                EnqueueInterfaceMethods(provider, "GetEnumerator", "System.Collections.IEnumerable");
                EnqueueInterfaceMethods(provider, "GetEnumerator", "System.Collections.Generic.IEnumerable`1");
                EnqueueInterfaceMethods(provider, "GetAsyncEnumerator", "System.Collections.Generic.IAsyncEnumerable`1");
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

                        if (productionType == null || !context.IsSolutionSymbol(productionType))
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
