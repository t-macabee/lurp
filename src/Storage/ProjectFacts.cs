using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

/// <summary>
///     Snapshot-scoped, per-project facts read once from the <c>projects</c> table:
///     the assembly name (the identity a candidate and a binding record carry) and the
///     parsed set of simple reference names per assembly. One row exists per indexed
///     project (or per target framework, when the pipeline records more than one), so
///     the reference set is the union over every row that shares an assembly name.
/// </summary>
internal sealed class ProjectFacts
{
    /// <summary>
    ///     Simple names of assemblies whose presence marks a project as a test project.
    ///     A project is a test project only because it references one of these, never
    ///     because of its name or its solution. Order is the order the constant lists;
    ///     membership is compared ordinally.
    /// </summary>
    private static readonly string[] TestFrameworkSimpleNames =
    [
        "Microsoft.VisualStudio.TestPlatform.ObjectModel",
        "Microsoft.Testing.Platform",
        "xunit.core",
        "xunit.v3.core",
        "nunit.framework",
        "Microsoft.VisualStudio.TestPlatform.TestFramework"
    ];

    private readonly Dictionary<string, string> _assemblyNameByProjectName;
    private readonly Dictionary<string, HashSet<string>> _referenceSimpleNamesByAssemblyName;

    private ProjectFacts(
        Dictionary<string, string> assemblyNameByProjectName,
        Dictionary<string, HashSet<string>> referenceSimpleNamesByAssemblyName)
    {
        _assemblyNameByProjectName = assemblyNameByProjectName;
        _referenceSimpleNamesByAssemblyName = referenceSimpleNamesByAssemblyName;
    }

    public static ProjectFacts Load(SqliteConnection connection, string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var assemblyNameByProjectName = new Dictionary<string, string>(StringComparer.Ordinal);
        var referenceSimpleNamesByAssemblyName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, metadata_reference_identities, assembly_name FROM projects WHERE snapshot_id = @snapshotId;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var projectName = reader.GetString(0);
            var identitiesJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            // A NULL column means a snapshot indexed before migration 034; such a snapshot
            // predates assembly-name storage, so the project name is the assembly name.
            var assemblyName = reader.IsDBNull(2) || string.IsNullOrEmpty(reader.GetString(2))
                ? projectName
                : reader.GetString(2);

            assemblyNameByProjectName[projectName] = assemblyName;
            if (!referenceSimpleNamesByAssemblyName.TryGetValue(assemblyName, out var simpleNames))
            {
                simpleNames = new HashSet<string>(StringComparer.Ordinal);
                referenceSimpleNamesByAssemblyName[assemblyName] = simpleNames;
            }

            if (string.IsNullOrEmpty(identitiesJson))
                continue;

            string[]? identities;
            try
            {
                identities = JsonSerializer.Deserialize<string[]>(identitiesJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse metadata_reference_identities for project '{projectName}'.", ex);
            }

            if (identities == null)
                continue;

            foreach (var identity in identities)
                simpleNames.Add(MetadataReferenceIdentity.SimpleName(identity));
        }

        return new ProjectFacts(assemblyNameByProjectName, referenceSimpleNamesByAssemblyName);
    }

    /// <summary>
    ///     True when any row sharing <paramref name="assemblyName" /> references
    ///     <paramref name="referenceSimpleName" />. NULL reference identities mean "no
    ///     references", so the answer is false for every name.
    /// </summary>
    public bool HasReference(string assemblyName, string referenceSimpleName)
    {
        return _referenceSimpleNamesByAssemblyName.TryGetValue(assemblyName, out var simpleNames)
               && simpleNames.Contains(referenceSimpleName);
    }

    /// <summary>
    ///     F14: a project is a test project when it references a known test framework
    ///     assembly. There is no name rule and no solution-name rule.
    /// </summary>
    public bool IsTestProject(string assemblyName)
    {
        if (!_referenceSimpleNamesByAssemblyName.TryGetValue(assemblyName, out var simpleNames))
            return false;

        foreach (var name in TestFrameworkSimpleNames)
            if (simpleNames.Contains(name))
                return true;

        return false;
    }

    /// <summary>
    ///     The assembly name stored for <paramref name="projectName" />, or
    ///     <paramref name="projectName" /> when the project is unknown or the column is
    ///     NULL (a snapshot indexed before migration 034).
    /// </summary>
    public string ToAssemblyName(string projectName)
    {
        return _assemblyNameByProjectName.TryGetValue(projectName, out var assemblyName)
            ? assemblyName
            : projectName;
    }
}

/// <summary>
///     Parses the simple name out of a stored metadata-reference identity. The stored
///     form is an assembly full name plus an optional <c>|sha256=…</c> suffix (see
///     <c>WorkspaceInfo.TryGetAssemblyIdentity</c>), so the simple name is everything
///     before the first <c>|</c>, then before the first <c>,</c>.
/// </summary>
internal static class MetadataReferenceIdentity
{
    public static string SimpleName(string identity)
    {
        var assemblyPart = identity.Split('|')[0];
        return assemblyPart.Split(',')[0].Trim();
    }
}
