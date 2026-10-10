using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     B16: <see cref="SqlIdList.Bind" /> binds a whole id list as one JSON array parameter, so a
///     list is never limited by SQLite's maximum of 32,766 bound parameters per statement.
/// </summary>
public sealed class SqlIdListTests
{
    [Fact]
    public void Bind_FortyThousandValues_MatchesEveryValueInOneStatement()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t(id TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        var values = new List<string>(40_000);
        for (var i = 0; i < 40_000; i++)
            values.Add($"id-{i}");

        using (var transaction = connection.BeginTransaction())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO t(id) VALUES (@id);";
            var value = insert.Parameters.Add("@id", SqliteType.Text);
            foreach (var id in values)
            {
                value.Value = id;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM t WHERE id IN (SELECT value FROM json_each(@ids));";
        SqlIdList.Bind(command, "@ids", values);

        Assert.Equal(40_000L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Bind_ValuesWithJsonAndLikeCharacters_RoundTripExactly()
    {
        var values = new[]
        {
            "a\"b",
            @"c\d",
            "e|f",
            "g%h_i",
            "Ünï",
            "global::Ns.C`1<N1,N2>"
        };

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM json_each(@ids);";
        SqlIdList.Bind(command, "@ids", values);

        var roundTripped = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            roundTripped.Add(reader.GetString(0));

        Assert.Equal(values, roundTripped);
    }
}
