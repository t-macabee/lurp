using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeclarationWriteStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var decl in declarations) SaveOne(command, snapshotId, decl);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void SaveOne(SqliteCommand command, string snapshotId, SymbolDeclaration decl)
    {
        command.CommandText = """
            INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind)
            VALUES (@symbolId, @docCommentId, @assemblyIdentity, @kind)
            ON CONFLICT(symbol_id) DO UPDATE SET
                doc_comment_id = excluded.doc_comment_id,
                assembly_identity = excluded.assembly_identity,
                kind = excluded.kind;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
        command.Parameters.AddWithValue("@docCommentId", decl.SymbolId.DocCommentId);
        command.Parameters.AddWithValue("@assemblyIdentity", decl.SymbolId.AssemblyIdentity);
        command.Parameters.AddWithValue("@kind", decl.Kind.ToString());
        command.ExecuteNonQuery();

        var fqn = decl.SymbolId.FullyQualifiedName;
        var bareFqn = fqn is null ? null : FqnForms.RemoveTypeParameterLists(fqn);

        command.CommandText = """
            INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn, bare_fqn, metadata_json)
            VALUES (@snapshotId, @symbolId, @fqn, @bareFqn, @metadataJson)
            ON CONFLICT(snapshot_id, symbol_id) DO UPDATE SET
                fqn = excluded.fqn,
                bare_fqn = excluded.bare_fqn,
                metadata_json = excluded.metadata_json;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
        command.Parameters.AddWithValue("@fqn", (object?)fqn ?? DBNull.Value);
        command.Parameters.AddWithValue("@bareFqn", (object?)bareFqn ?? DBNull.Value);
        command.Parameters.AddWithValue("@metadataJson", (object?)decl.MetadataJson ?? DBNull.Value);
        command.ExecuteNonQuery();

        command.CommandText = """
            INSERT OR IGNORE INTO symbol_target_frameworks (snapshot_id, symbol_id, tfm)
            VALUES (@snapshotId, @symbolId, @tfm);
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
        command.Parameters.AddWithValue("@tfm", decl.TargetFramework);
        command.ExecuteNonQuery();

        command.CommandText = """
            INSERT INTO declarations (symbol_id, document_version_id,full_start, full_end,signature_start, signature_end,body_start, body_end,name_start, name_end,is_partial,is_generated,generator_identity) VALUES (@symbolId, @documentVersionId,@fullStart, @fullEnd,@signatureStart, @signatureEnd,@bodyStart, @bodyEnd,@nameStart, @nameEnd,@isPartial,@isGenerated,@generatorIdentity)
            ON CONFLICT(symbol_id, document_version_id, COALESCE(full_start, -1), COALESCE(full_end, -1))
            DO UPDATE SET
                signature_start   = excluded.signature_start,
                signature_end     = excluded.signature_end,
                body_start        = excluded.body_start,
                body_end          = excluded.body_end,
                name_start        = excluded.name_start,
                name_end          = excluded.name_end,
                is_partial        = excluded.is_partial,
                is_generated      = excluded.is_generated,
                generator_identity = excluded.generator_identity;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
        command.Parameters.AddWithValue("@documentVersionId", decl.DocumentVersionId);
        command.Parameters.AddWithValue("@fullStart", (object?)decl.FullSpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@fullEnd", (object?)decl.FullSpan.End ?? DBNull.Value);
        command.Parameters.AddWithValue("@signatureStart", (object?)decl.SignatureSpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@signatureEnd", (object?)decl.SignatureSpan.End ?? DBNull.Value);
        command.Parameters.AddWithValue("@bodyStart", (object?)decl.BodySpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@bodyEnd", (object?)decl.BodySpan.End ?? DBNull.Value);
        command.Parameters.AddWithValue("@nameStart", (object?)decl.NameSpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@nameEnd", (object?)decl.NameSpan.End ?? DBNull.Value);
        command.Parameters.AddWithValue("@isPartial", decl.IsPartial ? 1 : 0);
        command.Parameters.AddWithValue("@isGenerated", decl.IsGenerated ? 1 : 0);
        command.Parameters.AddWithValue("@generatorIdentity", (object?)decl.GeneratorIdentity ?? DBNull.Value);
        command.ExecuteNonQuery();

        command.CommandText = """
            INSERT OR IGNORE INTO snapshot_declarations (snapshot_id, declaration_id)
            SELECT @snapshotId, declaration_id
            FROM declarations
            WHERE symbol_id = @symbolId AND document_version_id = @documentVersionId
              AND COALESCE(full_start, -1) = COALESCE(@fullStart, -1)
              AND COALESCE(full_end, -1) = COALESCE(@fullEnd, -1);
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
        command.Parameters.AddWithValue("@documentVersionId", decl.DocumentVersionId);
        command.Parameters.AddWithValue("@fullStart", (object?)decl.FullSpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@fullEnd", (object?)decl.FullSpan.End ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}