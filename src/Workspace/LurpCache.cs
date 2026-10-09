using System.Security.Cryptography;
using System.Text;

namespace Lurp.Workspace;

/// <summary>
///     Per-user cache location for the default index database and MSBuild
///     design-time intermediates (audit B2). The location is keyed by the
///     canonical solution path, so the same solution resolves to the same
///     directory regardless of how the path was spelled.
/// </summary>
internal static class LurpCache
{
    private const string RedirectTargetsFileName = "intermediate-redirect.targets";

    // Imported by Microsoft.Common.CurrentVersion.targets through the
    // CustomBeforeMicrosoftCommonTargets global property, i.e. in project context,
    // where $(MSBuildProjectName) actually expands. A global property value cannot
    // carry $(...) (verified: it stays literal), hence the hook file.
    // OutputPath is redirected too: PrepareForBuild makes $(OutDir), which would
    // otherwise leave empty bin/<config>/<tfm> directories in the target tree.
    private const string RedirectTargetsContent = """
        <Project>
          <PropertyGroup>
            <IntermediateOutputPath>$(LurpIntermediateRootDir)\$(MSBuildProjectName)\$(TargetFramework)\</IntermediateOutputPath>
            <OutputPath>$(LurpIntermediateRootDir)\$(MSBuildProjectName)\$(TargetFramework)\bin\</OutputPath>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    ///     <c>%LOCALAPPDATA%\lurp\&lt;sha256-12&gt;</c> for the canonical solution
    ///     path: the default output directory and the intermediate-redirect root.
    /// </summary>
    public static string ResolveSolutionCacheDir(string solutionPath)
    {
        var key = PathIdentity.CanonicalKey(solutionPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Path.GetTempPath();

        return Path.Combine(root, "lurp", hash[..12]);
    }

    /// <summary>
    ///     Global properties for <c>MSBuildWorkspace.Create</c> that redirect every
    ///     project's design-time intermediates into the cache instead of the target
    ///     tree. <c>MSBuildProjectExtensionsPath</c> is deliberately left alone:
    ///     it still points at the real <c>obj/</c>, so restore assets resolve. The
    ///     redirect folder is named <c>obj</c> so relocated generated documents are
    ///     still classified as build output by the existing path filter.
    /// </summary>
    public static Dictionary<string, string> CreateWorkspaceGlobalProperties(string solutionPath)
    {
        var cacheDir = ResolveSolutionCacheDir(solutionPath);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomBeforeMicrosoftCommonTargets"] = EnsureRedirectTargetsFile(cacheDir),
            ["LurpIntermediateRootDir"] = Path.Combine(cacheDir, "obj")
        };
    }

    private static string EnsureRedirectTargetsFile(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, RedirectTargetsFileName);
        if (File.Exists(path) && File.ReadAllText(path) == RedirectTargetsContent)
            return path;

        try
        {
            File.WriteAllText(path, RedirectTargetsContent);
        }
        catch (IOException) when (File.Exists(path))
        {
            // A concurrent run wrote the file first; its content is read-only data.
        }

        return path;
    }
}
