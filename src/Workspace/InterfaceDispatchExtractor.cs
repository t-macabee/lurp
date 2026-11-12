using Microsoft.CodeAnalysis;
using System.Text.Json;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
///     For every type that implements an interface, emit a may_dispatch_to edge
///     from each interface member to the effective implementation.
///     Dispatch resolution strategy:
///     For each concrete type, iterate over AllInterfaces and their members.
///     Use Roslyn's FindImplementationForInterfaceMember to resolve the
///     effective implementation (which may be inherited from a base type).
///     Then classify provenance: "compiler_proved" if the type that declares the
///     implementing member maps the interface member to it itself, "possible" if
///     only a derived type maps it to an inherited member (S01-F15).
///     When the interface is a constructed generic (e.g. IRepository&lt;Customer&gt;),
///     the concrete type arguments are captured as JSON in the edge's
///     TypeArgumentsJson field so consumers can distinguish dispatch targets
///     bound to different type arguments.
/// </summary>
internal sealed class InterfaceDispatchExtractor(PolymorphismExtractionContext context)
{
    internal List<EdgeRecord> Extract(List<INamedTypeSymbol> allTypes)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind, string typeArgs)>();

        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;

            if (type.AllInterfaces.IsEmpty)
                continue;

            foreach (var iface in type.AllInterfaces)
            {
                var typeArgsJson = GetTypeArgumentsJson(iface);

                foreach (var member in iface.GetMembers()) EmitInterfaceDispatchEdge(type, member, edges, seen, typeArgsJson);
            }
        }

        return edges;
    }

    private void EmitInterfaceDispatchEdge(INamedTypeSymbol type, ISymbol member, List<EdgeRecord> edges, HashSet<(string source, string target, string kind, string typeArgs)> seen, string? typeArgumentsJson)
    {
        if (member is not IMethodSymbol and not IPropertySymbol and not IEventSymbol)
            return;

        var ifaceMemberId = context.MakeSymbolId(member);
        if (ifaceMemberId == null)
            return;

        context.RecordFilteredExternal(member, BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(type));

        var implMember = type.FindImplementationForInterfaceMember(member);
        if (implMember == null)
            return;

        var implMemberId = context.MakeSymbolId(implMember);
        if (implMemberId == null || implMemberId == ifaceMemberId)
            return;

        var key = (ifaceMemberId, implMemberId, nameof(EdgeKind.MayDispatchTo), typeArgumentsJson ?? "");
        if (!seen.Add(key))
            return;

        // S01-F15: the provenance belongs to the pair (interface member, implementation), not
        // to the type that this loop visits, so it does not depend on the order of the types or
        // on the extraction scope. The pair is compiler-established when the type that declares
        // the implementing member maps the interface member to it: Roslyn proves that this member
        // implements the interface member. When only a derived type maps the member to an
        // inherited implementation, the pair is merely possible. Either way the edge is a
        // structural fact about the graph, not a per-call-site claim about which implementation
        // a call selects at runtime : composition into a call-site claim is graded separately by
        // the capsule tier builders.
        var isDirect = SymbolEqualityComparer.Default.Equals(
            implMember.ContainingType.FindImplementationForInterfaceMember(member), implMember);
        var provenance = isDirect ? Provenance.CompilerProved : Provenance.Possible;

        edges.Add(context.MakeMayDispatchEdge(ifaceMemberId, implMemberId, implMember, provenance, typeArgumentsJson));
    }

    /// <summary>
    ///     If the interface is a constructed generic type (e.g. IRepository&lt;Customer&gt;),
    ///     return a JSON array of the concrete type-argument display strings.
    ///     Returns null for non-generic or unconstructed interfaces.
    /// </summary>
    private static string? GetTypeArgumentsJson(INamedTypeSymbol iface)
    {
        if (!iface.IsGenericType || iface.TypeArguments.IsEmpty)
            return null;

        // Skip if the interface is the unconstructed generic definition itself
        if (SymbolEqualityComparer.Default.Equals(iface, iface.ConstructedFrom))
            return null;

        var args = iface.TypeArguments.Select(a => a.ToDisplayString()).ToArray();
        return JsonSerializer.Serialize(args);
    }
}