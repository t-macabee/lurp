using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotLifecycleStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    private const string WorkspaceUpsertSql = @"
            INSERT INTO workspaces (workspace_id, git_root, solution_path)
            VALUES (@workspaceId, @gitRoot, @solutionPath)
            ON CONFLICT(workspace_id) DO UPDATE SET
                git_root = excluded.git_root,
                solution_path = excluded.solution_path;
        ";

    internal void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = WorkspaceUpsertSql;
        command.Parameters.AddWithValue("@workspaceId", id);
        command.Parameters.AddWithValue("@gitRoot", gitRoot);
        command.Parameters.AddWithValue("@solutionPath", solutionPath);
        command.ExecuteNonQuery();
    }

    internal void SaveSnapshot(SnapshotRow manifest)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            UpsertWorkspace(manifest, transaction);
            InsertSnapshotHeader(manifest, transaction);
            InsertProjectGraph(manifest.SnapshotId, manifest.Projects, transaction);
            InsertDocumentsAndBindings(manifest.SnapshotId, manifest.Documents, transaction);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void UpsertWorkspace(SnapshotRow snapshot, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = WorkspaceUpsertSql;
        command.Parameters.AddWithValue("@workspaceId", snapshot.WorkspaceId);
        command.Parameters.AddWithValue("@gitRoot", snapshot.GitRoot);
        command.Parameters.AddWithValue("@solutionPath", snapshot.SolutionPath);
        command.ExecuteNonQuery();
    }

    private void InsertSnapshotHeader(SnapshotRow snapshot, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = @"
            INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, compiler_version,database_schema_version, output_schema_version, extractor_version,tool_version, previous_snapshot_id, skipped_adapters, status) VALUES (@snapshotId, @workspaceId, @builtAtUtc, @sdkVersion, @compilerVersion,@databaseSchemaVersion, @outputSchemaVersion, @extractorVersion,@toolVersion, @previousSnapshotId, @skippedAdapters, @status);
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshot.SnapshotId);
        command.Parameters.AddWithValue("@workspaceId", snapshot.WorkspaceId);
        command.Parameters.AddWithValue("@builtAtUtc", snapshot.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@sdkVersion", snapshot.SdkVersion ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@compilerVersion", snapshot.CompilerVersion ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@databaseSchemaVersion", (object)snapshot.DatabaseSchemaVersion);
        command.Parameters.AddWithValue("@outputSchemaVersion", (object)snapshot.OutputSchemaVersion);
        command.Parameters.AddWithValue("@extractorVersion", (object?)snapshot.ExtractorVersion ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@toolVersion", (object?)snapshot.ToolVersion ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@previousSnapshotId", (object?)snapshot.PreviousSnapshotId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@skippedAdapters", string.Join(",", snapshot.SkippedAdapters));
        command.Parameters.AddWithValue("@status", SnapshotStatusValues.InProgress);
        command.ExecuteNonQuery();
    }

    private void InsertProjectGraph(string snapshotId, IReadOnlyList<ProjectRow> projects, SqliteTransaction transaction)
    {
        if (projects.Count == 0)
            return;

        using var projectCommand = _connection.CreateCommand();
        projectCommand.Transaction = transaction;
        projectCommand.CommandText = @"
            INSERT INTO projects (snapshot_id, name, target_framework)
            VALUES (@snapshotId, @name, @targetFramework);
            SELECT last_insert_rowid();
        ";

        using var refCommand = _connection.CreateCommand();
        refCommand.Transaction = transaction;
        refCommand.CommandText = @"
            INSERT INTO project_references (project_id, referenced_project_name)
            VALUES (@projectId, @referencedProjectName);
        ";

        foreach (var project in projects)
        {
            projectCommand.Parameters.Clear();
            projectCommand.Parameters.AddWithValue("@snapshotId", snapshotId);
            projectCommand.Parameters.AddWithValue("@name", project.Name);
            projectCommand.Parameters.AddWithValue("@targetFramework", (object?)project.TargetFramework ?? (object)DBNull.Value);
            var projectId = projectCommand.ExecuteScalar();

            if (project.References.Count > 0 && projectId != null)
            {
                foreach (var reference in project.References)
                {
                    refCommand.Parameters.Clear();
                    refCommand.Parameters.AddWithValue("@projectId", (long)projectId);
                    refCommand.Parameters.AddWithValue("@referencedProjectName", reference);
                    refCommand.ExecuteNonQuery();
                }
            }
        }
    }

    private void InsertDocumentsAndBindings(string snapshotId, IReadOnlyList<DocumentVersion> documents, SqliteTransaction transaction)
    {
        using var docCommand = _connection.CreateCommand();
        docCommand.Transaction = transaction;
        docCommand.CommandText = @"
            INSERT INTO documents (document_id, relative_path, last_changed_snapshot_id)
            VALUES (@documentId, @relativePath, @snapshotId)
            ON CONFLICT(document_id) DO UPDATE SET
                last_changed_snapshot_id = excluded.last_changed_snapshot_id;
        ";

        using var versionCommand = _connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = @"
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash, content, encoding, byte_count, line_starts) VALUES (@documentVersionId, @documentId, @contentHash, @content, @encoding, @byteCount, @lineStarts);
        ";

        using var bindingCommand = _connection.CreateCommand();
        bindingCommand.Transaction = transaction;
        bindingCommand.CommandText = @"
            INSERT INTO snapshot_documents (snapshot_id, document_version_id)
            VALUES (@snapshotId, @documentVersionId);
        ";

        foreach (var doc in documents)
        {
            var documentVersionId = doc.DocumentId + ":" + doc.ContentHash;

            docCommand.Parameters.Clear();
            docCommand.Parameters.AddWithValue("@documentId", doc.DocumentId);
            docCommand.Parameters.AddWithValue("@relativePath", doc.FilePath);
            docCommand.Parameters.AddWithValue("@snapshotId", snapshotId);
            docCommand.ExecuteNonQuery();

            versionCommand.Parameters.Clear();
            versionCommand.Parameters.AddWithValue("@documentVersionId", documentVersionId);
            versionCommand.Parameters.AddWithValue("@documentId", doc.DocumentId);
            versionCommand.Parameters.AddWithValue("@contentHash", doc.ContentHash);
            versionCommand.Parameters.AddWithValue("@content", (object?)(doc.Content) ?? (object)DBNull.Value);
            versionCommand.Parameters.AddWithValue("@encoding", string.IsNullOrEmpty(doc.Encoding) ? (object)DBNull.Value : (object)doc.Encoding);
            versionCommand.Parameters.AddWithValue("@byteCount", doc.ByteCount > 0 ? (object)doc.ByteCount : (object)DBNull.Value);
            versionCommand.Parameters.AddWithValue("@lineStarts", string.IsNullOrEmpty(doc.LineStarts) ? (object)DBNull.Value : (object)doc.LineStarts);
            versionCommand.ExecuteNonQuery();

            bindingCommand.Parameters.Clear();
            bindingCommand.Parameters.AddWithValue("@snapshotId", snapshotId);
            bindingCommand.Parameters.AddWithValue("@documentVersionId", documentVersionId);
            bindingCommand.ExecuteNonQuery();
        }
    }

    internal void MarkSnapshotInProgress(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET status = @status WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@status", SnapshotStatusValues.InProgress);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    internal void MarkSnapshotComplete(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET status = @status WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@status", SnapshotStatusValues.Complete);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    internal void MarkSnapshotFailed(string snapshotId, string reasonCode, string? message)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            UPDATE snapshots
            SET status = @status, failure_reason_code = @reasonCode, failure_message = @message
            WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@status", SnapshotStatusValues.Failed);
        command.Parameters.AddWithValue("@reasonCode", reasonCode);
        command.Parameters.AddWithValue("@message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    internal SnapshotFailureRow? GetLatestSnapshotFailure(string? workspaceId = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = string.IsNullOrEmpty(workspaceId)
            ? @"SELECT snapshot_id, failure_reason_code, failure_message, built_at_utc
                FROM snapshots WHERE status = @status ORDER BY built_at_utc DESC LIMIT 1;"
            : @"SELECT snapshot_id, failure_reason_code, failure_message, built_at_utc
                FROM snapshots WHERE status = @status AND workspace_id = @workspaceId
                ORDER BY built_at_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("@status", SnapshotStatusValues.Failed);
        if (!string.IsNullOrEmpty(workspaceId))
            command.Parameters.AddWithValue("@workspaceId", workspaceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new SnapshotFailureRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? "unknown" : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    internal SnapshotRow? LoadLatestSnapshot(string? workspaceId = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = string.IsNullOrEmpty(workspaceId)
            ? @"
                SELECT s.snapshot_id, s.workspace_id, w.git_root, w.solution_path,
                       s.sdk_version, s.compiler_version, s.built_at_utc,
                       s.database_schema_version, s.output_schema_version,
                       s.extractor_version, s.tool_version, s.previous_snapshot_id,
                       s.skipped_adapters
                FROM snapshots s
                JOIN workspaces w ON w.workspace_id = s.workspace_id
                WHERE s.status = @status
                ORDER BY s.built_at_utc DESC
                LIMIT 1;
            "
            : @"
                SELECT s.snapshot_id, s.workspace_id, w.git_root, w.solution_path,
                       s.sdk_version, s.compiler_version, s.built_at_utc,
                       s.database_schema_version, s.output_schema_version,
                       s.extractor_version, s.tool_version, s.previous_snapshot_id,
                       s.skipped_adapters
                FROM snapshots s
                JOIN workspaces w ON w.workspace_id = s.workspace_id
                WHERE s.workspace_id = @workspaceId
                  AND s.status = @status
                ORDER BY s.built_at_utc DESC
                LIMIT 1;
            ";
        command.Parameters.AddWithValue("@status", SnapshotStatusValues.Complete);
        if (!string.IsNullOrEmpty(workspaceId))
            command.Parameters.AddWithValue("@workspaceId", workspaceId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var row = ReadSnapshotRow(reader);
        var documents = ReadDocumentVersions(row.SnapshotId);
        var projects = LoadProjects(row.SnapshotId);

        return new SnapshotRow
        {
            SnapshotId = row.SnapshotId,
            WorkspaceId = row.WorkspaceId,
            GitRoot = row.GitRoot,
            SolutionPath = row.SolutionPath,
            SdkVersion = row.SdkVersion,
            CompilerVersion = row.CompilerVersion,
            CreatedAtUtc = row.CreatedAtUtc,
            Documents = documents,
            DatabaseSchemaVersion = row.DatabaseSchemaVersion,
            OutputSchemaVersion = row.OutputSchemaVersion,
            ExtractorVersion = row.ExtractorVersion,
            ToolVersion = row.ToolVersion,
            PreviousSnapshotId = row.PreviousSnapshotId,
            Projects = projects,
            SkippedAdapters = row.SkippedAdapters,
        };
    }

    /// <summary>
    /// Loads the completeness-relevant fields of a snapshot (extractor
    /// version, skipped adapters, project target frameworks) without the
    /// expensive per-document read that <see cref="LoadLatestSnapshot"/>
    /// pays for. <see cref="SnapshotRow.Documents"/> is left empty.
    /// </summary>
    internal SnapshotRow? LoadSnapshotMetadata(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT s.snapshot_id, s.workspace_id, w.git_root, w.solution_path,
                   s.sdk_version, s.compiler_version, s.built_at_utc,
                   s.database_schema_version, s.output_schema_version,
                   s.extractor_version, s.tool_version, s.previous_snapshot_id,
                   s.skipped_adapters
            FROM snapshots s
            JOIN workspaces w ON w.workspace_id = s.workspace_id
            WHERE s.snapshot_id = @snapshotId;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var row = ReadSnapshotRow(reader);
        var projects = LoadProjects(snapshotId);

        return new SnapshotRow
        {
            SnapshotId = row.SnapshotId,
            WorkspaceId = row.WorkspaceId,
            GitRoot = row.GitRoot,
            SolutionPath = row.SolutionPath,
            SdkVersion = row.SdkVersion,
            CompilerVersion = row.CompilerVersion,
            CreatedAtUtc = row.CreatedAtUtc,
            DatabaseSchemaVersion = row.DatabaseSchemaVersion,
            OutputSchemaVersion = row.OutputSchemaVersion,
            ExtractorVersion = row.ExtractorVersion,
            ToolVersion = row.ToolVersion,
            PreviousSnapshotId = row.PreviousSnapshotId,
            Projects = projects,
            SkippedAdapters = row.SkippedAdapters,
        };
    }

    private static SnapshotRow ReadSnapshotRow(SqliteDataReader reader)
    {
        var snapshotId = reader.GetString(0);
        var workspaceIdStr = reader.GetString(1);
        var gitRoot = reader.GetString(2);
        var solutionPath = reader.GetString(3);
        var sdkVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
        var compilerVersion = reader.IsDBNull(5) ? null : reader.GetString(5);
        var builtAtUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var databaseSchemaVersion = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
        var outputSchemaVersion = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
        var extractorVersion = reader.IsDBNull(9) ? null : reader.GetString(9);
        var toolVersion = reader.IsDBNull(10) ? null : reader.GetString(10);
        var previousSnapshotId = reader.IsDBNull(11) ? null : reader.GetString(11);
        var skippedAdaptersRaw = reader.IsDBNull(12) ? "" : reader.GetString(12);
        var skippedAdapters = string.IsNullOrEmpty(skippedAdaptersRaw)
            ? new List<string>()
            : skippedAdaptersRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        return new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceIdStr,
            GitRoot = gitRoot,
            SolutionPath = solutionPath,
            SdkVersion = sdkVersion ?? "",
            CompilerVersion = compilerVersion ?? "",
            CreatedAtUtc = builtAtUtc,
            DatabaseSchemaVersion = databaseSchemaVersion,
            OutputSchemaVersion = outputSchemaVersion,
            ExtractorVersion = extractorVersion ?? "",
            ToolVersion = toolVersion ?? "",
            PreviousSnapshotId = previousSnapshotId,
            SkippedAdapters = skippedAdapters,
        };
    }

    private List<DocumentVersion> ReadDocumentVersions(string snapshotId)
    {
        var documents = new List<DocumentVersion>();
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT d.document_id, d.relative_path, dv.content_hash, dv.encoding,
                   dv.line_starts
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lineStarts = reader.IsDBNull(4) ? "" : reader.GetString(4);
            documents.Add(new DocumentVersion
            {
                DocumentId = reader.GetString(0),
                FilePath = reader.GetString(1),
                ContentHash = reader.GetString(2),
                Encoding = reader.IsDBNull(3) ? "" : reader.GetString(3),
                LineStart = lineStarts,
                CreatedAtUtc = DateTime.MinValue,
            });
        }

        return documents;
    }

    private List<ProjectRow> LoadProjects(string snapshotId)
    {
        var projects = new List<ProjectRow>();
        var referencesByProjectId = new Dictionary<long, List<string>>();

        using var refCommand = _connection.CreateCommand();
        refCommand.CommandText = @"
            SELECT pr.project_id, pr.referenced_project_name
            FROM project_references pr
            JOIN projects p ON p.project_id = pr.project_id
            WHERE p.snapshot_id = @snapshotId;
        ";
        refCommand.Parameters.AddWithValue("@snapshotId", snapshotId);
        using (var refReader = refCommand.ExecuteReader())
        {
            while (refReader.Read())
            {
                var projectId = refReader.GetInt64(0);
                var referencedName = refReader.GetString(1);
                if (!referencesByProjectId.TryGetValue(projectId, out var refs))
                {
                    refs = [];
                    referencesByProjectId[projectId] = refs;
                }
                refs.Add(referencedName);
            }
        }

        using var projectCommand = _connection.CreateCommand();
        projectCommand.CommandText = @"
            SELECT project_id, name, target_framework
            FROM projects
            WHERE snapshot_id = @snapshotId;
        ";
        projectCommand.Parameters.AddWithValue("@snapshotId", snapshotId);
        using (var projectReader = projectCommand.ExecuteReader())
        {
            while (projectReader.Read())
            {
                var projectId = projectReader.GetInt64(0);
                projects.Add(new ProjectRow
                {
                    Name = projectReader.GetString(1),
                    TargetFramework = projectReader.IsDBNull(2) ? "" : projectReader.GetString(2),
                    References = referencesByProjectId.TryGetValue(projectId, out var refs) ? refs : [],
                });
            }
        }

        return projects;
    }

    internal string? GetLatestSnapshotId(string? workspaceId = null)
    {
        using var command = _connection.CreateCommand();
        if (!string.IsNullOrEmpty(workspaceId))
        {
            command.CommandText = "SELECT snapshot_id FROM snapshots WHERE workspace_id = @workspaceId AND status = @status ORDER BY built_at_utc DESC LIMIT 1;";
            command.Parameters.AddWithValue("@workspaceId", workspaceId);
        }
        else
        {
            command.CommandText = "SELECT snapshot_id FROM snapshots WHERE status = @status ORDER BY built_at_utc DESC LIMIT 1;";
        }
        command.Parameters.AddWithValue("@status", SnapshotStatusValues.Complete);

        return command.ExecuteScalar() as string;
    }

    internal string? GetSnapshotGitRoot(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT w.git_root
            FROM snapshots s
            JOIN workspaces w ON w.workspace_id = s.workspace_id
            WHERE s.snapshot_id = @snapshotId;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        return command.ExecuteScalar() as string;
    }

    internal string? GetSnapshotStatus(string snapshotId, string workspaceId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT status
            FROM snapshots
            WHERE snapshot_id = @snapshotId
              AND workspace_id = @workspaceId;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@workspaceId", workspaceId);
        return command.ExecuteScalar() as string;
    }

    internal List<string> GetSnapshotIds(string workspaceId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT snapshot_id
            FROM snapshots
            WHERE workspace_id = @workspaceId
              AND status = @status
            ORDER BY built_at_utc;
        ";
        command.Parameters.AddWithValue("@workspaceId", workspaceId);
        command.Parameters.AddWithValue("@status", SnapshotStatusValues.Complete);

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }
}
