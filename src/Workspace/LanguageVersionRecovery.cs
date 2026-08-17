using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Xml;
using System.Xml.Linq;

namespace Lurp.Workspace;

/// <summary>
///     Restores compiler fidelity when MSBuildWorkspace cannot evaluate a project.
///     MSBuildWorkspace silently falls back to C# 7.3 parse options whenever its
///     project evaluation fails (for example, an SDK-style project with no
///     <c>&lt;TargetFramework&gt;</c> fails evaluation, which also leaves package
///     and metadata references unresolved). That hard-coded fallback compiles
///     modern C# source (file-scoped namespaces, records, global usings) as C# 7.3,
///     producing mass CS8370 diagnostics and suppressing semantic edges that depend
///     on those features binding.
///     This recovery derives each affected project's effective language version
///     from its own inputs instead of the fallback: an explicit
///     <c>&lt;LangVersion&gt;</c> property is authoritative, and an SDK-style
///     project with no explicit <c>LangVersion</c> uses the SDK default
///     (<c>latest</c>, mapped to <see cref="LanguageVersion.LatestMajor" />).
///     Non-SDK (legacy) projects are left untouched : C# 7.3 is their correct
///     default. Projects whose parse options were already resolved to a language
///     version other than the C# 7.3 fallback are also untouched.
/// </summary>
internal static class LanguageVersionRecovery
{
    /// <summary>
    ///     The language version MSBuildWorkspace assigns when it cannot
    ///     evaluate a project's <c>LangVersion</c>.
    /// </summary>
    private const LanguageVersion FallbackLanguageVersion = LanguageVersion.CSharp7_3;

    /// <summary>
    ///     Return a copy of <paramref name="solution" /> whose projects use their
    ///     effective language version instead of the C# 7.3 fallback. Projects
    ///     whose parse options are already correct are returned unchanged.
    /// </summary>
    public static Solution Apply(Solution solution, IOutputSink? output = null)
    {
        var sink = output ?? ConsoleOutputSink.Instance;
        var corrected = solution;

        foreach (var project in solution.Projects)
        {
            if (project.ParseOptions is not CSharpParseOptions parseOptions)
                continue;

            if (parseOptions.SpecifiedLanguageVersion != FallbackLanguageVersion)
                continue;

            if (!TryDetermineEffectiveLanguageVersion(project, out var effective, out var reason))
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
    ///     Derive the language version a project actually targets. Returns false
    ///     when the fallback is already the correct version (a non-SDK project with
    ///     no explicit <c>LangVersion</c>, or a project whose file cannot be read).
    /// </summary>
    private static bool TryDetermineEffectiveLanguageVersion(Project project, out LanguageVersion effective, out string reason)
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

        var ns = root.GetDefaultNamespace();

        // An explicit <LangVersion> in the project file is authoritative.
        var explicitLangVersion = root
            .Elements(ns + "PropertyGroup")
            .SelectMany(pg => pg.Elements(ns + "LangVersion"))
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);

        if (explicitLangVersion != null)
            if (TryParse(explicitLangVersion, out var parsed))
            {
                effective = parsed;
                reason = $"explicit LangVersion={explicitLangVersion}";
                return true;
            }

        // Unparseable explicit value: fall through to the SDK default so we
        // do not silently keep the C# 7.3 fallback for an SDK-style project.
        // SDK-style projects (Sdk="Microsoft.NET.Sdk*") default LangVersion to
        // "latest" when unset; that is what dotnet build would evaluate.
        var sdk = root.Attribute("Sdk")?.Value ?? "";
        if (!sdk.StartsWith("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase))
            return false;

        effective = LanguageVersion.LatestMajor;
        reason = "SDK-style project with unset LangVersion; SDK default is latest";
        return true;
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