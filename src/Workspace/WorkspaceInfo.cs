using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Lurp.Workspace;

public sealed class WorkspaceInfo
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private const string UnknownValue = "unknown";

    public WorkspaceId Id { get; }

    public IReadOnlyDictionary<DocumentId, DocumentVersionId> Documents { get; }

    public IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> DocumentContents { get; }

    public string SdkVersion { get; }

    public Version CompilerVersion { get; }

    public IReadOnlyDictionary<string, string> TargetFrameworks { get; }

    public IReadOnlyDictionary<string, ImmutableHashSet<string>> ProjectGraph { get; }

    public IReadOnlyDictionary<string, ImmutableArray<string>> MetadataReferenceIdentities { get; }

    public IReadOnlyDictionary<string, string> CompilationOptionsFingerprints { get; }

    public string IndexerVersion { get; }

    public string ExtractorVersion { get; }

    public IReadOnlySet<DocumentId> GeneratedDocuments { get; }

    public WorkspaceInfo(Solution solution, string gitRoot)
    {
        Id = WorkspaceId.Create(gitRoot, solution.FilePath ?? "");

        var (documents, contents, generatedDocs) = BuildDocumentMap(solution, gitRoot);
        Documents = documents;
        DocumentContents = contents;
        GeneratedDocuments = generatedDocs;

        SdkVersion = QuerySdkVersion();

        CompilerVersion = typeof(CSharpCompilation).Assembly.GetName().Version
                          ?? new Version(0, 0);

        TargetFrameworks = BuildTargetFrameworkMap(solution, gitRoot);

        ProjectGraph = BuildProjectGraph(solution);

        MetadataReferenceIdentities = BuildMetadataReferenceIdentities(solution);

        CompilationOptionsFingerprints = BuildCompilationOptionsFingerprints(solution);

        IndexerVersion = VersionConstants.ToolVersion;
        ExtractorVersion = VersionConstants.ExtractorVersion;
    }

    private static (Dictionary<DocumentId, DocumentVersionId> Hashes, Dictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> Contents,
                    IReadOnlySet<DocumentId> GeneratedDocuments)
        BuildDocumentMap(Solution solution, string gitRoot)
    {
        var map = new Dictionary<DocumentId, DocumentVersionId>();
        var contentMap = new Dictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>();
        var generatedDocs = new HashSet<DocumentId>(DocumentIdComparer.Instance);
        var normalizedRoot = PathNormalizer.NormalizeRoot(gitRoot);

        var gitIgnore = GitIgnoreMatcher.Load(normalizedRoot);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;

                var relPath = GetRelativePath(document.FilePath, normalizedRoot);

                // Skip build output directories and .gitignore-matched paths
                if (IsBuildOutputPath(relPath) || gitIgnore.IsIgnored(relPath))
                    continue;

                var docId = new DocumentId(relPath);

                var rawBytes = File.ReadAllBytes(document.FilePath);
                var normalized = NormalizeSourceBytes(relPath, rawBytes);
                var hash = DocumentVersionId.Compute(docId, normalized);
                var lineStarts = ComputeLineStarts(normalized);

                map[docId] = hash;
                contentMap[docId] = (normalized, "utf-8", lineStarts);

                if (IsGeneratedDocument(normalized, relPath))
                {
                    generatedDocs.Add(docId);
                }
            }
        }

        return (map, contentMap, generatedDocs);
    }

    private static bool IsBuildOutputPath(string relPath)
    {
        var normalized = PathNormalizer.ToForwardSlash(relPath);

        if (normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsGeneratedDocument(byte[] bytes, string relPath)
    {
        if (IsGeneratedPath(relPath))
            return true;

        if (IsGeneratedHeader(bytes))
            return true;

        return false;
    }

    private static bool IsGeneratedPath(string relPath)
    {
        var normalized = PathNormalizer.ToForwardSlash(relPath);
        return EdgeLocationResolver.IsGeneratedFilePath(normalized);
    }

    private static bool IsGeneratedHeader(byte[] bytes)
    {
        if (bytes.Length == 0)
            return false;

        var headerLength = Math.Min(512, bytes.Length);
        var headerText = Utf8NoBom.GetString(bytes, 0, headerLength);

        if (headerText.Contains("<auto-generated>", StringComparison.OrdinalIgnoreCase) ||
            headerText.Contains("[GeneratedCode(", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>Exposed for <see cref="WorkspaceFreshness.CheckFreshnessCheap"/>, which re-hashes a
    /// touched file without loading a full Roslyn workspace and must normalize bytes identically.</summary>
    internal static byte[] NormalizeSourceBytesForFreshnessCheck(string relativePath, byte[] rawBytes)
        => NormalizeSourceBytes(relativePath, rawBytes);

    private static byte[] NormalizeSourceBytes(string relativePath, byte[] rawBytes)
    {
        if (rawBytes.Length >= 3
            && rawBytes[0] == 0xEF
            && rawBytes[1] == 0xBB
            && rawBytes[2] == 0xBF)
        {
            var stripped = rawBytes.AsSpan(3);
            var text = Utf8NoBom.GetString(stripped);
            return Utf8NoBom.GetBytes(text);
        }

        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE)
        {
            try
            {
                var text = Utf16Le.GetString(rawBytes, 2, rawBytes.Length - 2);
                return Utf8NoBom.GetBytes(text);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid UTF-16 LE byte sequence in '{relativePath}'.", ex);
            }
        }

        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFE && rawBytes[1] == 0xFF)
        {
            try
            {
                var text = Utf16Be.GetString(rawBytes, 2, rawBytes.Length - 2);
                return Utf8NoBom.GetBytes(text);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid UTF-16 BE byte sequence in '{relativePath}'.", ex);
            }
        }

        try
        {
            var text = Utf8NoBom.GetString(rawBytes);
            return Utf8NoBom.GetBytes(text);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Invalid UTF-8 byte sequence in '{relativePath}'.", ex);
        }
    }

    private static string ComputeLineStarts(byte[] bytes)
    {
        var offsets = new List<int> { 0 };
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                if (i + 1 < bytes.Length)
                    offsets.Add(i + 1);
            }
        }
        return JsonSerializer.Serialize(offsets);
    }

    private static string GetRelativePath(string fullPath, string normalizedRoot)
        => PathNormalizer.ToGitRelativeFromNormalizedRoot(fullPath, normalizedRoot);

    private sealed class DocumentIdComparer : IEqualityComparer<DocumentId>
    {
        public static readonly DocumentIdComparer Instance = new();

        public bool Equals(DocumentId x, DocumentId y)
        {
            return x.RelativePath == y.RelativePath;
        }

        public int GetHashCode(DocumentId obj)
        {
            return obj.RelativePath?.GetHashCode() ?? 0;
        }
    }

    private static string QuerySdkVersion()
    {
        try
        {
            var instances = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions { DiscoveryTypes = DiscoveryType.DotNetSdk });

            return instances
                .OrderByDescending(i => i.Version)
                .FirstOrDefault()?.Version.ToString()
                ?? UnknownValue;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: Failed to detect MSBuild SDK version: {ex.Message}");
            return UnknownValue;
        }
    }

    private static string? ParseTfmFromProjectFile(string projectFilePath)
    {
        var doc = XDocument.Load(projectFilePath);
        var root = doc.Root;
        if (root == null) return null;

        XNamespace ns = root.GetDefaultNamespace();

        return root
            .Elements(ns + "PropertyGroup")
            .SelectMany(pg => pg.Elements(ns + "TargetFramework"))
            .Select(e => e.Value.Trim())
            .FirstOrDefault()
            ?? root
                .Elements(ns + "PropertyGroup")
                .SelectMany(pg => pg.Elements(ns + "TargetFrameworks"))
                .Select(e => e.Value.Trim())
                .FirstOrDefault();
    }

    private static string? FindTfmInDirectoryBuildProps(string projectFilePath, string repoRoot)
    {
        var dir = Path.GetDirectoryName(projectFilePath);
        if (dir == null) return null;

        var normalizedRoot = PathNormalizer.NormalizeRoot(repoRoot);

        while (dir.Length >= normalizedRoot.Length)
        {
            var propsPath = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(propsPath))
            {
                try
                {
                    var tfm = ParseTfmFromProjectFile(propsPath);
                    if (tfm != null)
                        return tfm;
                }
                catch
                {
                }
            }

            if (string.Equals(dir, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                break;

            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir)
                break;
            dir = parent;
        }

        return null;
    }

    private static Dictionary<string, string> BuildTargetFrameworkMap(Solution solution, string gitRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null || !File.Exists(project.FilePath))
            {
                map[project.Name] = UnknownValue;
                continue;
            }

            try
            {
                var tf = ParseTfmFromProjectFile(project.FilePath);

                tf ??= FindTfmInDirectoryBuildProps(project.FilePath, gitRoot);

                map[project.Name] = tf ?? UnknownValue;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: Failed to parse project file '{project.FilePath}' for TargetFramework: {ex.Message}");
                map[project.Name] = UnknownValue;
            }
        }

        return new Dictionary<string, string>(map, StringComparer.Ordinal);
    }

    private static Dictionary<string, ImmutableHashSet<string>> BuildProjectGraph(Solution solution)
    {
        var projectIdToName = solution.Projects.ToDictionary(p => p.Id, p => p.Name);

        var graph = new Dictionary<string, ImmutableHashSet<string>>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pr in project.ProjectReferences)
            {
                if (projectIdToName.TryGetValue(pr.ProjectId, out var name))
                    refs.Add(name);
            }

            graph[project.Name] = refs.ToImmutableHashSet(StringComparer.Ordinal);
        }

        return graph;
    }

    /// <summary>
    /// Per-project identity tokens for metadata references (NuGet packages,
    /// framework assemblies). For <see cref="PortableExecutableReference"/> the
    /// token is derived from the assembly metadata itself via
    /// <see cref="AssemblyName.GetAssemblyName(string)"/> (name + version +
    /// culture + public key token), so it is machine-independent and stable
    /// across path moves; when the header cannot be read the token falls back
    /// to file name + length + last-write time. Duplicates are removed and the
    /// identities are sorted so the result is canonical per project.
    /// </summary>
    private static Dictionary<string, ImmutableArray<string>> BuildMetadataReferenceIdentities(Solution solution)
    {
        var map = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            var identities = new List<string>();
            foreach (var reference in project.MetadataReferences)
            {
                if (reference is PortableExecutableReference pe)
                    identities.Add(TryGetAssemblyIdentity(pe.FilePath) ?? FallbackReferenceToken(pe.FilePath));
                else
                    identities.Add(reference.Display ?? "unknown");
            }

            map[project.Name] = identities
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToImmutableArray();
        }

        return map;
    }

    private static string? TryGetAssemblyIdentity(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            // Fold a content hash into the identity so a same-version patch
            // (different bytes, unchanged assembly full name) yields a different
            // identity — otherwise it is invisible to SnapshotId and freshness.
            // The hash is content-derived and therefore deterministic, unlike
            // file mtime, preserving the "identical indexed state => identical
            // id" invariant.
            var fullName = AssemblyName.GetAssemblyName(filePath).FullName;
            return $"{fullName}|sha256={ComputeFileHash(filePath)}";
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FallbackReferenceToken(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return "unknown";

        try
        {
            var info = new FileInfo(filePath);
            return $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return filePath;
        }
    }

    /// <summary>
    /// Per-project fingerprint of the compilation inputs that shape which code
    /// is compiled: optimization level, unsafe allowance, nullable context,
    /// platform, language version, and the sorted preprocessor symbols (which
    /// drive <c>#if</c>-guarded dead code). Serialized as a deterministic
    /// string so any change yields a different fingerprint.
    /// </summary>
    private static Dictionary<string, string> BuildCompilationOptionsFingerprints(Solution solution)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            var parts = new List<string>();

            var compilationOptions = project.CompilationOptions as CSharpCompilationOptions;
            if (compilationOptions != null)
            {
                parts.Add($"optimization={compilationOptions.OptimizationLevel}");
                parts.Add($"allowUnsafe={compilationOptions.AllowUnsafe}");
                parts.Add($"nullable={compilationOptions.NullableContextOptions}");
                parts.Add($"platform={compilationOptions.Platform}");
            }

            var parseOptions = project.ParseOptions as CSharpParseOptions;
            if (parseOptions != null)
            {
                parts.Add($"langVersion={parseOptions.LanguageVersion}");
                parts.Add($"defines={string.Join(",", parseOptions.PreprocessorSymbolNames.OrderBy(x => x, StringComparer.Ordinal))}");
            }

            map[project.Name] = string.Join("|", parts);
        }

        return map;
    }
}

