using System.Text;

namespace Lurp.Storage;

internal static class SymbolSearchNames
{
    internal static (string Name, string Container) FromDocCommentId(string docCommentId, string fqn)
    {
        // Strip method parameters and generic arity from the stored identity.
        var qualified = docCommentId.Length > 2 && docCommentId[1] == ':'
            ? docCommentId.AsSpan(2)
            : FqnForms.RemoveTypeParameterLists(fqn).AsSpan();
        var parameters = qualified.IndexOf('(');
        if (parameters >= 0)
            qualified = qualified[..parameters];
        var dot = qualified.LastIndexOf('.');
        var container = dot >= 0 ? qualified[..dot] : ReadOnlySpan<char>.Empty;
        var name = qualified[(dot + 1)..];
        if (name is "#ctor" or "#cctor")
        {
            name = container[(container.LastIndexOf('.') + 1)..];
        }
        else if (name.LastIndexOf('#') is var explicitInterface && explicitInterface >= 0)
        {
            name = name[(explicitInterface + 1)..];
        }

        var arity = name.IndexOf('`');
        return ((arity >= 0 ? name[..arity] : name).ToString(), container.ToString());
    }

    internal static string Tokenize(string name)
    {
        var tokens = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (current == '_')
            {
                tokens.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(current) &&
                (char.IsLower(name[i - 1]) ||
                 (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]))))
                tokens.Append(' ');
            tokens.Append(current);
        }
        return tokens.ToString();
    }

    internal static int KindRank(string kind, bool isAccessor = false) => kind switch
    {
        nameof(IndexedSymbolKind.Type) => 0,
        nameof(IndexedSymbolKind.Method) when !isAccessor => 1,
        nameof(IndexedSymbolKind.Property) => 2,
        nameof(IndexedSymbolKind.Event) => 3,
        nameof(IndexedSymbolKind.Field) => 4,
        nameof(IndexedSymbolKind.Method) => 5,
        nameof(IndexedSymbolKind.Namespace) => 6,
        _ => 7
    };
}
