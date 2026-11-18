using System.Xml.Linq;

namespace Lurp.Workspace;

/// <summary>
///     Single reader of a project's declared target framework: checks
///     <c>&lt;TargetFramework&gt;</c> and <c>&lt;TargetFrameworks&gt;</c> in the
///     project file first, then walks <c>Directory.Build.props</c> files up to the
///     repository root. Empty or whitespace-only values are treated as absent.
/// </summary>
internal static class ProjectTargetFramework
{
    public static string? Read(string projectFilePath, string repoRoot)
    {
        var doc = XDocument.Load(projectFilePath);
        return Read(doc.Root, projectFilePath, repoRoot);
    }

    public static string? Read(XElement? projectRoot, string projectFilePath, string repoRoot)
    {
        return ParseFromRoot(projectRoot) ?? FindInDirectoryBuildProps(projectFilePath, repoRoot);
    }

    private static string? ParseFromRoot(XElement? root)
    {
        if (root == null) return null;

        var ns = root.GetDefaultNamespace();

        return root
                   .Elements(ns + "PropertyGroup")
                   .SelectMany(pg => pg.Elements(ns + "TargetFramework"))
                   .Select(e => e.Value.Trim())
                   .FirstOrDefault(static v => v.Length > 0)
               ?? root
                   .Elements(ns + "PropertyGroup")
                   .SelectMany(pg => pg.Elements(ns + "TargetFrameworks"))
                   .Select(e => e.Value.Trim())
                   .FirstOrDefault(static v => v.Length > 0);
    }

    private static string? FindInDirectoryBuildProps(string projectFilePath, string repoRoot)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        if (dir == null) return null;

        var normalizedRoot = PathNormalizer.NormalizeRoot(repoRoot);

        while (dir.Length >= normalizedRoot.Length)
        {
            var propsPath = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(propsPath))
                try
                {
                    var doc = XDocument.Load(propsPath);
                    var tfm = ParseFromRoot(doc.Root);
                    if (tfm != null)
                        return tfm;
                }
                catch
                {
                }

            if (string.Equals(dir, normalizedRoot, PathNormalizer.PathComparison))
                break;

            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir)
                break;
            dir = parent;
        }

        return null;
    }
}
