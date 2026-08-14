// Purpose: shared FTS query helpers for the search stores.
// Owns: query escaping/identification logic duplicated across SearchSourceStore
// and SearchSymbolStore.
// Must not contain: any Roslyn dependency.

namespace Lurp.Storage;

internal static class SearchUtils
{
    // FTS5's query grammar (distinct from the unicode61 tokenizer) rejects unquoted
    // punctuation such as '.', so a raw user query like "CourseService.CreateAsync"
    // throws a SqliteException before any result is returned. Wrapping the query as a
    // quoted FTS5 phrase literal makes it a literal-text match instead of parsing it
    // through FTS5's operator grammar. Only the double quote needs escaping (doubled)
    // inside a phrase literal.
    internal static string ToFtsPhrase(string query) => "\"" + query.Replace("\"", "\"\"") + "\"";

    internal static bool IsPlainIdentifierQuery(string query)
    {
        var hasIdentifierChar = false;
        foreach (var c in query)
        {
            if (char.IsLetterOrDigit(c))
                hasIdentifierChar = true;
            else if (c != '.' && c != '_')
                return false;
        }
        return hasIdentifierChar;
    }
}
