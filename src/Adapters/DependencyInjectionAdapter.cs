using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class DependencyInjectionAdapter : IFrameworkAdapter
{
    public string Name => "Dependency Injection";
    public string Version => "di-v1";

    private static readonly HashSet<string> ConventionMethodNames = new()
    {
        "Scan", "AddClasses", "AsImplementedInterfaces",
        "AsMatchingInterface", "UsingRegistrationStrategy", "AddAssemblyTypes",
    };

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var diExtractorVersion = ExtractorConstants.DependencyInjectionExtractor;

        var serviceCollectionType = compilation.GetTypeByMetadataName(
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);

            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
                    continue;

                var methodName = methodSymbol.Name;

                // ── Category 1: Explicit generic registrations (existing) ──
                if (methodName is "AddScoped" or "AddTransient" or "AddSingleton")
                {
                    ProcessExplicitGeneric(invocation, methodSymbol, semanticModel,
                        assemblyIdentity, snapshotId, edges, seen);
                    continue;
                }

                // ── Category 2: Convention-based candidate patterns ──
                if (ConventionMethodNames.Contains(methodName))
                {
                    ProcessConventionCandidate(invocation, methodSymbol, semanticModel,
                        compilation, assemblyIdentity, snapshotId, diExtractorVersion, edges, seen);
                    continue;
                }

                // ── Category 3: Runtime-unknown patterns ──

                // Explicit runtime-unknown registration methods
                if (methodName is "AddHostedService" or "Configure" or "AddOptions")
                {
                    ProcessRuntimeUnknown(invocation, semanticModel,
                        assemblyIdentity, snapshotId, diExtractorVersion, edges, seen);
                    continue;
                }

                // IServiceCollection passed as parameter to an external method
                if (serviceCollectionType != null &&
                    IsExternalMethodWithServiceCollectionParam(methodSymbol, compilation, serviceCollectionType))
                {
                    ProcessRuntimeUnknown(invocation, semanticModel,
                        assemblyIdentity, snapshotId, diExtractorVersion, edges, seen);
                }
            }
        }

        return edges;
    }

    // ────────────────────────────────────────────────────────────────
    // Category 1 — explicit generic registrations (unchanged logic)
    // ────────────────────────────────────────────────────────────────

    private static void ProcessExplicitGeneric(
        InvocationExpressionSyntax invocation,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        string assemblyIdentity,
        string snapshotId,
        List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        var containingType = methodSymbol.ContainingType;
        if (containingType == null)
            return;

        bool isDiExtension = false;
        var current = containingType;
        while (current != null)
        {
            if (current.Name is "ServiceCollectionServiceExtensions" or
                "ExtensionsServiceCollectionExtensions" or
                "ServiceCollectionDescriptorExtensions")
            {
                isDiExtension = true;
                break;
            }
            current = current.BaseType;
        }

        if (!isDiExtension)
            return;

        var sourceId = ResolveSourceId(invocation, semanticModel, assemblyIdentity);
        if (sourceId == null)
            return;

        var typeArgs = new List<ITypeSymbol>();

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is GenericNameSyntax genericName)
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

        if (typeArgs.Count >= 2)
        {
            var implTypeId = MakeSymbolId(typeArgs[typeArgs.Count - 1], assemblyIdentity);
            if (implTypeId != null)
            {
                var key = (sourceId, implTypeId, EdgeKind.Registers.ToString());
                if (seen.Add(key))
                {
                    edges.Add(new EdgeRecord(
                        sourceSymbolId: sourceId,
                        targetSymbolId: implTypeId,
                        kind: EdgeKind.Registers.ToString(),
                        provenance: "framework_derived",
                        snapshotId: snapshotId,
                        extractorVersion: "di-v1"));
                }
            }
        }
        else if (typeArgs.Count == 1)
        {
            var implTypeId = MakeSymbolId(typeArgs[0], assemblyIdentity);
            if (implTypeId != null)
            {
                var key = (sourceId, implTypeId, EdgeKind.Registers.ToString());
                if (seen.Add(key))
                {
                    edges.Add(new EdgeRecord(
                        sourceSymbolId: sourceId,
                        targetSymbolId: implTypeId,
                        kind: EdgeKind.Registers.ToString(),
                        provenance: "framework_derived",
                        snapshotId: snapshotId,
                        extractorVersion: "di-v1"));
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Category 2 — convention-based registration candidates
    // ────────────────────────────────────────────────────────────────

    private static void ProcessConventionCandidate(
        InvocationExpressionSyntax invocation,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        Compilation compilation,
        string assemblyIdentity,
        string snapshotId,
        string extractorVersion,
        List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        var sourceId = ResolveSourceId(invocation, semanticModel, assemblyIdentity);
        if (sourceId == null)
            return;

        var assemblyName = TryExtractConventionAssemblyName(
            invocation, methodSymbol, semanticModel, compilation, assemblyIdentity);

        var targetId = $"convention:assembly_scan:{assemblyName}";

        var key = (sourceId, targetId, EdgeKind.Registers.ToString());
        if (seen.Add(key))
        {
            edges.Add(new EdgeRecord(
                sourceSymbolId: sourceId,
                targetSymbolId: targetId,
                kind: EdgeKind.Registers.ToString(),
                provenance: "convention",
                snapshotId: snapshotId,
                extractorVersion: extractorVersion));
        }
    }

    /// <summary>
    /// Attempts to determine the assembly name from a convention-based
    /// scanning call. Checks the following in order:
    ///   1. The invocation itself — if it is <c>FromAssemblyOf&lt;T&gt;</c> or
    ///      <c>AddAssemblyTypes&lt;T&gt;</c>, extract T's assembly.
    ///   2. If the invocation is <c>Scan(...)</c> with a lambda argument,
    ///      search the lambda body for <c>FromAssemblyOf&lt;T&gt;</c> /
    ///      <c>FromAssembliesOf(...)</c> and extract the first type's assembly.
    ///   3. Fall back to the current compilation's assembly name.
    /// </summary>
    private static string TryExtractConventionAssemblyName(
        InvocationExpressionSyntax invocation,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        Compilation compilation,
        string fallback)
    {
        // 1. Direct type-argument extraction (FromAssemblyOf<T>, AddAssemblyTypes<T>, etc.)
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is GenericNameSyntax genericName)
        {
            foreach (var typeArg in genericName.TypeArgumentList.Arguments)
            {
                var typeInfo = semanticModel.GetTypeInfo(typeArg);
                if (typeInfo.Type?.ContainingAssembly != null)
                    return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
            }
        }

        // 2. Scan(...) with a lambda — look for FromAssemblyOf<T> / FromAssembliesOf inside
        if (methodSymbol.Name == "Scan")
        {
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.Expression is LambdaExpressionSyntax lambda)
                {
                    foreach (var nested in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (nested.Expression is MemberAccessExpressionSyntax nestedAccess &&
                            nestedAccess.Name is SimpleNameSyntax nestedName &&
                            (nestedName.Identifier.Text == "FromAssemblyOf" ||
                             nestedName.Identifier.Text == "FromAssembliesOf"))
                        {
                            if (nestedAccess.Name is GenericNameSyntax nestedGeneric)
                            {
                                foreach (var typeArg in nestedGeneric.TypeArgumentList.Arguments)
                                {
                                    var typeInfo = semanticModel.GetTypeInfo(typeArg);
                                    if (typeInfo.Type?.ContainingAssembly != null)
                                        return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
                                }
                            }

                            // FromAssembliesOf can also take typeof(...) arguments
                            foreach (var nestedArg in nested.ArgumentList.Arguments)
                            {
                                if (nestedArg.Expression is TypeOfExpressionSyntax typeofExpr)
                                {
                                    var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
                                    if (typeInfo.Type?.ContainingAssembly != null)
                                        return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
                                }
                            }
                        }
                    }
                }
            }
        }

        return fallback;
    }

    // ────────────────────────────────────────────────────────────────
    // Category 3 — runtime-unknown registrations
    // ────────────────────────────────────────────────────────────────

    private static void ProcessRuntimeUnknown(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        string assemblyIdentity,
        string snapshotId,
        string extractorVersion,
        List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        var sourceId = ResolveSourceId(invocation, semanticModel, assemblyIdentity);
        if (sourceId == null)
            return;

        const string targetId = "runtime:unknown";

        var key = (sourceId, targetId, EdgeKind.Registers.ToString());
        if (seen.Add(key))
        {
            edges.Add(new EdgeRecord(
                sourceSymbolId: sourceId,
                targetSymbolId: targetId,
                kind: EdgeKind.Registers.ToString(),
                provenance: "runtime_unknown",
                snapshotId: snapshotId,
                extractorVersion: extractorVersion));
        }
    }

    /// <summary>
    /// Returns true when <paramref name="methodSymbol"/> is defined outside
    /// the current <paramref name="compilation"/> and has at least one
    /// parameter whose type is <paramref name="serviceCollectionType"/>.
    /// </summary>
    private static bool IsExternalMethodWithServiceCollectionParam(
        IMethodSymbol methodSymbol,
        Compilation compilation,
        INamedTypeSymbol serviceCollectionType)
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
    private static string? ResolveSourceId(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        string assemblyIdentity)
    {
        var containingMethod = invocation.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (containingMethod != null)
        {
            var methodSym = semanticModel.GetDeclaredSymbol(containingMethod);
            if (methodSym != null)
            {
                var id = MakeSymbolId(methodSym, assemblyIdentity);
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
                var id = MakeSymbolId(typeSym, assemblyIdentity);
                if (id != null)
                    return id;
            }
        }

        return null;
    }

    private static string? MakeSymbolId(ISymbol symbol, string assemblyIdentity)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{assemblyIdentity}";
    }
}
