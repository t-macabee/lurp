using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Workspace;

internal static class DeclarationChange
{
    /// <summary>
    ///     Returns true when <paramref name="newTree" /> and <paramref name="previousText" />
    ///     differ only inside member bodies, non-const field initializers, or trivia.
    ///     A non-C# tree or a null/empty previous text is never body-only.
    /// </summary>
    internal static bool IsBodyOnlyEdit(SyntaxTree newTree, string? previousText)
    {
        if (newTree is not CSharpSyntaxTree || string.IsNullOrEmpty(previousText))
            return false;

        var oldTree = CSharpSyntaxTree.ParseText(
            previousText,
            (CSharpParseOptions)newTree.Options,
            newTree.FilePath);

        return newTree.IsEquivalentTo(oldTree, topLevel: true);
    }
}
