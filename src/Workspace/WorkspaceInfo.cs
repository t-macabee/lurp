using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lurp.Workspace;

public sealed class WorkspaceInfo
{
    internal const string UnknownValue = "unknown";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, false, true);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(true, false, true);

    internal static Version CurrentCompilerVersion { get; } = typeof(CSharpCompilation).Assembly.GetName().Version
                                                              ?? new Version(0, 0);

    public WorkspaceInfo(Solution solution, string gitRoot, IOutputSink? output = null)
        : this(solution, gitRoot, output, []) { }

    internal WorkspaceInfo(Solution solution, string gitRoot, IOutputSink? output, IReadOnlyList<string> unloadedProjects)
    {
        var sink = output ?? ConsoleOutputSink.Instance;
        Id = WorkspaceId.Create(gitRoot, solution.FilePath ?? "");

        var (documents, contents, generatedDocs, projectDocuments, excludedDocuments) = BuildDocumentMap(solution, gitRoot, sink);
        Documents = documents;
        DocumentContents = contents;
        GeneratedDocuments = generatedDocs;

        SdkVersion = QuerySdkVersion(sink);

        CompilerVersion = CurrentCompilerVersion;

        TargetFrameworks = BuildTargetFrameworkMap(solution, gitRoot, sink);

        AssemblyNames = BuildAssemblyNameMap(solution);

        ProjectPaths = BuildProjectPathMap(solution, gitRoot);
        UnloadedProjects = unloadedProjects;

        InternalsVisibleTo = BuildInternalsVisibleToMap(solution);

        ProjectDocuments = projectDocuments.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)[.. kvp.Value.OrderBy(static path => path, StringComparer.Ordinal)],
            StringComparer.Ordinal);

        ExcludedDocuments = excludedDocuments.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)[.. kvp.Value.Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal)]);

        var projectIdToName = solution.Projects.ToDictionary(static p => p.Id, static p => p.Name);
        var excludedByName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (projectId, paths) in ExcludedDocuments)
        {
            if (!projectIdToName.TryGetValue(projectId, out var name))
                continue;
            if (!excludedByName.TryGetValue(name, out var list))
                excludedByName[name] = list = [];
            list.AddRange(paths);
        }

        _excludedDocumentsByName = excludedByName.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)[.. kvp.Value.Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal)],
            StringComparer.Ordinal);

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

    /// <summary>
    ///     Project name to <see cref="Project.AssemblyName" />. The assembly name is the
    ///     identity a dead-candidate's <c>assembly_identity</c> and a binding-incompleteness
    ///     record's <c>project_name</c> carry, and it differs from the project name whenever
    ///     <c>AssemblyName</c> is set in the project file (B18).
    /// </summary>
    public IReadOnlyDictionary<string, string> AssemblyNames { get; }

    public IReadOnlyDictionary<string, string> ProjectPaths { get; }

    public IReadOnlyList<string> UnloadedProjects { get; }

    /// <summary>
    ///     D48 / D171: per-project sorted, distinct <c>InternalsVisibleTo</c> target assembly
    ///     simple names (or <c>*</c> when a target cannot be named from syntax). A project with
    ///     no target has no entry.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> InternalsVisibleTo { get; }

    public IReadOnlyDictionary<string, ImmutableHashSet<string>> ProjectGraph { get; }

    public IReadOnlyDictionary<string, ImmutableArray<string>> MetadataReferenceIdentities { get; }

    public IReadOnlyDictionary<string, string> CompilationOptionsFingerprints { get; }

    public string ExtractorVersion { get; }

    public IReadOnlySet<DocumentId> GeneratedDocuments { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectDocuments { get; }

    private readonly Dictionary<string, IReadOnlyList<string>> _excludedDocumentsByName;

    public IReadOnlyDictionary<ProjectId, IReadOnlyList<string>> ExcludedDocuments { get; }

    internal IReadOnlyList<string> GetExcludedDocuments(Project? project, string projectName)
    {
        if (project != null)
            return ExcludedDocuments.TryGetValue(project.Id, out var byId) ? byId : [];

        return _excludedDocumentsByName.TryGetValue(projectName, out var byName) ? byName : [];
    }

    private static (Dictionary<DocumentId, DocumentVersionId> Hashes, Dictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> Contents,
        IReadOnlySet<DocumentId> GeneratedDocuments, Dictionary<string, List<string>> ProjectDocuments, Dictionary<ProjectId, List<string>> ExcludedDocuments)
        BuildDocumentMap(Solution solution, string gitRoot, IOutputSink sink)
    {
        var map = new Dictionary<DocumentId, DocumentVersionId>();
        var contentMap = new Dictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>();
        var generatedDocs = new HashSet<DocumentId>(DocumentIdComparer.Instance);
        var projectDocuments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var excludedDocuments = new Dictionary<ProjectId, List<string>>();
        var normalizedRoot = PathNormalizer.NormalizeRoot(gitRoot);

        var skipped = new List<string>();

        foreach (var project in solution.Projects)
        {
            var documentPaths = new List<string>();
            var excludedPaths = new List<string>();
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;

                var relPath = GetRelativePath(document.FilePath, normalizedRoot);

                // Skip build output directories (bin/ and obj/); record repository-local ones as excluded documents.
                if (BuildOutputPaths.IsBuildOutputPath(relPath))
                {
                    if (!relPath.StartsWith("..", StringComparison.Ordinal))
                        excludedPaths.Add(relPath);
                    continue;
                }

                var docId = new DocumentId(relPath);

                var rawBytes = File.ReadAllBytes(document.FilePath);
                if (!TryNormalizeSourceBytes(rawBytes, out var normalized))
                {
                    // Not valid UTF-8/UTF-16 text (binary content, wrong encoding,
                    // truncated multi-byte sequence). Indexing the whole workspace
                    // must not die on one bad file: drop it from the document set
                    // and warn once at the end. Every downstream consumer of Documents /
                    // DocumentContents already tolerates a missing DocumentId.
                    skipped.Add(relPath);
                    continue;
                }

                var hash = DocumentVersionId.Compute(docId, normalized);
                var lineStarts = LineBreaks.ComputeLineStartsJson(normalized);

                map[docId] = hash;
                contentMap[docId] = (normalized, "utf-8", lineStarts);
                documentPaths.Add(relPath);

                if (GeneratedCode.IsGenerated(relPath, normalized)) generatedDocs.Add(docId);
            }

            projectDocuments[project.Name] = documentPaths;
            if (excludedPaths.Count > 0)
                excludedDocuments[project.Id] = excludedPaths;
        }

        if (skipped.Count > 0)
            sink.WriteErrorLine(
                $"WARNING: Skipped {skipped.Count} document(s) that are not valid UTF-8/UTF-16 text (excluded from indexing): "
                + string.Join(", ", skipped.Take(10)) + (skipped.Count > 10 ? $", … +{skipped.Count - 10} more" : ""));

        return (map, contentMap, generatedDocs, projectDocuments, excludedDocuments);
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

    private static string GetRelativePath(string fullPath, string normalizedRoot)
    {
        return PathNormalizer.ToGitRelativeFromNormalizedRoot(fullPath, normalizedRoot);
    }

    internal static string QuerySdkVersion(IOutputSink? output = null)
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
                var tf = ProjectTargetFramework.Read(project.FilePath, gitRoot);
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

    private static Dictionary<string, string> BuildAssemblyNameMap(Solution solution)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
            map[project.Name] = project.AssemblyName;
        return map;
    }

    private static Dictionary<string, string> BuildProjectPathMap(Solution solution, string gitRoot)
    {
        var normalizedRoot = PathNormalizer.NormalizeRoot(gitRoot);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!string.IsNullOrWhiteSpace(project.FilePath))
                map[project.Name] = GetRelativePath(project.FilePath, normalizedRoot);
        }
        return map;
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildInternalsVisibleToMap(Solution solution)
    {
        var targetsByProject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            HashSet<string>? targets = null;
            var parseOptions = project.ParseOptions as CSharpParseOptions;

            foreach (var document in project.Documents)
            {
                string? text = null;
                if (document.FilePath != null && File.Exists(document.FilePath))
                {
                    var rawBytes = File.ReadAllBytes(document.FilePath);
                    if (!TryNormalizeSourceBytes(rawBytes, out var normalized))
                        continue;
                    text = Encoding.UTF8.GetString(normalized);
                }
                else
                {
                    text = document.GetTextAsync().GetAwaiter().GetResult().ToString();
                }

                if (!text.Contains("InternalsVisibleTo", StringComparison.Ordinal))
                    continue;

                var tree = CSharpSyntaxTree.ParseText(text, parseOptions);
                if (tree.GetRoot() is not CompilationUnitSyntax root)
                    continue;

                foreach (var attrList in root.AttributeLists)
                {
                    if (attrList.Target is null || !string.Equals(attrList.Target.Identifier.ValueText, "assembly", StringComparison.Ordinal))
                        continue;

                    foreach (var attr in attrList.Attributes)
                    {
                        var lastName = attr.Name switch
                        {
                            SimpleNameSyntax simple => simple.Identifier.ValueText,
                            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                            AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
                            _ => null
                        };

                        if (lastName is not ("InternalsVisibleTo" or "InternalsVisibleToAttribute"))
                            continue;

                        string target = "*";
                        var firstArg = attr.ArgumentList?.Arguments.FirstOrDefault();
                        if (firstArg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            var raw = literal.Token.ValueText;
                            var comma = raw.IndexOf(',', StringComparison.Ordinal);
                            var simpleName = (comma >= 0 ? raw[..comma] : raw).Trim();
                            if (simpleName.Length > 0)
                                target = simpleName;
                        }

                        if (targets == null && !targetsByProject.TryGetValue(project.Name, out targets))
                        {
                            targets = new HashSet<string>(StringComparer.Ordinal);
                            targetsByProject[project.Name] = targets;
                        }

                        targets.Add(target);
                    }
                }
            }
        }

        return targetsByProject.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)[.. kvp.Value.OrderBy(static t => t, StringComparer.Ordinal)],
            StringComparer.Ordinal);
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
        var tokensByPath = new Dictionary<string, string>(PathNormalizer.PathComparer);

        foreach (var project in solution.Projects)
        {
            var identities = new List<string>();
            foreach (var reference in project.MetadataReferences)
                if (reference is PortableExecutableReference pe)
                {
                    var path = pe.FilePath;
                    if (string.IsNullOrEmpty(path))
                    {
                        identities.Add(FallbackReferenceToken(path));
                    }
                    else
                    {
                        if (!tokensByPath.TryGetValue(path, out var token))
                            tokensByPath[path] = token = TryGetAssemblyIdentity(path) ?? FallbackReferenceToken(path);
                        identities.Add(token);
                    }
                }
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