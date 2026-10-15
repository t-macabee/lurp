namespace Lurp.Workspace;

internal static class FqnParts
{
    internal static string SimpleName(string? fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return string.Empty;
        var idx = fqn.LastIndexOf('.');
        return idx < 0 ? fqn : fqn[(idx + 1)..];
    }

    internal static string Container(string? fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return string.Empty;
        var idx = fqn.LastIndexOf('.');
        return idx < 0 ? string.Empty : fqn[..idx];
    }

    internal static SymbolTransitionKind? ClassifyRenameMove(string? previousFqn, string? currentFqn)
    {
        var previousName = SimpleName(previousFqn);
        var currentName = SimpleName(currentFqn);
        var previousContainer = Container(previousFqn);
        var currentContainer = Container(currentFqn);

        var nameChanged = !string.Equals(previousName, currentName, StringComparison.Ordinal);
        var containerChanged = !string.Equals(previousContainer, currentContainer, StringComparison.Ordinal);

        if (nameChanged && containerChanged)
            return SymbolTransitionKind.RenameAndMove;
        if (nameChanged)
            return SymbolTransitionKind.Rename;
        if (containerChanged)
            return SymbolTransitionKind.Move;

        return null;
    }
}
