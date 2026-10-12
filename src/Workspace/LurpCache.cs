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
    private const string RedirectPropsFileName = "intermediate-redirect.props";

    // Two files, imported in two stages. Stage 1 (a props file, imported right after
    // the solution's Directory.Build.props through the CustomAfterDirectoryBuildProps
    // global property) remembers the solution's own CustomBeforeMicrosoftCommonTargets
    // value and points that property at the hook. Stage 2 (the hook) imports the
    // remembered file -- or, when the solution set none, the machine-wide default, with
    // the SDK's own path text -- then sets IntermediateOutputPath and OutputPath.
    //
    // The pair exists because a global property replaces the solution's own value at
    // Microsoft.Common.CurrentVersion.targets:38, and the import at :43 then reads the
    // replaced value; a global property value cannot carry $(...) (verified: it stays
    // literal). Running after Directory.Build.props is what lets stage 1 read the
    // solution's value before it is replaced.
    //
    // The hook must stay on CustomBeforeMicrosoftCommonTargets: the
    // CustomBefore/AfterDirectoryBuildProps pair (Microsoft.Common.props:32,36) runs
    // too early ($(TargetFramework) is empty), and CustomBefore/AfterDirectoryBuildTargets
    // (Microsoft.Common.targets:58,62) runs after CurrentVersion.targets has derived
    // properties from $(IntermediateOutputPath), so generated files (TargetRefPath, TFM
    // attributes, app.config) go back into the target tree.
    //
    // OutputPath is redirected too: PrepareForBuild makes $(OutDir), which would
    // otherwise leave empty bin/<config>/<tfm> directories in the target tree.
    //
    // Known limits: (a) a solution that sets CustomAfterDirectoryBuildProps itself loses
    // that value to Lurp's global -- the redirect still applies and the loss is not
    // detected; (b) a project that assigns CustomBeforeMicrosoftCommonTargets in its own
    // body keeps that hook and loses the redirect -- WorkspaceLoader reports it as the
    // intermediate_redirect_not_applied boundary; (c) no machine-wide
    // Custom.Before.Microsoft.Common.targets exists on the development machine, so only
    // the chained-value branch is exercised by the tests.
    private const string RedirectPropsContent = """
        <Project>
          <PropertyGroup>
            <_LurpPreviousCustomBefore>$(CustomBeforeMicrosoftCommonTargets)</_LurpPreviousCustomBefore>
            <CustomBeforeMicrosoftCommonTargets>$(LurpHookFile)</CustomBeforeMicrosoftCommonTargets>
          </PropertyGroup>
        </Project>
        """;

    private const string RedirectTargetsContent = """
        <Project>
          <PropertyGroup>
            <_LurpChain Condition="'$(_LurpPreviousCustomBefore)' != ''">$(_LurpPreviousCustomBefore)</_LurpChain>
            <_LurpChain Condition="'$(_LurpChain)' == ''">$(MSBuildExtensionsPath)\v$(MSBuildToolsVersion)\Custom.Before.Microsoft.Common.targets</_LurpChain>
          </PropertyGroup>
          <Import Project="$(_LurpChain)" Condition="Exists('$(_LurpChain)')" />
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
    ///     tree. The <c>CustomAfterDirectoryBuildProps</c> props file (stage 1) chains
    ///     the solution's own <c>CustomBeforeMicrosoftCommonTargets</c> into
    ///     <c>LurpHookFile</c> (stage 2) before replacing it.
    ///     <c>MSBuildProjectExtensionsPath</c> is deliberately left alone: it still
    ///     points at the real <c>obj/</c>, so restore assets resolve. The redirect
    ///     folder is named <c>obj</c> so relocated generated documents are still
    ///     classified as build output by the existing path filter.
    /// </summary>
    public static Dictionary<string, string> CreateWorkspaceGlobalProperties(string solutionPath)
    {
        var cacheDir = ResolveSolutionCacheDir(solutionPath);
        var hookFile = EnsureRedirectFile(cacheDir, RedirectTargetsFileName, RedirectTargetsContent);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomAfterDirectoryBuildProps"] = EnsureRedirectFile(cacheDir, RedirectPropsFileName, RedirectPropsContent),
            ["LurpHookFile"] = hookFile,
            ["LurpIntermediateRootDir"] = Path.Combine(cacheDir, "obj")
        };
    }

    private static string EnsureRedirectFile(string cacheDir, string fileName, string content)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, fileName);
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return path;

        try
        {
            File.WriteAllText(path, content);
        }
        catch (IOException) when (File.Exists(path))
        {
            // A concurrent run wrote the file first; its content is read-only data.
        }

        return path;
    }
}
