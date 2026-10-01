namespace Lurp.Workspace;

internal static class DeadCandidateKinds
{
    internal static bool IsValidKind(string kind)
    {
        return string.Equals(kind, "Type", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Method", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Property", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Field", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Event", StringComparison.OrdinalIgnoreCase);
    }
}
