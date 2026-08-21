using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lurp.Workspace;

public readonly record struct WorkspaceId
{
    private WorkspaceId(string gitRoot, string solutionPath, string value)
    {
        GitRoot = gitRoot;
        SolutionPath = solutionPath;
        Value = value;
    }

    public string GitRoot { get; }

    public string SolutionPath { get; }

    public string Value { get; }

    public static WorkspaceId Create(string gitRoot, string solutionPath)
    {
        return Create(gitRoot, solutionPath, false);
    }

    /// <summary>
    ///     Build a <see cref="WorkspaceId" />. When <paramref name="solutionPathIsRootRelative" />
    ///     is true the solution path is already relative to <paramref name="gitRoot" /> and is
    ///     preserved as-is (forward-slashed) instead of being re-absolutized, which would
    ///     otherwise re-prefix a foreign-platform root with the current directory.
    /// </summary>
    public static WorkspaceId Create(string gitRoot, string solutionPath, bool solutionPathIsRootRelative)
    {
        var root = Normalise(gitRoot).TrimEnd('/');
        var sln = solutionPathIsRootRelative
            ? PathNormalizer.ToForwardSlash(solutionPath)
            : Normalise(solutionPath);

        var relative = sln.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? sln[(root.Length + 1)..]
            : sln;

        var value = $"workspace://{root}/{relative}";
        return new WorkspaceId(root, sln, value);
    }

    public override string ToString()
    {
        return Value;
    }

    private static string Normalise(string path)
    {
        return PathNormalizer.NormalizeForStorage(path);
    }
}

public readonly record struct SnapshotId(Guid Value)
{
    public static SnapshotId Parse(string value)
    {
        return new SnapshotId(Guid.Parse(value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Derives a snapshot id deterministically from a canonical identity
    ///     payload: SHA-256 the payload and take a stable 16-byte portion as the
    ///     GUID. Identical payloads produce identical ids; the existing
    ///     <see cref="Parse" /> and <see cref="ToString" /> formats are unchanged.
    /// </summary>
    public static SnapshotId CreateDeterministic(byte[] canonicalPayload)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        var hash = SHA256.HashData(canonicalPayload);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, guidBytes.Length);
        return new SnapshotId(new Guid(guidBytes));
    }

    public override string ToString()
    {
        return Value.ToString("N", CultureInfo.InvariantCulture);
    }
}

/// <summary>
///     The canonical identity input from which a deterministic
///     <see cref="SnapshotId" /> is derived. Every field participates in the hash;
///     fields are serialized ordinally sorted and length-prefixed so that
///     identical indexed state always yields an identical id.
/// </summary>
public sealed record SnapshotIdentityInput(
    WorkspaceId WorkspaceId,
    IReadOnlyDictionary<string, string> DocumentHashes,
    IReadOnlyDictionary<string, string> TargetFrameworks,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> ProjectGraph,
    IReadOnlyDictionary<string, ImmutableArray<string>> MetadataReferenceIdentities,
    IReadOnlyDictionary<string, string> CompilationOptionsFingerprints,
    string SdkVersion,
    string CompilerVersion,
    string ExtractorVersion,
    IReadOnlySet<string> SkipAdapters)
{
    public static SnapshotIdentityInput FromWorkspace(WorkspaceInfo workspace, IReadOnlySet<string>? skipAdapters)
    {
        return new SnapshotIdentityInput(
            workspace.Id,
            workspace.Documents.ToDictionary(
                kvp => kvp.Key.RelativePath,
                kvp => kvp.Value.Hash,
                StringComparer.Ordinal),
            new Dictionary<string, string>(workspace.TargetFrameworks, StringComparer.Ordinal),
            workspace.ProjectGraph.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyCollection<string>)kvp.Value,
                StringComparer.Ordinal),
            new Dictionary<string, ImmutableArray<string>>(workspace.MetadataReferenceIdentities, StringComparer.Ordinal),
            new Dictionary<string, string>(workspace.CompilationOptionsFingerprints, StringComparer.Ordinal),
            workspace.SdkVersion,
            workspace.CompilerVersion.ToString(),
            workspace.ExtractorVersion,
            skipAdapters ?? new HashSet<string>(StringComparer.Ordinal));
    }
}

public static class SnapshotIdentity
{
    public static SnapshotId Create(WorkspaceInfo workspace, IReadOnlySet<string>? skipAdapters)
    {
        return Create(SnapshotIdentityInput.FromWorkspace(workspace, skipAdapters));
    }

    public static SnapshotId Create(SnapshotIdentityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return SnapshotId.CreateDeterministic(BuildPayload(input));
    }

    private static byte[] BuildPayload(SnapshotIdentityInput input)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, false);

        // Fixed field names plus length-prefixed values (BinaryWriter writes a
        // 7-bit encoded length before each string), so the serialization is
        // unambiguous regardless of value content. Every collection is
        // enumerated in ordinal order.
        WriteField(writer, "workspace", input.WorkspaceId.Value);
        WriteField(writer, "sdkVersion", input.SdkVersion);
        WriteField(writer, "compilerVersion", input.CompilerVersion);
        WriteField(writer, "extractorVersion", input.ExtractorVersion);

        writer.Write("targetFrameworks");
        writer.Write(input.TargetFrameworks.Count);
        foreach (var kvp in input.TargetFrameworks.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            WriteField(writer, "project", kvp.Key);
            WriteField(writer, "targetFramework", kvp.Value);
        }

        writer.Write("projectGraph");
        writer.Write(input.ProjectGraph.Count);
        foreach (var kvp in input.ProjectGraph.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            WriteField(writer, "project", kvp.Key);
            writer.Write("references");
            writer.Write(kvp.Value.Count);
            foreach (var reference in kvp.Value.OrderBy(r => r, StringComparer.Ordinal))
                writer.Write(reference);
        }

        writer.Write("metadataReferences");
        writer.Write(input.MetadataReferenceIdentities.Count);
        foreach (var kvp in input.MetadataReferenceIdentities.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            WriteField(writer, "project", kvp.Key);
            writer.Write("identities");
            writer.Write(kvp.Value.Length);
            foreach (var identity in kvp.Value)
                writer.Write(identity);
        }

        writer.Write("compilationOptions");
        writer.Write(input.CompilationOptionsFingerprints.Count);
        foreach (var kvp in input.CompilationOptionsFingerprints.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            WriteField(writer, "project", kvp.Key);
            WriteField(writer, "fingerprint", kvp.Value);
        }

        writer.Write("documents");
        writer.Write(input.DocumentHashes.Count);
        foreach (var kvp in input.DocumentHashes.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            WriteField(writer, "path", kvp.Key);
            WriteField(writer, "hash", kvp.Value);
        }

        writer.Write("skippedAdapters");
        writer.Write(input.SkipAdapters.Count);
        foreach (var adapter in input.SkipAdapters.OrderBy(a => a, StringComparer.Ordinal))
            writer.Write(adapter);

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteField(BinaryWriter writer, string name, string value)
    {
        writer.Write(name);
        writer.Write(value);
    }
}

public readonly record struct DocumentId
{
    public DocumentId(string relativePath)
    {
        RelativePath = PathNormalizer.ToForwardSlash(relativePath ?? "");
    }

    public string RelativePath { get; }

    public override string ToString()
    {
        return RelativePath;
    }
}

public readonly record struct DocumentVersionId
{
    public DocumentVersionId(string documentPath, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        DocumentPath = PathNormalizer.ToForwardSlash(documentPath ?? "");
        Hash = hash;
    }

    public DocumentVersionId(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        DocumentPath = "";
        Hash = hash;
    }

    public string DocumentPath { get; }

    public string Hash { get; }

    public static DocumentVersionId Compute(DocumentId documentId, byte[] data)
    {
        var hash = SHA256.HashData(data);
        return new DocumentVersionId(documentId.RelativePath, Hex(hash));
    }

    public override string ToString()
    {
        return $"{DocumentPath}:{Hash}";
    }

    private static string Hex(byte[] bytes)
    {
        return Convert.ToHexStringLower(bytes);
    }
}