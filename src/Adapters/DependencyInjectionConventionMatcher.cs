// Purpose: detect Scrutor-style convention-based DI registrations.
// Owns: the convention-method syntax walk and convention:assembly_scan provenance.
// Must not contain: explicit/helper registration detection or edge persistence.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using Lurp.Shared;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

internal static class DependencyInjectionConventionMatcher
{
    internal static void ProcessConventionCandidate(InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol, SemanticModel semanticModel, Compilation compilation, ExtractionContext ctx)
    {
        var sourceId = DependencyInjectionAdapter.ResolveSourceId(invocation, semanticModel, ctx.AssemblyIdentity);
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
            EmitScanConventionRegistrationEdges(invocation, semanticModel, compilation, ctx);
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
    private static void EmitScanConventionRegistrationEdges(InvocationExpressionSyntax scanInvocation, SemanticModel semanticModel, Compilation compilation, ExtractionContext ctx)
    {
        var assemblyType = ResolveScannedAssemblyType(scanInvocation, semanticModel);
        if (assemblyType?.ContainingAssembly == null)
            return;

        if (!HasRecognizedConventionChain(scanInvocation))
            return;

        // The match set is open: any type added to the scanned assembly may newly
        // match this convention, and no persisted edge witnesses the new match, so
        // the reverse-edge closure cannot see it. Record an unobservable-completeness
        // row so FindAffectedDocPaths seeds this registration document on any change.
        // Only when the scanned assembly is the current compilation's assembly: an
        // external scanned assembly yields no in-snapshot type edges at all, so its
        // relation set is provably closed.
        if (SymbolEqualityComparer.Default.Equals(assemblyType.ContainingAssembly, compilation.Assembly))
            ctx.Incompleteness?.RecordConventionScan(scanInvocation);

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
}
