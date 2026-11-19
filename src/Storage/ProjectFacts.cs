using System.Text.Json;
using Lurp.Shared;
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
    private readonly Dictionary<string, string> _assemblyNameByProjectName;
    private readonly HashSet<string> _snapshotAssemblyNames;
    private readonly Dictionary<string, HashSet<string>> _referenceSimpleNamesByAssemblyName;
    private readonly Dictionary<string, HashSet<string>> _internalsVisibleToByAssemblyName;

    private ProjectFacts(
        Dictionary<string, string> assemblyNameByProjectName,
        Dictionary<string, HashSet<string>> referenceSimpleNamesByAssemblyName,
        Dictionary<string, HashSet<string>> internalsVisibleToByAssemblyName)
    {
        _assemblyNameByProjectName = assemblyNameByProjectName;
        // .NET matches InternalsVisibleTo target assembly names case-insensitively.
        _snapshotAssemblyNames = new HashSet<string>(assemblyNameByProjectName.Values, StringComparer.OrdinalIgnoreCase);
        _referenceSimpleNamesByAssemblyName = referenceSimpleNamesByAssemblyName;
        _internalsVisibleToByAssemblyName = internalsVisibleToByAssemblyName;
    }

    public static ProjectFacts Load(SqliteConnection connection, string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var assemblyNameByProjectName = new Dictionary<string, string>(StringComparer.Ordinal);
        var referenceSimpleNamesByAssemblyName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var internalsVisibleToByAssemblyName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, metadata_reference_identities, assembly_name, internals_visible_to FROM projects WHERE snapshot_id = @snapshotId;";
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
            var internalsVisibleToJson = reader.IsDBNull(3) ? null : reader.GetString(3);

            assemblyNameByProjectName[projectName] = assemblyName;
            if (!referenceSimpleNamesByAssemblyName.TryGetValue(assemblyName, out var simpleNames))
            {
                simpleNames = new HashSet<string>(StringComparer.Ordinal);
                referenceSimpleNamesByAssemblyName[assemblyName] = simpleNames;
            }

            if (!string.IsNullOrEmpty(identitiesJson))
            {
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

                if (identities != null)
                {
                    foreach (var identity in identities)
                        simpleNames.Add(MetadataReferenceIdentity.SimpleName(identity));
                }
            }

            if (!string.IsNullOrEmpty(internalsVisibleToJson))
            {
                string[]? targets;
                try
                {
                    targets = JsonSerializer.Deserialize<string[]>(internalsVisibleToJson);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to parse internals_visible_to for project '{projectName}'.", ex);
                }

                if (targets is { Length: > 0 })
                {
                    if (!internalsVisibleToByAssemblyName.TryGetValue(assemblyName, out var targetSet))
                    {
                        targetSet = new HashSet<string>(StringComparer.Ordinal);
                        internalsVisibleToByAssemblyName[assemblyName] = targetSet;
                    }

                    foreach (var target in targets)
                        targetSet.Add(target);
                }
            }
        }

        return new ProjectFacts(assemblyNameByProjectName, referenceSimpleNamesByAssemblyName, internalsVisibleToByAssemblyName);
    }

    /// <summary>
    ///     D48 / D171.3: true when any row of <paramref name="assemblyName" /> grants
    ///     <c>InternalsVisibleTo</c> to the wildcard <c>*</c> or to an assembly that is
    ///     not an assembly of this snapshot.
    /// </summary>
    public bool GrantsInternalsOutsideSnapshot(string assemblyName)
    {
        if (!_internalsVisibleToByAssemblyName.TryGetValue(assemblyName, out var targets))
            return false;

        foreach (var target in targets)
        {
            if (target == "*" || !_snapshotAssemblyNames.Contains(target))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     F14: a project is a test project when it references a known test framework
    ///     assembly. There is no name rule and no solution-name rule.
    /// </summary>
    public bool IsTestProject(string assemblyName)
    {
        return _referenceSimpleNamesByAssemblyName.TryGetValue(assemblyName, out var simpleNames) &&
               TestFrameworks.IsTestProjectReferenceSet(simpleNames);
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
