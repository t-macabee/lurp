// Purpose: one case-insensitive containment rule shared by C# scans and SQLite queries.
// Owns: OrdinalIgnoreCase substring predicate and deterministic SQLite function registration.
// Must not contain: Roslyn dependency.

using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal static class TextCaseMatching
{
    internal const string SqlFunctionName = "lurp_contains_ignore_case";

    internal static bool ContainsIgnoreCase(string? text, string? query)
    {
        if (text is null || query is null)
            return false;
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Register(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.CreateFunction<string?, string?, bool>(
            SqlFunctionName,
            ContainsIgnoreCase,
            isDeterministic: true);
    }
}
