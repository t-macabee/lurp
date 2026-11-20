using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Recomputes <c>document_versions.line_starts</c> using the Roslyn / C# line-break rule
///     (CR, LF, CRLF, NEL, LS, PS and trailing empty line) for every stored document version
///     that has content, temporarily dropping and recreating <c>trg_document_versions_immutable</c>
///     (D76, D186.3).
/// </summary>
public sealed class Migration_048_RecomputeLineStarts : IMigration
{
    public int Version => 48;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER IF EXISTS trg_document_versions_immutable;";
        command.ExecuteNonQuery();

        var ids = new List<string>();
        command.CommandText = "SELECT document_version_id FROM document_versions WHERE content IS NOT NULL;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                ids.Add(reader.GetString(0));
        }

        if (ids.Count > 0)
        {
            using var selectCommand = connection.CreateCommand();
            selectCommand.CommandText = "SELECT content FROM document_versions WHERE document_version_id = @id;";
            var selectIdParam = selectCommand.Parameters.Add("@id", SqliteType.Text);
            selectCommand.Prepare();

            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = "UPDATE document_versions SET line_starts = @lineStarts WHERE document_version_id = @id;";
            var lineStartsParam = updateCommand.Parameters.Add("@lineStarts", SqliteType.Text);
            var updateIdParam = updateCommand.Parameters.Add("@id", SqliteType.Text);
            updateCommand.Prepare();

            foreach (var id in ids)
            {
                selectIdParam.Value = id;
                var bytes = (byte[]?)selectCommand.ExecuteScalar();
                if (bytes is null)
                    continue;

                lineStartsParam.Value = LineBreaks.ComputeLineStartsJson(bytes);
                updateIdParam.Value = id;
                updateCommand.ExecuteNonQuery();
            }
        }

        command.CommandText = """
            CREATE TRIGGER trg_document_versions_immutable
            BEFORE UPDATE OF document_version_id, document_id, content_hash, content, encoding, byte_count, line_starts ON document_versions
            BEGIN
                SELECT RAISE(ABORT, 'document_versions is immutable');
            END;
            """;
        command.ExecuteNonQuery();
    }
}
