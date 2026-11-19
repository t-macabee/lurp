namespace Lurp.Storage;

internal static class AccessorEdges
{
    internal static bool Inherits(string accessorKind, string edgeKind)
    {
        return accessorKind switch
        {
            "get" => edgeKind != nameof(EdgeKind.Writes),
            "set" or "init" => edgeKind == nameof(EdgeKind.Writes),
            "add" or "remove" or "raise" => true,
            _ => false
        };
    }
}
