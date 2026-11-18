namespace Lurp.Shared;

/// <summary>
///     The single owner of the file-name half of the generated-code rule (D4, D167.1):
///     Roslyn's <c>GeneratedCodeUtilities</c> file-name patterns plus EF Core's
///     <c>ModelSnapshot.cs</c>, all matched case-insensitively on the file name.
///     The leading-comment half lives in <c>Lurp.Workspace.GeneratedCode</c>, which
///     has access to Roslyn.
/// </summary>
public static class GeneratedFilePaths
{
    public static bool IsGenerated(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var normalized = PathNormalizer.ToForwardSlash(path);
        var lastSlash = normalized.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? normalized.AsSpan(lastSlash + 1) : normalized.AsSpan();
        if (fileName.IsEmpty)
            return false;

        return fileName.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase);
    }
}
