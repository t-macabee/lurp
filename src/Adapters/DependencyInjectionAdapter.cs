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

    internal sealed record DiExtractionContext(
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

    public List<EdgeRecord> Extract(AdapterExtractionContext context)
    {
        var compilation = context.Compilation;
        var snapshotId = context.SnapshotId;
        var locationResolver = context.LocationResolver;
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        var ctx = new DiExtractionContext(
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
            var semanticModel = context.GetSemanticModel(tree);

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
                    DependencyInjectionConventionMatcher.ProcessConventionCandidate(invocation, methodSymbol, semanticModel, compilation, ctx);
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

    private static void ProcessExplicitGeneric(InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol, SemanticModel semanticModel, DiExtractionContext ctx)
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
    private static void EmitInterfaceToImplementationEdge(InvocationExpressionSyntax invocation, List<ITypeSymbol> typeArgs, string implTypeId, DiExtractionContext ctx)
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


    private static void ProcessRuntimeUnknown(InvocationExpressionSyntax invocation, SemanticModel semanticModel, DiExtractionContext ctx)
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

        var typeArgs = ResolveRegistrationTypeArgs(invocation, semanticModel);
        if (typeArgs.Count == 0)
            return;

        var implTypeId = SymbolIdFactory.Make(typeArgs[^1], ctx.AssemblyIdentity);
        if (implTypeId == null)
            return;

        var concreteKey = (sourceId, implTypeId, EdgeKind.Registers.ToString());
        if (ctx.Seen.Add(concreteKey))
        {
            var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(invocation.GetLocation());

            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = implTypeId,
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
    internal static string? ResolveSourceId(InvocationExpressionSyntax invocation,SemanticModel semanticModel,string assemblyIdentity)
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
