using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class TestAdapter : IFrameworkAdapter
{
    private const string AdapterVersion = "test-v3";

    public string Name => "Test";
    public string Version => AdapterVersion;
    public string Description => "Production-to-test tested-by edges";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var edges = new List<EdgeRecord>();

        var isTestProject = IsTestProject(context.Compilation);
        if (!isTestProject)
            return edges;

        foreach (var type in context.GetAllNamedTypes())
            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;

                if (!IsTestMethod(method))
                    continue;

                var testMethodId = context.MakeSymbolId(method);
                if (testMethodId == null)
                    continue;

                // Resolve the evidence site: the test method declaration
                var location = context.LocationResolver.Resolve(method);

                foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                {
                    if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax methodSyntax)
                        continue;

                    if (!context.IsInScope(methodSyntax.SyntaxTree))
                        continue;

                    var bodySyntax = methodSyntax.Body ?? (SyntaxNode?)methodSyntax.ExpressionBody;
                    if (bodySyntax == null)
                        continue;

                    var semanticModel = context.GetSemanticModel(methodSyntax.SyntaxTree);
                    var testCtx = new TestEmitContext(context, edges, testMethodId, [], location);

                    CollectTestReferences(bodySyntax, semanticModel, testCtx);
                }
            }

        return edges;
    }

    private static bool IsTestProject(Compilation compilation)
    {
        var assemblyName = compilation.Assembly.Name;
        if (assemblyName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.EndsWith(".Specs", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase))
            return true;

        return compilation.ReferencedAssemblyNames.Any(refAsm =>
            refAsm.Name.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
            refAsm.Name.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
            refAsm.Name.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
            refAsm.Name.Contains("MSTest", StringComparison.OrdinalIgnoreCase) ||
            refAsm.Name.Contains("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTestMethod(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass == null)
                continue;

            var name = attr.AttributeClass.Name;
            if (name is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute" or
                "Test" or "TestAttribute" or "TestMethod" or "TestMethodAttribute")
                return true;

            var fullName = attr.AttributeClass.ToDisplayString();
            if (fullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute" or "NUnit.Framework.TestAttribute" or "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute")
                return true;
        }

        return false;
    }

    private static void AddProductionRef(ISymbol symbol, TestEmitContext context)
    {
        var productionSymbol = symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
            ? symbol.ContainingType
            : symbol;

        if (productionSymbol == null)
            return;

        if (productionSymbol is INamespaceSymbol)
            return;

        var productionAssembly = productionSymbol.ContainingAssembly;
        if (productionAssembly != null && productionAssembly.Identity.GetDisplayName() == context.Adapter.AssemblyIdentity)
            return;

        var productionId = context.Adapter.MakeSymbolId(productionSymbol);
        if (productionId == null)
            return;

        if (!context.ReferencedSymbols.Add(productionId))
            return;

        context.Edges.Add(context.Adapter.MakeEdge(productionId, context.TestMethodId, nameof(EdgeKind.TestedBy), Provenance.FrameworkDerived,
            AdapterVersion, context.Location));
    }

    private static void CollectTestReferences(SyntaxNode bodySyntax, SemanticModel semanticModel, TestEmitContext context)
    {
        foreach (var invocation in bodySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol != null)
                AddProductionRef(symbolInfo.Symbol, context);
        }

        foreach (var creation in bodySyntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var createdType = semanticModel.GetTypeInfo(creation).Type;
            if (createdType != null)
                AddProductionRef(createdType, context);
        }

        foreach (var memberAccess in bodySyntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);
            if (symbolInfo.Symbol != null)
                AddProductionRef(symbolInfo.Symbol, context);
        }
    }

    /// <summary>The adapter context, the edge list and the evidence site of one test method body.</summary>
    private sealed record TestEmitContext(
        AdapterExtractionContext Adapter,
        List<EdgeRecord> Edges,
        string TestMethodId,
        HashSet<string> ReferencedSymbols,
        (string? path, int? sl, int? sc, int? el, int? ec) Location);
}
