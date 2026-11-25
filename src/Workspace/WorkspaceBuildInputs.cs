using System.Security.Cryptography;

namespace Lurp.Workspace;

/// <summary>Build-file paths and content observed before MSBuild opens the solution.</summary>
internal sealed class WorkspaceBuildInputs
{
    // Opened by name rather than by listing the directory. A parent directory that is
    // executable but not readable still resolves these names, and a missing file is
    // allowed. NuGet on Linux accepts the three spellings below.
    private static readonly string[] ParentInputNames =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json",
        "nuget.config", "NuGet.config", "NuGet.Config"
    ];

    private static readonly EnumerationOptions ObjAssetsEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = true
    };

    private readonly string _root;
    private readonly string _normalizedRoot;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<string, string> _hashes = new(PathNormalizer.PathComparer);
    private readonly HashSet<string> _sampledExternalProjects = new(PathNormalizer.PathComparer);
    private bool _captureFailed;

    public WorkspaceBuildInputs(string root, SnapshotRow? previous, CancellationToken cancellationToken)
    {
        _root = Path.GetFullPath(root);
        _normalizedRoot = PathNormalizer.NormalizeRoot(_root);
        _cancellationToken = cancellationToken;

        try
        {
            foreach (var (directory, isObj) in WorkspaceFreshness.WalkInputDirectories(_normalizedRoot, includeObj: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = isObj
                    ? Directory.EnumerateFiles(directory, "project.assets.json", ObjAssetsEnumerationOptions)
                    : Directory.EnumerateFiles(directory, "*", WorkspaceFreshness.WalkEnumerationOptions);

                foreach (var path in files)
                {
                    if (isObj ? Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)
                              : IsBuildInputPath(path))
                        AddHash(path, allowMissing: false);
                }
            }

            for (var parent = Directory.GetParent(_root); parent is not null; parent = parent.Parent)
            {
                foreach (var name in ParentInputNames)
                    AddHash(Path.Combine(parent.FullName, name), allowMissing: true);
            }

            if (previous is not null)
                CaptureExternalProjects(previous.Projects.Select(project => project.ProjectPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _captureFailed = true;
        }
    }

    public void CaptureExternalProjects(IEnumerable<string?> paths)
    {
        if (_captureFailed)
            return;

        try
        {
            foreach (var path in paths)
            {
                if (path is null)
                    continue;

                var fullPath = Path.GetFullPath(path, _root);
                var relativePath = RelativePath(fullPath);
                if (IsExternal(relativePath) && !_hashes.ContainsKey(relativePath) &&
                    _sampledExternalProjects.Add(relativePath))
                    AddHash(fullPath, allowMissing: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _captureFailed = true;
        }
    }

    public bool Matches(IReadOnlyDictionary<string, string> stored) =>
        !_captureFailed && stored.Count > 0 && stored.Count == _hashes.Count &&
        _sampledExternalProjects.All(_hashes.ContainsKey) &&
        _hashes.All(input => stored.TryGetValue(input.Key, out var hash) && hash == input.Value);

    public IReadOnlyDictionary<string, string> ForLoadedProjects(IEnumerable<string> paths)
    {
        if (_captureFailed)
            _hashes.Clear();
        else
        {
            var loadedPaths = paths.ToHashSet(PathNormalizer.PathComparer);
            foreach (var path in _sampledExternalProjects)
            {
                if (!loadedPaths.Contains(path))
                    _hashes.Remove(path);
            }
        }

        // An external project first discovered by MSBuild has no pre-load hash.
        // Its stored project path makes the next precheck capture it and reload.
        return _hashes;
    }

    private string RelativePath(string path) =>
        PathNormalizer.ToGitRelativeFromNormalizedRoot(path, _normalizedRoot);

    private static bool IsExternal(string relativePath) =>
        relativePath.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relativePath);

    private void AddHash(string path, bool allowMissing)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var relativePath = RelativePath(path);
        if (_hashes.ContainsKey(relativePath))
            return;

        try
        {
            using var file = File.OpenRead(path);
            _hashes.Add(relativePath, Convert.ToHexStringLower(SHA256.HashData(file)));
        }
        catch (Exception ex) when (allowMissing && ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A formerly loaded project can disappear before the next run.
        }
    }

    private static bool IsBuildInputPath(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            return true;

        var fileName = Path.GetFileName(path);
        return fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("nuget.config", StringComparison.OrdinalIgnoreCase);
    }
}
