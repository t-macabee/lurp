using Lurp.Storage.Migrations;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Lurp.Storage;

public class MigrationRunner
{
    private readonly string _dbPath;

    public MigrationRunner(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
    }

    public static IReadOnlyList<int> MigrationVersions => [.. GetMigrations().Select(static migration => migration.Version)];

    public void RunMigrations()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString);
        connection.Open();

        // WAL is a database property, so setting it once here (index start is the
        // only migration entry point) lets readers run concurrently with the writer
        // instead of contending on the rollback journal. busy_timeout keeps a
        // concurrent writer waiting instead of failing with SQLITE_BUSY.
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "PRAGMA busy_timeout=30000;";
            setup.ExecuteNonQuery();
        }

        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            journal.ExecuteNonQuery();
        }

        var currentVersion = GetCurrentSchemaVersion(connection);
        var migrations = GetMigrations().OrderBy(m => m.Version).ToList();

        foreach (var migration in migrations)
        {
            if (migration.Version <= currentVersion)
                continue;

            using var transaction = connection.BeginTransaction();
            try
            {
                migration.Up(connection);
                UpdateSchemaVersion(connection, migration.Version, migration.GetType().Name, transaction);
                transaction.Commit();
                currentVersion = migration.Version;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public int GetCurrentSchemaVersion()
    {
        // Reading the schema marker must also work when index.db is read-only
        // (e.g. a shared or archived database), so this probe never opens for
        // write. A missing file simply has no schema.
        if (!File.Exists(_dbPath))
            return 0;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout=30000;";
            timeout.ExecuteNonQuery();
        }

        return GetCurrentSchemaVersion(connection);
    }

    private static int GetCurrentSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_metadata';";
        var tableExists = command.ExecuteScalar();
        if (tableExists == null || tableExists == DBNull.Value)
            return 0;

        command.CommandText = "SELECT version FROM schema_metadata ORDER BY version DESC LIMIT 1;";
        var result = command.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static void UpdateSchemaVersion(SqliteConnection connection, int version, string migrationId, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                VALUES (@version, @appliedAtUtc, @migrationId);
                """;
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@appliedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@migrationId", migrationId);
        command.ExecuteNonQuery();
    }

    private static List<IMigration> GetMigrations()
    {
        return
        [
            new Migration_001_InitialSchema(),
            new Migration_002_AddLineStarts(),
            new Migration_003_SymbolTables(),
            new Migration_004_FtsSearch(),
            new Migration_005_A5OperationalTables(),
            new Migration_006_ExpandEdges(),
            new Migration_007_SemanticChanges(),
            new Migration_008_GeneratedCodeAwareness(),
            new Migration_009_PerSnapshotSymbolData(),
            new Migration_010_AddLastChangedSnapshotId(),
            new Migration_011_SnapshotStatus(),
            new Migration_012_SnapshotTimings(),
            new Migration_013_ClearDefaultProjects(),
            new Migration_014_AddCrossGeneratedFlag(),
            new Migration_015_FixSnapshotStatusDefault(),
            new Migration_016_RecomposeDocumentVersionId(),
            new Migration_017_UniqueEdgeRelations(),
            new Migration_018_AddSkippedAdapters(),
            new Migration_019_SchemaHardening(),
            new Migration_020_EdgeTypeArguments(),
            new Migration_021_GraphNodeMembership(),
            new Migration_022_BindingIncompleteness(),
            new Migration_023_FailedSnapshotState(),
            new Migration_024_FailedSnapshotTombstone(),
            new Migration_025_CallReceiverConstraints(),
            new Migration_026_AnnotationDocumentPath(),
            new Migration_027_ProjectCompilationInputs(),
            new Migration_028_AnnotationDocumentIndex(),
            new Migration_029_AddSnapshotPins(),
            new Migration_030_EdgeSnapshotTargetIndex(),
            new Migration_031_SymbolTargetFrameworks(),
            new Migration_032_ProjectDocumentPaths(),
            new Migration_033_SnapshotMetrics(),
            new Migration_034_ProjectAssemblyName(),
            new Migration_035_DeclarationIndexes()
        ];
    }
}