using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Persists the per-project compilation inputs that participate in the
/// deterministic snapshot identity: metadata reference identities (NuGet
/// package version bumps, added/removed packages) and the compilation options
/// fingerprint (define constants, nullability, optimization — the inputs that
/// change which <c>#if</c>-guarded code is compiled). Both columns are
/// nullable: a null value on a pre-027 snapshot means "unknown", not
/// "different", and freshness comparators must skip rather than report a
/// mismatch.
/// </summary>
public sealed class Migration_027_ProjectCompilationInputs : IMigration
{
    public int Version => 27;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE projects ADD COLUMN metadata_reference_identities TEXT;";
        command.ExecuteNonQuery();

        command.CommandText = "ALTER TABLE projects ADD COLUMN compilation_options_fingerprint TEXT;";
        command.ExecuteNonQuery();
    }
}
