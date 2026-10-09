using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Shared helpers of the query-plan tests: the probe project and the plan reader.
/// </summary>
internal static class QueryPlanFixture
{
    // With statistics the planner scans tables that have a handful of rows, so the fixture
    // needs enough declarations and edges for the index plans to win; 200 chained methods
    // give the plans of a real database (probe 2026-10-08).
    internal static string ChainedMethodsSource(string namespaceName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {namespaceName};");
        sb.AppendLine();
        sb.AppendLine("internal class Probe");
        sb.AppendLine("{");
        sb.AppendLine("    internal void A() { }");
        sb.AppendLine("    internal void B() { }");
        sb.AppendLine("    internal void C() { }");
        sb.AppendLine("    internal void F0() { }");
        for (var i = 1; i < 200; i++)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    internal void F{i}() {{ F{i - 1}(); }}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Returns the detail column of EXPLAIN QUERY PLAN for <paramref name="sql" />.</summary>
    internal static List<string> Explain(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object> parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        var details = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return details;
    }
}
