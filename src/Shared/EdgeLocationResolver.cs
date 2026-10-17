using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics.CodeAnalysis;

namespace Lurp.Shared;

public sealed class EdgeLocationResolver
{
    private readonly Dictionary<string, string> _documentPathLookup;
    private readonly HashSet<string> _generatedDocumentPaths;
    private readonly string _gitRoot;

    public EdgeLocationResolver(
        IEnumerable<string> documentPaths,
        IEnumerable<string> generatedDocumentPaths,
        string gitRoot)
    {
        ArgumentNullException.ThrowIfNull(documentPaths);
        ArgumentNullException.ThrowIfNull(generatedDocumentPaths);

        var paths = documentPaths
            .Select(static path => PathNormalizer.ToForwardSlash(path))
            .ToArray();
        _documentPathLookup = BuildDocumentPathLookup(paths);
        _generatedDocumentPaths = generatedDocumentPaths
            .Select(static path => PathNormalizer.ToForwardSlash(path))
            .ToHashSet(StringComparer.Ordinal);
        _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
    }

    public (string? path, int? sl, int? sc, int? el, int? ec) Resolve(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        var path = ResolveDocumentPath(location.SourceTree);
        return (path, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character, lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character);
    }

    public (string? path, int? sl, int? sc, int? el, int? ec) Resolve(ISymbol symbol)
    {
        var syntaxRef = PrimaryDeclaration(symbol);
        if (syntaxRef == null)
            return (null, null, null, null, null);

        return Resolve(DeclarationNode(symbol, syntaxRef.GetSyntax()).GetLocation());
    }

    /// <summary>
    ///     Roslyn reports the type declaration as a primary constructor's declaring syntax,
    ///     so its span would be the whole type: any member edit would move every edge located
    ///     at the constructor, and a line anywhere in the type would resolve to it. The
    ///     parameter list is the primary constructor's own declaration; every other symbol
    ///     keeps the syntax node Roslyn reports.
    /// </summary>
    internal static SyntaxNode DeclarationNode(ISymbol symbol, SyntaxNode declaringSyntax)
    {
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } &&
            declaringSyntax is TypeDeclarationSyntax { ParameterList: { } parameterList })
            return parameterList;

        return declaringSyntax;
    }

    /// <summary>
    ///     Deterministically select the declaring syntax reference used as a symbol's
    ///     single source location. Roslyn does not guarantee a stable order for
    ///     <see cref="ISymbol.DeclaringSyntaxReferences" /> across compilations, so a
    ///     bare <c>FirstOrDefault()</c> can pick a different partial declaration on a
    ///     full vs. incremental rebuild. A declaration in a generated file sorts after a
    ///     hand-written one; then (file path, span start) makes the choice reproducible,
    ///     which the full==incremental parity reference requires.
    /// </summary>
    public static SyntaxReference? PrimaryDeclaration(ISymbol symbol)
    {
        return symbol.DeclaringSyntaxReferences
            .OrderBy(static r => GeneratedFilePaths.IsGenerated(PathNormalizer.ToForwardSlash(r.SyntaxTree.FilePath)))
            .ThenBy(static r => r.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(static r => r.Span.Start)
            .FirstOrDefault();
    }

    public bool IsGenerated(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var normalized = PathNormalizer.ToForwardSlash(path);
        if (_generatedDocumentPaths.Contains(normalized))
            return true;

        return GeneratedFilePaths.IsGenerated(normalized);
    }

    private string? ResolveDocumentPath(SyntaxTree? syntaxTree)
    {
        if (syntaxTree == null)
            return null;

        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return null;

        var normalized = PathNormalizer.ToForwardSlash(filePath);

        if (TryResolveNormalizedPath(normalized, out var match))
            return match;

        if (!Path.IsPathRooted(filePath))
            return normalized;

        return PathNormalizer.ToGitRelative(filePath, _gitRoot);
    }

    private bool TryResolveNormalizedPath(string normalized, [NotNullWhen(true)] out string? match)
    {
        if (_documentPathLookup.TryGetValue(normalized, out match))
            return true;

        var span = normalized.AsSpan();
        for (var i = 0; i < span.Length; i++)
            if (span[i] == '/')
            {
                var suffix = span[(i + 1)..].ToString();
                if (_documentPathLookup.TryGetValue(suffix, out match))
                    return true;
            }

        match = null;
        return false;
    }

    private static Dictionary<string, string> BuildDocumentPathLookup(IReadOnlyList<string> documentPaths)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        var suffixClaims = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in documentPaths)
        {
            lookup[path] = path;

            var span = path.AsSpan();
            for (var i = 0; i < span.Length; i++)
                if (span[i] == '/')
                {
                    var suffix = span[(i + 1)..].ToString();
                    if (lookup.ContainsKey(suffix))
                        continue;
                    if (ambiguous.Contains(suffix))
                        continue;
                    if (suffixClaims.TryGetValue(suffix, out var existing))
                    {
                        if (!string.Equals(existing, path, StringComparison.Ordinal))
                        {
                            suffixClaims.Remove(suffix);
                            ambiguous.Add(suffix);
                        }
                    }
                    else
                    {
                        suffixClaims[suffix] = path;
                    }
                }
        }

        foreach (var kv in suffixClaims)
            lookup[kv.Key] = kv.Value;

        return lookup;
    }
}