using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using Lurp.Shared;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class DependencyInjectionAdapter : IFrameworkAdapter
{
    public string Name => "Dependency Injection";
    public string Version => "di-v1";

    private sealed record ExtractionContext(
        string AssemblyIdentity,
        string SnapshotId,
        string ExtractorVersion,
        List<EdgeRecord> Edges,
        HashSet<(string source, string target, string kind)> Seen,
        EdgeLocationResolver LocationResolver
    );

    private static readonly HashSet<string> _conventionMethodNames =
    [
        "Scan", "AddClasses", "AsImplementedInterfaces",
        "AsMatchingInterface", "UsingRegistrationStrategy", "AddAssemblyTypes",
    ];

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId, EdgeLocationResolver locationResolver)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        var ctx = new ExtractionContext(
            AssemblyIdentity: compilation.Assembly.Identity.GetDisplayName(),
            SnapshotId: snapshotId,
            ExtractorVersion: Version,
            Edges: edges,
            Seen: seen,
            LocationResolver: locationResolver
        );

        var serviceCollectionType = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.IServiceCollection");

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);

            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
                    continue;

                var methodName = methodSymbol.Name;

                if (methodName is "AddScoped" or "AddTransient" or "AddSingleton")
                {
                    ProcessExplicitGeneric(invocation, methodSymbol, semanticModel, ctx);
                    continue;
                }

                if (_conventionMethodNames.Contains(methodName))
                {
                    ProcessConventionCandidate(invocation, methodSymbol, semanticModel, compilation, ctx);
                    continue;
                }

                if (methodName is "AddHostedService" or "Configure" or "AddOptions")
                {
                    ProcessRuntimeUnknown(invocation, semanticModel, ctx);
                    continue;
                }

                if (serviceCollectionType != null && IsExternalMethodWithServiceCollectionParam(methodSymbol, compilation, serviceCollectionType))
                {
                    ProcessRuntimeUnknown(invocation, semanticModel, ctx);
                }
            }
        }

        return edges;
    }

    private static void ProcessExplicitGeneric(InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol, SemanticModel semanticModel, ExtractionContext ctx)
    {
        if (!IsDependencyInjectionExtensionMethod(methodSymbol))
            return;

        var sourceId = ResolveSourceId(invocation, semanticModel, ctx.AssemblyIdentity);
        if (sourceId == null)
            return;

        var typeArgs = ResolveRegistrationTypeArgs(invocation, semanticModel);
        if (typeArgs.Count == 0)
            return;

        var implTypeId = SymbolIdFactory.Make(typeArgs[^1], ctx.AssemblyIdentity);
        if (implTypeId == null)
            return;

        var key = (sourceId, implTypeId, EdgeKind.Registers.ToString());
        if (ctx.Seen.Add(key))
        {
            var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(invocation.GetLocation());

            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = implTypeId,
                Kind = EdgeKind.Registers.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = ctx.ExtractorVersion,
                SourceDocumentPath = path,
                SourceStartLine = sl,
                SourceStartColumn = sc,
                SourceEndLine = el,
                SourceEndColumn = ec,
                IsCrossGenerated = ctx.LocationResolver.IsGenerated(path),
            });
        }

        EmitInterfaceToImplementationEdge(invocation, typeArgs, implTypeId, ctx);
    }

    /// <summary>
    /// Emits the service interface → implementation edge for an explicit
    /// registration such as <c>AddScoped&lt;IService, Service&gt;()</c>.
    /// Skipped when fewer than two resolvable type arguments exist (e.g.
    /// self-registration <c>AddScoped&lt;Service&gt;()</c>) or when the source
    /// and target resolve to the same symbol.
    /// </summary>
    private static void EmitInterfaceToImplementationEdge(InvocationExpressionSyntax invocation, List<ITypeSymbol> typeArgs, string implTypeId, ExtractionContext ctx)
    {
        if (typeArgs.Count < 2)
            return;

        var serviceType = typeArgs[0];
        if (serviceType.TypeKind == TypeKind.Error)
            return;

        var serviceTypeId = SymbolIdFactory.Make(serviceType, ctx.AssemblyIdentity);
        if (serviceTypeId == null || serviceTypeId == implTypeId)
            return;

        var key = (serviceTypeId, implTypeId, EdgeKind.Registers.ToString());
        if (!ctx.Seen.Add(key))
            return;

        var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(invocation.GetLocation());

        ctx.Edges.Add(new EdgeRecord
        {
            SourceSymbolId = serviceTypeId,
            TargetSymbolId = implTypeId,
            Kind = EdgeKind.Registers.ToString(),
            Provenance = Provenance.FrameworkDerived,
            SnapshotId = ctx.SnapshotId,
            ExtractorVersion = ctx.ExtractorVersion,
            SourceDocumentPath = path,
            SourceStartLine = sl,
            SourceStartColumn = sc,
            SourceEndLine = el,
            SourceEndColumn = ec,
            IsCrossGenerated = ctx.LocationResolver.IsGenerated(path),
        });
    }

    private static bool IsDependencyInjectionExtensionMethod(IMethodSymbol methodSymbol)
    {
        var current = methodSymbol.ContainingType;
        while (current != null)
        {
            if (current.Name is "ServiceCollectionServiceExtensions" or "ExtensionsServiceCollectionExtensions" or "ServiceCollectionDescriptorExtensions")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static List<ITypeSymbol> ResolveRegistrationTypeArgs(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var typeArgs = new List<ITypeSymbol>();

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess && memberAccess.Name is GenericNameSyntax genericName)
        {
            foreach (var typeArg in genericName.TypeArgumentList.Arguments)
            {
                var typeInfo = semanticModel.GetTypeInfo(typeArg);
                if (typeInfo.Type != null)
                    typeArgs.Add(typeInfo.Type);
            }
        }

        if (typeArgs.Count == 0)
        {
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.Expression is TypeOfExpressionSyntax typeofExpr)
                {
                    var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
                    if (typeInfo.Type != null)
                        typeArgs.Add(typeInfo.Type);
                }
            }
        }

        return typeArgs;
    }


    private static void ProcessConventionCandidate(InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol, SemanticModel semanticModel, Compilation compilation, ExtractionContext ctx)
    {
        var sourceId = ResolveSourceId(invocation, semanticModel, ctx.AssemblyIdentity);
        if (sourceId == null)
            return;

        var assemblyName = ExtractConventionAssemblyName(invocation, methodSymbol, semanticModel, compilation, ctx.AssemblyIdentity);

        var targetId = $"{GraphNodeIds.AssemblyScanConventionPrefix}{assemblyName}";

        var key = (sourceId, targetId, EdgeKind.Registers.ToString());
        if (ctx.Seen.Add(key))
        {
            var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(invocation.GetLocation());

            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = EdgeKind.Registers.ToString(),
                Provenance = Provenance.Convention,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = ctx.ExtractorVersion,
                SourceDocumentPath = path,
                SourceStartLine = sl,
                SourceStartColumn = sc,
                SourceEndLine = el,
                SourceEndColumn = ec,
                IsCrossGenerated = ctx.LocationResolver.IsGenerated(path),
                TargetNodeKind = GraphNodeKind.Convention,
            });
        }

        if (methodSymbol.Name == "Scan")
        {
            EmitScanConventionRegistrationEdges(invocation, semanticModel, ctx);
        }
    }

    /// <summary>
    /// Emits interface → concrete class edges for the statically recognizable
    /// Scrutor fluent chain:
    /// <c>Scan(scan =&gt; scan.FromAssembliesOf(...).AddClasses().AsImplementedInterfaces().WithScopedLifetime())</c>.
    /// For unsupported, incomplete, or dynamic Scan calls this emits nothing;
    /// the convention placeholder edge from <see cref="ProcessConventionCandidate"/>
    /// is preserved in all cases.
    /// </summary>
    private static void EmitScanConventionRegistrationEdges(InvocationExpressionSyntax scanInvocation, SemanticModel semanticModel, ExtractionContext ctx)
    {
        var assemblyType = ResolveScannedAssemblyType(scanInvocation, semanticModel);
        if (assemblyType?.ContainingAssembly == null)
            return;

        if (!HasRecognizedConventionChain(scanInvocation))
            return;

        var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(scanInvocation.GetLocation());
        var isCrossGenerated = ctx.LocationResolver.IsGenerated(path);

        var assembly = assemblyType.ContainingAssembly;

        foreach (var type in EnumerateNamedTypes(assembly.GlobalNamespace))
        {
            if (type.TypeKind != TypeKind.Class)
                continue;
            if (type.IsAbstract)
                continue;
            if (type.SpecialType == SpecialType.System_Object)
                continue;
            if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, assembly))
                continue;

            var implTypeId = SymbolIdFactory.Make(type, ctx.AssemblyIdentity);
            if (implTypeId == null)
                continue;

            foreach (var iface in type.AllInterfaces)
            {
                if (iface.TypeKind == TypeKind.Error)
                    continue;

                var ifaceTypeId = SymbolIdFactory.Make(iface, ctx.AssemblyIdentity);
                if (ifaceTypeId == null)
                    continue;

                var key = (ifaceTypeId, implTypeId, EdgeKind.Registers.ToString());
                if (!ctx.Seen.Add(key))
                    continue;

                ctx.Edges.Add(new EdgeRecord
                {
                    SourceSymbolId = ifaceTypeId,
                    TargetSymbolId = implTypeId,
                    Kind = EdgeKind.Registers.ToString(),
                    Provenance = Provenance.Convention,
                    SnapshotId = ctx.SnapshotId,
                    ExtractorVersion = ctx.ExtractorVersion,
                    SourceDocumentPath = path,
                    SourceStartLine = sl,
                    SourceStartColumn = sc,
                    SourceEndLine = el,
                    SourceEndColumn = ec,
                    IsCrossGenerated = isCrossGenerated,
                });
            }
        }
    }

    /// <summary>
    /// Resolves the statically known type whose assembly is scanned by a
    /// <c>Scan</c> call, from <c>FromAssemblyOf&lt;T&gt;()</c> or
    /// <c>FromAssembliesOf(typeof(T))</c>. Returns null for dynamic or
    /// unresolved assembly selection.
    /// </summary>
    private static ITypeSymbol? ResolveScannedAssemblyType(InvocationExpressionSyntax scanInvocation, SemanticModel semanticModel)
    {
        foreach (var arg in scanInvocation.ArgumentList.Arguments)
        {
            if (arg.Expression is not LambdaExpressionSyntax lambda)
                continue;

            foreach (var nested in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (nested.Expression is not MemberAccessExpressionSyntax access)
                    continue;

                if (access.Name.Identifier.Text is not ("FromAssemblyOf" or "FromAssembliesOf"))
                    continue;

                if (access.Name is GenericNameSyntax generic)
                {
                    foreach (var typeArg in generic.TypeArgumentList.Arguments)
                    {
                        var typeInfo = semanticModel.GetTypeInfo(typeArg);
                        if (typeInfo.Type is { TypeKind: not TypeKind.Error } resolved)
                            return resolved;
                    }
                }

                foreach (var nestedArg in nested.ArgumentList.Arguments)
                {
                    if (nestedArg.Expression is TypeOfExpressionSyntax typeofExpr)
                    {
                        var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
                        if (typeInfo.Type is { TypeKind: not TypeKind.Error } resolved)
                            return resolved;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Recognizes the full fluent chain
    /// <c>FromAssembliesOf(...).AddClasses().AsImplementedInterfaces().WithScopedLifetime()</c>
    /// inside the Scan lambda. Rejects chains with a predicate filter on
    /// <c>AddClasses</c> since arbitrary lambda predicates are not evaluated.
    /// </summary>
    private static bool HasRecognizedConventionChain(InvocationExpressionSyntax scanInvocation)
    {
        var chainNames = new HashSet<string>(StringComparer.Ordinal);
        var hasPredicateFilter = false;

        foreach (var arg in scanInvocation.ArgumentList.Arguments)
        {
            if (arg.Expression is not LambdaExpressionSyntax lambda)
                continue;

            foreach (var nested in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (nested.Expression is not MemberAccessExpressionSyntax access)
                    continue;

                switch (access.Name.Identifier.Text)
                {
                    case "FromAssemblyOf":
                    case "FromAssembliesOf":
                        chainNames.Add("FromAssembliesOf");
                        break;
                    case "AddClasses":
                        chainNames.Add("AddClasses");
                        if (nested.ArgumentList.Arguments.Any(a => a.Expression is LambdaExpressionSyntax))
                            hasPredicateFilter = true;
                        break;
                    case "AsImplementedInterfaces":
                        chainNames.Add("AsImplementedInterfaces");
                        break;
                    case "WithScopedLifetime":
                        chainNames.Add("WithScopedLifetime");
                        break;
                }
            }
        }

        return !hasPredicateFilter
            && chainNames.Contains("FromAssembliesOf")
            && chainNames.Contains("AddClasses")
            && chainNames.Contains("AsImplementedInterfaces")
            && chainNames.Contains("WithScopedLifetime");
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateNamedTypes(type))
                yield return nested;
        }

        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in EnumerateNamedTypes(nestedNamespace))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNamedTypes(nested))
                yield return deeper;
        }
    }

    
    private static string ExtractConventionAssemblyName(InvocationExpressionSyntax invocation,IMethodSymbol methodSymbol,SemanticModel semanticModel,Compilation compilation,string fallback)
    {
        var directAssembly = ResolveAssemblyFromGenericTypeArgs(invocation, semanticModel);
        if (directAssembly != null)
            return directAssembly;

        if (methodSymbol.Name == "Scan")
        {
            var scannedAssembly = ResolveAssemblyFromScanLambda(invocation, semanticModel);
            if (scannedAssembly != null)
                return scannedAssembly;
        }

        return fallback;
    }

    private static string? ResolveAssemblyFromGenericTypeArgs(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess || memberAccess.Name is not GenericNameSyntax genericName)
            return null;

        foreach (var typeArg in genericName.TypeArgumentList.Arguments)
        {
            var typeInfo = semanticModel.GetTypeInfo(typeArg);
            if (typeInfo.Type?.ContainingAssembly != null)
                return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
        }

        return null;
    }

    private static string? ResolveAssemblyFromScanLambda(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is not LambdaExpressionSyntax lambda)
                continue;

            foreach (var nested in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var assembly = ResolveAssemblyFromAssemblyScanCall(nested, semanticModel);
                if (assembly != null)
                    return assembly;
            }
        }

        return null;
    }

    private static string? ResolveAssemblyFromAssemblyScanCall(InvocationExpressionSyntax nested, SemanticModel semanticModel)
    {
        if (nested.Expression is not MemberAccessExpressionSyntax nestedAccess || nestedAccess.Name is not SimpleNameSyntax nestedName)
            return null;

        if (nestedName.Identifier.Text != "FromAssemblyOf" && nestedName.Identifier.Text != "FromAssembliesOf")
            return null;

        if (nestedAccess.Name is GenericNameSyntax nestedGeneric)
        {
            foreach (var typeArg in nestedGeneric.TypeArgumentList.Arguments)
            {
                var typeInfo = semanticModel.GetTypeInfo(typeArg);
                if (typeInfo.Type?.ContainingAssembly != null)
                    return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
            }
        }

        foreach (var nestedArg in nested.ArgumentList.Arguments)
        {
            if (nestedArg.Expression is TypeOfExpressionSyntax typeofExpr)
            {
                var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
                if (typeInfo.Type?.ContainingAssembly != null)
                    return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
            }
        }

        return null;
    }

    private static void ProcessRuntimeUnknown(InvocationExpressionSyntax invocation, SemanticModel semanticModel, ExtractionContext ctx)
    {
        var sourceId = ResolveSourceId(invocation, semanticModel, ctx.AssemblyIdentity);

        if (sourceId == null)
            return;

        const string targetId = GraphNodeIds.RuntimeUnknown;

        var key = (sourceId, targetId, EdgeKind.Registers.ToString());

        if (ctx.Seen.Add(key))
        {
            var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(invocation.GetLocation());

            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = EdgeKind.Registers.ToString(),
                Provenance = Provenance.RuntimeUnknown,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = ctx.ExtractorVersion,
                SourceDocumentPath = path,
                SourceStartLine = sl,
                SourceStartColumn = sc,
                SourceEndLine = el,
                SourceEndColumn = ec,
                IsCrossGenerated = ctx.LocationResolver.IsGenerated(path),
                TargetNodeKind = GraphNodeKind.RuntimePlaceholder,
            });
        }
    }

    /// <summary>
    /// Returns true when <paramref name="methodSymbol"/> is defined outside
    /// the current <paramref name="compilation"/> and has at least one
    /// parameter whose type is <paramref name="serviceCollectionType"/>.
    /// </summary>
    private static bool IsExternalMethodWithServiceCollectionParam(IMethodSymbol methodSymbol,Compilation compilation,INamedTypeSymbol serviceCollectionType)
    {
        if (SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingAssembly, compilation.Assembly))
            return false;

        foreach (var param in methodSymbol.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(param.Type, serviceCollectionType))
                return true;
        }

        return false;
    }

    // ────────────────────────────────────────────────────────────────
    // Shared helpers
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the source symbol ID for an invocation. Prefers the enclosing
    /// method, falling back to the enclosing type declaration.
    /// </summary>
    private static string? ResolveSourceId(InvocationExpressionSyntax invocation,SemanticModel semanticModel,string assemblyIdentity)
    {
        var containingMethod = invocation.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (containingMethod != null)
        {
            var methodSym = semanticModel.GetDeclaredSymbol(containingMethod);
            if (methodSym != null)
            {
                var id = SymbolIdFactory.Make(methodSym, assemblyIdentity);
                if (id != null)
                    return id;
            }
        }

        var containingTypeDecl = invocation.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (containingTypeDecl != null)
        {
            var typeSym = semanticModel.GetDeclaredSymbol(containingTypeDecl);
            if (typeSym != null)
            {
                var id = SymbolIdFactory.Make(typeSym, assemblyIdentity);
                if (id != null)
                    return id;
            }
        }

        return null;
    }

}
