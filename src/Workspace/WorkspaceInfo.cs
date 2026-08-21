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
    private const string UnknownValue = "unknown";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, false, true);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(true, false, true);

    // Used only for the generated-file header sniff, which just scans a 512-byte
    // prefix for plain-ASCII markers. Replacement (not throwing) fallback is safe
    // there: the markers can't be produced by a replacement char, and the decoded
    // string is discarded, never stored. See IsGeneratedHeader.
    private static readonly Encoding Utf8Lenient = new UTF8Encoding(false, false);

    public WorkspaceInfo(Solution solution, string gitRoot, IOutputSink? output = null)
    {
        var sink = output ?? ConsoleOutputSink.Instance;
        Id = WorkspaceId.Create(gitRoot, solution.FilePath ?? "");

        var (documents, contents, generatedDocs) = BuildDocumentMap(solution, gitRoot, sink);
        Documents = documents;
        DocumentContents = contents;
        GeneratedDocuments = generatedDocs;

        SdkVersion = QuerySdkVersion(sink);

        CompilerVersion = typeof(CSharpCompilation).Assembly.GetName().Version
                          ?? new Version(0, 0);

        TargetFrameworks = BuildTargetFrameworkMap(solution, gitRoot, sink);

        ProjectGraph = BuildProjectGraph(solution);

        MetadataReferenceIdentities = BuildMetadataReferenceIdentities(solution);

        CompilationOptionsFingerprints = BuildCompilationOptionsFingerprints(solution);

        ExtractorVersion = VersionConstants.ExtractorVersion;
    }

    public WorkspaceId Id { get; }

    public IReadOnlyDictionary<DocumentId, DocumentVersionId> Documents { get; }

    public IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> DocumentContents { get; }

    public string SdkVersion { get; }

    public Version CompilerVersion { get; }

    public IReadOnlyDictionary<string, string> TargetFrameworks { get; }

    public IReadOnlyDictionary<string, ImmutableHashSet<string>> ProjectGraph { get; }

    public IReadOnlyDictionary<string, ImmutableArray<string>> MetadataReferenceIdentities { get; }

    public IReadOnlyDictionary<string, string> CompilationOptionsFingerprints { get; }

    public string ExtractorVersion { get; }

    public IReadOnlySet<DocumentId> GeneratedDocuments { get; }

    private static (Dictionary<DocumentId, DocumentVersionId> Hashes, Dictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> Contents,
        IReadOnlySet<DocumentId> GeneratedDocuments)
        BuildDocumentMap(Solution solution, string gitRoot, IOutputSink sink)
    {
        var map = new Dictionary<DocumentId, DocumentVersionId>();
        var contentMap = new Dictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>();
        var generatedDocs = new HashSet<DocumentId>(DocumentIdComparer.Instance);
        var normalizedRoot = PathNormalizer.NormalizeRoot(gitRoot);

        var gitIgnore = GitIgnoreMatcher.Load(normalizedRoot);
        var skipped = new List<string>();

        foreach (var project in solution.Projects)
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;

                var relPath = GetRelativePath(document.FilePath, normalizedRoot);

                // Skip build output directories and .gitignore-matched paths
                if (IsBuildOutputPath(relPath) || gitIgnore.IsIgnored(relPath))
                    continue;

                var docId = new DocumentId(relPath);

                var rawBytes = File.ReadAllBytes(document.FilePath);
                if (!TryNormalizeSourceBytes(rawBytes, out var normalized))
                {
                    // Not valid UTF-8/UTF-16 text (binary content, wrong encoding,
                    // truncated multi-byte sequence). Indexing the whole workspace
                    // must not die on one bad file: drop it from the document set
                    // exactly as a gitignored/build-output file would be, and warn
                    // once at the end. Every downstream consumer of Documents /
                    // DocumentContents already tolerates a missing DocumentId.
                    skipped.Add(relPath);
                    continue;
                }

                var hash = DocumentVersionId.Compute(docId, normalized);
                var lineStarts = ComputeLineStarts(normalized);

                map[docId] = hash;
                contentMap[docId] = (normalized, "utf-8", lineStarts);

                if (IsGeneratedDocument(normalized, relPath)) generatedDocs.Add(docId);
            }

        if (skipped.Count > 0)
            sink.WriteErrorLine(
                $"WARNING: Skipped {skipped.Count} document(s) that are not valid UTF-8/UTF-16 text (excluded from indexing): "
                + string.Join(", ", skipped.Take(10)) + (skipped.Count > 10 ? $", … +{skipped.Count - 10} more" : ""));

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

        // Lenient/replacement decode: this only scans for plain-ASCII markers, so
        // a replacement character can never manufacture a false match, and can
        // only suppress one in the (already-broken) case of a marker straddling
        // the 512-byte cutoff mid-character. The decoded string is discarded.
        var headerLength = Math.Min(512, bytes.Length);
        var headerText = Utf8Lenient.GetString(bytes, 0, headerLength);

        if (headerText.Contains("<auto-generated>", StringComparison.OrdinalIgnoreCase) ||
            headerText.Contains("[GeneratedCode(", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    ///     Exposed for <see cref="WorkspaceFreshness.CheckFreshnessCheap" />, which re-hashes a
    ///     touched file without loading a full Roslyn workspace and must normalize bytes identically.
    ///     Returns <see langword="false" /> (rather than throwing) when <paramref name="rawBytes" />
    ///     is not valid UTF-8/UTF-16 text, so a file that has gone from source to binary (or to a
    ///     different encoding) since it was indexed reads as "changed", not as a crash.
    /// </summary>
    internal static bool TryNormalizeSourceBytesForFreshnessCheck(byte[] rawBytes, out byte[] normalized)
    {
        return TryNormalizeSourceBytes(rawBytes, out normalized);
    }

    /// <summary>
    ///     Normalizes raw file bytes to BOM-free UTF-8, or returns <see langword="false" /> when
    ///     the bytes aren't valid UTF-8/UTF-16 text (binary content, wrong encoding, a truncated
    ///     multi-byte sequence, etc.). Never throws on bad content — callers decide what "not
    ///     text" means for them (skip-and-warn during indexing, "stale" during a freshness check).
    /// </summary>
    private static bool TryNormalizeSourceBytes(byte[] rawBytes, out byte[] normalized)
    {
        if (rawBytes is [0xEF, 0xBB, 0xBF, ..])
        {
            var stripped = rawBytes.AsSpan(3);
            return TryDecodeAndReencode(Utf8NoBom, stripped, out normalized);
        }

        if (rawBytes is [0xFF, 0xFE, ..])
            return TryDecodeAndReencode(Utf16Le, rawBytes.AsSpan(2), out normalized);

        if (rawBytes is [0xFE, 0xFF, ..])
            return TryDecodeAndReencode(Utf16Be, rawBytes.AsSpan(2), out normalized);

        return TryDecodeAndReencode(Utf8NoBom, rawBytes, out normalized);
    }

    private static bool TryDecodeAndReencode(Encoding sourceEncoding, ReadOnlySpan<byte> bytes, out byte[] normalized)
    {
        try
        {
            var text = sourceEncoding.GetString(bytes);
            normalized = Utf8NoBom.GetBytes(text);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = [];
            return false;
        }
    }

    private static string ComputeLineStarts(byte[] bytes)
    {
        var offsets = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n')
                if (i + 1 < bytes.Length)
                    offsets.Add(i + 1);

        return JsonSerializer.Serialize(offsets);
    }

    private static string GetRelativePath(string fullPath, string normalizedRoot)
    {
        return PathNormalizer.ToGitRelativeFromNormalizedRoot(fullPath, normalizedRoot);
    }

    private static string QuerySdkVersion(IOutputSink? output = null)
    {
        var sink = output ?? ConsoleOutputSink.Instance;
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
            sink.WriteErrorLine($"WARNING: Failed to detect MSBuild SDK version: {ex.Message}");
            return UnknownValue;
        }
    }

    private static string? ParseTfmFromProjectFile(string projectFilePath)
    {
        var doc = XDocument.Load(projectFilePath);
        var root = doc.Root;
        if (root == null) return null;

        var ns = root.GetDefaultNamespace();

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
                try
                {
                    var tfm = ParseTfmFromProjectFile(propsPath);
                    if (tfm != null)
                        return tfm;
                }
                catch
                {
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

    private static Dictionary<string, string> BuildTargetFrameworkMap(Solution solution, string gitRoot, IOutputSink? output = null)
    {
        var sink = output ?? ConsoleOutputSink.Instance;
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
                sink.WriteErrorLine($"WARNING: Failed to parse project file '{project.FilePath}' for TargetFramework: {ex.Message}");
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
                if (projectIdToName.TryGetValue(pr.ProjectId, out var name))
                    refs.Add(name);

            graph[project.Name] = refs.ToImmutableHashSet(StringComparer.Ordinal);
        }

        return graph;
    }

    /// <summary>
    ///     Per-project identity tokens for metadata references (NuGet packages,
    ///     framework assemblies). For <see cref="PortableExecutableReference" /> the
    ///     token is derived from the assembly metadata itself via
    ///     <see cref="AssemblyName.GetAssemblyName(string)" /> (name + version +
    ///     culture + public key token), so it is machine-independent and stable
    ///     across path moves; when the header cannot be read the token falls back
    ///     to file name + length + last-write time. Duplicates are removed and the
    ///     identities are sorted so the result is canonical per project.
    /// </summary>
    private static Dictionary<string, ImmutableArray<string>> BuildMetadataReferenceIdentities(Solution solution)
    {
        var map = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            var identities = new List<string>();
            foreach (var reference in project.MetadataReferences)
                if (reference is PortableExecutableReference pe)
                    identities.Add(TryGetAssemblyIdentity(pe.FilePath) ?? FallbackReferenceToken(pe.FilePath));
                else
                    identities.Add(reference.Display ?? "unknown");

            map[project.Name] = [.. identities
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)];
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
    ///     Per-project fingerprint of the compilation inputs that shape which code
    ///     is compiled: optimization level, unsafe allowance, nullable context,
    ///     platform, language version, and the sorted preprocessor symbols (which
    ///     drive <c>#if</c>-guarded dead code). Serialized as a deterministic
    ///     string so any change yields a different fingerprint.
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
}