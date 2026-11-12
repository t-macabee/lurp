using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Lurp.Workspace;

internal sealed class ReflectionExtractionContext : ExtractionContextBase
{
    internal ReflectionExtractionContext(ProjectExtractionInputs inputs)
        : base(inputs)
    {
        foreach (var type in GetAllNamedTypes())
        {
            Add(_typesBySimpleName, type.MetadataName, type);
            Add(_typesByFullName, ExtractionUtils.FullMetadataName(type), type);
            foreach (var member in type.GetMembers())
                Add(_membersByName, member.MetadataName, member);
        }
    }

    // D21: names match ordinally; a call that passes IgnoreCase reads them ignoring case.
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesBySimpleName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesByFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ISymbol>> _membersByName = new(StringComparer.Ordinal);

    /// <summary>The declared members (nested types included) with the metadata name <paramref name="name" />.</summary>
    internal IEnumerable<ISymbol> MembersNamed(string name, bool ignoreCase) => Lookup(_membersByName, name, ignoreCase);

    /// <summary>The declared types with the simple metadata name <paramref name="name" /> (<c>Inner`1</c>).</summary>
    internal IEnumerable<INamedTypeSymbol> TypesBySimpleName(string name, bool ignoreCase) => Lookup(_typesBySimpleName, name, ignoreCase);

    /// <summary>
    ///     D144: the declared types with the namespace-qualified metadata name <paramref name="name" />
    ///     (<c>N.Outer+Inner`1</c>), the form that <c>Type.GetType</c>, <c>Assembly.GetType</c> and
    ///     <c>Activator.CreateInstance</c> read.
    /// </summary>
    internal IEnumerable<INamedTypeSymbol> TypesByFullName(string name, bool ignoreCase) => Lookup(_typesByFullName, name, ignoreCase);

    /// <summary>
    ///     D144: the type part of a runtime type name: the text before the assembly qualifier (the
    ///     first comma outside brackets) and before a generic argument list or an array suffix.
    /// </summary>
    internal static string TypeNameOf(string text)
    {
        var end = text.Length;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '[')
                depth++;
            else if (text[i] == ']')
                depth--;
            else if (text[i] == ',' && depth == 0)
            {
                end = i;
                break;
            }
        }

        var typeName = text[..end];
        var bracket = typeName.IndexOf('[', StringComparison.Ordinal);
        return (bracket < 0 ? typeName : typeName[..bracket]).Trim();
    }

    private static IEnumerable<T> Lookup<T>(Dictionary<string, List<T>> index, string name, bool ignoreCase)
    {
        if (!ignoreCase)
            return index.TryGetValue(name, out var exact) ? exact : [];
        return index.Where(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => entry.Value);
    }

    private static void Add<T>(Dictionary<string, List<T>> index, string name, T symbol)
    {
        if (!index.TryGetValue(name, out var list))
        {
            list = [];
            index.Add(name, list);
        }

        list.Add(symbol);
    }

    private Dictionary<SyntaxTree, List<(TextSpan Span, ISymbol Owner)>>? _rootsByTree;

    /// <summary>
    ///     D20: the member that runs <paramref name="node" />, by the one owner rule of
    ///     <see cref="ExecutableRoots" />: the owner of the smallest root that contains the node.
    ///     Null outside every root (for example an assembly attribute).
    /// </summary>
    internal ISymbol? OwnerOf(SyntaxNode node)
    {
        _rootsByTree ??= BuildRootIndex();
        if (!_rootsByTree.TryGetValue(node.SyntaxTree, out var roots))
            return null;

        ISymbol? owner = null;
        var smallest = int.MaxValue;
        foreach (var (span, candidate) in roots)
        {
            if (span.Length < smallest && span.Contains(node.Span))
            {
                owner = candidate;
                smallest = span.Length;
            }
        }

        return owner;
    }

    internal string? OwnerIdOf(SyntaxNode node)
    {
        return OwnerOf(node) is { } owner ? MakeSymbolId(owner) : null;
    }

    private Dictionary<SyntaxTree, List<(TextSpan Span, ISymbol Owner)>> BuildRootIndex()
    {
        var index = new Dictionary<SyntaxTree, List<(TextSpan Span, ISymbol Owner)>>();
        foreach (var (owner, node) in ExecutableRoots.Bodies(this))
            Add(node.SyntaxTree, node.Span, owner);
        foreach (var (owner, attribute) in ExecutableRoots.Attributes(this))
        {
            if (attribute.ApplicationSyntaxReference is { } application && IsInScope(application.SyntaxTree))
                Add(application.SyntaxTree, application.Span, owner);
        }

        return index;

        void Add(SyntaxTree tree, TextSpan span, ISymbol owner)
        {
            if (!index.TryGetValue(tree, out var roots))
            {
                roots = [];
                index.Add(tree, roots);
            }

            roots.Add((span, owner));
        }
    }
}
