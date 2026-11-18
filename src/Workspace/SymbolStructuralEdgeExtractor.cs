using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class SymbolStructuralEdgeExtractor(SymbolExtractionContext context)
{
    internal List<EdgeRecord> ExtractEdges()
    {
        var edges = new List<EdgeRecord>();

        foreach (var typeSymbol in ExtractionUtils.GetNamespaceTypeMembers(context.Compilation.Assembly.GlobalNamespace)) CollectTypeEdges(typeSymbol, edges);

        return edges;
    }

    private bool IsTypeInScope(INamedTypeSymbol typeSymbol)
    {
        if (context.ScopeDocuments == null)
            return true;
        return typeSymbol.DeclaringSyntaxReferences.Any(syntaxRef => context.IsInScope(syntaxRef.SyntaxTree));
    }

    private void CollectTypeEdges(INamedTypeSymbol typeSymbol, List<EdgeRecord> edges)
    {
        // Skip types not in scope : they'll produce edges with no SourceDocumentPath
        // and their structural relationships (inherits, implements) are global per compilation anyway.
        if (!IsTypeInScope(typeSymbol))
            return;

        var sourceId = MakeSymbolId(typeSymbol);
        if (sourceId == null)
            return;

        var sourceSyntax = BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(typeSymbol);

        if (typeSymbol.BaseType != null && typeSymbol.BaseType.SpecialType != SpecialType.System_Object && typeSymbol.BaseType.SpecialType != SpecialType.System_ValueType)
        {
            context.RecordFilteredExternal(typeSymbol.BaseType, sourceSyntax);
            var targetId = MakeSymbolId(typeSymbol.BaseType);
            if (targetId != null)
                edges.Add(MakeEdge(sourceId, targetId, nameof(EdgeKind.Inherits), typeSymbol,
                    ExtractionUtils.IsOutsideCompilation(typeSymbol.BaseType, context.Compilation)));
        }

        foreach (var iface in typeSymbol.Interfaces)
        {
            context.RecordFilteredExternal(iface, sourceSyntax);
            var targetId = MakeSymbolId(iface);
            if (targetId != null)
                edges.Add(MakeEdge(sourceId, targetId, nameof(EdgeKind.Implements), typeSymbol,
                    ExtractionUtils.IsOutsideCompilation(iface, context.Compilation)));
        }

        AddWrittenSignatureReferences(sourceId, typeSymbol, sourceSyntax, edges);

        foreach (var nested in typeSymbol.GetTypeMembers())
        {
            CollectTypeEdges(nested, edges);
            var nestedId = MakeSymbolId(nested);
            if (nestedId != null) edges.Add(MakeEdge(sourceId, nestedId, nameof(EdgeKind.Contains), typeSymbol));
        }

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is INamedTypeSymbol)
                continue;

            CollectMemberReferenceEdges(member, edges);
        }
    }

    private void CollectMemberReferenceEdges(ISymbol member, List<EdgeRecord> edges)
    {
        // D38: a compiler-made member that Lurp stores has the signature facts of a written
        // member (ExtractionUtils.HasStructuralFacts). An accessor and an enum constant add no
        // edge: the property or event, and the enum type, carry their types. A delegate type
        // owns the signature of its Invoke (ExtractionUtils.SignatureOwner).
        if (!ExtractionUtils.HasStructuralFacts(member)
            || member is IMethodSymbol { AssociatedSymbol: not null }
            || member is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum })
            return;

        var sourceSymbol = member is IMethodSymbol signatureMethod ? ExtractionUtils.SignatureOwner(signatureMethod) : member;
        if (sourceSymbol is null)
            return;

        var referencedType = member switch
        {
            IMethodSymbol method => method.ReturnType,
            IPropertySymbol prop => prop.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol evt => evt.Type,
            _ => null
        };

        var memberId = SymbolIdFactory.Make(sourceSymbol, context.AssemblyIdentity);
        if (memberId is null)
            return;

        // D14 reads written constraint syntax; a compiler-made member writes none.
        if (member is IMethodSymbol { IsImplicitlyDeclared: false })
            AddWrittenSignatureReferences(memberId, member, BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(member), edges);

        if (referencedType is null)
            return;

        // The member is the source of every member-signature References edge, as in
        // ParameterDependencyEdgeExtractor and OperationShapeExtractor.
        foreach (var used in ExtractionUtils.TypeUses(referencedType))
        {
            context.RecordFilteredExternal(used, BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(sourceSymbol));
            var usedId = MakeSymbolId(used);
            if (usedId != null) edges.Add(MakeEdge(memberId, usedId, nameof(EdgeKind.References), sourceSymbol));
        }
    }

    /// <summary>
    ///     D14: each type argument in the base list and each constraint type that the declaration
    ///     of <paramref name="symbol" /> writes is a References fact from it (an exception to
    ///     mapping rule 6). Only written types count: a record's compiler-made
    ///     <c>IEquatable&lt;R&gt;</c> and the inherited constraints of an override are not written.
    /// </summary>
    private void AddWrittenSignatureReferences(string sourceId, ISymbol symbol, SyntaxNode? sourceSyntax, List<EdgeRecord> edges)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            if (!context.IsInScope(syntaxRef.SyntaxTree))
                continue;

            var declaration = syntaxRef.GetSyntax();
            var model = context.GetOrCreateSemanticModel(syntaxRef.SyntaxTree);
            var used = new List<INamedTypeSymbol>();
            if (declaration is BaseTypeDeclarationSyntax { BaseList: { } baseList })
            {
                foreach (var baseType in baseList.Types)
                    if (model.GetTypeInfo(baseType.Type).Type is { } bound)
                        used.AddRange(ExtractionUtils.NestedTypeUses(bound));
            }

            var constraintClauses = declaration switch
            {
                TypeDeclarationSyntax type => type.ConstraintClauses,
                DelegateDeclarationSyntax @delegate => @delegate.ConstraintClauses,
                MethodDeclarationSyntax method => method.ConstraintClauses,
                _ => default
            };
            foreach (var clause in constraintClauses)
            foreach (var constraint in clause.Constraints.OfType<TypeConstraintSyntax>())
                if (model.GetTypeInfo(constraint.Type).Type is { } bound)
                    used.AddRange(ExtractionUtils.TypeUses(bound));

            foreach (var type in used)
            {
                context.RecordFilteredExternal(type, sourceSyntax);
                var targetId = MakeSymbolId(type);
                if (targetId != null)
                    edges.Add(MakeEdge(sourceId, targetId, nameof(EdgeKind.References), symbol));
            }
        }
    }

    private string? MakeSymbolId(ITypeSymbol typeSymbol)
    {
        return SymbolIdFactory.Make(typeSymbol, context.AssemblyIdentity);
    }

    private EdgeRecord MakeEdge(string sourceId, string targetId, string kind, ISymbol sourceSymbol, bool targetIsBoundary = false)
    {
        var loc = GetSymbolSourceLocation(sourceSymbol);
        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = Provenance.CompilerProved,
            SnapshotId = context.SnapshotId,
            ExtractorVersion = VersionConstants.ExtractorVersion,
            SourceDocumentPath = loc?.path,
            SourceStartLine = loc?.startLine,
            SourceStartColumn = loc?.startColumn,
            SourceEndLine = loc?.endLine,
            SourceEndColumn = loc?.endColumn,
            IsCrossGenerated = IsGeneratedSymbol(sourceSymbol),
            TargetIsBoundary = targetIsBoundary
        };
    }

    private bool IsGeneratedSymbol(ISymbol symbol)
    {
        var syntaxRef = EdgeLocationResolver.PrimaryDeclaration(symbol);
        if (syntaxRef == null)
            return false;
        var documentId = context.ResolveDocumentId(syntaxRef.SyntaxTree);
        if (documentId == null)
            return false;
        if (context.GeneratedDocuments.Contains(documentId.Value))
            return true;
        return GeneratedFilePaths.IsGenerated(documentId.Value.ToString());
    }

    private (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)?
        GetSymbolSourceLocation(ISymbol symbol)
    {
        var syntaxRef = EdgeLocationResolver.PrimaryDeclaration(symbol);
        var syntaxTree = syntaxRef?.SyntaxTree;
        if (syntaxRef == null || syntaxTree == null)
            return null;
        var documentId = context.ResolveDocumentId(syntaxTree);
        if (documentId == null)
            return null;
        var location = syntaxRef.GetSyntax().GetLocation();
        if (location == null || !location.IsInSource)
            return null;
        var lineSpan = location.GetLineSpan();
        return (documentId.Value.ToString(),
            lineSpan.StartLinePosition.Line,
            lineSpan.StartLinePosition.Character,
            lineSpan.EndLinePosition.Line,
            lineSpan.EndLinePosition.Character);
    }
}