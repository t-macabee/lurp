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
    private readonly Dictionary<string, HashSet<string>> _assembliesByProjectDirectory;

    private ProjectFacts(
        Dictionary<string, string> assemblyNameByProjectName,
        Dictionary<string, HashSet<string>> referenceSimpleNamesByAssemblyName,
        Dictionary<string, HashSet<string>> internalsVisibleToByAssemblyName,
        Dictionary<string, HashSet<string>> assembliesByProjectDirectory)
    {
        _assemblyNameByProjectName = assemblyNameByProjectName;
        _snapshotAssemblyNames = new HashSet<string>(assemblyNameByProjectName.Values, AssemblyNameComparer);
        _referenceSimpleNamesByAssemblyName = referenceSimpleNamesByAssemblyName;
        _internalsVisibleToByAssemblyName = internalsVisibleToByAssemblyName;
        _assembliesByProjectDirectory = assembliesByProjectDirectory;
    }

    /// <summary>
    ///     .NET compares assembly simple names case-insensitively. Every lookup by assembly
    ///     name, here and in the capsule's project scope, uses this comparer.
    /// </summary>
    internal static readonly StringComparer AssemblyNameComparer = StringComparer.OrdinalIgnoreCase;

    internal static string GetAssemblyName(ProjectRow project) =>
        string.IsNullOrEmpty(project.AssemblyName) ? project.Name : project.AssemblyName;

    public static ProjectFacts Load(SqliteConnection connection, string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var projects = new List<ProjectRow>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, metadata_reference_identities, assembly_name, internals_visible_to, project_path FROM projects WHERE snapshot_id = @snapshotId;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            projects.Add(new ProjectRow
            {
                Name = reader.GetString(0),
                MetadataReferenceIdentitiesJson = reader.IsDBNull(1) ? null : reader.GetString(1),
                AssemblyName = reader.IsDBNull(2) ? null : reader.GetString(2),
                InternalsVisibleToJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                ProjectPath = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }

        return FromRows(projects);
    }

    internal static ProjectFacts FromRows(IReadOnlyList<ProjectRow> projects)
    {
        var assemblyNameByProjectName = new Dictionary<string, string>(StringComparer.Ordinal);
        var referenceSimpleNamesByAssemblyName = new Dictionary<string, HashSet<string>>(AssemblyNameComparer);
        var internalsVisibleToByAssemblyName = new Dictionary<string, HashSet<string>>(AssemblyNameComparer);
        var assembliesByProjectDirectory = new Dictionary<string, HashSet<string>>(PathNormalizer.PathComparer);

        foreach (var project in projects)
        {
            var projectName = project.Name;
            var assemblyName = GetAssemblyName(project);
            var identitiesJson = project.MetadataReferenceIdentitiesJson;
            var internalsVisibleToJson = project.InternalsVisibleToJson;

            assemblyNameByProjectName[projectName] = assemblyName;
            if (!string.IsNullOrEmpty(project.ProjectPath))
            {
                var path = PathNormalizer.ToForwardSlash(project.ProjectPath);
                var directory = path[..(path.LastIndexOf('/') + 1)];
                if (!assembliesByProjectDirectory.TryGetValue(directory, out var assemblies))
                    assembliesByProjectDirectory[directory] = assemblies = new HashSet<string>(AssemblyNameComparer);
                assemblies.Add(assemblyName);
            }

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

        return new ProjectFacts(assemblyNameByProjectName, referenceSimpleNamesByAssemblyName,
            internalsVisibleToByAssemblyName, assembliesByProjectDirectory);
    }

    internal bool IsOwnedDocument(string assemblyName, string relativePath) =>
        GetDocumentOwners(relativePath).Contains(assemblyName, AssemblyNameComparer);

    internal IReadOnlyCollection<string> GetDocumentOwners(string relativePath)
    {
        var path = PathNormalizer.ToForwardSlash(relativePath);
        // Walk from the file's directory toward the root. A slash at the end of each
        // prefix keeps similarly named sibling directories distinct.
        for (var end = path.LastIndexOf('/'); end >= 0; end = path.LastIndexOf('/', end - 1))
        {
            if (_assembliesByProjectDirectory.TryGetValue(path[..(end + 1)], out var owners))
                return owners;
            if (end == 0)
                break;
        }

        if (!path.StartsWith("../", StringComparison.Ordinal) &&
            _assembliesByProjectDirectory.TryGetValue(string.Empty, out var rootOwners))
            return rootOwners;

        return [];
    }

    internal bool HasAssembly(string assemblyName) => _snapshotAssemblyNames.Contains(assemblyName);

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
