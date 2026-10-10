using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

/// <summary>
///     Binds a list of values as one JSON array parameter. SQL reads it with
///     <c>IN (SELECT value FROM json_each(@name))</c>. One parameter holds any number of
///     values, so a list is never limited by SQLite's maximum of 32,766 bound parameters
///     per statement, and every query stays one statement (B16).
/// </summary>
internal static class SqlIdList
{
    internal static void Bind(SqliteCommand command, string parameterName, IEnumerable<string> values) =>
        command.Parameters.AddWithValue(parameterName, JsonSerializer.Serialize(values));
}
