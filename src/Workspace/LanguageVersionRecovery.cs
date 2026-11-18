using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Lurp.Workspace;

/// <summary>
///     Restores compiler fidelity when MSBuildWorkspace cannot evaluate a project.
///     MSBuildWorkspace silently falls back to C# 7.3 parse options whenever its
///     project evaluation fails (for example, an SDK-style project with no
///     <c>&lt;TargetFramework&gt;</c> fails evaluation, which also leaves package
///     and metadata references unresolved). A project that evaluated cleanly is
///     never changed — MSBuild itself selects C# 7.3 for <c>netstandard2.0</c>,
///     .NET Framework targets, and projects inheriting <c>LangVersion=7.3</c> from
///     <c>Directory.Build.props</c>.
///     For a project whose evaluation failed, recovery uses an explicit
///     <c>&lt;LangVersion&gt;</c> when present and parseable; otherwise an
///     SDK-style project uses the target-framework default (or
///     <see cref="LanguageVersion.LatestMajor" /> when no target framework is
///     declared), and a non-SDK project stays at C# 7.3.
/// </summary>
internal static class LanguageVersionRecovery
{
    /// <summary>
    ///     The language version MSBuildWorkspace assigns when it cannot
    ///     evaluate a project's <c>LangVersion</c>.
    /// </summary>
    private const LanguageVersion FallbackLanguageVersion = LanguageVersion.CSharp7_3;

    /// <summary>
    ///     Return a copy of <paramref name="solution" /> whose projects that failed
    ///     evaluation use their effective language version instead of the C# 7.3
    ///     fallback. Projects that evaluated cleanly or already resolved to another
    ///     language version are returned unchanged.
    /// </summary>
    public static Solution Apply(
        Solution solution,
        string gitRoot,
        IReadOnlyList<WorkspaceDiagnostic> loadDiagnostics,
        IOutputSink? output = null)
    {
        var sink = output ?? ConsoleOutputSink.Instance;
        var corrected = solution;

        foreach (var project in solution.Projects)
        {
            if (project.ParseOptions is not CSharpParseOptions parseOptions)
                continue;

            if (parseOptions.SpecifiedLanguageVersion != FallbackLanguageVersion)
                continue;

            if (!TryDetermineEffectiveLanguageVersion(project, gitRoot, loadDiagnostics, out var effective, out var reason))
                continue;

            var recovered = parseOptions.WithLanguageVersion(effective);
            if (Equals(recovered, parseOptions))
                continue;

            corrected = corrected.WithProjectParseOptions(project.Id, recovered);
            sink.WriteLine($"  [language-version recovery] {project.Name}: {reason} (was C# {parseOptions.LanguageVersion}, now {recovered.LanguageVersion})");
        }

        return corrected;
    }

    /// <summary>
    ///     Derive the language version a project actually targets after a failed
    ///     evaluation. Returns false when the project evaluated cleanly, when it is
    ///     not SDK-style and has no explicit <c>LangVersion</c>, or when its project
    ///     file cannot be read.
    /// </summary>
    private static bool TryDetermineEffectiveLanguageVersion(
        Project project,
        string gitRoot,
        IReadOnlyList<WorkspaceDiagnostic> loadDiagnostics,
        out LanguageVersion effective,
        out string reason)
    {
        effective = LanguageVersion.CSharp7_3;
        reason = "";

        if (project.FilePath == null || !File.Exists(project.FilePath))
            return false;

        XDocument document;
        try
        {
            document = XDocument.Load(project.FilePath);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var root = document.Root;
        if (root == null)
            return false;

        var rawTfm = ProjectTargetFramework.Read(root, project.FilePath, gitRoot);
        var firstTfmFromFile = ExtractFirstTargetFramework(rawTfm);

        var evaluationFailed = HasLoadFailure(project, loadDiagnostics) || firstTfmFromFile == null;
        if (!evaluationFailed)
            return false;

        var ns = root.GetDefaultNamespace();

        // An explicit <LangVersion> in the project file is authoritative.
        var explicitLangVersion = root
            .Elements(ns + "PropertyGroup")
            .SelectMany(pg => pg.Elements(ns + "LangVersion"))
            .Select(e => e.Value.Trim())
            .FirstOrDefault(static v => v.Length > 0);

        if (explicitLangVersion != null && TryParse(explicitLangVersion, out var parsed))
        {
            effective = parsed;
            reason = $"explicit LangVersion={explicitLangVersion}";
            return true;
        }

        if (!IsSdkStyle(root, ns))
            return false;

        var tfm = ExtractTargetFrameworkSuffix(project.Name) ?? firstTfmFromFile;
        if (tfm == null)
        {
            effective = LanguageVersion.LatestMajor;
            reason = "SDK-style project without TargetFramework";
            return true;
        }

        effective = MapTargetFrameworkDefault(tfm);
        reason = $"target framework {tfm} default";
        return true;
    }

    private static bool HasLoadFailure(Project project, IReadOnlyList<WorkspaceDiagnostic> loadDiagnostics)
    {
        var normalizedProjectPath = project.FilePath != null
            ? PathNormalizer.ToForwardSlash(Path.GetFullPath(project.FilePath))
            : null;
        foreach (var diagnostic in loadDiagnostics)
        {
            if (diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                continue;

            if (diagnostic is ProjectDiagnostic projectDiagnostic && projectDiagnostic.ProjectId == project.Id)
                return true;

            if (project.FilePath != null && diagnostic.Message.Contains(project.FilePath, PathNormalizer.PathComparison))
                return true;

            if (normalizedProjectPath != null
                && PathNormalizer.ToForwardSlash(diagnostic.Message).Contains(normalizedProjectPath, PathNormalizer.PathComparison))
                return true;
        }

        return false;
    }

    private static bool IsSdkStyle(XElement root, XNamespace ns)
    {
        var sdkAttribute = root.Attribute("Sdk")?.Value;
        if (sdkAttribute != null && sdkAttribute.StartsWith("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase))
            return true;

        return root.Elements(ns + "Sdk")
            .Select(static e => e.Attribute("Name")?.Value)
            .Any(static name => name != null && name.StartsWith("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractTargetFrameworkSuffix(string projectName)
    {
        var open = projectName.LastIndexOf('(');
        if (open >= 0 && projectName.EndsWith(')') && open < projectName.Length - 2)
        {
            var suffix = projectName[(open + 1)..^1].Trim();
            if (suffix.Length > 0)
                return suffix;
        }

        return null;
    }

    private static string? ExtractFirstTargetFramework(string? rawTfm)
    {
        if (string.IsNullOrWhiteSpace(rawTfm))
            return null;

        foreach (var entry in rawTfm.Split(';'))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return null;
    }

    private static LanguageVersion MapTargetFrameworkDefault(string tfm)
    {
        var normalized = tfm.Trim().ToLowerInvariant();
        var dash = normalized.IndexOf('-');
        if (dash >= 0)
            normalized = normalized[..dash];

        if (normalized.StartsWith("netcoreapp3.", StringComparison.Ordinal))
            return LanguageVersion.CSharp8;

        if (normalized == "netstandard2.1")
            return LanguageVersion.CSharp8;

        if (normalized.StartsWith("net", StringComparison.Ordinal)
            && normalized.EndsWith(".0", StringComparison.Ordinal)
            && int.TryParse(normalized[3..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return major switch
            {
                5 => LanguageVersion.CSharp9,
                6 => LanguageVersion.CSharp10,
                7 => LanguageVersion.CSharp11,
                8 => LanguageVersion.CSharp12,
                9 => LanguageVersion.CSharp13,
                10 => LanguageVersion.CSharp14,
                >= 11 => LanguageVersion.LatestMajor,
                _ => LanguageVersion.CSharp7_3
            };
        }

        return LanguageVersion.CSharp7_3;
    }

    private static bool TryParse(string value, out LanguageVersion version)
    {
        var normalized = value.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "default":
                version = LanguageVersion.Default;
                return true;
            case "latest":
            case "latestmajor":
                version = LanguageVersion.LatestMajor;
                return true;
            case "preview":
                version = LanguageVersion.Preview;
                return true;
        }

        // Numeric MSBuild forms: "7", "7.3", "8.0", ..., "14.0".
        var parts = normalized.Split('.');
        var candidate = parts.Length switch
        {
            1 => "CSharp" + parts[0],
            2 when parts[1] == "0" => "CSharp" + parts[0],
            2 => "CSharp" + parts[0] + "_" + parts[1],
            _ => null
        };

        if (candidate != null && Enum.TryParse(candidate, true, out version))
            return true;

        version = LanguageVersion.Default;
        return false;
    }
}
